import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { num, PurchaseStatus } from "./shared";

type Receipt = components["schemas"]["ReceiptSummary"];
type Receivable = components["schemas"]["ReceivableLine"];

interface ReceiptLineForm {
  orderLineId: string;
  quantity: string;
  lotNumber: string;
  expiresOn: string;
  serialNumbers: string;
}

interface ReceiptForm {
  id: string | null;
  orderId: string;
  warehouseId: string;
  postingDate: string;
  supplierDeliveryNote: string;
  lines: ReceiptLineForm[];
}

/** Goods receipts (roadmap 4.3): open order lines received within the supplier's tolerance, posted into stock at the expected cost against GRNI, reversed as a whole. */
export function ReceiptsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ReceiptForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/receipts");
  const [reversal, setReversal] = useState<string | null>(null);
  const warehouses = useWarehouses(companyId);

  const list = useQuery({
    queryKey: ["receipts", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const receivable = useQuery({
    queryKey: ["receivable", companyId],
    enabled: Boolean(companyId) && Boolean(form),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/receivable", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["receipt", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/{receiptId}", { params: { path: { receiptId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["receipts"], ["receipt"], ["receivable"], ["orders"], ["order"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: ReceiptForm) => {
      const body = {
        orderId: f.orderId,
        warehouseId: f.warehouseId || null,
        postingDate: f.postingDate || null,
        supplierDeliveryNote: f.supplierDeliveryNote || null,
        lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ orderLineId: l.orderLineId, quantity: num(l.quantity), lotNumber: l.lotNumber || null, expiresOn: l.expiresOn || null, serialNumbers: l.serialNumbers.split(/[\s,]+/).filter(Boolean) })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/purchasing/receipts/{receiptId}", { params: { path: { receiptId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/receipts", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { receiptId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/purchasing/receipts/{receiptId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Receipt, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "order", accessorKey: "orderNumber", header: t("nav.purchaseOrders"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.orderNumber}</span> },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
      { id: "note", accessorKey: "supplierDeliveryNote", header: t("purchasing.deliveryNote"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.supplierDeliveryNote ?? ""}</span> },
      { id: "value", accessorKey: "totalExpectedCost", header: t("purchasing.expectedValue"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalExpectedCost, row.original.functionalCurrency)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ id: null, orderId: "", warehouseId: "", postingDate: today(), supplierDeliveryNote: "", lines: [] }); };
  const chooseOrder = (orderId: string): void => {
    if (!form) {
      return;
    }
    const lines = (receivable.data ?? []).filter((l) => l.orderId === orderId);
    const only = (warehouses.data ?? []).length === 1 ? warehouses.data?.[0]?.id : undefined;
    setForm({ ...form, orderId, warehouseId: lines[0]?.warehouseId ?? (form.warehouseId || only) ?? "", lines: lines.map((l) => ({ orderLineId: l.orderLineId, quantity: String(l.remaining), lotNumber: "", expiresOn: "", serialNumbers: "" })) });
  };
  const patchLine = (index: number, change: Partial<ReceiptLineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const orders = useMemo(() => {
    const seen = new Map<string, string>();
    for (const l of receivable.data ?? []) {
      seen.set(l.orderId, l.orderNumber);
    }
    return [...seen.entries()];
  }, [receivable.data]);
  const lineInfo = (orderLineId: string): Receivable | undefined => receivable.data?.find((l) => l.orderLineId === orderLineId);
  const r = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.receipts")}
        description={t("purchasing.receiptsDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-receipt">
            <Plus aria-hidden="true" />
            {t("purchasing.newReceipt")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "posted", "reversed"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Receipt> label="nav.receipts" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyReceipts")} emptyDescription={t("purchasing.emptyReceiptsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("purchasing.editReceipt") : t("purchasing.newReceipt")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("nav.purchaseOrders")} required>
                  <SelectField value={form.orderId} onChange={(e) => { chooseOrder(e.target.value); }} required disabled={Boolean(form.id)} data-testid="receipt-order">
                    <option value="">—</option>
                    {orders.map(([id, number]) => (
                      <option key={id} value={id}>{number}</option>
                    ))}
                  </SelectField>
                </Field>
                <WarehouseSelect warehouses={warehouses.data ?? []} value={form.warehouseId} onChange={(id) => { setForm({ ...form, warehouseId: id }); }} label={t("purchasing.receiveInto")} testId="receipt-warehouse" required />
                <Field label={t("purchasing.postingDate")}>
                  <TextField type="date" value={form.postingDate} onChange={(e) => { setForm({ ...form, postingDate: e.target.value }); }} dir="ltr" data-testid="receipt-date" />
                </Field>
                <Field label={t("purchasing.deliveryNote")}>
                  <TextField value={form.supplierDeliveryNote} onChange={(e) => { setForm({ ...form, supplierDeliveryNote: e.target.value }); }} dir="ltr" data-testid="receipt-delivery-note" />
                </Field>
              </div>
              {form.lines.length > 0 ? (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.item")}</TableHead>
                      <TableHead>{t("purchasing.ordered")}</TableHead>
                      <TableHead>{t("purchasing.receivedSoFar")}</TableHead>
                      <TableHead>{t("purchasing.maxReceivable")}</TableHead>
                      <TableHead>{t("purchasing.receiveNow")}</TableHead>
                      <TableHead>{t("purchasing.lot")}</TableHead>
                      <TableHead>{t("purchasing.expiresOn")}</TableHead>
                      <TableHead>{t("purchasing.serials")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => {
                      const info = lineInfo(line.orderLineId);
                      const lotTracked = info?.tracking === "lot" || info?.tracking === "lot_and_serial";
                      const serialTracked = info?.tracking === "serial" || info?.tracking === "lot_and_serial";
                      return (
                        <TableRow key={line.orderLineId} data-testid="receipt-line">
                          <TableCell dir="auto">{info ? `${info.itemCode} · ${localized(info.itemName)}` : ""}</TableCell>
                          <TableCell className="tabular" dir="ltr">{info ? `${formatNumber(info.ordered, { maximumFractionDigits: 3 })} ${info.uomCode}` : ""}</TableCell>
                          <TableCell className="tabular" dir="ltr">{info ? formatNumber(info.received, { maximumFractionDigits: 3 }) : ""}</TableCell>
                          <TableCell className="tabular" dir="ltr">{info ? formatNumber(info.maxReceivable, { maximumFractionDigits: 3 }) : ""}</TableCell>
                          <TableCell><TextField aria-label={t("purchasing.receiveNow")} inputMode="decimal" value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-24" data-testid={`receive-qty-${String(index)}`} /></TableCell>
                          <TableCell>{lotTracked ? <TextField aria-label={t("purchasing.lot")} value={line.lotNumber} onChange={(e) => { patchLine(index, { lotNumber: e.target.value }); }} dir="ltr" className="w-28" data-testid={`receive-lot-${String(index)}`} /> : null}</TableCell>
                          <TableCell>{lotTracked ? <TextField aria-label={t("purchasing.expiresOn")} type="date" value={line.expiresOn} onChange={(e) => { patchLine(index, { expiresOn: e.target.value }); }} dir="ltr" data-testid={`receive-expiry-${String(index)}`} /> : null}</TableCell>
                          <TableCell>{serialTracked ? <TextField aria-label={t("purchasing.serials")} value={line.serialNumbers} onChange={(e) => { patchLine(index, { serialNumbers: e.target.value }); }} dir="ltr" className="w-40" placeholder={t("purchasing.serialsHelp")} data-testid={`receive-serials-${String(index)}`} /> : null}</TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              ) : (
                <p className="text-xs text-fg-muted">{form.orderId ? t("purchasing.nothingReceivable") : t("purchasing.chooseOrder")}</p>
              )}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.orderId} data-testid="save-receipt">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="receipt-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <PurchaseStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("nav.purchaseOrders"), r.orderNumber],
                [t("partners.supplier"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                [t("purchasing.receiveInto"), r.warehouseCode ?? "—"],
                [t("purchasing.postingDate"), formatDate(r.postingDate)],
                [t("purchasing.deliveryNote"), r.supplierDeliveryNote ?? "—"],
                [t("purchasing.expectedValue"), <span key="value" data-testid="receipt-value">{formatMoney(r.totalExpectedCost, r.functionalCurrency)}{r.exchangeRate !== 1 ? ` (${r.currency} @ ${formatNumber(r.exchangeRate, { maximumFractionDigits: 6 })})` : ""}</span>],
                ...(r.reversalReason ? [[t("purchasing.reversalReason"), r.reversalReason] as [string, string]] : []),
              ]} />
              <Table data-testid="receipt-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("purchasing.item")}</TableHead>
                    <TableHead>{t("purchasing.quantity")}</TableHead>
                    <TableHead>{t("purchasing.lot")}</TableHead>
                    <TableHead>{t("purchasing.expectedUnitCost")}</TableHead>
                    <TableHead>{t("purchasing.expectedValue")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {r.lines.map((l) => (
                    <TableRow key={l.id} data-testid="receipt-line-row">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">{l.itemCode} · {localized(l.itemName)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                      <TableCell dir="ltr">{l.lotNumber ?? ""}{l.serialNumbers.length > 0 ? ` ${l.serialNumbers.join(", ")}` : ""}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.expectedUnitCost, { maximumFractionDigits: 4 })}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(l.expectedCostAmount, r.functionalCurrency)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {reversal !== null ? (
                <Field label={t("purchasing.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {r.status === "draft" ? <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: r.id, orderId: r.orderId, warehouseId: r.warehouseId, postingDate: r.postingDate, supplierDeliveryNote: r.supplierDeliveryNote ?? "", lines: r.lines.map((l) => ({ orderLineId: l.orderLineId, quantity: String(l.quantity), lotNumber: l.lotNumber ?? "", expiresOn: l.expiresOn ?? "", serialNumbers: l.serialNumbers.join(" ") })) }); }} data-testid="edit-receipt">{t("common.edit")}</Button> : null}
                {r.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-receipt">{t("purchasing.deleteDraft")}</Button> : null}
                {r.status === "draft" ? <Button onClick={() => { act.mutate({ id: r.id, action: "post" }); }} loading={act.isPending} data-testid="post-receipt">{t("purchasing.postReceipt")}</Button> : null}
                {r.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-receipt">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: r.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
