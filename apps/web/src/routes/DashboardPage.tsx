import { cn } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useId, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { compare } from "../lib/decimal";
import { formatDate, formatMoney, formatNumber, localized } from "../lib/format";
import { useCan } from "../lib/permissions";
import { useSession } from "../session/session";
import { today } from "./accounting/shared";
import { PageHeader } from "./common";
import { DashboardGrid, useDashboardRun, useDashboards, type Dashboard } from "./dashboards/shared";
import { CompanyFilter, useCompanyContext } from "./inventory/shared";
import { SetupCard } from "./setup/SetupCard";

type Home = components["schemas"]["HomeDashboard"];
type Day = components["schemas"]["HomeDay"];

/** A number with its name, opening the screen it was counted from; the hint says what is behind it. */
function Stat({ label, value, to, hint, tone, children, testId }: { label: string; value: string; to: string; hint?: ReactNode; tone?: "attention" | "good" | undefined; children?: ReactNode; testId?: string | undefined }) {
  return (
    <Link to={to} className="flex flex-col rounded-lg border border-border bg-surface p-4 shadow-sm transition-colors hover:bg-surface-sunken focus:outline-none focus-visible:ring-2 focus-visible:ring-accent" data-testid={testId}>
      <div className="text-sm text-fg-muted">{label}</div>
      <div className={cn("mt-1 text-2xl font-semibold", tone === "attention" ? "text-danger" : "")} dir="ltr" data-testid={testId ? `${testId}-value` : undefined}>{value}</div>
      {hint ? <div className="mt-1 text-xs text-fg-muted">{hint}</div> : null}
      {children}
    </Link>
  );
}

/** The change of this month against the same days of the month before, signed; up is good for sales. */
function Delta({ current, previous }: { current: string; previous: string }) {
  const { t } = useTranslation();
  if (compare(previous, 0) <= 0) {
    return null;
  }
  const change = (Number(current) - Number(previous)) / Number(previous);
  const pct = Math.round(Math.abs(change) * 100);
  const up = change >= 0;
  return (
    <span className={cn("font-medium", up ? "text-success" : "text-danger")} data-testid="sales-delta">
      {up ? "▲" : "▼"} {formatNumber(pct)}% {t("dashboard.today.vsLastMonth")}
    </span>
  );
}

/**
 * The last thirty days of net sales as thin bars from one baseline, today in the accent and the rest in the quiet
 * gray; a bar's day and amount show on hover and focus, and the same numbers sit in a table for assistive technology.
 */
