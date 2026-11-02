import { Badge, Button, cn } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { useSession } from "../../session/session";
import { today } from "../accounting/shared";
import { PageHeader } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { SetupCard } from "../setup/SetupCard";
import { traderWarningText, warningTargets, warningTones } from "./warnings";

type Health = components["schemas"]["TraderHealth"];
type Home = components["schemas"]["HomeDashboard"];

const n = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/** A number with its name, opening the screen it comes from. */
function Tile({ label, value, hint, to, tone, testId }: { label: string; value: string; hint?: string | undefined; to: string; tone?: "danger" | "good" | undefined; testId: string }) {
  return (
    <Link to={to} className="flex flex-col rounded-lg border border-border bg-surface p-4 shadow-sm transition-colors hover:bg-surface-sunken focus:outline-none focus-visible:ring-2 focus-visible:ring-accent" data-testid={testId}>
      <div className="text-sm text-fg-muted">{label}</div>
      <div className={cn("mt-1 text-2xl font-semibold tabular", tone === "danger" ? "text-danger" : tone === "good" ? "text-success" : "")} dir="auto" data-testid={`${testId}-value`}>{value}</div>
      {hint ? <div className="mt-1 text-xs text-fg-muted" dir="auto">{hint}</div> : null}
    </Link>
  );
}

/**
 * The trader's home (ADR-0035 §10): the day in plain words. What was sold today, whether the month pays, what is
 * owed each way, the cash, what is on the way; then what to do today (the health warnings, each opening the screen
 * that acts on it), the four things a trader does most, and sales by month. Every number is the Health, Money and
 * home read models' own; nothing is computed here.
 */
