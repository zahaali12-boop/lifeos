import { Button, EmptyState, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { cachedItem, resolveItem, type ResolvedItem } from "./items";
import { enqueue, syncQueue } from "./queue";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";

interface Line {
  key: string;
  code: string;
  resolved: ResolvedItem | null;
  quantity: string;
}

/** A one-step transfer built by scanning (roadmap 3.8): lines from the operator's warehouse to another, shipped on sync. */
export function MobileTransferPage() {
  const { t } = useTranslation();
  const context = useScanContext();
  const online = useOnline();
  const companyId = context.companyId ?? "";
  const fromId = context.warehouseId ?? "";
  const ready = context.companyId !== null && context.warehouseId !== null;
  const warehouses = useQuery({
    queryKey: ["mobile", "warehouses", context.companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
    enabled: ready,
  });
  const [toId, setToId] = useState("");
  const [item, setItem] = useState<{ code: string; resolved: ResolvedItem | null } | null>(null);
  const [quantity, setQuantity] = useState("");
  const [lines, setLines] = useState<Line[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const onCode = async (code: string): Promise<void> => {
    setError(null);
    setNotice(null);
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
    setQuantity("");
  };

  const addLine = (): void => {
    if (!item) {
      return;
    }
    const parsed = parseQuantity(quantity);
    if (parsed === null || parsed === "0") {
      setError(t("mobile.count.quantityInvalid"));
      return;
    }
    setLines((previous) => [...previous, { key: crypto.randomUUID(), code: item.code, resolved: item.resolved, quantity: parsed }]);
    setItem(null);
    setQuantity("");
    setError(null);
  };

  const ship = (): void => {
    const to = warehouses.data?.find((w) => w.id === toId);
    if (!to || lines.length === 0) {
      return;
    }
    if (to.id === fromId) {
      setError(t("mobile.transfer.sameWarehouse"));
      return;
    }
    enqueue({
      kind: "transfer",
      label: to.code,
      shipDate: null,
      request: {
        companyId,
        fromWarehouseId: fromId,
        toWarehouseId: to.id,
        kind: "one_step",
        lines: lines.map((line) => ({
          itemId: line.resolved?.itemId ?? null,
          itemCode: line.resolved?.itemCode ?? line.code,
          quantity: line.quantity,
          uom: line.resolved?.uomCode ?? null,
          variantId: line.resolved?.variantId ?? null,
        })),
      },
    });
    setLines([]);
    setNotice(t("mobile.transfer.queued"));
    if (online) {
      void syncQueue();
    }
  };

  if (!ready) {
    return <EmptyState title={t("mobile.transfer.title")} description={t("mobile.count.noContext")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("mobile.transfer.title")}</h1>
      <Field label={t("mobile.transfer.to")}>
        <SelectField className="h-12 text-base" data-testid="transfer-to" value={toId} onChange={(event) => { setToId(event.target.value); }}>
          <option value="">{t("mobile.transfer.chooseTo")}</option>
          {(warehouses.data ?? [])
            .filter((w) => w.id !== fromId)
            .map((w) => (
              <option key={w.id} value={w.id}>
                {w.code} · {localized(w.name)}
              </option>
            ))}
        </SelectField>
      </Field>
      <ScanBox label={t("mobile.transfer.scanItem")} error={error} onCode={onCode} disabled={toId === ""} />
      {notice ? <p className="text-sm text-fg-muted" data-testid="transfer-notice">{notice}</p> : null}
      {item ? (
        <div className="flex flex-col gap-3 rounded-md border border-border bg-surface p-3">
          <div data-testid="current-item">
            <div className="font-medium">{item.resolved?.itemCode ?? item.code}</div>
            <div className="text-sm text-fg-muted">{localized(item.resolved?.name)}</div>
          </div>
          <Field label={t("mobile.transfer.quantity")}>
            <TextField className="h-12 text-lg" inputMode="decimal" autoComplete="off" data-testid="transfer-quantity" value={quantity} onChange={(event) => { setQuantity(event.target.value); }} autoFocus />
          </Field>
          <Button size="lg" className="h-12" data-testid="transfer-add-line" onClick={addLine}>
            {t("mobile.transfer.addLine")}
          </Button>
        </div>
      ) : null}
      <section aria-labelledby="transfer-lines-heading">
        <h2 id="transfer-lines-heading" className="mb-2 text-sm font-semibold text-fg-muted">
          {t("mobile.transfer.lines")}
        </h2>
        {lines.length === 0 ? (
          <p className="text-sm text-fg-subtle">{t("mobile.transfer.noLines")}</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {lines.map((line) => (
              <li key={line.key} className="flex items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-sm" data-testid="transfer-line">
                <span className="truncate">
                  {line.resolved?.itemCode ?? line.code} × {line.quantity}
                </span>
                <Button variant="ghost" size="icon" aria-label={t("mobile.transfer.remove")} onClick={() => { setLines((previous) => previous.filter((l) => l.key !== line.key)); }}>
                  <Trash2 aria-hidden="true" />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <FormError message={warehouses.isError ? t("common.saveFailed") : null} />
      <Button size="lg" className="h-12" data-testid="transfer-ship" disabled={lines.length === 0 || toId === ""} onClick={ship}>
        {t("mobile.transfer.ship", { count: lines.length })}
      </Button>
    </div>
  );
}
