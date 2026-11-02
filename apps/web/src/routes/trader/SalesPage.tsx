import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { emptyLine, LinesEditor, num, type LineForm } from "../purchasing/shared";
import { useBankAccounts } from "../payables/shared";

type Invoice = components["schemas"]["SalesInvoiceSummary"];

interface SaleForm {
  partnerId: string;
  warehouseId: string;
  date: string;
  reference: string;
  lines: LineForm[];
}

interface PaymentForm {
  invoice: Invoice;
  openItemId: string;
  amount: string;
  accountId: string;
  method: string;
  date: string;
}

const n = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/** What is still owed on an invoice: the remainders of its open items. */
function remainingOf(invoice: Invoice): number {
  return invoice.openItems.reduce((sum, item) => sum + n(item.remainingTc), 0);
}

function paymentState(invoice: Invoice, today: string): string {
  if (invoice.status !== "posted") {
    return invoice.status;
  }
  const remaining = remainingOf(invoice);
  if (remaining <= 0) {
    return "paid";
  }
  const due = invoice.openItems.map((i) => i.dueDate).sort()[0];
  if (due !== undefined && due < today) {
    return "overdue";
  }
  return remaining < n(invoice.totalGross) ? "partly" : "unpaid";
}

const tones: Record<string, "success" | "warning" | "danger" | "info" | "neutral"> = { paid: "success", partly: "info", unpaid: "warning", overdue: "danger", draft: "neutral", cancelled: "neutral", reversed: "neutral" };

/**
 * Sales (trader edition, ADR-0035 §6): a sale in one step (the order confirmed, the goods shipped from the store, the
 * invoice posted), the invoices with what each customer still owes, and payments recorded against them.
 */