export function TraderHomePage() {
  const { t } = useTranslation();
  const session = useSession();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const asOf = today();
  const health = useQuery({
    queryKey: ["trader-health", companyId, "month"],
    enabled: Boolean(companyId),
    refetchInterval: 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/health", { params: { query: { companyId } } })),
  });
  const home = useQuery({
    queryKey: ["home", companyId, asOf],
    enabled: Boolean(companyId),
    refetchInterval: 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/home", { params: { query: { companyId, asOf } } })),
  });

  const h: Health | undefined = health.data;
  const d: Home | undefined = home.data;
  const currency = h?.currency ?? d?.functionalCurrency ?? company?.functionalCurrency ?? "";
  const money = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : formatMoney(value, currency));
  const net = h ? n(h.income.netProfit) : 0;
  const monthValue = !h ? "—" : net > 0 ? t("trader.home.profit", { amount: money(net) }) : net < 0 ? t("trader.home.loss", { amount: money(-net) }) : n(h.income.netSales) === 0 ? t("trader.home.nothingYet") : t("trader.home.even");
  const todos = (h?.warnings ?? []).map((w) => ({ code: w.code, text: traderWarningText(t, w, Number(h?.coverDays ?? 90), currency), target: warningTargets[w.code] ?? { to: "/trader/health", label: "nav.trader.health" }, tone: warningTones[w.code] ?? "info" }));
  const months = h?.months ?? [];
  const monthsMax = Math.max(1, ...months.map((m) => Math.abs(n(m.netSales))));
  const monthLabel = (iso: string): string => new Date(`${iso}T00:00:00Z`).toLocaleDateString(undefined, { month: "short", year: "numeric", timeZone: "UTC" });

  return (
    <>
      <PageHeader title={t("trader.home.title", { name: session?.user.displayName ?? "" })} description={t("trader.home.description", { company: company ? localized(company.legalName) : "", date: formatDate(asOf) })} />
      {companies.length > 1 ? (
        <div className="mb-3 flex flex-wrap items-end gap-3">
          <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        </div>
      ) : null}
      <SetupCard companyId={companyId} />

      <section className="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-3" data-testid="home-tiles">
        <Tile label={t("trader.home.soldToday")} value={d?.sales ? money(d.sales.today) : "—"} hint={d?.sales ? t("trader.home.monthSales", { amount: money(d.sales.monthToDate) }) : undefined} to="/trader/sales" testId="home-sold-today" />
        <Tile label={t("trader.home.thisMonth")} value={monthValue} hint={h ? t("trader.home.monthHint", { sales: money(h.income.netSales), gross: money(h.income.grossProfit), expenses: money(h.income.expenses) }) : undefined} to="/trader/health" tone={!h ? undefined : net < 0 ? "danger" : net > 0 ? "good" : undefined} testId="home-month" />
        <Tile label={t("trader.home.cash")} value={h ? money(h.viability.cash) : "—"} hint={h?.viability.cashRunwayMonths !== null && h?.viability.cashRunwayMonths !== undefined ? t("trader.home.runway", { months: formatNumber(h.viability.cashRunwayMonths, { maximumFractionDigits: 1 }) }) : undefined} to="/trader/money" testId="home-cash" />
        <Tile label={t("trader.home.owedToYou")} value={h ? money(h.viability.owedToYou) : "—"} hint={h && n(h.viability.owedToYouOverdue) > 0 ? t("trader.home.overdue", { amount: money(h.viability.owedToYouOverdue) }) : undefined} to="/trader/money" tone={h && n(h.viability.owedToYouOverdue) > 0 ? "danger" : undefined} testId="home-owed" />
        <Tile label={t("trader.home.youOwe")} value={h ? money(h.viability.youOwe) : "—"} hint={h && n(h.viability.youOweOverdue) > 0 ? t("trader.home.overdue", { amount: money(h.viability.youOweOverdue) }) : undefined} to="/trader/money" tone={h && n(h.viability.youOweOverdue) > 0 ? "danger" : undefined} testId="home-owe" />
        <Tile label={t("trader.home.onTheWay")} value={h ? formatNumber(h.shipments.length) : "—"} hint={h && h.shipments.length > 0 ? t("trader.home.onTheWayHint", { goods: money(h.goodsOnTheWayRc), unpaid: money(h.unpaidOnTheWayRc) }) : h ? t("trader.home.nothingOnTheWay") : undefined} to="/trader/shipments" testId="home-on-the-way" />
      </section>

      <section className="mb-6 grid gap-4 lg:grid-cols-[2fr_1fr]">
        <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="home-todo">
          <h2 className="text-base font-semibold">{t("trader.home.todo")}</h2>
          {h && todos.length === 0 ? <p className="text-sm" data-testid="nothing-to-do">{t("trader.home.nothingToDo")}</p> : null}
          {todos.length > 0 ? (
            <ul className="flex flex-col gap-2">
              {todos.map((todo) => (
                <li key={todo.code} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-border p-2 text-sm" data-testid="todo-row" data-code={todo.code}>
                  <span className="flex flex-wrap items-center gap-2" dir="auto"><Badge tone={todo.tone}>{t(`trader.health.warningNames.${todo.code}`, { defaultValue: todo.code })}</Badge>{todo.text}</span>
                  <Link to={todo.target.to} className="text-accent underline">{t(todo.target.label)}</Link>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
        <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="home-actions">
          <h2 className="text-base font-semibold">{t("trader.home.actions")}</h2>
          <Button asChild><Link to="/trader/sales">{t("trader.home.sell")}</Link></Button>
          <Button asChild variant="secondary"><Link to="/trader/offers">{t("trader.home.compareOffers")}</Link></Button>
          <Button asChild variant="secondary"><Link to="/trader/stock">{t("trader.home.countStore")}</Link></Button>
          <Button asChild variant="secondary"><Link to="/trader/pricing">{t("trader.home.setPrices")}</Link></Button>
        </div>
      </section>

      {months.length > 0 ? (
        <section className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4" data-testid="home-months">
          <h2 className="text-base font-semibold">{t("trader.home.salesByMonth")}</h2>
          <ul className="flex flex-col gap-1">
            {months.map((m) => (
              <li key={m.month} className="grid grid-cols-[minmax(0,8rem)_1fr_auto] items-center gap-2 text-sm" data-testid="home-month-row">
                <span className="truncate text-fg-muted" dir="ltr">{monthLabel(m.month)}</span>
                <span className="h-3 rounded-full bg-surface-sunken" aria-hidden="true"><span className="block h-3 rounded-full bg-accent" style={{ width: `${String(Math.round((Math.abs(n(m.netSales)) / monthsMax) * 100))}%` }} /></span>
                <span className="tabular" dir="ltr">{money(m.netSales)}</span>
              </li>
            ))}
          </ul>
          <Link to="/trader/health" className="text-sm text-accent underline">{t("trader.home.healthLink")}</Link>
        </section>
      ) : null}
    </>
  );
}
