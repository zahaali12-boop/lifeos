import { Badge, Button, EmptyState, Field } from "@quicker/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { cachedItem, resolveItem, type ResolvedItem } from "./items";
import { enqueue, syncQueue, useQueue } from "./queue";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";

type Bin = components["schemas"]["BinSummary"];

interface ScannedItem {
  code: string;
  resolved: ResolvedItem | null;
}

interface Capture {
  id: string;
  label: string;
}

/**
 * Counting by scanning (roadmap 3.8): pick the frozen count, scan the bin (when the warehouse has bins), scan the
 * item, type the quantity. Each capture sets the counted quantity of that bin/item line and goes through the queue,
 * so it is the same online and offline.
 */
export function MobileCountPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const context = useScanContext();
  const online = useOnline();
  const queue = useQueue();
  const companyId = context.companyId ?? "";
  const warehouseId = context.warehouseId ?? "";
  const ready = context.companyId !== null && context.warehouseId !== null;

  const warehouses = useQuery({
    queryKey: ["mobile", "warehouses", context.companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
    enabled: ready,
  });
  const warehouse = warehouses.data?.find((w) => w.id === warehouseId);
  const needsBin = warehouse?.binsEnabled === true;
  const counts = useQuery({
    queryKey: ["mobile", "counts", companyId, warehouseId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: { companyId, warehouseId } } })),
    enabled: ready,
  });
  const open = (counts.data ?? []).filter((c) => c.status === "frozen" || c.status === "counting");
  const [chosenCount, setChosenCount] = useState<string | null>(null);
  const count = open.find((c) => c.id === chosenCount) ?? open[0];
  const countId = count?.id ?? null;
  const bins = useQuery({
    queryKey: ["mobile", "bins", warehouseId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId } } })),
    enabled: needsBin,
  });
  const sheet = useQuery({
    queryKey: ["mobile", "sheet", countId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{countId}/sheet", { params: { path: { countId: countId ?? "" } } })),
    enabled: countId !== null && online,
  });

  const [bin, setBin] = useState<Bin | null>(null);
  const [item, setItem] = useState<ScannedItem | null>(null);
  const [quantity, setQuantity] = useState("");
  const [lot, setLot] = useState("");
  const [serial, setSerial] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [captures, setCaptures] = useState<Capture[]>([]);

  const step: "bin" | "item" | "quantity" = needsBin && !bin ? "bin" : item ? "quantity" : "item";
  const tracking = item?.resolved?.tracking ?? "none";
  const wantsLot = tracking === "lot" || tracking === "lot_and_serial";
  const wantsSerial = tracking === "serial" || tracking === "lot_and_serial";

  const onCode = async (code: string): Promise<void> => {
    setError(null);
    setNotice(null);
    if (step === "bin") {
      const found = (bins.data ?? []).find((b) => b.code.localeCompare(code, undefined, { sensitivity: "accent" }) === 0);
      if (!found) {
        setError(t("mobile.count.binUnknown", { code }));
        return;
      }
      setBin(found);
      return;
    }
    let resolved = cachedItem(code);
    if (!resolved && online) {
      try {
        resolved = await resolveItem(code);
      } catch (caught) {
        if (!(caught instanceof TypeError)) {
          throw caught;
        }
      }
      if (!resolved && navigator.onLine) {
        setError(t("mobile.count.itemUnknown", { code }));
        return;
      }
    }
    if (!resolved) {
      setNotice(t("mobile.count.itemOffline", { code }));
    }
    setItem({ code, resolved });
    const serialOnly = resolved?.tracking === "serial" || resolved?.tracking === "lot_and_serial";
    setQuantity(serialOnly ? "1" : "");
    setLot("");
    setSerial("");
  };

  const add = (): void => {
    if (!item || !count) {
      return;
    }
    const parsed = parseQuantity(quantity);
    if (parsed === null) {
      setError(t("mobile.count.quantityInvalid"));
      return;
    }
    const itemCode = item.resolved?.itemCode ?? item.code;
    const label = [bin?.code, itemCode, lot || null, serial || null, `× ${parsed}`].filter((part) => part !== null && part !== undefined && part !== "").join(" · ");
    const queued = enqueue({
      kind: "count",
      countId: count.id,
      countNumber: count.number,
      label,
      entry: {
        itemId: item.resolved?.itemId ?? null,
        itemCode,
        binId: bin?.id ?? null,
        lotNumber: lot || null,
        serialNumber: serial || null,
        variantId: item.resolved?.variantId ?? null,
        countedQty: parsed,
      },
    });
    setCaptures((previous) => [{ id: queued.id, label }, ...previous]);
    setItem(null);
    setQuantity("");
    setLot("");
    setSerial("");
    setError(null);
    setNotice(null);
    if (online) {
      void syncQueue().then(() => queryClient.invalidateQueries({ queryKey: ["mobile", "sheet"] }));
    }
  };

  const statusOf = (id: string): { key: string; tone: "success" | "warning" | "danger" } => {
    const pending = queue.find((q) => q.id === id);
    if (!pending) {
      return { key: "mobile.count.synced", tone: "success" };
    }
    return pending.error ? { key: "mobile.count.failed", tone: "danger" } : { key: "mobile.count.queued", tone: "warning" };
  };

  if (!ready) {
    return <EmptyState title={t("mobile.count.title")} description={t("mobile.count.noContext")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("mobile.count.title")}</h1>
      {counts.isSuccess && open.length === 0 ? (
        <EmptyState title={t("mobile.count.pick")} description={t("mobile.count.none")} />
      ) : (
        <Field label={t("mobile.count.pick")}>
          <SelectField className="h-12 text-base" data-testid="scan-count" value={countId ?? ""} onChange={(event) => { setChosenCount(event.target.value || null); setBin(null); setItem(null); }}>
            {countId === null ? <option value="">{t("mobile.count.chooseCount")}</option> : null}
            {open.map((c) => (
              <option key={c.id} value={c.id}>
                {c.number} · {c.scope}
              </option>
            ))}
          </SelectField>
        </Field>
      )}
      {count ? (
        <>
          {sheet.data ? <p className="text-sm text-fg-muted">{t("mobile.count.lines", { count: Number(sheet.data.count.countedLines) })}</p> : null}
          {needsBin && bin ? (
            <div className="flex items-center justify-between rounded-md border border-border bg-surface px-3 py-2" data-testid="current-bin">
              <span>
                <span className="text-xs text-fg-muted">{t("mobile.count.bin")}</span>
                <span className="ms-2 font-medium">{bin.code}</span>
              </span>
              <Button variant="ghost" size="sm" data-testid="change-bin" onClick={() => { setBin(null); setItem(null); }}>
                {t("mobile.count.changeBin")}
              </Button>
            </div>
          ) : null}
          <ScanBox label={step === "bin" ? t("mobile.count.scanBin") : t("mobile.count.scanItem")} error={error} onCode={onCode} disabled={step === "bin" && !bins.isSuccess} />
          {notice ? <p className="text-sm text-warning">{notice}</p> : null}
          {item ? (
            <div className="flex flex-col gap-3 rounded-md border border-border bg-surface p-3">
              <div data-testid="current-item">
                <div className="font-medium">{item.resolved?.itemCode ?? item.code}</div>
                <div className="text-sm text-fg-muted">
                  {localized(item.resolved?.name)}
                  {item.resolved ? ` · ${item.resolved.uomCode ?? item.resolved.baseUom}` : null}
                </div>
              </div>
              <Field label={t("mobile.count.quantity")}>
                <TextField className="h-12 text-lg" inputMode="decimal" autoComplete="off" data-testid="count-quantity" value={quantity} onChange={(event) => { setQuantity(event.target.value); }} autoFocus />
              </Field>
              {wantsLot ? (
                <Field label={t("mobile.count.lot")}>
                  <TextField className="h-12" autoComplete="off" data-testid="count-lot" value={lot} onChange={(event) => { setLot(event.target.value); }} />
                </Field>
              ) : null}
              {wantsSerial ? (
                <Field label={t("mobile.count.serial")}>
                  <TextField className="h-12" autoComplete="off" data-testid="count-serial" value={serial} onChange={(event) => { setSerial(event.target.value); }} />
                </Field>
              ) : null}
              <Button size="lg" className="h-12" data-testid="count-add" onClick={add}>
                {t("mobile.count.add")}
              </Button>
            </div>
          ) : null}
          <FormError message={counts.isError || bins.isError ? t("common.saveFailed") : null} />
          {captures.length > 0 ? (
            <section aria-labelledby="captures-heading">
              <h2 id="captures-heading" className="mb-2 text-sm font-semibold text-fg-muted">
                {t("mobile.count.captured")}
              </h2>
              <ul className="flex flex-col gap-1">
                {captures.map((capture) => {
                  const status = statusOf(capture.id);
                  return (
                    <li key={capture.id} className="flex items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-sm" data-testid="capture">
                      <span className="truncate">{capture.label}</span>
                      <Badge tone={status.tone}>{t(status.key)}</Badge>
                    </li>
                  );
                })}
              </ul>
            </section>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
