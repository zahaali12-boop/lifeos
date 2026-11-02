import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { traderWarningText, warningTones } from "./warnings";

type Health = components["schemas"]["TraderHealth"];
type Income = components["schemas"]["TraderIncome"];
type Warning = components["schemas"]["TraderWarning"];

const periodKeys = ["month", "lastMonth", "year", "days90"] as const;
type PeriodKey = (typeof periodKeys)[number];

const fixedCostsKey = "trader.health.fixed_costs_monthly";

const n =(value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/** The dates a period key stands for, in UTC calendar days; "this month so far" is the API's own default. */
function periodRange(key: PeriodKey): { from?: string; to?: string } {
  const today = new Date();
  const iso = (d: Date): string => d.toISOString().slice(0, 10);
  const utc = (year: number, month: number, day: number): Date => new Date(Date.UTC(year, month, day));
  switch (key) {
    case "lastMonth":
      return { from: iso(utc(today.getUTCFullYear(), today.getUTCMonth() - 1, 1)), to: iso(utc(today.getUTCFullYear(), today.getUTCMonth(), 0)) };
    case "year":
      return { from: iso(utc(today.getUTCFullYear(), 0, 1)) };
    case "days90":
      return { from: iso(utc(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - 89)) };
    default:
      return {};
  }
}

function Bars({ rows, currency, testId }: { rows: { key: string; label: string; value: number }[]; currency: string; testId: string }) {
  const max = Math.max(1, ...rows.map((r) => Math.abs(r.value)));
  return (
    <ul className="flex flex-col gap-1">
      {rows.map((r) => (
        <li key={r.key} className="grid grid-cols-[minmax(0,8rem)_1fr_auto] items-center gap-2 text-sm" data-testid={testId}>
          <span className="truncate text-fg-muted" dir="ltr">{r.label}</span>
          <span className="h-3 rounded-full bg-surface-sunken" aria-hidden="true"><span className={`block h-3 rounded-full ${r.value < 0 ? "bg-danger" : "bg-success"}`} style={{ width: `${String(Math.round((Math.abs(r.value) / max) * 100))}%` }} /></span>
          <span className={`tabular ${r.value < 0 ? "text-danger" : ""}`} dir="ltr">{formatMoney(r.value, currency)}</span>
        </li>
      ))}
    </ul>
  );
}

/**
 * Health (trader edition, ADR-0035 §8): is the project alive? The period's income statement in plain words with the
 * period before it and six months of results; the fixed costs, the sales a month needs to break even, where this month
 * leads at its pace, the cash and how long it lasts, the money position; warnings in plain words; the shipments on the
 * way with their money; what sold by item and by customer; what to reorder. One read; the fixed costs are the one
 * thing edited here.
 */
export function HealthPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [period, setPeriod] = useState<PeriodKey>("month");
  const [fixedCosts, setFixedCosts] = useState<string | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const range = periodRange(period);

  const health = useQuery({
    queryKey: ["trader-health", companyId, period],
    enabled: Boolean(companyId),
    queryFn: async () => {
      const query: { companyId: string; from?: string; to?: string } = { companyId };
      if (range.from) {
        query.from = range.from;
      }
      if (range.to) {
        query.to = range.to;
      }
      return unwrap(await api.GET("/api/v1/trader/health", { params: { query } }));
    },
  });
  const saveFixedCosts = useMutation({
    mutationFn: async (value: string) => unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: fixedCostsKey } }, body: { value: Number(value) || 0, valueType: "number" } })),
    onSuccess: async () => {
      setFixedCosts(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["trader-health"] });
    },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const h: Health | undefined = health.data;
  const currency = h?.currency ?? "";
  const money = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : formatMoney(value, currency));
  const pct = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : `${formatNumber(value, { maximumFractionDigits: 1 })}%`);
  const qty = (value: number | string | null | undefined): string => formatNumber(n(value), { maximumFractionDigits: 3 });
  const verdict = (income: Income, label: string): string => {
    const net = n(income.netProfit);
    if (net > 0) {
      return t("trader.health.verdict.profit", { period: label, amount: money(net) });
    }
    if (net < 0) {
      return t("trader.health.verdict.loss", { period: label, amount: money(-net) });
    }
    return n(income.netSales) === 0 && n(income.expenses) === 0 ? t("trader.health.verdict.nothing", { period: label }) : t("trader.health.verdict.even", { period: label });
  };
  const warningText = (w: Warning): string => traderWarningText(t, w, Number(h?.coverDays ?? 90), currency);
  const submitFixedCosts = (event: FormEvent): void => {
    event.preventDefault();
    if (fixedCosts !== null) {
      saveFixedCosts.mutate(fixedCosts);
    }
  };
  const monthLabel = (iso: string): string => new Date(`${iso}T00:00:00Z`).toLocaleDateString(undefined, { month: "short", year: "numeric", timeZone: "UTC" });
  const periodLabel = t(`trader.health.periods.${period}`);
  const coverage = h ? n(h.viability.breakEvenCoveragePct) : 0;
  const incomeRows = (income: Income): { key: string; label: string; value: number; bold?: boolean; indent?: boolean; danger?: boolean; pct?: string }[] => [
    { key: "sales", label: t("trader.health.income.sales"), value: n(income.sales) },
    { key: "returns", label: t("trader.health.income.returns"), value: -n(income.returnsAndDiscounts), indent: true },
    { key: "netSales", label: t("trader.health.income.netSales"), value: n(income.netSales), bold: true },
    { key: "cogs", label: t("trader.health.income.costOfGoods"), value: -n(income.costOfGoods), indent: true },
    { key: "gross", label: t("trader.health.income.grossProfit"), value: n(income.grossProfit), bold: true, pct: pct(income.grossMarginPct) },
    { key: "other", label: t("trader.health.income.otherIncome"), value: n(income.otherIncome), indent: true },
    { key: "finance", label: t("trader.health.income.financeCosts"), value: -n(income.financeCosts), indent: true },
    { key: "tax", label: t("trader.health.income.incomeTax"), value: -n(income.incomeTax), indent: true },
    { key: "net", label: t("trader.health.income.netProfit"), value: n(income.netProfit), bold: true, danger: n(income.netProfit) < 0, pct: pct(income.netMarginPct) },
  ];

  return (
    <>
      <PageHeader title={t("trader.health.title")} description={t("trader.health.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("trader.health.period")}>
          <SelectField value={period} onChange={(e) => { setPeriod(e.target.value as PeriodKey); }} data-testid="health-period">
            {periodKeys.map((key) => <option key={key} value={key}>{t(`trader.health.periods.${key}`)}</option>)}
          </SelectField>
        </Field>
        {h ? <span className="text-sm text-fg-muted" dir="ltr">{formatDate(h.income.from)} – {formatDate(h.income.to)}</span> : null}
      </div>
      <FormError message={problem?.message ?? null} />

      {h ? (
        <div className="flex flex-col gap-4">
          <section className={`rounded-lg border p-4 ${n(h.income.netProfit) < 0 ? "border-danger/40 bg-danger-soft" : "border-border bg-surface"}`} data-testid="verdict">
            <p className="text-xl font-semibold" dir="auto">{verdict(h.income, periodLabel)}</p>
            <p className="text-sm text-fg-muted" dir="auto">{t("trader.health.verdict.detail", { sales: money(h.income.netSales), gross: money(h.income.grossProfit), margin: pct(h.income.grossMarginPct), expenses: money(h.income.expenses) })}</p>
            <p className="text-sm text-fg-muted" dir="auto" data-testid="previous-verdict">{verdict(h.previous, t("trader.health.previousPeriod", { from: formatDate(h.previous.from), to: formatDate(h.previous.to) }))}</p>
          </section>

          <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="warnings">
            <h2 className="text-base font-semibold">{t("trader.health.warningsTitle")}</h2>
            {h.warnings.length === 0 ? <p className="text-sm" data-testid="no-warnings">{t("trader.health.noWarnings")}</p> : (
              <ul className="flex flex-col gap-2">
                {h.warnings.map((w) => (
                  <li key={w.code} className="flex flex-wrap items-center gap-2 text-sm" data-testid="warning-row" data-code={w.code}>
                    <Badge tone={warningTones[w.code] ?? "info"}>{t(`trader.health.warningNames.${w.code}`, { defaultValue: w.code })}</Badge>
                    <span dir="auto">{warningText(w)}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="grid gap-4 lg:grid-cols-2">
            <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="income-statement">
              <h2 className="text-base font-semibold">{t("trader.health.income.title")}</h2>
              <p className="text-sm text-fg-muted">{t("trader.health.income.hint")}</p>
              <dl className="grid grid-cols-[minmax(0,1fr)_auto_auto] gap-x-3 gap-y-1 text-sm">
                {incomeRows(h.income).flatMap((row) => {
                  const line = (
                    <div key={row.key} className="contents" data-testid={`income-${row.key}`}>
                      <dt className={`${row.indent ? "ps-4 text-fg-muted" : ""} ${row.bold ? "font-semibold" : ""}`}>{row.label}</dt>
                      <dd className="tabular text-end text-fg-muted" dir="ltr">{row.pct ?? ""}</dd>
                      <dd className={`tabular text-end ${row.bold ? "font-semibold" : ""} ${row.danger ? "text-danger" : ""}`} dir="ltr">{formatMoney(row.value, currency)}</dd>
                    </div>
                  );
                  if (row.key !== "other") {
                    return [line];
                  }
                  // The expenses by name sit between other income and the finance costs.
                  return [
                    line,
                    <div key="expenses" className="contents" data-testid="income-expenses">
                      <dt className="ps-4 text-fg-muted">{t("trader.health.income.expenses")}</dt>
                      <dd />
                      <dd className="tabular text-end" dir="ltr">{formatMoney(-n(h.income.expenses), currency)}</dd>
                    </div>,
                    ...h.income.expenseLines.map((l) => (
                      <div key={l.accountId} className="contents" data-testid="expense-line">
                        <dt className="ps-8 text-fg-muted" dir="auto">{localized(l.name)}</dt>
                        <dd />
                        <dd className="tabular text-end text-fg-muted" dir="ltr">{formatMoney(-n(l.amount), currency)}</dd>
                      </div>
                    )),
                  ];
                })}
              </dl>
            </div>

            <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4" data-testid="viability">
              <h2 className="text-base font-semibold">{t("trader.health.viability.title")}</h2>
              <div className="flex flex-col gap-1 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="text-fg-muted">{t("trader.health.viability.fixedCosts")}</span>
                  {fixedCosts === null ? (
                    <span className="flex items-center gap-2">
                      <span className="tabular" dir="ltr" data-testid="fixed-costs-value">{money(h.viability.fixedCostsMonthly)}</span>
                      <Button type="button" variant="ghost" size="sm" onClick={() => { setFixedCosts(h.viability.fixedCostsMonthly === null ? "" : String(h.viability.fixedCostsMonthly)); }} data-testid="edit-fixed-costs">{t("common.edit")}</Button>
                    </span>
                  ) : null}
                </div>
                {fixedCosts !== null ? (
                  <form onSubmit={submitFixedCosts} className="grid items-end gap-2 sm:grid-cols-[1fr_auto]">
                    <Field label={t("trader.health.viability.fixedCostsInput")} description={t("trader.health.viability.fixedCostsHint")}>
                      <DecimalField value={fixedCosts} onChange={(e) => { setFixedCosts(e.target.value); }} data-testid="fixed-costs" />
                    </Field>
                    <div className="flex gap-2">
                      <Button type="button" variant="secondary" onClick={() => { setFixedCosts(null); }}>{t("common.cancel")}</Button>
                      <Button type="submit" loading={saveFixedCosts.isPending} data-testid="save-fixed-costs">{t("common.save")}</Button>
                    </div>
                  </form>
                ) : (
                  <p className="text-xs text-fg-muted" dir="auto">{t(`trader.health.viability.fixedCostsSource.${h.viability.fixedCostsSource}`)}</p>
                )}
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.margin")}</span><span className="tabular" dir="ltr">{pct(h.viability.marginPct)}</span></div>
                <p className="text-xs text-fg-muted" dir="auto">{t(`trader.health.viability.marginSource.${h.viability.marginSource}`)}</p>
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.breakEven")}</span><span className="tabular font-semibold" dir="ltr" data-testid="break-even">{money(h.viability.breakEvenSalesMonthly)}</span></div>
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.monthToDate")}</span><span className="tabular" dir="ltr" data-testid="month-to-date">{money(h.viability.monthToDateSales)}</span></div>
                <p className="text-xs text-fg-muted" dir="auto" data-testid="month-pace">{t("trader.health.viability.pace", { amount: money(h.viability.projectedMonthSales), pct: pct(h.viability.breakEvenCoveragePct) })}</p>
                {h.viability.breakEvenCoveragePct !== null ? (
                  <span className="h-2 rounded-full bg-surface-sunken" aria-hidden="true"><span className={`block h-2 rounded-full ${coverage >= 100 ? "bg-success" : "bg-warning"}`} style={{ width: `${String(Math.min(100, Math.max(0, Math.round(coverage))))}%` }} /></span>
                ) : null}
              </div>
              <div className="flex flex-col gap-1 border-t border-border pt-2 text-sm">
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.cash")}</span><span className="tabular font-semibold" dir="ltr" data-testid="cash-total">{money(h.viability.cash)}</span></div>
                {h.viability.cashAccounts.map((a) => (
                  <div key={a.id} className="flex justify-between gap-2 ps-4 text-fg-muted" data-testid="cash-account"><span dir="auto">{a.code} · {localized(a.name)}</span><span className="tabular" dir="ltr">{formatMoney(a.balanceTc, a.currency)}</span></div>
                ))}
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.owedToYou")}</span><span className="tabular" dir="ltr" data-testid="owed-to-you">{money(h.viability.owedToYou)}</span></div>
                {n(h.viability.owedToYouOverdue) > 0 ? <p className="ps-4 text-xs text-danger" dir="auto">{t("trader.health.viability.overdue", { amount: money(h.viability.owedToYouOverdue) })}</p> : null}
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.youOwe")}</span><span className="tabular" dir="ltr" data-testid="you-owe">{money(h.viability.youOwe)}</span></div>
                {n(h.viability.youOweOverdue) > 0 ? <p className="ps-4 text-xs text-danger" dir="auto">{t("trader.health.viability.overdue", { amount: money(h.viability.youOweOverdue) })}</p> : null}
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.netPosition")}</span><span className={`tabular font-semibold ${n(h.viability.netPosition) < 0 ? "text-danger" : ""}`} dir="ltr" data-testid="net-position">{money(h.viability.netPosition)}</span></div>
                <p className="text-xs text-fg-muted" dir="auto">{t("trader.health.viability.netPositionHint")}</p>
                <div className="flex justify-between gap-2"><span className="text-fg-muted">{t("trader.health.viability.runway")}</span><span className="tabular font-semibold" dir="ltr" data-testid="cash-runway">{h.viability.cashRunwayMonths === null ? "—" : t("trader.health.viability.months", { months: formatNumber(h.viability.cashRunwayMonths, { maximumFractionDigits: 1 }) })}</span></div>
                <Link to="/trader/money" className="text-sm text-accent underline">{t("trader.health.viability.moneyLink")}</Link>
              </div>
            </div>
          </section>

          <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="months">
            <h2 className="text-base font-semibold">{t("trader.health.months.title")}</h2>
            <p className="text-sm text-fg-muted">{t("trader.health.months.hint")}</p>
            <Bars rows={h.months.map((m) => ({ key: m.month, label: monthLabel(m.month), value: n(m.netProfit) }))} currency={currency} testId="month-row" />
          </section>

          <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="on-the-way">
            <h2 className="text-base font-semibold">{t("trader.health.shipments.title")}</h2>
            {h.shipments.length === 0 ? <p className="text-sm" data-testid="nothing-on-the-way">{t("trader.health.shipments.none")}</p> : (
              <>
                <p className="text-sm" dir="auto" data-testid="on-the-way-summary">{t("trader.health.shipments.summary", { count: formatNumber(h.shipments.length), goods: money(h.goodsOnTheWayRc), paid: money(h.paidOnTheWayRc), unpaid: money(h.unpaidOnTheWayRc) })}</p>
                <Table data-testid="underway-table">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("trader.health.shipments.order")}</TableHead>
                      <TableHead>{t("trader.health.shipments.stage")}</TableHead>
                      <TableHead>{t("trader.health.shipments.expected")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.shipments.goods")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.shipments.paid")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.shipments.costs")}</TableHead>
                      <TableHead>{t("trader.health.shipments.hints")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {h.shipments.map((s) => (
                      <TableRow key={s.shipmentId} data-testid="underway-row">
                        <TableCell dir="auto"><Link to="/trader/shipments" search={{ open: s.shipmentId }} className="text-accent underline">{s.orderNumber}</Link> · {localized(s.partnerName)}</TableCell>
                        <TableCell>{t(`trader.shipments.stages.${s.status}`, { defaultValue: s.status })}</TableCell>
                        <TableCell dir="ltr">{s.expectedArrival ? formatDate(s.expectedArrival) : "—"}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatMoney(s.goodsTotal, s.currency)}{s.currency !== currency ? <span className="block text-xs text-fg-muted">{money(s.goodsTotalRc)}</span> : null}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{money(s.paidAmountRc)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{money(s.costsTotalRc)}</TableCell>
                        <TableCell><div className="flex flex-wrap gap-1">{s.hints.map((code) => <Badge key={code} tone={code === "arrival_overdue" || code === "stale" ? "danger" : "warning"}>{t(`trader.health.hintNames.${code}`, { defaultValue: code })}</Badge>)}</div></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </>
            )}
          </section>

          <section className="grid gap-4 lg:grid-cols-2">
            <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="by-item">
              <h2 className="text-base font-semibold">{t("trader.health.byItem.title")}</h2>
              {h.byItem.length === 0 ? <p className="text-sm text-fg-muted">{t("trader.health.noSales")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("trader.health.byItem.item")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.byItem.quantity")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.byItem.sales")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.share")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {h.byItem.map((row) => (
                      <TableRow key={row.itemId} data-testid="by-item-row">
                        <TableCell dir="auto">{row.itemCode} · {localized(row.itemName)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{qty(row.quantity)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{money(row.netSales)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{pct(row.sharePct)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
            <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="by-customer">
              <h2 className="text-base font-semibold">{t("trader.health.byCustomer.title")}</h2>
              {h.byCustomer.length === 0 ? <p className="text-sm text-fg-muted">{t("trader.health.noSales")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("trader.health.byCustomer.customer")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.byCustomer.invoices")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.byCustomer.sales")}</TableHead>
                      <TableHead className="text-end">{t("trader.health.share")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {h.byCustomer.map((row) => (
                      <TableRow key={row.partnerId} data-testid="by-customer-row">
                        <TableCell dir="auto">{row.partnerCode} · {localized(row.partnerName)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatNumber(row.invoices)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{money(row.netSales)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{pct(row.sharePct)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          </section>

          <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="reorder">
            <h2 className="text-base font-semibold">{t("trader.health.reorder.title")}</h2>
            <p className="text-sm text-fg-muted">{t("trader.health.reorder.hint", { days: formatNumber(h.coverDays) })}</p>
            {h.reorder.length === 0 ? <p className="text-sm" data-testid="nothing-to-reorder">{t("trader.health.reorder.none")}</p> : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("trader.health.byItem.item")}</TableHead>
                    <TableHead className="text-end">{t("trader.health.reorder.onHand")}</TableHead>
                    <TableHead className="text-end">{t("trader.health.reorder.sold", { days: formatNumber(h.coverDays) })}</TableHead>
                    <TableHead className="text-end">{t("trader.health.reorder.daysOfCover")}</TableHead>
                    <TableHead className="text-end">{t("trader.health.reorder.onTheWay")}</TableHead>
                    <TableHead className="text-end">{t("trader.health.reorder.suggested")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {h.reorder.map((row) => (
                    <TableRow key={row.itemId} data-testid="reorder-row">
                      <TableCell dir="auto">{row.itemCode} · {localized(row.itemName)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{qty(row.onHand)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{qty(row.soldInCoverDays)}</TableCell>
                      <TableCell className={`tabular text-end ${n(row.daysOfCover) < 30 ? "text-danger" : ""}`} dir="ltr">{row.daysOfCover === null ? "—" : formatNumber(row.daysOfCover, { maximumFractionDigits: 1 })}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{qty(row.onTheWay)}</TableCell>
                      <TableCell className="tabular text-end font-semibold" dir="ltr" data-testid="suggested-quantity">{qty(row.suggestedQuantity)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
            <Link to="/trader/offers" className="text-sm text-accent underline">{t("trader.health.reorder.offersLink")}</Link>
          </section>
        </div>
      ) : null}
    </>
  );
}
