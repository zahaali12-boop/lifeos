import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";
/** Valuation (roadmap 3.3): what the stock is worth at any date, by item and warehouse, equal to the inventory accounts; and the cost adjustment runs the engine made. */
export function ValuationPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId, company } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [warehouseId, setWarehouseId] = useState("");
    const [asOf, setAsOf] = useState(today());
    const [includeZero, setIncludeZero] = useState(false);
    const report = useQuery({
        queryKey: ["valuation", companyId, warehouseId, asOf, includeZero],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/valuation", { params: { query: { companyId, asOf, includeZero, ...(warehouseId ? { warehouseId } : {}) } } })),
    });
    const runs = useQuery({
        queryKey: ["cost-runs", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/runs", { params: { query: { companyId, limit: 50 } } })),
    });
    const runNow = useMutation({
        mutationFn: async (runId) => unwrap(await api.POST("/api/v1/inventory/costing/runs/{runId}/run", { params: { path: { runId } } })),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ["cost-runs"] });
            await queryClient.invalidateQueries({ queryKey: ["valuation"] });
        },
    });
    const columns = useMemo(() => [
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 220 },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
        { id: "quantity", accessorKey: "quantity", header: t("inventory.quantity"), size: 120, cell: ({ row }) => _jsx(Qty, { value: row.original.quantity, uom: row.original.baseUom }) },
        { id: "average", accessorKey: "averageUnitCost", header: t("inventory.valuation.averageCost"), size: 130, cell: ({ row }) => _jsx(Amount, { value: row.original.averageUnitCost }) },
        { id: "actual", accessorKey: "actual", header: t("inventory.valuation.actual"), size: 130, cell: ({ row }) => _jsx(Amount, { value: row.original.actual }) },
        { id: "expected", accessorKey: "expected", header: t("inventory.valuation.expected"), size: 130, cell: ({ row }) => _jsx(Amount, { value: row.original.expected }) },
        { id: "value", accessorKey: "value", header: t("inventory.valuation.value"), size: 140, cell: ({ row }) => _jsx(Amount, { value: row.original.value }) },
    ], [t]);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.valuation"), description: t("inventory.valuation.description") }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-4", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: warehouseId, onChange: setWarehouseId, allowAll: true }), _jsx(Field, { label: t("inventory.valuation.asOf"), children: _jsx(TextField, { type: "date", value: asOf, onChange: (e) => { setAsOf(e.target.value); }, dir: "ltr", "data-testid": "valuation-date" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end pb-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: includeZero, onChange: (e) => { setIncludeZero(e.target.checked); } }), t("inventory.valuation.includeZero")] })] }), report.data ? (_jsxs("div", { className: "mb-3 flex flex-wrap gap-4 text-sm", "data-testid": "valuation-totals", children: [_jsxs("span", { children: [t("inventory.valuation.totalValue"), ": ", _jsx("strong", { children: _jsx(Amount, { value: report.data.totalValue }) }), " ", company?.functionalCurrency] }), _jsxs("span", { className: "text-fg-muted", children: [t("inventory.valuation.totalActual"), ": ", _jsx(Amount, { value: report.data.totalActual })] }), _jsxs("span", { className: "text-fg-muted", children: [t("inventory.valuation.totalExpected"), ": ", _jsx(Amount, { value: report.data.totalExpected })] }), _jsx("span", { className: "text-fg-muted", children: formatDate(report.data.asOf) })] })) : null, _jsx(FormError, { message: report.isError ? t("accounting.loadFailed") : null }), _jsx(DataGrid, { label: "nav.valuation", columns: columns, data: report.data?.lines ?? [], rowKey: (row) => `${row.itemId}:${row.warehouseId}`, loading: report.isPending && Boolean(companyId), emptyTitle: t("inventory.valuation.emptyTitle"), emptyDescription: t("inventory.valuation.emptyDescription"), height: 420 }), _jsxs("section", { className: "mt-6", children: [_jsx("h2", { className: "mb-2 text-base font-semibold", children: t("inventory.valuation.runs") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.valuation.started") }), _jsx(TableHead, { children: t("inventory.valuation.trigger") }), _jsx(TableHead, { children: t("inventory.item") }), _jsx(TableHead, { className: "text-end", children: t("inventory.valuation.walked") }), _jsx(TableHead, { className: "text-end", children: t("inventory.valuation.reapplied") }), _jsx(TableHead, { className: "text-end", children: t("inventory.valuation.adjusted") }), _jsx(TableHead, { children: t("common.status") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: (runs.data?.items ?? []).map((r) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: formatDateTime(r.startedAt) }), _jsxs(TableCell, { children: [t(`inventory.valuation.triggerKinds.${r.triggerKind}`, { defaultValue: r.triggerKind }), " \u00B7 ", t(`inventory.valuation.sourceDocuments.${r.triggerDocumentType}`, { defaultValue: r.triggerDocumentType })] }), _jsx(TableCell, { dir: "ltr", children: r.itemCode ?? r.itemId }), _jsx(TableNumberCell, { children: String(r.entriesWalked) }), _jsx(TableNumberCell, { children: String(r.entriesReapplied) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: r.amountAdjusted }) }), _jsxs(TableCell, { children: [_jsx(DocStatus, { status: r.status }), r.error ? _jsx("span", { className: "ms-2 text-danger", children: r.error }) : null] }), _jsx(TableCell, { children: r.status === "queued" || r.status === "failed" ? (_jsx(Button, { variant: "secondary", size: "sm", onClick: () => { runNow.mutate(r.id); }, loading: runNow.isPending, children: t("inventory.valuation.run") })) : null })] }, r.id))) })] })] })] }));
}
