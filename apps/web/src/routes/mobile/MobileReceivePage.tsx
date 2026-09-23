import { Button, EmptyState, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { toFormProblem } from "../../lib/problem";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";

type Receivable = components["schemas"]["ReceivableLine"];

interface Entry {
  quantity: string;
  lotNumber: string;
  expiresOn: string;
}

/** Receiving at the dock (roadmap 4.3): the open lines of one purchase order, quantities entered by scanning or typing, posted into the operator's warehouse at once. */
export function MobileReceivePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const context = useScanContext();
  const online = useOnline();
  const companyId = context.companyId ?? "";
  const warehouseId = context.warehouseId ?? "";
  const ready = context.companyId !== null && context.warehouseId !== null;
  const receivable = useQuery({
    queryKey: ["mobile", "receivable", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/receivable", { params: { query: { companyId } } })),
    enabled: ready && online,
  });
  const [orderId, setOrderId] = useState("");
  const [deliveryNote, setDeliveryNote] = useState("");
  const [entries, setEntries] = useState<Record<string, Entry>>({});
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const orders = new Map<string, string>();
  for (const line of receivable.data ?? []) {
    orders.set(line.orderId, line.orderNumber);
  }
  const lines = (receivable.data ?? []).filter((l) => l.orderId === orderId);
  const entry = (line: Receivable): Entry => entries[line.orderLineId] ?? { quantity: "", lotNumber: "", expiresOn: "" };
  const patch = (line: Receivable, change: Partial<Entry>): void => { setEntries((prev) => ({ ...prev, [line.orderLineId]: { ...entry(line), ...change } })); };

  const onCode = (code: string): void => {
    setError(null);
    const line = lines.find((l) => l.itemCode.toUpperCase() === code.trim().toUpperCase());
    if (!line) {
      setError(t("mobile.receive.notOnOrder", { code }));
      return;
    }
    const current = parseQuantity(entry(line).quantity) ?? "0";
    patch(line, { quantity: String(Number(current) + 1) });
  };

  const receive = useMutation({
    mutationFn: async () => {
      const body = {
        orderId,
        warehouseId,
        supplierDeliveryNote: deliveryNote || null,
        lines: lines
          .map((l) => ({ line: l, e: entry(l) }))
          .filter(({ e }) => (parseQuantity(e.quantity) ?? "0") !== "0")
          .map(({ line, e }) => ({ orderLineId: line.orderLineId, quantity: Number(parseQuantity(e.quantity) ?? "0"), lotNumber: e.lotNumber || null, expiresOn: e.expiresOn || null })),
      };
      if (body.lines.length === 0) {
        throw new Error(t("mobile.receive.nothingEntered"));
      }
      const draft = unwrap(await api.POST("/api/v1/purchasing/receipts", { body }));
      return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/post", { params: { path: { receiptId: draft.id } } }));
    },
    onSuccess: async (posted) => {
      setError(null);
      setDone(posted.number);
      setEntries({});
      setDeliveryNote("");
      await queryClient.invalidateQueries({ queryKey: ["mobile", "receivable"] });
    },
    onError: (caught) => { setError(caught instanceof Error && !("status" in caught) ? caught.message : toFormProblem(caught, t("common.saveFailed")).message); },
  });

  if (!ready) {
    return <EmptyState title={t("mobile.count.noContext")} description={t("mobile.count.noContextDescription")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{t("mobile.receive.title")}</h1>
        <p className="mt-1 text-sm text-fg-muted">{t("mobile.receive.description")}</p>
      </div>
      {!online ? <p className="text-sm text-warning" data-testid="receive-offline">{t("mobile.receive.offline")}</p> : null}
      <Field label={t("nav.purchaseOrders")}>
        <SelectField className="h-12 text-base" value={orderId} onChange={(e) => { setOrderId(e.target.value); setEntries({}); setDone(null); }} data-testid="receive-order">
          <option value="">{t("mobile.receive.chooseOrder")}</option>
          {[...orders.entries()].map(([id, number]) => (
            <option key={id} value={id}>{number}</option>
          ))}
        </SelectField>
      </Field>
      {orderId ? (
        <>
          <ScanBox label={t("mobile.receive.scan")} hint={t("mobile.receive.scanHint")} error={null} disabled={false} onCode={onCode} />
          <Field label={t("purchasing.deliveryNote")}>
            <TextField className="h-12 text-base" value={deliveryNote} onChange={(e) => { setDeliveryNote(e.target.value); }} dir="ltr" data-testid="receive-note" />
          </Field>
          <ul className="flex flex-col gap-3" data-testid="receive-lines">
            {lines.map((line, index) => {
              const e = entry(line);
              const lotTracked = line.tracking === "lot" || line.tracking === "lot_and_serial";
              return (
                <li key={line.orderLineId} className="rounded-md border border-border p-3">
                  <div className="flex items-baseline justify-between gap-2">
                    <span className="font-medium" dir="auto">{line.itemCode} · {localized(line.itemName)}</span>
                    <span className="text-xs text-fg-muted tabular" dir="ltr">{t("mobile.receive.remaining", { remaining: String(line.remaining), uom: line.uomCode })}</span>
                  </div>
                  <div className="mt-2 grid grid-cols-2 gap-2">
                    <Field label={t("purchasing.receiveNow")}>
                      <TextField className="h-12 text-base" inputMode="decimal" value={e.quantity} onChange={(ev) => { patch(line, { quantity: ev.target.value }); }} dir="ltr" data-testid={`mobile-receive-qty-${String(index)}`} />
                    </Field>
                    {lotTracked ? (
                      <Field label={t("purchasing.lot")}>
                        <TextField className="h-12 text-base" value={e.lotNumber} onChange={(ev) => { patch(line, { lotNumber: ev.target.value }); }} dir="ltr" data-testid={`mobile-receive-lot-${String(index)}`} />
                      </Field>
                    ) : null}
                    {lotTracked ? (
                      <Field label={t("purchasing.expiresOn")}>
                        <TextField className="h-12 text-base" type="date" value={e.expiresOn} onChange={(ev) => { patch(line, { expiresOn: ev.target.value }); }} dir="ltr" />
                      </Field>
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ul>
          <FormError message={error} />
          {done ? <p className="text-sm text-success" data-testid="receive-done">{t("mobile.receive.done", { number: done })}</p> : null}
          <Button size="lg" className="h-14 text-base" onClick={() => { receive.mutate(); }} loading={receive.isPending} disabled={!online || lines.length === 0} data-testid="receive-post">
            {t("mobile.receive.post")}
          </Button>
        </>
      ) : null}
    </div>
  );
}
