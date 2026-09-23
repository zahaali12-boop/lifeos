import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { Badge, Button, Input } from "@quicker/ui";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime } from "../lib/format";
import { PageHeader } from "./common";
const tones = { created: "success", updated: "accent", deleted: "danger" };
/** The audit explorer: newest first, cursor-paged, filtered by text, record type and action. */
export function AuditPage() {
    const { t } = useTranslation();
    const [q, setQ] = useState("");
    const [entityType, setEntityType] = useState("");
    const [action, setAction] = useState("");
    const events = useInfiniteQuery({
        queryKey: ["audit", q, entityType, action],
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/audit/events", { params: { query: { Limit: 100, ...(q ? { Q: q } : {}), ...(entityType ? { EntityType: entityType } : {}), ...(action ? { Action: action } : {}), ...(pageParam ? { Cursor: pageParam } : {}) } } })),
        initialPageParam: "",
        getNextPageParam: (last) => last.nextCursor ?? undefined,
    });
    const columns = useMemo(() => [
        { id: "seq", accessorKey: "seq", header: "#", size: 70, cell: ({ row }) => _jsx("span", { className: "tabular", children: String(row.original.seq) }) },
        { id: "occurredAt", accessorKey: "occurredAt", header: t("audit.when"), size: 170, cell: ({ row }) => formatDateTime(row.original.occurredAt) },
        { id: "actor", accessorFn: (row) => row.actor.display, header: t("audit.actor"), size: 180 },
        { id: "action", accessorKey: "action", header: t("audit.action"), size: 110, cell: ({ row }) => _jsx(Badge, { tone: tones[row.original.action] ?? "neutral", children: row.original.action }) },
        { id: "entityType", accessorKey: "entityType", header: t("audit.entityType"), size: 160, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.entityType }) },
        { id: "entityDisplay", accessorKey: "entityDisplay", header: t("audit.record"), size: 220 },
        { id: "reason", accessorKey: "reason", header: t("common.reason"), size: 200 },
    ], [t]);
    const rows = events.data?.pages.flatMap((page) => page.items) ?? [];
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.audit"), description: t("audit.description") }), _jsx(DataGrid, { label: "nav.audit", columns: columns, data: rows, rowKey: (row) => row.id, loading: events.isPending, emptyTitle: t("audit.emptyTitle"), toolbar: _jsxs(_Fragment, { children: [_jsx(Input, { type: "search", placeholder: t("audit.searchPlaceholder"), value: q, onChange: (e) => { setQ(e.target.value); }, className: "w-56", "aria-label": t("common.search") }), _jsx(Input, { placeholder: t("audit.entityType"), value: entityType, onChange: (e) => { setEntityType(e.target.value); }, className: "w-40", "aria-label": t("audit.entityType"), dir: "ltr" }), _jsx(Input, { placeholder: t("audit.action"), value: action, onChange: (e) => { setAction(e.target.value); }, className: "w-32", "aria-label": t("audit.action"), dir: "ltr" })] }) }), events.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void events.fetchNextPage(); }, loading: events.isFetchingNextPage, children: t("common.loadMore") })) : null] }));
}
