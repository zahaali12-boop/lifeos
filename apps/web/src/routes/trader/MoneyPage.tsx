import { Badge, Button } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";

type OpenItem = components["schemas"]["OpenItemSummary"];

interface SupplierPayForm {
  partnerId: string;
  partnerName: string;
  items: OpenItem[];
  currency: string;
  bankAccountId: string;
  amount: string;
  bankAmount: string;
  on: string;
}

interface AgingLike {
  partnerId: string;
  partnerCode: string;
  partnerName: Record<string, string>;
  notDue: number | string;
  days1To30: number | string;
  days31To60: number | string;
  days61To90: number | string;
  over90: number | string;
  totalFc: number | string;
}

const n = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));
const overdueOf = (row: AgingLike): number => n(row.days1To30) + n(row.days31To60) + n(row.days61To90) + n(row.over90);

/** The oldest bucket with money in it, in days late: 91, 61, 31, 1 or 0. */
function oldestLate(row: AgingLike): number {
  if (n(row.over90) > 0) {
    return 91;
  }
  if (n(row.days61To90) > 0) {
    return 61;
  }
  if (n(row.days31To60) > 0) {
    return 31;
  }
  return n(row.days1To30) > 0 ? 1 : 0;
}

function Buckets({ rows, currency, testId }: { rows: { label: string; value: number }[]; currency: string; testId: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <ul className="flex flex-col gap-1" data-testid={testId}>
      {rows.map((r) => (
        <li key={r.label} className="grid grid-cols-[minmax(0,10rem)_1fr_auto] items-center gap-2 text-sm">
          <span className="truncate text-fg-muted">{r.label}</span>
          <span className="h-2 rounded-full bg-surface-sunken" aria-hidden="true"><span className="block h-2 rounded-full bg-accent" style={{ width: `${String(Math.round((r.value / max) * 100))}%` }} /></span>
          <span className="tabular" dir="ltr">{formatMoney(r.value, currency)}</span>
        </li>
      ))}
    </ul>
  );
}

/**
 * Money (trader edition, ADR-0035 §6): who owes the trader and whom he owes, from the receivables and payables aging
 * of today, in plain words with what to do first; and sales by month from the posted invoices. A supplier or an agent
 * is paid from here (ADR-0035 §11): a posted supplier payment settling their oldest open invoices first.
 */
