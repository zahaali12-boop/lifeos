import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Select, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { RefreshCw } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime, formatNumber } from "../lib/format";
import { PageHeader } from "./common";
const tones = { succeeded: "success", running: "accent", queued: "neutral", failed: "warning", dead: "danger" };
/** Background work: the job queue with retry/cancel, and the tenant's schedules. */
export function JobsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [state, setState] = useState("");
    const jobs = useQuery({ queryKey: ["jobs", state], queryFn: async () => unwrap(await api.GET("/api/v1/platform/jobs", { params: { query: { limit: 500, ...(state ? { state } : {}) } } })), refetchInterval: 15_000 });
    const schedules = useQuery({ queryKey: ["schedules"], queryFn: async () => unwrap(await api.GET("/api/v1/platform/schedules")) });
    const retry = useMutation({ mutationFn: async (id) => unwrap(await api.POST("/api/v1/platform/jobs/{jobId}/retry", { params: { path: { jobId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["jobs"] }) });
    const cancel = useMutation({ mutationFn: async (id) => unwrap(await api.POST("/api/v1/platform/jobs/{jobId}/cancel", { params: { path: { jobId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["jobs"] }) });
    const columns = useMemo(() => [
        { id: "type", accessorKey: "type", header: t("jobs.type"), size: 220, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.type }) },
        { id: "state", accessorKey: "state", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(Badge, { tone: tones[row.original.state ?? "queued"] ?? "neutral", children: t(`jobs.states.${row.original.state ?? "queued"}`) }) },
        { id: "attempts", accessorFn: (row) => `${row.attempts ?? 0}/${row.maxAttempts ?? 0}`, header: t("jobs.attempts"), size: 100, cell: ({ row }) => _jsxs("span", { className: "tabular", children: [formatNumber(row.original.attempts ?? 0), "/", formatNumber(row.original.maxAttempts ?? 0)] }) },
        { id: "createdAt", accessorKey: "createdAt", header: t("common.created"), size: 170, cell: ({ row }) => formatDateTime(row.original.createdAt) },
        { id: "finishedAt", accessorKey: "finishedAt", header: t("jobs.finished"), size: 170, cell: ({ row }) => formatDateTime(row.original.finishedAt) },
        { id: "error", accessorKey: "error", header: t("jobs.error"), size: 300, cell: ({ row }) => _jsx("span", { className: "truncate text-danger", children: row.original.error }) },
    ], [t]);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.jobs"), description: t("jobs.description"), actions: _jsxs(Button, { variant: "secondary", onClick: () => { void jobs.refetch(); }, loading: jobs.isFetching, children: [_jsx(RefreshCw, { "aria-hidden": "true" }), t("common.refresh")] }) }), _jsx(DataGrid, { label: "nav.jobs", columns: columns, data: jobs.data ?? [], rowKey: (row) => row.id ?? "", selectable: true, loading: jobs.isPending, emptyTitle: t("jobs.emptyTitle"), toolbar: _jsxs(Select, { value: state, onChange: (e) => { setState(e.target.value); }, "aria-label": t("common.status"), className: "w-40", children: [_jsx("option", { value: "", children: t("jobs.allStates") }), ["queued", "running", "succeeded", "failed", "dead"].map((s) => (_jsx("option", { value: s, children: t(`jobs.states.${s}`) }, s)))] }), bulkActions: (selected, clear) => (_jsxs(_Fragment, { children: [_jsx(Button, { size: "sm", variant: "secondary", onClick: () => { selected.forEach((id) => { retry.mutate(id); }); clear(); }, children: t("jobs.retry") }), _jsx(Button, { size: "sm", variant: "secondary", onClick: () => { selected.forEach((id) => { cancel.mutate(id); }); clear(); }, children: t("jobs.cancel") })] })) }), _jsx("h2", { className: "mb-2 mt-8 text-sm font-semibold uppercase tracking-wide text-fg-muted", children: t("jobs.schedules") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("jobs.scheduleCode") }), _jsx(TableHead, { children: t("jobs.type") }), _jsx(TableHead, { children: t("jobs.cron") }), _jsx(TableHead, { children: t("jobs.timeZone") }), _jsx(TableHead, { children: t("jobs.nextRun") }), _jsx(TableHead, { children: t("common.status") })] }) }), _jsxs(TableBody, { children: [(schedules.data ?? []).map((schedule) => (_jsxs(TableRow, { children: [_jsx(TableCell, { className: "font-medium", dir: "ltr", children: schedule.code }), _jsx(TableCell, { dir: "ltr", children: schedule.jobType }), _jsx(TableCell, { dir: "ltr", children: schedule.cron }), _jsx(TableCell, { dir: "ltr", children: schedule.timeZone }), _jsx(TableCell, { children: formatDateTime(schedule.nextRunAt) }), _jsx(TableCell, { children: _jsx(Badge, { tone: schedule.enabled ? "success" : "neutral", children: schedule.enabled ? t("common.active") : t("common.inactive") }) })] }, schedule.id))), (schedules.data ?? []).length === 0 ? (_jsx(TableRow, { children: _jsx(TableCell, { colSpan: 6, className: "text-center text-fg-muted", children: t("jobs.noSchedules") }) })) : null] })] })] }));
}
