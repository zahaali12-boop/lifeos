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
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { emptyLine, LinesEditor, num, useChargeTypes, useSuppliers, type LineForm } from "../purchasing/shared";
import { useBankAccounts } from "../payables/shared";
import { QuickCreateButton } from "../QuickCreate";
import { n, paymentState, remainingOf, stateTones } from "./invoices";
import { PeriodFilter, usePeriod } from "./PeriodFilter";

type Invoice = components["schemas"]["InvoiceSummary"];
type Settlement = components["schemas"]["SettlementInfo"];

interface CostForm {
  chargeTypeId: string;
  amount: string;
  currency: string;
  partnerId: string;
}

interface GoodsForm {
  mode: "supplier" | "prior";
  partnerId: string;
  warehouseId: string;
  date: string;
  currency: string;
  invoiceNumber: string;
  reference: string;
  lines: LineForm[];
  costs: CostForm[];
  payNow: boolean;
  accountId: string;
  paidRc: string;
}

interface PaymentForm {
  invoice: Invoice;
  openItemId: string;
  amount: string;
  accountId: string;
  bankAmount: string;
  method: string;
  date: string;
}

/** What the program offers next: the prices after goods arrived, the payment of what is owed, what Money says after a payment. */
type NextStep = { kind: "received"; number: string; invoiceId: string; owed: boolean } | { kind: "prior" } | { kind: "paid" };

const OPENING_REASON = "OPENING";

/**
 * Supplier invoices (trader edition, ADR-0035 §22): what is owed to each supplier invoice by invoice, the payment of
 * each (in full or in part) recorded here and every payment listed under its invoice; and goods already in the store
 * entered without an offer or a shipment, bought from a supplier (a purchase in one step, owed or paid now) or there
 * before the program (opening stock at cost), then priced on the board.
 */
