import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { Plus, Printer } from "lucide-react";
import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { emptyLine, LinesEditor, num, type LineForm } from "../purchasing/shared";
import { useBankAccounts } from "../payables/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";
import { QuickCreateButton } from "../QuickCreate";
import { n, paymentState, printVariants, remainingOf, stateTones as tones } from "./invoices";
import { rangeParams } from "./period";
import { PeriodFilter, usePeriod } from "./PeriodFilter";

type Invoice = components["schemas"]["SalesInvoiceSummary"];
type Settlement = components["schemas"]["ReceivableSettlementInfo"];

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

/** What the program offers next after a sale (the payment, the same customer again) or a payment (what is owed, another sale). */
type NextStep = { kind: "sold"; number: string; partnerId: string; invoiceId: string } | { kind: "paid"; receiptId: string; receiptNumber: string; partnerId: string };

/**
 * Sales (trader edition, ADR-0035 §6, §16, §22): a sale in one step (the order confirmed, the goods shipped from the
 * store, the invoice posted), the invoices with what each customer still owes, payments recorded against them (in
 * full or in part) and listed under each invoice, and the next step offered after each: a customer made elsewhere
 * arrives here with the sale open and the customer chosen.
 */
export function SalesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search: { customer?: string } = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [sale, setSale] = useState<SaleForm | null>(null);
  const [payment, setPayment] = useState<PaymentForm | null>(null);
  const [next, setNext] = useState<NextStep | null>(null);
  const [printing, setPrinting] = useState<PrintSubject | null>(null);
  const [openedId, setOpenedId] = useState<string | null>(null);
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
  const [period, setPeriod, range] = usePeriod("month");
  const invoices = useQuery({
    queryKey: ["invoices", companyId, "trader", range.from ?? "", range.to ?? ""],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId, ...rangeParams(range) } } })),
  });
  const board = useQuery({
    queryKey: ["trader-pricing", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/pricing/board", { params: { query: { companyId } } })),
  });
  // The invoice opened from its row: its items and every payment recorded against it (the customer's settlements, those of its open items).
  const opened = (invoices.data ?? []).find((i) => i.id === openedId) ?? null;
  const settlements = useQuery({
    queryKey: ["ar-settlements", companyId, opened?.partnerId ?? ""],
    enabled: Boolean(companyId) && opened !== null,
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/settlements", { params: { query: { companyId, partnerId: opened?.partnerId ?? "" } } })),
  });
  const paymentsOf = (invoice: Invoice): Settlement[] => {
    const items = new Set(invoice.openItems.map((i) => i.id));
    return (settlements.data ?? []).filter((s) => items.has(s.settledItemId) && s.status !== "reversed").sort((a, b) => b.settlementDate.localeCompare(a.settlementDate));
  };
  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  // The refusals a trader meets most often, in the trader's own words from the facts the server gave.
  const wordProblem = (p: FormProblem | null): string | null => {
    if (p?.code === "quick_sale.not_enough_stock" && p.why) {
      return t("trader.sales.notEnoughStock", { item: typeof p.why.item === "string" ? p.why.item : "", available: formatNumber(n(p.why.available as number | string | null)), wanted: formatNumber(n(p.why.wanted as number | string | null)) });
    }
    return p?.message ?? null;
  };
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["invoices"] });
    await queryClient.invalidateQueries({ queryKey: ["ar-aging"] });
    await queryClient.invalidateQueries({ queryKey: ["ar-settlements"] });
    await queryClient.invalidateQueries({ queryKey: ["stock"] });
    await queryClient.invalidateQueries({ queryKey: ["stock-balances"] });
    await queryClient.invalidateQueries({ queryKey: ["trader-pricing"] });
  };

  const sell = useMutation({
    mutationFn: async (f: SaleForm) =>
      unwrap(await api.POST("/api/v1/sales/quick-sales", {
        body: { companyId, partnerId: f.partnerId, warehouseId: f.warehouseId, date: f.date || null, customerReference: f.reference || null, lines: f.lines.filter((l) => l.itemCode.trim()).map((l) => ({ itemCode: l.itemCode, quantity: num(l.quantity), uom: l.uom || null, unitPrice: l.price.trim() ? num(l.price) : null })) },
      })),
    onSuccess: async (result, f) => {
      setProblem(null);
      setSale(null);
      setNotice(t("trader.sales.sold", { number: result.invoiceNumber, amount: formatMoney(result.totalGross, result.currency) }));
      setNext({ kind: "sold", number: result.invoiceNumber, partnerId: f.partnerId, invoiceId: result.invoiceId });
      await refresh();
    },
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
    onSuccess: async (receipt, p) => { setProblem(null); setPayment(null); setNotice(t("trader.sales.paymentRecorded")); setNext({ kind: "paid", receiptId: receipt.id, receiptNumber: receipt.number, partnerId: p.invoice.partnerId }); await refresh(); await queryClient.invalidateQueries({ queryKey: ["receipts"] }); },
    onError: fail,
  });
  const invoiceByNumber = (number: string): Invoice | undefined => invoices.data?.find((i) => i.number === number);
  const openPaymentFor = (invoice: Invoice): void => {
    const open = invoice.openItems.find((i) => n(i.remainingTc) > 0);
    if (open) {
      setProblem(null);
      setPayment({ invoice, openItemId: open.id, amount: String(n(open.remainingTc)), accountId: accounts.data?.[0]?.id ?? "", method: "cash", date: "" });
    }
  };

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
          const print = row.original.status === "posted" ? (
            <Button type="button" variant="ghost" size="icon" aria-label={t("trader.sales.printInvoice", { number: row.original.number })} title={t("trader.sales.printInvoice", { number: row.original.number })} onClick={() => { setPrinting({ documentType: "sales_invoice", documentId: row.original.id, number: row.original.number, partnerId: row.original.partnerId, variants: printVariants }); }} data-testid="print-invoice">
              <Printer aria-hidden="true" />
            </Button>
          ) : null;
          return <div className="flex items-center gap-1">{print}{row.original.status === "posted" && open ? <Button type="button" variant="ghost" size="sm" onClick={(e) => { e.stopPropagation(); setProblem(null); setPayment({ invoice: row.original, openItemId: open.id, amount: String(n(open.remainingTc)), accountId: accounts.data?.[0]?.id ?? "", method: "cash", date: "" }); }} data-testid={`pay-${row.original.number}`}>{t("trader.sales.recordPayment")}</Button> : null}</div>;
        },
      },
    ],
    [t, today, accounts.data],
  );


  const openSaleFor = useCallback((partnerId: string): void => { setProblem(null); setNext(null); setSale({ partnerId, warehouseId: warehouses.data?.[0]?.id ?? "", date: "", reference: "", lines: [emptyLine()] }); }, [warehouses.data]);
  const openSale = (): void => { openSaleFor(""); };
  useNewFromUrl("/trader/sales", openSale, Boolean(companyId));
  // A customer just made (on Customers, from the + menu) comes with the sale as the next step: the form opens with them chosen.
  const wantedCustomer = search.customer;
  useEffect(() => {
    if (wantedCustomer && companyId && warehouses.data) {
      openSaleFor(wantedCustomer);
      setNotice(t("trader.sales.customerReady"));
      void navigate({ to: "/trader/sales", search: {}, replace: true });
    }
  }, [wantedCustomer, companyId, warehouses.data, openSaleFor, navigate, t]);
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
        <PeriodFilter value={period} onChange={setPeriod} />
      </div>
      <FormError message={!sale && !payment ? wordProblem(problem) : null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="sales-notice">{notice}</p> : null}
      {next ? (
        <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-border p-2 text-sm" data-testid="next-step">
          <span className="font-medium">{t("trader.next.title")}</span>
          {next.kind === "sold" ? (
            <>
              <Button type="button" size="sm" disabled={!invoiceByNumber(next.number)} onClick={() => { const invoice = invoiceByNumber(next.number); if (invoice) { openPaymentFor(invoice); } }} data-testid="next-record-payment">{t("trader.next.recordPayment", { number: next.number })}</Button>
              <Button type="button" size="sm" variant="secondary" onClick={() => { setPrinting({ documentType: "sales_invoice", documentId: next.invoiceId, number: next.number, partnerId: next.partnerId, variants: printVariants }); }} data-testid="next-invoice-pdf">{t("trader.next.invoicePdf")}</Button>
              <Button type="button" size="sm" variant="secondary" onClick={() => { openSaleFor(next.partnerId); }} data-testid="next-sell-again">{t("trader.next.sellAgain")}</Button>
            </>
          ) : (
            <>
              <Button type="button" size="sm" onClick={() => { setPrinting({ documentType: "bank_receipt", documentId: next.receiptId, number: next.receiptNumber, partnerId: next.partnerId, variants: printVariants }); }} data-testid="next-receipt-pdf">{t("trader.next.receiptPdf", { number: next.receiptNumber })}</Button>
              <Button asChild size="sm" variant="secondary"><Link to="/trader/money" data-testid="next-money">{t("trader.next.seeMoney")}</Link></Button>
              <Button type="button" size="sm" variant="secondary" onClick={openSale} data-testid="next-new-sale">{t("trader.sales.newSale")}</Button>
            </>
          )}
        </div>
      ) : null}
      {printing ? <PrintDialog subject={printing} onClose={() => { setPrinting(null); }} /> : null}
      <DataGrid<Invoice> label="trader.sales.title" columns={columns} data={invoices.data ?? []} rowKey={(row) => row.id} loading={invoices.isPending && Boolean(companyId)} emptyTitle={t("trader.sales.empty")} emptyDescription={t("trader.sales.emptyDescription")} onOpen={(row) => { setProblem(null); setOpenedId(row.id); }} />

      <Dialog open={opened !== null} onOpenChange={(isOpen) => { if (!isOpen) { setOpenedId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {opened ? (
            <div className="flex flex-col gap-4" data-testid="invoice-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold"><span dir="ltr">{t("trader.sales.invoiceDetails", { number: opened.number })}</span></DialogTitle>
              </DialogHeader>
              <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
                <dt className="text-fg-muted">{t("trader.sales.customer")}</dt><dd dir="auto">{opened.partnerCode} · {localized(opened.partnerName)}</dd>
                <dt className="text-fg-muted">{t("trader.sales.date")}</dt><dd dir="ltr">{formatDate(opened.documentDate)}</dd>
                <dt className="text-fg-muted">{t("trader.sales.status")}</dt><dd><Badge tone={tones[paymentState(opened, today)] ?? "neutral"}>{t(`trader.sales.statuses.${paymentState(opened, today)}`, { defaultValue: opened.status })}</Badge></dd>
              </dl>
              <div>
                <h3 className="mb-1 text-sm font-semibold">{t("trader.sales.lines")}</h3>
                <table className="w-full text-sm">
                  <thead><tr className="text-fg-muted"><th className="text-start font-medium">{t("trader.sales.item")}</th><th className="text-end font-medium">{t("trader.sales.quantity")}</th><th className="text-end font-medium">{t("trader.sales.unitPrice")}</th><th className="text-end font-medium">{t("trader.sales.net")}</th></tr></thead>
                  <tbody>
                    {opened.lines.map((line) => (
                      <tr key={line.id} className="border-t border-border">
                        <td dir="auto">{line.itemCode ? `${line.itemCode} · ${localized(line.itemName ?? {})}` : (line.description ?? "")}</td>
                        <td className="tabular text-end" dir="ltr">{formatNumber(n(line.quantity))} {line.uomCode ?? ""}</td>
                        <td className="tabular text-end" dir="ltr">{formatMoney(line.unitPrice, opened.currency)}</td>
                        <td className="tabular text-end" dir="ltr">{formatMoney(line.netAmount, opened.currency)}</td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot><tr className="border-t border-border font-semibold"><td colSpan={3}>{t("trader.sales.total")}</td><td className="tabular text-end" dir="ltr">{formatMoney(opened.totalGross, opened.currency)}</td></tr></tfoot>
                </table>
              </div>
              <div>
                <h3 className="mb-1 text-sm font-semibold">{t("trader.sales.payments")}</h3>
                {paymentsOf(opened).length === 0 ? <p className="text-sm text-fg-muted" data-testid="no-payments">{t("trader.sales.noPayments")}</p> : (
                  <ul className="flex flex-col gap-1 text-sm" data-testid="payments-list">
                    {paymentsOf(opened).map((s) => (
                      <li key={s.id} className="flex items-center justify-between gap-2 rounded-md border border-border px-2 py-1" data-testid="payment-row">
                        <span dir="ltr">{formatDate(s.settlementDate)} · {s.settlingDocumentNumber}</span>
                        <span className="tabular" dir="ltr">{formatMoney(s.amountTc, s.currency)}</span>
                      </li>
                    ))}
                  </ul>
                )}
                <p className="mt-2 text-sm font-medium" data-testid="dialog-remaining">{t("trader.sales.remaining")}: <span className="tabular" dir="ltr">{formatMoney(remainingOf(opened), opened.currency)}</span></p>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setOpenedId(null); }}>{t("common.close")}</Button>
                {opened.status === "posted" ? <Button type="button" variant="secondary" onClick={() => { setPrinting({ documentType: "sales_invoice", documentId: opened.id, number: opened.number, partnerId: opened.partnerId, variants: printVariants }); }} data-testid="dialog-print">{t("trader.next.invoicePdf")}</Button> : null}
                {opened.status === "posted" && remainingOf(opened) > 0 ? <Button type="button" onClick={() => { openPaymentFor(opened); }} data-testid="dialog-pay">{t("trader.sales.recordPayment")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(sale)} onOpenChange={(isOpen) => { if (!isOpen) { setSale(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {sale ? (
            <form onSubmit={submitSale} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.sales.newSale")}</DialogTitle>
              </DialogHeader>
              <FormError message={wordProblem(problem)} />
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
              <div className="flex flex-wrap items-center gap-2 text-xs text-fg-muted">
                <span>{t("trader.sales.newCustomerHint")}</span>
                <QuickCreateButton kind="customer" companyId={companyId} currency={currency} onCreated={(created) => { setSale((prev) => (prev ? { ...prev, partnerId: created.id } : prev)); }} />
                <span>· {t("trader.sales.priceHint")}</span>
              </div>
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
              <FormError message={wordProblem(problem)} />
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