function Sparkline({ days, currency }: { days: Day[]; currency: string }) {
  const { t } = useTranslation();
  const tableId = useId();
  const [hovered, setHovered] = useState<number | null>(null);
  const max = Math.max(...days.map((d) => Number(d.amount)), 0);
  const width = 240;
  const height = 40;
  const gap = 2;
  const slot = width / days.length;
  const bar = Math.max(2, Math.min(24, slot - gap));
  const shown = hovered === null ? days[days.length - 1] : days[hovered];
  return (
    <div className="mt-3" data-testid="sales-trend">
      <svg viewBox={`0 0 ${width} ${height}`} className="h-10 w-full" role="img" aria-describedby={tableId} aria-label={t("dashboard.today.trend")} onMouseLeave={() => { setHovered(null); }}>
        <line x1="0" x2={width} y1={height - 0.5} y2={height - 0.5} className="stroke-border" strokeWidth="1" />
        {days.map((d, i) => {
          const amount = Number(d.amount);
          const h = max > 0 ? Math.max(amount > 0 ? 2 : 0, (amount / max) * (height - 2)) : 0;
          const isToday = i === days.length - 1;
          return (
            <g key={d.day} onMouseEnter={() => { setHovered(i); }}>
              {/* The hit target is the whole slot, taller than the bar. */}
              <rect x={i * slot} y="0" width={slot} height={height} fill="transparent" />
              {h > 0 ? (
                <path
                  d={`M${(i * slot + (slot - bar) / 2).toFixed(2)} ${height - 1} v${-(h - 2)} a2 2 0 0 1 2 -2 h${(bar - 4).toFixed(2)} a2 2 0 0 1 2 2 v${h - 2} z`}
                  className={cn(isToday || hovered === i ? "fill-accent" : "fill-fg-subtle/40")}
                />
              ) : null}
            </g>
          );
        })}
      </svg>
      <div className="mt-1 text-xs text-fg-muted" aria-live="polite" data-testid="sales-trend-caption">
        {shown ? `${formatDate(shown.day)} · ${formatMoney(shown.amount, currency)}` : null}
      </div>
      <table id={tableId} className="sr-only">
        <caption>{t("dashboard.today.trend")}</caption>
        <tbody>
          {days.map((d) => (
            <tr key={d.day}><th scope="row">{formatDate(d.day)}</th><td>{formatMoney(d.amount, currency)}</td></tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Today in the chosen company: what was sold, the money in the bank and the cash boxes, what is past due to and from
 * the company, the approvals waiting for the person, and the documents waiting for their next step. Every number
 * opens the screen it was counted from; a card shows only when the person may read what it counts.
 */
function Today() {
  const { t } = useTranslation();
  const can = useCan();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const on = (permission: string): boolean => Boolean(companyId) && can(permission);
  const asOf = today();

  const home = useQuery({
    queryKey: ["home", companyId, asOf],
    enabled: Boolean(companyId),
    refetchInterval: 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/home", { params: { query: { companyId, asOf } } })),
  });
  const approvals = useQuery({ queryKey: ["approvals", "inbox"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/requests")) });
  const late = useQuery({
    queryKey: ["open-order-lines", companyId, "late"],
    enabled: on("purchasing.order.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/reports/open-order-lines", { params: { query: { companyId, lateOnly: true } } })),
  });
  const blocked = useQuery({
    queryKey: ["invoices", companyId, "blocked"],
    enabled: on("purchasing.invoice.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices", { params: { query: { companyId, status: "blocked" } } })),
  });
  const reorder = useQuery({
    queryKey: ["suggestions", companyId, "", "open"],
    enabled: on("inventory.replenishment.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/suggestions", { params: { query: { companyId, status: "open" } } })),
  });
  const idle = useQuery({
    queryKey: ["slow-moving", companyId, "", asOf, 90],
    enabled: on("inventory.stock.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/slow-moving", { params: { query: { companyId, idleDays: 90, asOf } } })),
  });

  const data: Home | undefined = home.data;
  const currency = data?.functionalCurrency ?? company?.functionalCurrency ?? "";
  const pending = (approvals.data ?? []).filter((r) => r.status === "pending");
  const sales = data?.sales ?? null;
  const cash = data?.cash ?? null;
  const receivables = data?.receivables ?? null;
  const payables = data?.payables ?? null;
  const pipeline = data?.pipeline ?? null;
  const money = (amount: string | number): string => formatMoney(amount, currency);

  return (
    <>
      <SetupCard companyId={companyId} />
      <section className="mb-8" aria-labelledby="work-today">
        <div className="mb-3 flex flex-wrap items-end justify-between gap-3">
          <h2 id="work-today" className="text-lg font-semibold">{t("dashboard.work.title")}</h2>
          {companies.length > 1 ? <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} /> : null}
        </div>
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4" data-testid="work-today">
          {sales ? (
            <Stat
              label={t("dashboard.today.sales")}
              value={money(sales.today)}
              to="/sales/invoices"
              hint={
                <>
                  <span>{t("dashboard.today.monthToDate", { amount: money(sales.monthToDate) })}</span>
                  {" "}
                  <Delta current={String(sales.monthToDate)} previous={String(sales.previousMonthToDate)} />
                  <br />
                  {t("dashboard.today.invoicesToday", { count: sales.invoicesToday })}
                  {Number(sales.draftInvoices) > 0 ? ` · ${t("dashboard.today.drafts", { count: Number(sales.draftInvoices) })}` : ""}
                </>
              }
              testId="work-sales"
            >
              <Sparkline days={sales.days} currency={currency} />
            </Stat>
          ) : null}
          {cash ? (
            <Stat
              label={t("dashboard.work.cash")}
              value={money(cash.total)}
              to="/banking/bank-accounts"
              hint={`${t("dashboard.today.received", { amount: money(cash.receivedMonthToDate) })} · ${t("dashboard.today.paid", { amount: money(cash.paidMonthToDate) })}`}
              testId="work-cash"
            >
              {cash.accounts.length > 0 ? (
                <ul className="mt-3 flex flex-col gap-1 text-xs" data-testid="cash-accounts">
                  {cash.accounts.slice(0, 5).map((a) => (
                    <li key={a.id} className="flex justify-between gap-2">
                      <span className="truncate text-fg-muted">{localized(a.name)}</span>
                      <span dir="ltr">{formatMoney(a.balanceTc, a.currency)}</span>
                    </li>
                  ))}
                  {cash.accounts.length > 5 ? <li className="text-fg-subtle">{t("dashboard.today.moreAccounts", { count: cash.accounts.length - 5 })}</li> : null}
                </ul>
              ) : null}
            </Stat>
          ) : null}
          {receivables ? (
            <Stat
              label={t("dashboard.today.receivablesOverdue")}
              value={money(receivables.overdue)}
              to="/receivables/open-items"
              tone={compare(String(receivables.overdue), 0) > 0 ? "attention" : undefined}
              hint={`${t("dashboard.today.customers", { count: receivables.overdueCustomers })} · ${t("dashboard.today.dueSoon", { amount: money(receivables.dueSoon) })} · ${t("dashboard.today.inAll", { amount: money(receivables.total) })}`}
              testId="work-receivables"
            />
          ) : null}
          {payables ? (
            <Stat
              label={t("dashboard.work.overduePayables")}
              value={money(payables.overdue)}
              to="/payables/open-items"
              tone={compare(String(payables.overdue), 0) > 0 ? "attention" : undefined}
              hint={`${t("dashboard.today.suppliers", { count: payables.overdueSuppliers })} · ${t("dashboard.today.dueSoon", { amount: money(payables.dueSoon) })} · ${t("dashboard.today.inAll", { amount: money(payables.total) })}`}
              testId="work-overdue"
            />
          ) : null}
          <Stat label={t("dashboard.work.approvals")} value={formatNumber(pending.length)} to="/approvals" tone={pending.length > 0 ? "attention" : undefined} testId="work-approvals" />
        </div>
      </section>

      <section className="mb-8" aria-labelledby="next-step">
        <h2 id="next-step" className="mb-3 text-lg font-semibold">{t("dashboard.next.title")}</h2>
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5" data-testid="next-step">
          {pipeline?.openQuotations !== null && pipeline?.openQuotations !== undefined ? <Stat label={t("dashboard.next.openQuotations")} value={formatNumber(pipeline.openQuotations)} to="/sales/quotations" testId="next-quotations" /> : null}
          {pipeline?.ordersToShip !== null && pipeline?.ordersToShip !== undefined ? <Stat label={t("dashboard.next.ordersToShip")} value={formatNumber(pipeline.ordersToShip)} to="/sales/orders" tone={Number(pipeline.ordersToShip) > 0 ? "attention" : undefined} testId="next-orders" /> : null}
          {pipeline?.draftShipments !== null && pipeline?.draftShipments !== undefined ? <Stat label={t("dashboard.next.draftShipments")} value={formatNumber(pipeline.draftShipments)} to="/sales/shipments" testId="next-shipments" /> : null}
          {sales ? <Stat label={t("dashboard.next.draftInvoices")} value={formatNumber(sales.draftInvoices)} to="/sales/invoices" testId="next-invoices" /> : null}
          {pipeline?.openReturns !== null && pipeline?.openReturns !== undefined ? <Stat label={t("dashboard.next.openReturns")} value={formatNumber(pipeline.openReturns)} to="/sales/returns" testId="next-returns" /> : null}
          {late.data ? (
            <Stat label={t("dashboard.work.lateDeliveries")} value={formatNumber(late.data.lateLines)} to="/purchasing/open-lines" tone={Number(late.data.lateLines) > 0 ? "attention" : undefined} hint={late.data.totals.map((x) => formatMoney(x.amount, x.currency)).join(" · ") || undefined} testId="work-late" />
          ) : null}
          {blocked.data ? (
            <Stat label={t("dashboard.work.blockedInvoices")} value={formatNumber(blocked.data.length)} to="/purchasing/invoices" tone={blocked.data.length > 0 ? "attention" : undefined} testId="work-blocked" />
          ) : null}
          {reorder.data ? (
            <Stat label={t("dashboard.work.reorder")} value={formatNumber(reorder.data.length)} to="/inventory/replenishment" testId="work-reorder" />
          ) : null}
          {idle.data ? (
            <Stat
              label={t("dashboard.work.idleStock")}
              value={idle.data.totalValue !== null && currency ? formatMoney(idle.data.totalValue, currency) : formatNumber(idle.data.rows.length)}
              hint={t("dashboard.work.idleLines", { count: idle.data.rows.length })}
              to="/inventory/slow-moving"
              testId="work-idle"
            />
          ) : null}
        </div>
      </section>
    </>
  );
}

/** The dashboards pinned to the home page (founder programme item 9d), each run for the selected company over its default period. */
function HomeDashboards() {
  const { t } = useTranslation();
  const { companyId } = useCompanyContext();
  const pinned = useDashboards(companyId, true);
  if (!companyId || !pinned.data || pinned.data.length === 0) {
    return null;
  }
  return (
    <div className="mb-6 flex flex-col gap-6" data-testid="home-dashboards">
      {pinned.data.map((d) => <HomeDashboard key={d.id} dashboard={d} companyId={companyId} />)}
    </div>
  );
  function HomeDashboard({ dashboard, companyId: company }: { dashboard: Dashboard; companyId: string }) {
    const run = useDashboardRun(dashboard.id, company, dashboard.defaultPeriod, "", "", dashboard.refreshSeconds);
    return (
      <section aria-labelledby={`home-dashboard-${dashboard.id}`}>
        <div className="mb-3 flex items-baseline justify-between gap-2">
          <h2 id={`home-dashboard-${dashboard.id}`} className="text-lg font-semibold" dir="auto">{localized(dashboard.name)}</h2>
          <Link to="/dashboards/view" search={{ dashboard: dashboard.id }} className="text-sm text-accent hover:underline">{t("dashboards.open")}</Link>
        </div>
        {run.data ? <DashboardGrid run={run.data} /> : null}
      </section>
    );
  }
}

export function DashboardPage() {
  const { t } = useTranslation();
  const session = useSession();
  const companies = useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
  const members = useQuery({ queryKey: ["members"], queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
  const unread = useQuery({ queryKey: ["notifications", "unread-count"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/notifications/unread-count")) });
  const jobs = useQuery({ queryKey: ["jobs", "queued"], queryFn: async () => unwrap(await api.GET("/api/v1/platform/jobs", { params: { query: { state: "queued", limit: 100 } } })) });

  return (
    <>
      <PageHeader title={t("dashboard.title", { name: session?.user.displayName ?? "" })} description={t("dashboard.description", { tenant: session?.tenant.name ?? "" })} />
      <Today />
      <HomeDashboards />
      <h2 className="mb-3 text-lg font-semibold">{t("dashboard.workspace")}</h2>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label={t("nav.companies")} value={formatNumber(companies.data?.length ?? 0)} to="/companies" />
        <Stat label={t("nav.members")} value={formatNumber(members.data?.length ?? 0)} to="/members" />
        <Stat label={t("dashboard.unreadNotifications")} value={formatNumber(unread.data?.count ?? 0)} to="/notifications" />
        <Stat label={t("dashboard.queuedJobs")} value={formatNumber(jobs.data?.length ?? 0)} to="/jobs" />
      </div>
      <p className="mt-6 text-sm text-fg-muted">{t("dashboard.hint")}</p>
    </>
  );
}