export function PurchasesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [goods, setGoods] = useState<GoodsForm | null>(null);
  const [payment, setPayment] = useState<PaymentForm | null>(null);
  const [next, setNext] = useState<NextStep | null>(null);
  const [openId, setOpenId] = useOpenRecord("/trader/purchases");
  const warehouses = useWarehouses(companyId);
  const suppliers = useSuppliers(companyId);
  const accounts = useBankAccounts(companyId);
  const chargeTypes = useChargeTypes();
  const company = companies.find((c) => c.id === companyId);
  const currency = company?.functionalCurrency ?? "";
  const today = new Date().toISOString().slice(0, 10);
  const [period, setPeriod, range] = usePeriod("month");

  const invoices = useQuery({
    queryKey: ["purchase-invoices", companyId, "trader"],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices", { params: { query: { companyId } } })),
  });
  const listed = useMemo(() => (invoices.data ?? []).filter((i) => i.kind !== "debit_note" && (!range.from || i.documentDate >= range.from) && (!range.to || i.documentDate <= range.to)), [invoices.data, range.from, range.to]);
  const opened = (invoices.data ?? []).find((i) => i.id === openId) ?? null;
  const settlements = useQuery({
    queryKey: ["ap-settlements", companyId, opened?.partnerId ?? ""],
    enabled: Boolean(companyId) && opened !== null,
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/settlements", { params: { query: { companyId, partnerId: opened?.partnerId ?? "" } } })),
  });
  const paymentsOf = (invoice: Invoice): Settlement[] => {
    const items = new Set(invoice.openItems.map((i) => i.id));
    return (settlements.data ?? []).filter((s) => items.has(s.settledItemId) && s.status !== "reversed").sort((a, b) => b.settlementDate.localeCompare(a.settlementDate));
  };

  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const refresh = async (): Promise<void> => {
    await Promise.all([["purchase-invoices"], ["ap-aging"], ["ap-settlements"], ["shipments"], ["shipment"], ["stock"], ["stock-balances"], ["trader-pricing"], ["trader-cash"], ["bank-accounts"], ["trader-health"], ["home"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };

  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive);
  const createCash = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency, isActive: true } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["bank-accounts"] }); },
    onError: fail,
  });

  // A payment of one invoice, in full or in part, from a box or account: the oldest open instalment first.
  const openPaymentFor = (invoice: Invoice): void => {
    const open = invoice.openItems.filter((i) => n(i.remainingTc) > 0).sort((a, b) => a.dueDate.localeCompare(b.dueDate))[0];
    if (open) {
      setProblem(null);
      const account = liveAccounts.find((a) => a.currency === invoice.currency) ?? liveAccounts[0];
      setPayment({ invoice, openItemId: open.id, amount: String(n(open.remainingTc)), accountId: account?.id ?? "", bankAmount: "", method: account?.kind === "bank" ? "transfer" : "cash", date: "" });
    }
  };
  const payAccount = payment ? liveAccounts.find((a) => a.id === payment.accountId) : undefined;
  const needsBankAmount = payment !== null && payAccount !== undefined && payAccount.currency !== payment.invoice.currency;
  const pay = useMutation({
    mutationFn: async (p: PaymentForm) => {
      const created = unwrap(await api.POST("/api/v1/banking/payments", {
        body: { companyId, partnerId: p.invoice.partnerId, bankAccountId: p.accountId, kind: "supplier_payment", paymentDate: p.date || null, method: p.method, currency: p.invoice.currency, lines: [{ openItemId: p.openItemId, amount: num(p.amount) }], onAccount: 0, charges: 0, bankAmount: needsBankAmount && p.bankAmount.trim() ? num(p.bankAmount) : null, applyWht: false },
      }));
      return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params: { path: { paymentId: created.id } } }));
    },
    onSuccess: async (posted) => {
      setProblem(null);
      setPayment(null);
      setNotice(t("trader.purchases.paymentRecorded", { number: posted.number, amount: formatMoney(posted.amountTc, posted.currency) }));
      setNext({ kind: "paid" });
      await refresh();
    },
    onError: fail,
  });

  // Goods already here: bought from a supplier (the order, the arrival, the invoice in one step; paid now when asked), or there before the program (opening stock at cost).
  const openGoods = (): void => {
    setProblem(null);
    setNext(null);
    setGoods({ mode: "supplier", partnerId: "", warehouseId: warehouses.data?.[0]?.id ?? "", date: "", currency, invoiceNumber: "", reference: "", lines: [emptyLine()], costs: [], payNow: false, accountId: liveAccounts[0]?.id ?? "", paidRc: "" });
  };
  useNewFromUrl("/trader/purchases", openGoods, Boolean(companyId));
  const goodsAccount = goods ? liveAccounts.find((a) => a.id === goods.accountId) : undefined;
  const goodsNeedsRc = goods !== null && goods.payNow && goods.currency !== currency && goodsAccount?.currency === currency;
  const goodsTotal = goods ? goods.lines.reduce((sum, l) => sum + num(l.quantity) * num(l.price), 0) : 0;
  const receive = useMutation({
    mutationFn: async (f: GoodsForm) => {
      const lines = f.lines.filter((l) => l.itemCode.trim());
      if (f.mode === "prior") {
        const reasons = unwrap(await api.GET("/api/v1/inventory/reason-codes", { params: { query: { appliesTo: "adjustment" } } }));
        if (!reasons.some((r) => r.code === OPENING_REASON)) {
          unwrap(await api.POST("/api/v1/inventory/reason-codes", { body: { code: OPENING_REASON, name: { en: "Opening stock", ar: "رصيد افتتاحي" }, appliesTo: "adjustment", requiresNote: false, isActive: true } }));
        }
        const adjustment = unwrap(await api.POST("/api/v1/inventory/adjustments", { body: { companyId, warehouseId: f.warehouseId, kind: "opening", postingDate: f.date || null, reference: f.reference || null, lines: lines.map((l) => ({ itemCode: l.itemCode.trim(), quantity: num(l.quantity), uom: l.uom || null, unitCost: num(l.price), reasonCode: OPENING_REASON })) } }));
        unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/submit", { params: { path: { adjustmentId: adjustment.id } } }));
        return { kind: "prior" as const, count: lines.length };
      }
      const bought = unwrap(await api.POST("/api/v1/purchasing/quick-purchases", {
        body: { companyId, partnerId: f.partnerId, warehouseId: f.warehouseId, currency: f.currency || null, date: f.date || null, supplierInvoiceNumber: f.invoiceNumber || null, reference: f.reference || null, lines: lines.map((l) => ({ itemCode: l.itemCode.trim(), quantity: num(l.quantity), unitPrice: num(l.price), uom: l.uom || null })), costs: f.costs.filter((c) => c.chargeTypeId && c.amount.trim()).map((c) => ({ chargeTypeId: c.chargeTypeId, amount: num(c.amount), currency: c.currency || null, partnerId: c.partnerId || null })) },
      }));
      if (f.payNow && f.accountId) {
        unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/pay", { params: { path: { shipmentId: bought.shipmentId } }, body: { bankAccountId: f.accountId, paidRc: goodsNeedsRc && f.paidRc.trim() ? num(f.paidRc) : null, paidOn: f.date || null } }));
      }
      return { kind: "supplier" as const, bought, paid: f.payNow && Boolean(f.accountId) };
    },
    onSuccess: async (result, f) => {
      setProblem(null);
      setGoods(null);
      if (result.kind === "prior") {
        setNotice(t("trader.purchases.goods.priorDone", { count: formatNumber(result.count) }));
        setNext({ kind: "prior" });
      } else {
        const supplier = suppliers.data?.find((s) => s.partnerId === f.partnerId);
        const amount = formatMoney(result.bought.totalGross, result.bought.currency);
        setNotice(result.paid ? t("trader.purchases.goods.receivedPaid", { number: result.bought.invoiceNumber, amount }) : t("trader.purchases.goods.received", { number: result.bought.invoiceNumber, amount, name: supplier ? localized(supplier.partnerName) : "" }));
        setNext({ kind: "received", number: result.bought.invoiceNumber, invoiceId: result.bought.invoiceId, owed: !result.paid });
      }
      await refresh();
    },
    onError: fail,
  });
  const submitGoods = (event: FormEvent): void => { event.preventDefault(); if (goods) { receive.mutate(goods); } };
  const patchCost = (index: number, change: Partial<CostForm>): void => { if (goods) { setGoods({ ...goods, costs: goods.costs.map((c, i) => (i === index ? { ...c, ...change } : c)) }); } };

  const columns = useMemo<ColumnDef<Invoice, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("trader.purchases.invoice"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}{row.original.supplierInvoiceNumber ? <span className="text-fg-muted"> · {row.original.supplierInvoiceNumber}</span> : null}</span> },
      { id: "supplier", accessorFn: (row) => `${row.partnerCode} ${localized(row.partnerName)}`, header: t("trader.purchases.supplier"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "documentDate", header: t("trader.purchases.date"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.documentDate)}</span> },
      { id: "due", accessorFn: (row) => row.openItems.map((i) => i.dueDate).sort()[0] ?? row.dueDate ?? "", header: t("trader.purchases.due"), size: 110, cell: ({ row }) => { const due = row.original.openItems.map((i) => i.dueDate).sort()[0] ?? row.original.dueDate; return <span dir="ltr">{due ? formatDate(due) : ""}</span>; } },
      { id: "total", accessorKey: "totalGross", header: t("trader.purchases.total"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
      { id: "remaining", accessorFn: (row) => remainingOf(row), header: t("trader.purchases.remaining"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr" data-testid="purchase-remaining">{row.original.status === "posted" ? formatMoney(remainingOf(row.original), row.original.currency) : ""}</span> },
      { id: "state", accessorFn: (row) => paymentState(row, today), header: t("trader.purchases.status"), size: 130, cell: ({ row }) => { const state = paymentState(row.original, today); return <Badge tone={stateTones[state] ?? "neutral"} data-testid="purchase-state">{t(`trader.purchases.statuses.${state}`, { defaultValue: state })}</Badge>; } },
      {
        id: "actions", header: "", size: 150, enableSorting: false,
        cell: ({ row }) => (row.original.status === "posted" && remainingOf(row.original) > 0 ? <Button type="button" variant="ghost" size="sm" onClick={(e) => { e.stopPropagation(); openPaymentFor(row.original); }} data-testid={`pay-${row.original.number}`}>{t("trader.purchases.recordPayment")}</Button> : null),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the payment opener reads the accounts of the moment
    [t, today, liveAccounts],
  );

  return (
    <>
      <PageHeader
        title={t("trader.purchases.title")}
        description={t("trader.purchases.description")}
        actions={
          <Button onClick={openGoods} disabled={!companyId} data-testid="new-goods">
            <Plus aria-hidden="true" />
            {t("trader.purchases.newGoods")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <PeriodFilter value={period} onChange={setPeriod} />
      </div>
      <FormError message={!goods && !payment ? (problem?.message ?? null) : null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="purchases-notice">{notice}</p> : null}
      {next ? (
        <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-border p-2 text-sm" data-testid="next-step">
          <span className="font-medium">{t("trader.next.title")}</span>
          {next.kind === "received" || next.kind === "prior" ? (
            <Button asChild size="sm"><Link to="/trader/pricing" data-testid="next-price">{t("trader.purchases.next.price")}</Link></Button>
          ) : null}
          {next.kind === "received" && next.owed ? (
            <Button type="button" size="sm" variant="secondary" disabled={!invoices.data?.some((i) => i.id === next.invoiceId)} onClick={() => { const invoice = invoices.data?.find((i) => i.id === next.invoiceId); if (invoice) { openPaymentFor(invoice); } }} data-testid="next-pay">{t("trader.purchases.next.pay", { number: next.number })}</Button>
          ) : null}
          {next.kind === "paid" ? (
            <Button asChild size="sm" variant="secondary"><Link to="/trader/money" data-testid="next-money">{t("trader.purchases.next.seeMoney")}</Link></Button>
          ) : null}
          <Button type="button" size="sm" variant="secondary" onClick={openGoods} data-testid="next-more-goods">{t("trader.purchases.next.another")}</Button>
        </div>
      ) : null}
      <DataGrid<Invoice> label="trader.purchases.title" columns={columns} data={listed} rowKey={(row) => row.id} loading={invoices.isPending && Boolean(companyId)} emptyTitle={t("trader.purchases.empty")} emptyDescription={t("trader.purchases.emptyDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={opened !== null} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {opened ? (
            <div className="flex flex-col gap-4" data-testid="purchase-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold"><span dir="ltr">{t("trader.purchases.invoiceDetails", { number: opened.number })}</span></DialogTitle>
              </DialogHeader>
              <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
                <dt className="text-fg-muted">{t("trader.purchases.supplier")}</dt><dd dir="auto">{opened.partnerCode} · {localized(opened.partnerName)}</dd>
                <dt className="text-fg-muted">{t("trader.purchases.supplierReference")}</dt><dd dir="ltr">{opened.supplierInvoiceNumber ?? "—"}</dd>
                <dt className="text-fg-muted">{t("trader.purchases.date")}</dt><dd dir="ltr">{formatDate(opened.documentDate)}</dd>
                <dt className="text-fg-muted">{t("trader.purchases.status")}</dt><dd><Badge tone={stateTones[paymentState(opened, today)] ?? "neutral"}>{t(`trader.purchases.statuses.${paymentState(opened, today)}`, { defaultValue: opened.status })}</Badge></dd>
              </dl>
              <div>
                <h3 className="mb-1 text-sm font-semibold">{t("trader.purchases.lines")}</h3>
                <table className="w-full text-sm">
                  <thead><tr className="text-start text-fg-muted"><th className="text-start font-medium">{t("trader.purchases.item")}</th><th className="text-end font-medium">{t("trader.purchases.quantity")}</th><th className="text-end font-medium">{t("trader.purchases.unitPrice")}</th><th className="text-end font-medium">{t("trader.purchases.net")}</th></tr></thead>
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
                  <tfoot><tr className="border-t border-border font-semibold"><td colSpan={3}>{t("trader.purchases.total")}</td><td className="tabular text-end" dir="ltr">{formatMoney(opened.totalGross, opened.currency)}</td></tr></tfoot>
                </table>
              </div>
              <div>
                <h3 className="mb-1 text-sm font-semibold">{t("trader.purchases.payments")}</h3>
                {paymentsOf(opened).length === 0 ? <p className="text-sm text-fg-muted" data-testid="no-payments">{t("trader.purchases.noPayments")}</p> : (
                  <ul className="flex flex-col gap-1 text-sm" data-testid="payments-list">
                    {paymentsOf(opened).map((s) => (
                      <li key={s.id} className="flex items-center justify-between gap-2 rounded-md border border-border px-2 py-1" data-testid="payment-row">
                        <span dir="ltr">{formatDate(s.settlementDate)} · {s.settlingDocumentNumber}</span>
                        <span className="tabular" dir="ltr">{formatMoney(s.amountTc, s.currency)}</span>
                      </li>
                    ))}
                  </ul>
                )}
                <p className="mt-2 text-sm font-medium" data-testid="dialog-remaining">{t("trader.purchases.remaining")}: <span className="tabular" dir="ltr">{formatMoney(remainingOf(opened), opened.currency)}</span></p>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setOpenId(null); }}>{t("common.close")}</Button>
                {opened.status === "posted" && remainingOf(opened) > 0 ? <Button type="button" onClick={() => { openPaymentFor(opened); }} data-testid="dialog-pay">{t("trader.purchases.recordPayment")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(payment)} onOpenChange={(isOpen) => { if (!isOpen) { setPayment(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-lg">
          {payment ? (
            <form onSubmit={(e) => { e.preventDefault(); pay.mutate(payment); }} className="flex flex-col gap-4" data-testid="supplier-payment-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.purchases.paymentFor", { number: payment.invoice.number, name: localized(payment.invoice.partnerName) })}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              {liveAccounts.length === 0 ? (
                <div className="flex flex-wrap items-center gap-2 text-sm" data-testid="no-accounts">
                  <span>{t("trader.purchases.noAccounts")}</span>
                  <Button type="button" variant="secondary" size="sm" onClick={() => { createCash.mutate(); }} loading={createCash.isPending} data-testid="create-cash">{t("trader.purchases.createCash")}</Button>
                </div>
              ) : null}
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("trader.purchases.amount", { currency: payment.invoice.currency })} required>
                  <DecimalField value={payment.amount} onChange={(e) => { setPayment({ ...payment, amount: e.target.value }); }} dir="ltr" required data-testid="supplier-payment-amount" />
                </Field>
                <Field label={t("trader.purchases.payFrom")} required>
                  <SelectField value={payment.accountId} onChange={(e) => { const account = liveAccounts.find((a) => a.id === e.target.value); setPayment({ ...payment, accountId: e.target.value, method: account?.kind === "bank" ? "transfer" : "cash" }); }} required data-testid="supplier-payment-account">
                    {!payment.accountId ? <option value="" /> : null}
                    {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
                  </SelectField>
                </Field>
                {needsBankAmount ? (
                  <Field label={t("trader.purchases.bankAmount", { currency: payAccount.currency })} required>
                    <DecimalField value={payment.bankAmount} onChange={(e) => { setPayment({ ...payment, bankAmount: e.target.value }); }} dir="ltr" required data-testid="supplier-payment-bank-amount" />
                  </Field>
                ) : null}
                <Field label={t("trader.purchases.method")}>
                  <SelectField value={payment.method} onChange={(e) => { setPayment({ ...payment, method: e.target.value }); }} data-testid="supplier-payment-method">
                    <option value="cash">{t("trader.purchases.methods.cash")}</option>
                    <option value="transfer">{t("trader.purchases.methods.transfer")}</option>
                  </SelectField>
                </Field>
                <Field label={t("trader.purchases.paymentDate")}>
                  <TextField type="date" value={payment.date} onChange={(e) => { setPayment({ ...payment, date: e.target.value }); }} dir="ltr" data-testid="supplier-payment-date" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setPayment(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={pay.isPending} disabled={!payment.accountId || num(payment.amount) <= 0} data-testid="save-supplier-payment">{t("trader.purchases.savePayment")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(goods)} onOpenChange={(isOpen) => { if (!isOpen) { setGoods(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {goods ? (
            <form onSubmit={submitGoods} className="flex flex-col gap-4" data-testid="goods-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.purchases.goods.title")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("trader.purchases.goods.hint")}</p>
              <FormError message={problem?.message ?? null} />
              <div className="flex flex-wrap gap-4 text-sm" role="radiogroup" aria-label={t("trader.purchases.goods.title")}>
                <label className="flex items-center gap-2"><input type="radio" name="goods-mode" checked={goods.mode === "supplier"} onChange={() => { setGoods({ ...goods, mode: "supplier" }); }} data-testid="goods-mode-supplier" />{t("trader.purchases.goods.modeSupplier")}</label>
                <label className="flex items-center gap-2"><input type="radio" name="goods-mode" checked={goods.mode === "prior"} onChange={() => { setGoods({ ...goods, mode: "prior", currency, payNow: false }); }} data-testid="goods-mode-prior" />{t("trader.purchases.goods.modePrior")}</label>
              </div>
              <div className="grid gap-4 sm:grid-cols-2">
                {goods.mode === "supplier" ? (
                  <Field label={t("trader.purchases.goods.supplier")} required>
                    <div className="flex items-center gap-2">
                      <SelectField value={goods.partnerId} onChange={(e) => { setGoods({ ...goods, partnerId: e.target.value }); }} required data-testid="goods-supplier">
                        <option value="">{t("trader.purchases.goods.chooseSupplier")}</option>
                        {(suppliers.data ?? []).map((s) => <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>)}
                      </SelectField>
                      <QuickCreateButton kind="supplier" companyId={companyId} currency={currency} onCreated={(created) => { void queryClient.invalidateQueries({ queryKey: ["suppliers"] }); setGoods((prev) => (prev ? { ...prev, partnerId: created.id } : prev)); }} />
                    </div>
                  </Field>
                ) : null}
                <WarehouseSelect warehouses={warehouses.data ?? []} value={goods.warehouseId} onChange={(id) => { setGoods({ ...goods, warehouseId: id }); }} label={t("trader.purchases.goods.warehouse")} testId="goods-warehouse" required />
                <Field label={t("trader.purchases.goods.date")}>
                  <TextField type="date" value={goods.date} onChange={(e) => { setGoods({ ...goods, date: e.target.value }); }} dir="ltr" data-testid="goods-date" />
                </Field>
                {goods.mode === "supplier" ? (
                  <>
                    <Field label={t("trader.purchases.goods.currency")}>
                      <SelectField value={goods.currency} onChange={(e) => { setGoods({ ...goods, currency: e.target.value }); }} data-testid="goods-currency">
                        {[currency, "USD", "EUR", "CNY", "AED", "TRY"].filter((c, i, all) => c && all.indexOf(c) === i).map((c) => <option key={c} value={c}>{c}</option>)}
                      </SelectField>
                    </Field>
                    <Field label={t("trader.purchases.goods.invoiceNumber")}>
                      <TextField value={goods.invoiceNumber} onChange={(e) => { setGoods({ ...goods, invoiceNumber: e.target.value }); }} dir="ltr" data-testid="goods-invoice-number" />
                    </Field>
                  </>
                ) : null}
                <Field label={t("trader.purchases.goods.reference")}>
                  <TextField value={goods.reference} onChange={(e) => { setGoods({ ...goods, reference: e.target.value }); }} dir="auto" data-testid="goods-reference" />
                </Field>
              </div>
              {goods.mode === "prior" ? <p className="text-xs text-fg-muted">{t("trader.purchases.goods.priorHint")}</p> : null}
              <LinesEditor lines={goods.lines} onChange={(lines) => { setGoods({ ...goods, lines }); }} priceLabel={goods.mode === "prior" ? t("trader.purchases.goods.costLabel", { currency }) : t("trader.purchases.goods.priceLabel", { currency: goods.currency })} />
              <p className="text-sm font-medium" data-testid="goods-total">{t("trader.purchases.total")}: <span className="tabular" dir="ltr">{formatMoney(goodsTotal, goods.mode === "prior" ? currency : goods.currency)}</span></p>
              {goods.mode === "supplier" ? (
                <>
                  <div className="flex flex-col gap-2 rounded-md border border-border p-3">
                    <div className="flex items-center justify-between gap-2">
                      <h3 className="text-sm font-semibold">{t("trader.purchases.goods.costs")}</h3>
                      <Button type="button" variant="secondary" size="sm" onClick={() => { setGoods({ ...goods, costs: [...goods.costs, { chargeTypeId: chargeTypes.data?.[0]?.id ?? "", amount: "", currency, partnerId: "" }] }); }} data-testid="goods-add-cost">{t("trader.purchases.goods.addCost")}</Button>
                    </div>
                    {goods.costs.map((cost, index) => (
                      <div key={index} className="grid items-end gap-2 sm:grid-cols-[1fr_1fr_6rem_1fr_auto]" data-testid="goods-cost">
                        <Field label={t("trader.purchases.goods.costType")}>
                          <SelectField value={cost.chargeTypeId} onChange={(e) => { patchCost(index, { chargeTypeId: e.target.value }); }} data-testid={`goods-cost-type-${String(index)}`}>
                            {(chargeTypes.data ?? []).map((c) => <option key={c.id} value={c.id}>{localized(c.name)}</option>)}
                          </SelectField>
                        </Field>
                        <Field label={t("trader.purchases.goods.costAmount")}>
                          <DecimalField value={cost.amount} onChange={(e) => { patchCost(index, { amount: e.target.value }); }} dir="ltr" data-testid={`goods-cost-amount-${String(index)}`} />
                        </Field>
                        <Field label={t("trader.purchases.goods.costCurrency")}>
                          <SelectField value={cost.currency} onChange={(e) => { patchCost(index, { currency: e.target.value }); }}>
                            {[currency, goods.currency, "USD"].filter((c, i, all) => c && all.indexOf(c) === i).map((c) => <option key={c} value={c}>{c}</option>)}
                          </SelectField>
                        </Field>
                        <Field label={t("trader.purchases.goods.costPaidTo")}>
                          <SelectField value={cost.partnerId} onChange={(e) => { patchCost(index, { partnerId: e.target.value }); }} data-testid={`goods-cost-partner-${String(index)}`}>
                            <option value="">{t("trader.purchases.goods.nobody")}</option>
                            {(suppliers.data ?? []).map((s) => <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>)}
                          </SelectField>
                        </Field>
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setGoods({ ...goods, costs: goods.costs.filter((_, i) => i !== index) }); }}>{t("trader.purchases.goods.removeCost")}</Button>
                      </div>
                    ))}
                  </div>
                  <div className="flex flex-col gap-2 rounded-md border border-border p-3">
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={goods.payNow} onChange={(e) => { setGoods({ ...goods, payNow: e.target.checked }); }} data-testid="goods-pay-now" />{t("trader.purchases.goods.payNow")}</label>
                    {goods.payNow ? (
                      <div className="grid gap-4 sm:grid-cols-2">
                        {liveAccounts.length === 0 ? (
                          <div className="flex flex-wrap items-center gap-2 text-sm sm:col-span-2">
                            <span>{t("trader.purchases.noAccounts")}</span>
                            <Button type="button" variant="secondary" size="sm" onClick={() => { createCash.mutate(); }} loading={createCash.isPending}>{t("trader.purchases.createCash")}</Button>
                          </div>
                        ) : (
                          <Field label={t("trader.purchases.goods.payFrom")} required>
                            <SelectField value={goods.accountId} onChange={(e) => { setGoods({ ...goods, accountId: e.target.value }); }} required data-testid="goods-pay-account">
                              {!goods.accountId ? <option value="" /> : null}
                              {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
                            </SelectField>
                          </Field>
                        )}
                        {goodsNeedsRc ? (
                          <Field label={t("trader.purchases.goods.paidRc", { currency })} required>
                            <DecimalField value={goods.paidRc} onChange={(e) => { setGoods({ ...goods, paidRc: e.target.value }); }} dir="ltr" required data-testid="goods-paid-rc" />
                          </Field>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                </>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setGoods(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={receive.isPending} disabled={!goods.warehouseId || (goods.mode === "supplier" && !goods.partnerId) || !goods.lines.some((l) => l.itemCode.trim())} data-testid="save-goods">{t("trader.purchases.goods.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
