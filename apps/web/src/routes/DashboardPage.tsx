import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { add, compare } from "../lib/decimal";
import { formatMoney, formatNumber } from "../lib/format";
import { useCan } from "../lib/permissions";
import { useSession } from "../session/session";
import { today } from "./accounting/shared";
import { PageHeader } from "./common";
import { CompanyFilter, useCompanyContext } from "./inventory/shared";

function Stat({ label, value, to, hint, tone, testId }: { label: string; value: string; to: string; hint?: string | undefined; tone?: "attention" | undefined; testId?: string | undefined }) {
  return (
    <Link to={to} className="rounded-lg border border-border bg-surface p-4 shadow-sm transition-colors hover:bg-surface-sunken" data-testid={testId}>
      <div className="text-sm text-fg-muted">{label}</div>
      <div className={`mt-1 text-2xl font-semibold tabular ${tone === "attention" ? "text-danger" : ""}`} dir="ltr" data-testid={testId ? `${testId}-value` : undefined}>{value}</div>
      {hint ? <div className="mt-1 text-xs text-fg-muted">{hint}</div> : null}
    </Link>
  );
}

/**
 * What needs doing today in the chosen company, from the screens that hold it: approvals waiting for the person,
 * deliveries past their date, supplier invoices on hold, what is overdue to suppliers, the money in the bank and the
 * items the planner says to reorder. A card shows only when the person may read what it counts; each opens its screen.
 */
function WorkToday() {
  const { t } = useTranslation();
  const can = useCan();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const on = (permission: string): boolean => Boolean(companyId) && can(permission);

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
  const aging = useQuery({
    queryKey: ["aging", companyId, today(), ""],
    enabled: on("payables.open_item.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/aging", { params: { query: { companyId, asOf: today() } } })),
  });
  const banks = useQuery({
    queryKey: ["bank-accounts", companyId],
    enabled: on("banking.bank_account.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })),
  });
  const reorder = useQuery({
    queryKey: ["suggestions", companyId, "", "open"],
    enabled: on("inventory.replenishment.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/suggestions", { params: { query: { companyId, status: "open" } } })),
  });

  const idle = useQuery({
    queryKey: ["slow-moving", companyId, "", today(), 90],
    enabled: on("inventory.stock.read"),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/slow-moving", { params: { query: { companyId, idleDays: 90, asOf: today() } } })),
  });

  const pending = (approvals.data ?? []).filter((r) => r.status === "pending");
  const totals = aging.data?.totals;
  const overdue = totals ? add(totals.days1To30, totals.days31To60, totals.days61To90, totals.over90) : null;
  const activeBanks = (banks.data ?? []).filter((b) => b.isActive);
  const functional = company?.functionalCurrency ?? activeBanks[0]?.functionalCurrency ?? aging.data?.functionalCurrency ?? "";
  const cash = add(...activeBanks.map((b) => b.balanceFc));

  return (
    <section className="mb-8" aria-labelledby="work-today">
      <div className="mb-3 flex flex-wrap items-end justify-between gap-3">
        <h2 id="work-today" className="text-lg font-semibold">{t("dashboard.work.title")}</h2>
        {companies.length > 1 ? <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} /> : null}
      </div>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" data-testid="work-today">
        <Stat label={t("dashboard.work.approvals")} value={formatNumber(pending.length)} to="/approvals" tone={pending.length > 0 ? "attention" : undefined} testId="work-approvals" />
        {late.data ? (
          <Stat label={t("dashboard.work.lateDeliveries")} value={formatNumber(late.data.lateLines)} to="/purchasing/open-lines" tone={Number(late.data.lateLines) > 0 ? "attention" : undefined} hint={late.data.totals.map((x) => formatMoney(x.amount, x.currency)).join(" · ") || undefined} testId="work-late" />
        ) : null}
        {blocked.data ? (
          <Stat label={t("dashboard.work.blockedInvoices")} value={formatNumber(blocked.data.length)} to="/purchasing/invoices" tone={blocked.data.length > 0 ? "attention" : undefined} testId="work-blocked" />
        ) : null}
        {aging.data && overdue !== null ? (
          <Stat label={t("dashboard.work.overduePayables")} value={formatMoney(overdue, aging.data.functionalCurrency)} to="/payables/open-items" tone={compare(overdue, 0) > 0 ? "attention" : undefined} hint={t("dashboard.work.totalPayables", { amount: formatMoney(aging.data.totals.totalFc, aging.data.functionalCurrency) })} testId="work-overdue" />
        ) : null}
        {banks.data && functional ? (
          <Stat label={t("dashboard.work.cash")} value={formatMoney(cash, functional)} to="/banking/bank-accounts" hint={t("dashboard.work.accounts", { count: activeBanks.length })} testId="work-cash" />
        ) : null}
        {reorder.data ? (
          <Stat label={t("dashboard.work.reorder")} value={formatNumber(reorder.data.length)} to="/inventory/replenishment" testId="work-reorder" />
        ) : null}
        {idle.data ? (
          <Stat
            label={t("dashboard.work.idleStock")}
            value={idle.data.totalValue !== null && functional ? formatMoney(idle.data.totalValue, functional) : formatNumber(idle.data.rows.length)}
            hint={t("dashboard.work.idleLines", { count: idle.data.rows.length })}
            to="/inventory/slow-moving"
            testId="work-idle"
          />
        ) : null}
      </div>
    </section>
  );
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
      <WorkToday />
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
