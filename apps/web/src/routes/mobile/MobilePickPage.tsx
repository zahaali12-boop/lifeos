import { Badge, Button, EmptyState, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { ArrowLeft, CircleCheck, MapPin, PackageSearch, Undo2 } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatNumber } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { FormError, TextField } from "../common";
import { PickProgress, PickStatus, type PickList, type PickListLine } from "../inventory/picking";
import { useOnline, useScanContext } from "./context";
import { resolveItem } from "./items";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";

type Step = "bin" | "item" | "serials" | "confirm";

const quickReasons = ["picking.reasons.empty", "picking.reasons.damaged", "picking.reasons.wrongItem"] as const;

const qty = (value: number | string): string => formatNumber(value, { maximumFractionDigits: 3 });

/**
 * Picking on the scanner (roadmap 5.5, A-154): take a pick list, walk it line by line in the order the warehouse is
 * laid out, scan the bin and the item (and each serial), confirm the quantity or short-pick it with a reason. Picking
 * checks live stock, so it needs a connection; nothing leaves the ledger until the office posts the shipment.
 */
export function MobilePickPage() {
  const { t } = useTranslation();
  const context = useScanContext();
  const online = useOnline();
  const search: { list?: string } = useSearch({ strict: false });
  const navigate = useNavigate();
  const listId = search.list ?? null;
  const ready = context.companyId !== null && context.warehouseId !== null;

  if (!ready && !listId) {
    return <EmptyState title={t("picking.scanner.title")} description={t("mobile.count.noContext")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      {!online ? <p className="rounded-md bg-warning-soft px-3 py-2 text-sm text-warning" data-testid="pick-offline">{t("picking.scanner.offline")}</p> : null}
      {listId ? (
        <PickWork listId={listId} onBack={() => { void navigate({ to: "/m/pick", search: {} }); }} />
      ) : (
        <PickQueue companyId={context.companyId ?? ""} warehouseId={context.warehouseId ?? ""} onOpen={(id) => { void navigate({ to: "/m/pick", search: { list: id } }); }} />
      )}
    </div>
  );
}

function PickQueue({ companyId, warehouseId, onOpen }: { companyId: string; warehouseId: string; onOpen: (id: string) => void }) {
  const { t } = useTranslation();
  const [error, setError] = useState<string | null>(null);
  const lists = useQuery({
    queryKey: ["mobile", "pick-lists", companyId, warehouseId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/pick-lists", { params: { query: { companyId, warehouseId, status: "live" } } })),
    refetchInterval: 30_000,
  });
  const mine = useQuery({
    queryKey: ["mobile", "pick-lists", "mine", companyId, warehouseId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/pick-lists", { params: { query: { companyId, warehouseId, status: "live", mine: true } } })),
  });
  const claim = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/claim", { params: { path: { pickListId: id } } })),
    onSuccess: (list) => { setError(null); onOpen(list.id); },
    onError: (caught) => { setError(toFormProblem(caught, t("common.saveFailed")).message); },
  });
  const mineIds = new Set((mine.data ?? []).map((l) => l.id));
  const rows = [...(lists.data ?? [])].sort((a, b) => Number(mineIds.has(b.id)) - Number(mineIds.has(a.id)));

  return (
    <>
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{t("picking.scanner.title")}</h1>
        <p className="mt-1 text-sm text-fg-muted">{t("picking.scanner.description")}</p>
      </div>
      <FormError message={error} />
      {rows.length === 0 && !lists.isPending ? (
        <EmptyState title={t("picking.scanner.none")} description={t("picking.scanner.noneDescription")} />
      ) : (
        <ul className="flex flex-col gap-2" data-testid="pick-queue">
          {rows.map((row) => {
            const isMine = mineIds.has(row.id);
            const taken = row.assignedTo !== null && !isMine;
            return (
              <li key={row.id} className="flex flex-col gap-2 rounded-md border border-border bg-surface p-3" data-testid="pick-queue-item">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-semibold" dir="ltr">{row.number}</span>
                  <PickStatus status={row.status} />
                </div>
                <div className="flex items-center justify-between gap-2 text-sm text-fg-muted">
                  <span dir="ltr">{row.sourceNumber}</span>
                  <PickProgress done={row.linesDone} total={row.linesTotal} />
                </div>
                <div className="text-xs text-fg-muted">
                  {isMine ? t("picking.scanner.yours") : row.assignedName ? t("picking.scanner.takenBy", { name: row.assignedName }) : t("picking.unassigned")}
                </div>
                <Button size="lg" className="h-12" variant={taken ? "secondary" : "primary"} loading={claim.isPending && claim.variables === row.id} onClick={() => { if (isMine) { onOpen(row.id); } else { claim.mutate(row.id); } }} data-testid="pick-start">
                  {isMine ? t("picking.scanner.continue") : t("picking.scanner.start")}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}

function PickWork({ listId, onBack }: { listId: string; onBack: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const detail = useQuery({
    queryKey: ["mobile", "pick-list", listId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/pick-lists/{pickListId}", { params: { path: { pickListId: listId } } })),
  });
  const list = detail.data;
  const bins = useQuery({
    queryKey: ["mobile", "bins", list?.warehouseId],
    enabled: Boolean(list?.warehouseId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: list?.warehouseId ?? "" } } })),
  });
  const [chosen, setChosen] = useState<string | null>(null);
  const openLines = (list?.lines ?? []).filter((l) => l.status === "open");
  const line = (list?.lines ?? []).find((l) => l.id === chosen) ?? openLines[0] ?? null;

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["mobile", "pick-list", listId] });
    await queryClient.invalidateQueries({ queryKey: ["mobile", "pick-lists"] });
  };
  const reset = useMutation({
    mutationFn: async (lineId: string) => unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/lines/{lineId}/reset", { params: { path: { pickListId: listId, lineId } } })),
    onSuccess: async (_, lineId) => { setChosen(lineId); await refresh(); },
  });

  if (!list) {
    return detail.isError ? <FormError message={t("picking.scanner.loadFailed")} /> : null;
  }

  const done = list.status === "picked";
  return (
    <>
      <div className="flex items-center gap-2">
        <Button variant="ghost" size="icon" aria-label={t("picking.scanner.backToLists")} onClick={onBack} data-testid="pick-back">
          <ArrowLeft aria-hidden="true" className="rtl:rotate-180" />
        </Button>
        <div className="min-w-0 flex-1">
          <h1 className="truncate text-lg font-semibold tracking-tight" dir="ltr">{list.number}</h1>
          <p className="text-xs text-fg-muted">{t("picking.scanner.forSource", { source: list.sourceNumber })}</p>
        </div>
        <PickProgress done={list.linesDone} total={list.linesTotal} testId="pick-work-progress" />
      </div>
      {done ? (
        <div className="flex flex-col items-center gap-2 rounded-md border border-success bg-success-soft p-4 text-center" data-testid="pick-done">
          <CircleCheck className="size-8 text-success" aria-hidden="true" />
          <p className="font-semibold">{t("picking.scanner.allPicked")}</p>
          <p className="text-sm text-fg-muted">{t("picking.scanner.allPickedHint", { picked: qty(list.qtyPicked), planned: qty(list.qtyToPick) })}</p>
          <Button size="lg" className="h-12 w-full" onClick={onBack}>{t("picking.scanner.backToLists")}</Button>
        </div>
      ) : line ? (
        <PickLineCard key={line.id} list={list} line={line} bins={bins.data ?? []} onPicked={async () => { setChosen(null); await refresh(); }} />
      ) : null}
      <section aria-labelledby="pick-route-heading">
        <h2 id="pick-route-heading" className="mb-2 text-sm font-semibold text-fg-muted">{t("picking.scanner.route")}</h2>
        <ol className="flex flex-col gap-1" data-testid="pick-route">
          {list.lines.map((l) => (
            <li key={l.id} className={`flex items-center gap-2 rounded-md border px-3 py-2 text-sm ${l.id === line?.id && !done ? "border-accent bg-accent-soft" : "border-border bg-surface"}`} data-testid="pick-route-line">
              <span className="w-6 tabular text-fg-muted">{String(l.lineNo)}</span>
              <span className="w-16 font-medium" dir="ltr">{l.binCode ?? "—"}</span>
              <span className="min-w-0 flex-1 truncate" dir="ltr">{l.itemCode}</span>
              <span className="tabular" dir="ltr">{l.status === "open" ? qty(l.qtyToPick) : `${qty(l.qtyPicked)}/${qty(l.qtyToPick)}`}</span>
              {l.status === "open" ? (
                l.id !== line?.id ? <Button variant="ghost" size="sm" onClick={() => { setChosen(l.id); }} data-testid="pick-route-go">{t("picking.scanner.go")}</Button> : null
              ) : (
                <>
                  <PickStatus status={l.status} testId="pick-route-status" />
                  <Button variant="ghost" size="icon" aria-label={t("picking.scanner.undo")} onClick={() => { reset.mutate(l.id); }} data-testid="pick-undo">
                    <Undo2 aria-hidden="true" />
                  </Button>
                </>
              )}
            </li>
          ))}
        </ol>
      </section>
    </>
  );
}

function PickLineCard({ list, line, bins, onPicked }: { list: PickList; line: PickListLine; bins: { id: string; code: string }[]; onPicked: () => Promise<void> }) {
  const { t } = useTranslation();
  const serialTracked = line.serialNumbers.length > 0;
  const lotTracked = line.lotId !== null;
  const firstStep: Step = line.binCode ? "bin" : "item";
  const [step, setStep] = useState<Step>(firstStep);
  const [bin, setBin] = useState<{ id: string; code: string } | null>(null);
  const [serials, setSerials] = useState<string[]>([]);
  const [quantity, setQuantity] = useState(String(line.qtyToPick));
  const [lot, setLot] = useState("");
  const [reason, setReason] = useState("");
  const [shorting, setShorting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const planned = Number(line.qtyToPick);

  useEffect(() => { setStep(firstStep); }, [firstStep]);

  const pick = useMutation({
    mutationFn: async (body: { quantity: number; binId: string | null; lotNumber: string | null; serialNumbers: string[] | null; shortReason: string | null }) =>
      unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/lines/{lineId}/pick", { params: { path: { pickListId: list.id, lineId: line.id } }, body })),
    onSuccess: async () => { setError(null); await onPicked(); },
    onError: (caught) => { setError(toFormProblem(caught, t("common.saveFailed")).message); },
  });

  const onCode = async (code: string): Promise<void> => {
    setError(null);
    setNotice(null);
    if (step === "bin") {
      const found = bins.find((b) => b.code.localeCompare(code, undefined, { sensitivity: "accent" }) === 0);
      if (!found) {
        setError(t("mobile.count.binUnknown", { code }));
        return;
      }
      if (found.id !== line.binId) {
        setNotice(t("picking.scanner.otherBin", { bin: found.code, planned: line.binCode ?? "" }));
      }
      setBin(found);
      setStep("item");
      return;
    }
    if (step === "item") {
      let resolved = null;
      try {
        resolved = await resolveItem(code);
      } catch {
        setError(t("common.saveFailed"));
        return;
      }
      if (!resolved) {
        setError(t("mobile.count.itemUnknown", { code }));
        return;
      }
      if (resolved.itemId !== line.itemId) {
        setError(t("picking.scanner.wrongItem", { scanned: resolved.itemCode, expected: line.itemCode }));
        return;
      }
      setStep(serialTracked ? "serials" : "confirm");
      return;
    }
    if (step === "serials") {
      if (serials.includes(code)) {
        setError(t("picking.scanner.serialTwice", { serial: code }));
        return;
      }
      const next = [...serials, code];
      setSerials(next);
      if (next.length >= planned) {
        setStep("confirm");
      }
    }
  };

  const picked = serialTracked ? serials.length : Number(parseQuantity(quantity) ?? Number.NaN);
  const short = Number.isFinite(picked) && picked < planned;
  const confirm = (): void => {
    if (!Number.isFinite(picked) || picked < 0 || picked > planned) {
      setError(t("picking.scanner.quantityInvalid", { max: qty(planned) }));
      return;
    }
    if ((short || shorting) && !reason.trim()) {
      setShorting(true);
      setError(t("picking.scanner.reasonRequired"));
      return;
    }
    pick.mutate({
      quantity: picked,
      binId: bin?.id ?? null,
      lotNumber: lot.trim() || null,
      serialNumbers: serialTracked ? serials : null,
      shortReason: short ? reason.trim() : null,
    });
  };
  const nothingHere = (): void => {
    if (!reason.trim()) {
      setShorting(true);
      setError(t("picking.scanner.reasonRequired"));
      return;
    }
    pick.mutate({ quantity: 0, binId: bin?.id ?? null, lotNumber: null, serialNumbers: null, shortReason: reason.trim() });
  };

  const prompt = step === "bin" ? t("picking.scanner.scanBin", { bin: line.binCode ?? "" }) : step === "item" ? t("picking.scanner.scanItem") : step === "serials" ? t("picking.scanner.scanSerial", { count: serials.length + 1, total: qty(planned) }) : t("picking.scanner.confirmPrompt");
  const steps = useMemo<Step[]>(() => [...(line.binCode ? ["bin" as const] : []), "item", ...(serialTracked ? ["serials" as const] : []), "confirm"], [line.binCode, serialTracked]);

  return (
    <section className="flex flex-col gap-3 rounded-lg border-2 border-accent bg-surface p-4" aria-labelledby="pick-current-heading" data-testid="pick-current">
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2">
          <MapPin className="size-6 text-accent" aria-hidden="true" />
          <div>
            <div className="text-xs text-fg-muted">{t("picking.bin")}{line.zone ? ` · ${t("picking.zone", { zone: line.zone })}` : ""}</div>
            <div id="pick-current-heading" className="text-3xl font-bold tracking-tight" dir="ltr" data-testid="pick-current-bin">{line.binCode ?? t("picking.scanner.anyShelf")}</div>
          </div>
        </div>
        <div className="text-end">
          <div className="text-xs text-fg-muted">{t("picking.toPick")}</div>
          <div className="text-3xl font-bold tabular" dir="ltr" data-testid="pick-current-qty">{qty(line.qtyToPick)} <span className="text-base font-medium text-fg-muted">{line.uomCode}</span></div>
        </div>
      </div>
      <div className="flex items-start gap-2">
        <PackageSearch className="mt-0.5 size-5 text-fg-muted" aria-hidden="true" />
        <div>
          <div className="font-semibold" dir="ltr">{line.itemCode}</div>
          <div className="text-sm text-fg-muted">{localized(line.itemName)}</div>
          {lotTracked ? (
            <div className="text-sm" dir="ltr" data-testid="pick-current-lot">
              {t("picking.lotShort", { lot: line.lotNumber ?? "" })}
              {line.expiresOn ? ` · ${t("picking.expires", { date: formatDate(line.expiresOn) })}` : ""}
            </div>
          ) : null}
          {serialTracked ? <div className="text-xs text-fg-muted" dir="ltr">{t("picking.scanner.plannedSerials", { serials: line.serialNumbers.join(", ") })}</div> : null}
        </div>
      </div>
      <ol className="flex gap-1" aria-label={t("picking.scanner.steps")}>
        {steps.map((s) => (
          <li key={s} className="flex-1">
            <Badge tone={s === step ? "accent" : steps.indexOf(s) < steps.indexOf(step) ? "success" : "neutral"} className="w-full justify-center">
              {t(`picking.scanner.step.${s}`)}
            </Badge>
          </li>
        ))}
      </ol>
      {step !== "confirm" ? <ScanBox label={prompt} error={error} onCode={onCode} /> : <p className="text-sm font-medium">{prompt}</p>}
      {notice ? <p className="text-sm text-warning" data-testid="pick-notice">{notice}</p> : null}
      {serialTracked && serials.length > 0 ? (
        <ul className="flex flex-wrap gap-1" data-testid="pick-serials">
          {serials.map((sn) => <li key={sn}><Badge tone="info" dir="ltr">{sn}</Badge></li>)}
        </ul>
      ) : null}
      {step === "serials" && serials.length > 0 ? (
        <Button variant="secondary" onClick={() => { setStep("confirm"); }} data-testid="pick-serials-done">{t("picking.scanner.serialsDone")}</Button>
      ) : null}
      {step === "confirm" ? (
        <>
          {!serialTracked ? (
            <Field label={t("picking.scanner.quantityPicked")}>
              <TextField className="h-12 text-lg" inputMode="decimal" autoComplete="off" value={quantity} onChange={(e) => { setQuantity(e.target.value); }} data-testid="pick-quantity" dir="ltr" />
            </Field>
          ) : null}
          {lotTracked ? (
            <Field label={t("picking.scanner.otherLot")} description={t("picking.scanner.otherLotHint", { lot: line.lotNumber ?? "" })}>
              <TextField className="h-12" autoComplete="off" value={lot} onChange={(e) => { setLot(e.target.value); }} data-testid="pick-lot" dir="ltr" />
            </Field>
          ) : null}
          {error ? <FormError message={error} /> : null}
        </>
      ) : null}
      {short || shorting ? (
        <div className="flex flex-col gap-2" data-testid="pick-short">
          <Field label={t("picking.scanner.shortReason")} required>
            <TextField className="h-12" value={reason} onChange={(e) => { setReason(e.target.value); }} data-testid="pick-short-reason" />
          </Field>
          <div className="flex flex-wrap gap-1">
            {quickReasons.map((key) => (
              <Button key={key} type="button" variant="secondary" size="sm" onClick={() => { setReason(t(key)); }} data-testid="pick-quick-reason">{t(key)}</Button>
            ))}
          </div>
        </div>
      ) : null}
      {step === "confirm" ? (
        <Button size="lg" className="h-14 text-base" onClick={confirm} loading={pick.isPending} data-testid="pick-confirm">
          {short ? t("picking.scanner.confirmShort", { picked: qty(picked), planned: qty(planned) }) : t("picking.scanner.confirm", { quantity: qty(planned) })}
        </Button>
      ) : null}
      <Button variant="ghost" onClick={() => { if (shorting) { nothingHere(); } else { setShorting(true); } }} data-testid="pick-nothing">
        {shorting ? t("picking.scanner.confirmNothing") : t("picking.scanner.nothingHere")}
      </Button>
    </section>
  );
}
