import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatNumber } from "../lib/format";
import { useSession } from "../session/session";
import { PageHeader } from "./common";

function Stat({ label, value, to }: { label: string; value: string; to: string }) {
  return (
    <Link to={to} className="rounded-lg border border-border bg-surface p-4 shadow-sm transition-colors hover:bg-surface-sunken">
      <div className="text-sm text-fg-muted">{label}</div>
      <div className="mt-1 text-2xl font-semibold tabular">{value}</div>
    </Link>
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
