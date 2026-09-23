import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatNumber, localized } from "../lib/format";
import { PageHeader } from "./common";
/** Roles and their grants (read-only in the shell; editing arrives with the role designer in the identity screens). */
export function RolesPage() {
    const { t } = useTranslation();
    const [selected, setSelected] = useState(null);
    const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("roles.code"), size: 140 },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("roles.name"), size: 220 },
        { id: "description", accessorKey: "description", header: t("roles.descriptionColumn"), size: 300 },
        { id: "grants", accessorFn: (row) => row.grants.length, header: t("roles.grants"), size: 100, cell: ({ row }) => _jsx("span", { className: "tabular", children: formatNumber(row.original.grants.length) }) },
        { id: "isSystem", accessorKey: "isSystem", header: t("roles.kind"), size: 110, cell: ({ row }) => _jsx(Badge, { tone: row.original.isSystem ? "accent" : "neutral", children: row.original.isSystem ? t("roles.system") : t("roles.custom") }) },
    ], [t]);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.roles"), description: t("roles.description") }), _jsx(DataGrid, { label: "nav.roles", columns: columns, data: roles.data ?? [], rowKey: (row) => row.id, loading: roles.isPending, onOpen: setSelected, emptyTitle: t("roles.emptyTitle") }), _jsx(Dialog, { open: selected !== null, onOpenChange: (open) => { if (!open) {
                    setSelected(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: selected ? (_jsxs(_Fragment, { children: [_jsxs(DialogHeader, { children: [_jsxs(DialogTitle, { className: "text-lg font-semibold", children: [selected.code, " \u00B7 ", localized(selected.name)] }), _jsx(DialogDescription, { className: "text-sm text-fg-muted", children: selected.description })] }), _jsx("ul", { className: "flex max-h-80 flex-wrap gap-1.5 overflow-y-auto", "aria-label": t("roles.grants"), children: selected.grants.map((grant) => (_jsx("li", { children: _jsx(Badge, { tone: "neutral", dir: "ltr", children: grant }) }, grant))) })] })) : null }) })] }));
}