export function SalesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [sale, setSale] = useState<SaleForm | null>(null);
  const [payment, setPayment] = useState<PaymentForm | null>(null);
  const warehouses = useWarehouses(companyId);
  const accounts = useBankAccounts(companyId);
  const company = companies.find((c) => c.id === companyId);
  const currency = company?.functionalCurrency ?? "";
  const today = new Date().toISOString().slice(0, 10);

  const customers = useQuery({
    queryKey: ["customers", companyId, "", "", "", false],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/customers", { params: { query: { companyId } } })),
  });
  const invoices = useQuery({
    queryKey: ["invoices", companyId, "trader"],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId } } })),
  });
  const board = useQuery({
    queryKey: ["trader-pricing", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/pricing/board", { params: { query: { companyId } } })),
  });
  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["invoices"] });
    await queryClient.invalidateQueries({ queryKey: ["ar-aging"] });
    await queryClient.invalidateQueries({ queryKey: ["stock"] });
    await queryClient.invalidateQueries({ queryKey: ["trader-pricing"] });
  };

  const sell = useMutation({
    mutationFn: async (f: SaleForm) =>
      unwrap(await api.POST("/api/v1/sales/quick-sales", {
        body: { companyId, partnerId: f.partnerId, warehouseId: f.warehouseId, date: f.date || null, customerReference: f.reference || null, lines: f.lines.filter((l) => l.itemCode.trim()).map((l) => ({ itemCode: l.itemCode, quantity: num(l.quantity), uom: l.uom || null, unitPrice: l.price.trim() ? num(l.price) : null })) },
      })),
    onSuccess: async (result) => { setProblem(null); setSale(null); setNotice(t("trader.sales.sold", { number: result.invoiceNumber, amount: formatMoney(result.totalGross, result.currency) })); await refresh(); },
    onError: fail,
  });
  const createCash = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency, isActive: true } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["bank-accounts"] }); },
    onError: fail,
  });
  const pay = useMutation({
    mutationFn: async (p: PaymentForm) => {
      const receipt = unwrap(await api.POST("/api/v1/banking/receipts", { body: { companyId, partnerId: p.invoice.partnerId, bankAccountId: p.accountId, kind: "customer_receipt", receiptDate: p.date || null, method: p.method, lines: [{ openItemId: p.openItemId, amount: num(p.amount) }], onAccount: 0, charges: 0 } }));
      return unwrap(await api.POST("/api/v1/banking/receipts/{receiptId}/post", { params: { path: { receiptId: receipt.id } } }));
    },
    onSuccess: async () => { setProblem(null); setPayment(null); setNotice(t("trader.sales.paymentRecorded")); await refresh(); await queryClient.invalidateQueries({ queryKey: ["receipts"] }); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Invoice, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("trader.sales.invoice"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "customer", accessorFn: (row) => `${row.partnerCode} ${localized(row.partnerName)}`, header: t("trader.sales.customer"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "documentDate", header: t("trader.sales.date"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.documentDate)}</span> },
      { id: "total", accessorKey: "totalGross", header: t("trader.sales.total"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
      { id: "remaining", accessorFn: (row) => remainingOf(row), header: t("trader.sales.remaining"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr" data-testid="invoice-remaining">{row.original.status === "posted" ? formatMoney(remainingOf(row.original), row.original.currency) : ""}</span> },
      { id: "state", accessorFn: (row) => paymentState(row, today), header: t("trader.sales.status"), size: 130, cell: ({ row }) => { const state = paymentState(row.original, today); return <Badge tone={tones[state] ?? "neutral"} data-testid="invoice-state">{t(`trader.sales.statuses.${state}`, { defaultValue: state })}</Badge>; } },
      {
        id: "actions", header: "", size: 160, enableSorting: false,
        cell: ({ row }) => {
          const open = row.original.openItems.find((i) => n(i.remainingTc) > 0);
          return row.original.status === "posted" && open ? <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setPayment({ invoice: row.original, openItemId: open.id, amount: String(n(open.remainingTc)), accountId: accounts.data?.[0]?.id ?? "", method: "cash", date: "" }); }} data-testid={`pay-${row.original.number}`}>{t("trader.sales.recordPayment")}</Button> : null;
        },
      },
    ],
    [t, today, accounts.data],
  );

  const openSale = (): void => { setProblem(null); setSale({ partnerId: "", warehouseId: warehouses.data?.[0]?.id ?? "", date: "", reference: "", lines: [emptyLine()] }); };
  // A line that just got its item takes the selling price from the pricing board when no price was typed.
  const onLines = (lines: LineForm[]): void => {
    if (!sale) {
      return;
    }
    const priced = lines.map((line, index) => {
      const before = sale.lines[index];
      if (line.price.trim() || line.itemCode === before?.itemCode) {
        return line;
      }
      const row = board.data?.rows.find((r) => r.itemCode.toLowerCase() === line.itemCode.trim().toLowerCase());
      return row?.currentPrice !== null && row?.currentPrice !== undefined ? { ...line, price: String(row.currentPrice) } : line;
    });
    setSale({ ...sale, lines: priced });
  };
  const submitSale = (event: FormEvent): void => { event.preventDefault(); if (sale) { sell.mutate(sale); } };

  return (
    <>
      <PageHeader
        title={t("trader.sales.title")}
        description={t("trader.sales.description")}
        actions={
          <Button onClick={openSale} disabled={!companyId} data-testid="new-sale">
            <Plus aria-hidden="true" />
            {t("trader.sales.newSale")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <FormError message={!sale && !payment ? problem?.message ?? null : null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="sales-notice">{notice}</p> : null}
      <DataGrid<Invoice> label="trader.sales.title" columns={columns} data={invoices.data ?? []} rowKey={(row) => row.id} loading={invoices.isPending && Boolean(companyId)} emptyTitle={t("trader.sales.empty")} emptyDescription={t("trader.sales.emptyDescription")} />

      <Dialog open={Boolean(sale)} onOpenChange={(isOpen) => { if (!isOpen) { setSale(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {sale ? (
            <form onSubmit={submitSale} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.sales.newSale")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("trader.sales.customer")} required>
                  <SelectField value={sale.partnerId} onChange={(e) => { setSale({ ...sale, partnerId: e.target.value }); }} required data-testid="sale-customer">
                    <option value="">{t("trader.sales.chooseCustomer")}</option>
                    {(customers.data ?? []).map((c) => <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>)}
                  </SelectField>
                </Field>
                <WarehouseSelect warehouses={warehouses.data ?? []} value={sale.warehouseId} onChange={(id) => { setSale({ ...sale, warehouseId: id }); }} label={t("trader.sales.warehouse")} testId="sale-warehouse" required />
                <Field label={t("trader.sales.date")}>
                  <TextField type="date" value={sale.date} onChange={(e) => { setSale({ ...sale, date: e.target.value }); }} dir="ltr" data-testid="sale-date" />
                </Field>
                <Field label={t("trader.sales.reference")}>
                  <TextField value={sale.reference} onChange={(e) => { setSale({ ...sale, reference: e.target.value }); }} dir="auto" data-testid="sale-reference" />
                </Field>
              </div>
              <p className="text-xs text-fg-muted"><Link to="/sales/customers" className="text-accent underline">{t("trader.sales.newCustomer")}</Link> · {t("trader.sales.priceHint")}</p>
              <LinesEditor lines={sale.lines} onChange={onLines} priceLabel={t("trader.pricing.currentPrice")} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setSale(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={sell.isPending} disabled={!sale.partnerId || !sale.warehouseId} data-testid="sell">{t("trader.sales.sell")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(payment)} onOpenChange={(isOpen) => { if (!isOpen) { setPayment(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-lg">
          {payment ? (
            <form onSubmit={(e) => { e.preventDefault(); pay.mutate(payment); }} className="flex flex-col gap-4" data-testid="payment-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.sales.paymentFor", { number: payment.invoice.number })}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              {(accounts.data ?? []).length === 0 ? (
                <div className="flex flex-wrap items-center gap-2 text-sm" data-testid="no-accounts">
                  <span>{t("trader.sales.noAccounts")}</span>
                  <Button type="button" variant="secondary" size="sm" onClick={() => { createCash.mutate(); }} loading={createCash.isPending} data-testid="create-cash">{t("trader.sales.createCash")}</Button>
                </div>
              ) : null}
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("trader.sales.amount")} required>
                  <DecimalField value={payment.amount} onChange={(e) => { setPayment({ ...payment, amount: e.target.value }); }} dir="ltr" required data-testid="payment-amount" />
                </Field>
                <Field label={t("trader.sales.paidInto")} required>
                  <SelectField value={payment.accountId} onChange={(e) => { setPayment({ ...payment, accountId: e.target.value }); }} required data-testid="payment-account">
                    {!payment.accountId ? <option value="" /> : null}
                    {(accounts.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("trader.sales.method")}>
                  <SelectField value={payment.method} onChange={(e) => { setPayment({ ...payment, method: e.target.value }); }} data-testid="payment-method">
                    <option value="cash">{t("trader.sales.methods.cash")}</option>
                    <option value="transfer">{t("trader.sales.methods.transfer")}</option>
                  </SelectField>
                </Field>
                <Field label={t("trader.sales.paymentDate")}>
                  <TextField type="date" value={payment.date} onChange={(e) => { setPayment({ ...payment, date: e.target.value }); }} dir="ltr" data-testid="payment-date" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setPayment(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={pay.isPending} disabled={!payment.accountId} data-testid="save-payment">{t("trader.sales.savePayment")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
