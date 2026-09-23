import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatNumber } from "../lib/format";
import { useSession } from "../session/session";
import { PageHeader } from "./common";
function Stat({ label, value, to }) {
    return (_jsxs(Link, { to: to, className: "rounded-lg border border-border bg-surface p-4 shadow-sm transition-colors hover:bg-surface-sunken", children: [_jsx("div", { className: "text-sm text-fg-muted", children: label }), _jsx("div", { className: "mt-1 text-2xl font-semibold tabular", children: value })] }));
}
export function DashboardPage() {
    const { t } = useTranslation();
    const session = useSession();
    const companies = useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
    const members = useQuery({ queryKey: ["members"], queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
    const unread = useQuery({ queryKey: ["notifications", "unread-count"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/notifications/unread-count")) });
    const jobs = useQuery({ queryKey: ["jobs", "queued"], queryFn: async () => unwrap(await api.GET("/api/v1/platform/jobs", { params: { query: { state: "queued", limit: 100 } } })) });
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("dashboard.title", { name: session?.user.displayName ?? "" }), description: t("dashboard.description", { tenant: session?.tenant.name ?? "" }) }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2 lg:grid-cols-4", children: [_jsx(Stat, { label: t("nav.companies"), value: formatNumber(companies.data?.length ?? 0), to: "/companies" }), _jsx(Stat, { label: t("nav.members"), value: formatNumber(members.data?.length ?? 0), to: "/members" }), _jsx(Stat, { label: t("dashboard.unreadNotifications"), value: formatNumber(unread.data?.count ?? 0), to: "/notifications" }), _jsx(Stat, { label: t("dashboard.queuedJobs"), value: formatNumber(jobs.data?.length ?? 0), to: "/jobs" })] }), _jsx("p", { className: "mt-6 text-sm text-fg-muted", children: t("dashboard.hint") })] }));
}