export function MoneyPage() {
  const { t } = useTranslation();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const receivables = useQuery({ queryKey: ["ar-aging", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items/aging", { params: { query: { companyId } } })) });
  const payables = useQuery({ queryKey: ["ap-aging", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/aging", { params: { query: { companyId } } })) });
  const invoices = useQuery({ queryKey: ["invoices", companyId, "posted", "money"], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId, status: "posted" } } })) });
  const queryClient = useQueryClient();
  const accounts = useBankAccounts(companyId);
  const [statement, setStatement] = useState<PrintSubject | null>(null);
  const [paying, setPaying] = useState<SupplierPayForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive);
  const startPaying = async (row: AgingLike): Promise<void> => {
    setProblem(null);
    setNotice(null);
    const open = unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: row.partnerId, status: "live" } } }))
      .filter((i) => i.item.kind === "invoice" && n(i.item.remainingTc) > 0)
      .sort((a, b) => a.item.dueDate.localeCompare(b.item.dueDate));
    const currency = open[0]?.item.currency ?? receivables.data?.functionalCurrency ?? payables.data?.functionalCurrency ?? "";
    const items = open.filter((i) => i.item.currency === currency);
    const owed = items.reduce((sum, i) => sum + n(i.item.remainingTc), 0);
    setPaying({ partnerId: row.partnerId, partnerName: localized(row.partnerName), items, currency, bankAccountId: liveAccounts[0]?.id ?? "", amount: String(owed), bankAmount: "", on: "" });
  };
  const payAccount = paying ? liveAccounts.find((a) => a.id === paying.bankAccountId) : undefined;
  const needsBankAmount = paying !== null && payAccount !== undefined && payAccount.currency !== paying.currency;
  const paySupplier = useMutation({
    mutationFn: async (f: SupplierPayForm) => {
      // The oldest invoices are paid first, each up to what it still asks for.
      let left = Number(f.amount);
      const lines: { openItemId: string; amount: number }[] = [];
      for (const i of f.items) {
        if (left <= 0) {
          break;
        }
        const take = Math.min(left, n(i.item.remainingTc));
        lines.push({ openItemId: i.item.id, amount: take });
        left -= take;
      }
      const created = unwrap(await api.POST("/api/v1/banking/payments", {
        body: { companyId, partnerId: f.partnerId, bankAccountId: f.bankAccountId, kind: "supplier_payment", paymentDate: f.on || null, method: payAccount?.kind === "bank" ? "transfer" : "cash", currency: f.currency, lines, onAccount: left > 0 ? left : 0, charges: 0, bankAmount: needsBankAmount && f.bankAmount.trim() ? Number(f.bankAmount) : null, applyWht: true },
      }));
      return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params: { path: { paymentId: created.id } } }));
    },
    onSuccess: async (posted) => {
      setPaying(null);
      setProblem(null);
      setNotice(t("trader.money.paid", { number: posted.number, amount: formatMoney(posted.amountTc, posted.currency) }));
      // The shipment the invoice came from shows what is left to pay, so it is refreshed too (the cache keeps pages for ten seconds).
      await Promise.all([["ap-aging"], ["bank-accounts"], ["trader-health"], ["home"], ["shipments"], ["shipment"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error: unknown) => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const ar = receivables.data;
  const ap = payables.data;
  const currency = ar?.functionalCurrency ?? ap?.functionalCurrency ?? "";
  const bucketRows = (totals: { notDue: number | string; days1To30: number | string; days31To60: number | string; days61To90: number | string; over90: number | string }) => [
    { label: t("trader.money.notDue"), value: n(totals.notDue) },
    { label: t("trader.money.days1To30"), value: n(totals.days1To30) },
    { label: t("trader.money.days31To60"), value: n(totals.days31To60) },
    { label: t("trader.money.days61To90"), value: n(totals.days61To90) },
    { label: t("trader.money.over90"), value: n(totals.over90) },
  ];
  // Everyone who owes, largest first: each with the statement to print or send (T12b).
  const owing = (ar?.rows ?? []).filter((r) => n(r.totalFc) > 0).sort((a, b) => n(b.totalFc) - n(a.totalFc));
  const chase = (ar?.rows ?? []).filter((r) => overdueOf(r) > 0).sort((a, b) => oldestLate(b) - oldestLate(a) || overdueOf(b) - overdueOf(a));
  const pay = (ap?.rows ?? []).filter((r) => n(r.totalFc) > 0).sort((a, b) => overdueOf(b) - overdueOf(a) || n(b.totalFc) - n(a.totalFc));
  const chaseText = (row: AgingLike): string => {
    const amount = formatMoney(overdueOf(row), currency);
    const name = localized(row.partnerName);
    const days = oldestLate(row);
    if (days >= 91) {
      return t("trader.money.chaseAction.stop", { name, amount: formatMoney(n(row.over90), currency) });
    }
    return days >= 31 ? t("trader.money.chaseAction.call", { name, amount, days: formatNumber(days) }) : t("trader.money.chaseAction.remind", { name, amount });
  };
  const payText = (row: AgingLike): string => {
    const name = localized(row.partnerName);
    return overdueOf(row) > 0 ? t("trader.money.payAction.now", { name, amount: formatMoney(overdueOf(row), currency) }) : t("trader.money.payAction.soon", { name, amount: formatMoney(n(row.notDue), currency) });
  };

  // Sales by month: the posted invoices of the last six months, credit notes subtracted.
  const months: { key: string; label: string; value: number }[] = [];
  const today = new Date();
  for (let i = 5; i >= 0; i--) {
    const d = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() - i, 1));
    months.push({ key: d.toISOString().slice(0, 7), label: d.toLocaleDateString(undefined, { month: "short", year: "numeric" }), value: 0 });
  }
  for (const inv of invoices.data ?? []) {
    const bucket = months.find((m) => m.key === inv.postingDate.slice(0, 7));
    if (bucket) {
      bucket.value += n(inv.totalGross) * (inv.kind === "credit_note" ? -1 : 1);
    }
  }
  const anySales = months.some((m) => m.value !== 0);
  const salesMax = Math.max(1, ...months.map((m) => Math.abs(m.value)));

  return (
    <>
      <PageHeader title={t("trader.money.title")} description={t("trader.money.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        {ar ? <span className="text-sm text-fg-muted">{t("trader.money.asOf", { date: formatDate(ar.asOf) })}</span> : null}
      </div>
      <FormError message={problem?.message ?? null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="money-notice">{notice}</p> : null}
      {ar && ap ? (
        <div className="flex flex-col gap-4">
          {statement ? <PrintDialog subject={statement} onClose={() => { setStatement(null); }} /> : null}
          <section className="grid gap-4 lg:grid-cols-2">
            <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4" data-testid="owed-to-you">
              <div>
                <div className="text-sm text-fg-muted">{t("trader.money.owedToYou")}</div>
                <div className="text-2xl font-semibold tabular" dir="ltr" data-testid="owed-to-you-total">{formatMoney(ar.totals.totalFc, currency)}</div>
                {overdueOf({ ...ar.totals, partnerId: "", partnerCode: "", partnerName: {} }) > 0 ? <div className="text-sm text-danger" dir="ltr">{formatMoney(overdueOf({ ...ar.totals, partnerId: "", partnerCode: "", partnerName: {} }), currency)} {t("trader.money.overdue")}</div> : null}
              </div>
              <Buckets rows={bucketRows(ar.totals)} currency={currency} testId="ar-buckets" />
              {owing.length > 0 ? (
                <ul className="flex flex-col gap-1 border-t border-border pt-2" data-testid="owed-by-customer">
                  {owing.map((row) => (
                    <li key={row.partnerId} className="flex items-center justify-between gap-2 text-sm" data-testid="owed-row">
                      <span dir="auto" className="truncate">{row.partnerCode} · {localized(row.partnerName)}</span>
                      <span className="flex items-center gap-2">
                        <span className="tabular" dir="ltr">{formatMoney(n(row.totalFc), currency)}</span>
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setStatement({ documentType: "customer_statement", documentId: row.partnerId, parameters: { companyId }, number: row.partnerCode, partnerId: row.partnerId, variants: ["brand", "standard"] }); }} data-testid={`statement-${row.partnerCode}`}>{t("trader.money.statement")}</Button>
                      </span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </div>
            <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4" data-testid="you-owe">
              <div>
                <div className="text-sm text-fg-muted">{t("trader.money.youOwe")}</div>
                <div className="text-2xl font-semibold tabular" dir="ltr" data-testid="you-owe-total">{formatMoney(ap.totals.totalFc, currency)}</div>
                {overdueOf({ ...ap.totals, partnerId: "", partnerCode: "", partnerName: {} }) > 0 ? <div className="text-sm text-danger" dir="ltr">{formatMoney(overdueOf({ ...ap.totals, partnerId: "", partnerCode: "", partnerName: {} }), currency)} {t("trader.money.overdue")}</div> : null}
              </div>
              <Buckets rows={bucketRows(ap.totals)} currency={currency} testId="ap-buckets" />
            </div>
          </section>

          <section className="grid gap-4 lg:grid-cols-2">
            <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="chase-list">
              <h2 className="text-base font-semibold">{t("trader.money.chase")}</h2>
              <p className="text-sm text-fg-muted">{t("trader.money.chaseHint")}</p>
              {chase.length === 0 ? <p className="text-sm" data-testid="nothing-to-chase">{t("trader.money.nothingToChase")}</p> : (
                <ol className="flex flex-col gap-2">
                  {chase.map((row) => (
                    <li key={row.partnerId} className="flex flex-col gap-0.5 rounded-md border border-border p-2 text-sm" data-testid="chase-row">
                      <span dir="auto">{chaseText(row)}</span>
                      <span className="text-xs text-fg-muted" dir="ltr">{row.partnerCode} · {t("trader.money.total")} {formatMoney(row.totalFc, currency)}</span>
                    </li>
                  ))}
                </ol>
              )}
            </div>
            <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="pay-list">
              <h2 className="text-base font-semibold">{t("trader.money.pay")}</h2>
              <p className="text-sm text-fg-muted">{t("trader.money.payHint")}</p>
              {pay.length === 0 ? <p className="text-sm" data-testid="nothing-to-pay">{t("trader.money.nothingToPay")}</p> : (
                <ol className="flex flex-col gap-2">
                  {pay.map((row) => (
                    <li key={row.partnerId} className="flex flex-col gap-0.5 rounded-md border border-border p-2 text-sm" data-testid="pay-row">
                      <span dir="auto">{payText(row)}</span>
                      <span className="text-xs text-fg-muted" dir="ltr">{row.partnerCode} · {t("trader.money.total")} {formatMoney(row.totalFc, currency)}</span>
                      <div className="flex flex-wrap items-center gap-2">
                        {overdueOf(row) > 0 ? <Badge tone="danger" className="w-fit">{t("trader.money.overdue")}</Badge> : null}
                        {paying?.partnerId !== row.partnerId ? <Button type="button" variant="secondary" size="sm" onClick={() => { void startPaying(row); }} data-testid="pay-supplier">{t("trader.money.payButton")}</Button> : null}
                      </div>
                      {paying?.partnerId === row.partnerId ? (
                        <form onSubmit={(e) => { e.preventDefault(); paySupplier.mutate(paying); }} className="mt-2 grid items-end gap-2 sm:grid-cols-2" data-testid="supplier-pay-form">
                          <Field label={t("trader.money.payFrom")} required>
                            <SelectField value={paying.bankAccountId} onChange={(e) => { setPaying({ ...paying, bankAccountId: e.target.value }); }} required data-testid="supplier-pay-account">
                              <option value="">{t("trader.money.chooseAccount")}</option>
                              {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
                            </SelectField>
                          </Field>
                          <Field label={t("trader.money.payAmount", { currency: paying.currency })} required>
                            <DecimalField value={paying.amount} onChange={(e) => { setPaying({ ...paying, amount: e.target.value }); }} required data-testid="supplier-pay-amount" />
                          </Field>
                          {needsBankAmount ? (
                            <Field label={t("trader.money.payBankAmount", { currency: payAccount.currency })} required>
                              <DecimalField value={paying.bankAmount} onChange={(e) => { setPaying({ ...paying, bankAmount: e.target.value }); }} required data-testid="supplier-pay-bank-amount" />
                            </Field>
                          ) : null}
                          <Field label={t("trader.money.payDate")}>
                            <TextField type="date" value={paying.on} onChange={(e) => { setPaying({ ...paying, on: e.target.value }); }} dir="ltr" data-testid="supplier-pay-date" />
                          </Field>
                          {paying.items.length === 0 ? <p className="text-xs text-fg-muted sm:col-span-2" dir="auto">{t("trader.money.payNothingOpen", { name: paying.partnerName })}</p> : null}
                          <div className="flex justify-end gap-2 sm:col-span-2">
                            <Button type="button" variant="secondary" onClick={() => { setPaying(null); }}>{t("common.cancel")}</Button>
                            <Button type="submit" loading={paySupplier.isPending} disabled={!paying.bankAccountId || paying.items.length === 0} data-testid="save-supplier-payment">{t("trader.money.payNow")}</Button>
                          </div>
                        </form>
                      ) : null}
                    </li>
                  ))}
                </ol>
              )}
            </div>
          </section>

          <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="monthly-sales">
            <h2 className="text-base font-semibold">{t("trader.money.monthlySales")}</h2>
            <p className="text-sm text-fg-muted">{t("trader.money.monthlySalesHint")}</p>
            {anySales ? (
              <ul className="flex flex-col gap-1">
                {months.map((m) => (
                  <li key={m.key} className="grid grid-cols-[minmax(0,8rem)_1fr_auto] items-center gap-2 text-sm" data-testid="sales-month">
                    <span className="truncate text-fg-muted" dir="ltr">{m.label}</span>
                    <span className="h-3 rounded-full bg-surface-sunken" aria-hidden="true"><span className="block h-3 rounded-full bg-success" style={{ width: `${String(Math.round((Math.abs(m.value) / salesMax) * 100))}%` }} /></span>
                    <span className="tabular" dir="ltr">{formatMoney(m.value, currency)}</span>
                  </li>
                ))}
              </ul>
            ) : <p className="text-sm text-fg-muted">{t("trader.money.noSales")}</p>}
          </section>
        </div>
      ) : null}
    </>
  );
}
