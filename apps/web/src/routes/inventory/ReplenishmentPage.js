import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Play } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, KeyValues, Qty, WarehouseSelect, plain, useCompanyContext, useWarehouses } from "./shared";
const statuses = ["open", "accepted", "dismissed", "superseded", "all"];
/** Replenishment (roadmap 3.7): run the planner, read why each suggestion exists, accept it (the purchase order lands with M4) or dismiss it with a reason. */
export function ReplenishmentPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [warehouseId, setWarehouseId] = useState("");
    const [status, setStatus] = useState("open");
    const [asOf, setAsOf] = useState(today());
    const [quantity, setQuantity] = useState("");
    const [note, setNote] = useState("");
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const suggestions = useQuery({
        queryKey: ["suggestions", companyId, warehouseId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/suggestions", { params: { query: { companyId, status, ...(warehouseId ? { warehouseId } : {}) } } })),
    });
    const runs = useQuery({
        queryKey: ["replenishment-runs", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/runs", { params: { query: { companyId } } })),
    });
    const detail = suggestions.data?.find((s) => s.id === openId);
    const open = (id) => { setQuantity(""); setNote(""); void navigate({ to: "/inventory/replenishment", search: id ? { open: id } : {} }); };
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["suggestions"] });
        await queryClient.invalidateQueries({ queryKey: ["replenishment-runs"] });
    };
    const run = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/replenishment/run", { body: { companyId, warehouseId: warehouseId || null, asOf: asOf || null } })),
        onSuccess: async () => { setProblem(null); await refresh(); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const decide = useMutation({
        mutationFn: async (action) => {
            const suggestionId = openId ?? "";
            return action === "accept"
                ? unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/accept", { params: { path: { suggestionId } }, body: { quantity: quantity || null, note: note || null } }))
                : unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/dismiss", { params: { path: { suggestionId } }, body: { reason: note } }));
        },
        onSuccess: async () => { setProblem(null); await refresh(); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 220 },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
        { id: "qty", accessorKey: "suggestedQty", header: t("inventory.replenishment.suggested"), size: 120, cell: ({ row }) => _jsx(Qty, { value: row.original.suggestedQty, uom: row.original.baseUom }) },
        { id: "neededBy", accessorKey: "neededBy", header: t("inventory.replenishment.neededBy"), size: 120, cell: ({ row }) => formatDate(row.original.neededBy) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.replenishment"), description: t("inventory.replenishment.description"), actions: _jsxs(Button, { onClick: () => { run.mutate(); }, disabled: !companyId, loading: run.isPending, "data-testid": "run-planner", children: [_jsx(Play, { "aria-hidden": "true" }), t("inventory.replenishment.runNow")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-4", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: warehouseId, onChange: setWarehouseId, allowAll: true }), _jsx(Field, { label: t("inventory.replenishment.asOf"), children: _jsx(TextField, { type: "date", value: asOf, onChange: (e) => { setAsOf(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s === "all" ? t("accounting.anyStatus") : t(`inventory.statuses.${s}`) }, s))) }) })] }), _jsx(FormError, { message: problem && !openId ? problem.message : null }), _jsx(DataGrid, { label: "nav.replenishment", columns: columns, data: suggestions.data ?? [], rowKey: (row) => row.id, loading: suggestions.isPending && Boolean(companyId), emptyTitle: t("inventory.replenishment.emptyTitle"), emptyDescription: t("inventory.replenishment.emptyDescription"), onOpen: (row) => { open(row.id); }, height: 400 }), _jsxs("section", { className: "mt-6", children: [_jsx("h2", { className: "mb-2 text-base font-semibold", children: t("inventory.replenishment.runs") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.replenishment.ranAt") }), _jsx(TableHead, { children: t("inventory.replenishment.asOf") }), _jsx(TableHead, { className: "text-end", children: t("inventory.replenishment.checked") }), _jsx(TableHead, { className: "text-end", children: t("inventory.replenishment.created") }), _jsx(TableHead, { className: "text-end", children: t("inventory.replenishment.refreshed") }), _jsx(TableHead, { className: "text-end", children: t("inventory.replenishment.closed") })] }) }), _jsx(TableBody, { children: (runs.data ?? []).map((r) => (_jsxs(TableRow, { "data-testid": "planner-run", children: [_jsx(TableCell, { children: formatDateTime(r.ranAt) }), _jsx(TableCell, { children: formatDate(r.asOf) }), _jsx(TableNumberCell, { children: String(r.itemsChecked) }), _jsx(TableNumberCell, { children: String(r.suggestionsCreated) }), _jsx(TableNumberCell, { children: String(r.suggestionsRefreshed) }), _jsx(TableNumberCell, { children: String(r.suggestionsClosed) })] }, r.id))) })] })] }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.itemCode} · ${localized(detail.itemName)} · ${detail.warehouseCode}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "suggestion-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: detail.status }), _jsxs("span", { children: [t("inventory.replenishment.suggested"), ": ", _jsx(Qty, { value: detail.suggestedQty, uom: detail.baseUom })] }), detail.neededBy ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.replenishment.neededBy"), ": ", formatDate(detail.neededBy)] }) : null, detail.decisionNote ? _jsx("span", { className: "text-fg-muted", children: detail.decisionNote }) : null] }), _jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.replenishment.explanation") }), _jsx(KeyValues, { entries: Object.entries(detail.explanation).map(([key, value]) => [t(`inventory.replenishment.why.${key}`, { defaultValue: key }), plain(value)]) })] }), _jsx(FormError, { message: problem?.message ?? null }), detail.status === "open" ? (_jsxs("div", { className: "grid gap-3 sm:grid-cols-2", children: [_jsx(Field, { label: t("inventory.replenishment.acceptedQty"), children: _jsx(TextField, { inputMode: "decimal", value: quantity, onChange: (e) => { setQuantity(e.target.value); }, dir: "ltr", placeholder: String(detail.suggestedQty) }) }), _jsx(Field, { label: t("inventory.replenishment.note"), children: _jsx(TextField, { value: note, onChange: (e) => { setNote(e.target.value); }, "data-testid": "decision-note" }) })] })) : null, _jsx(DialogFooter, { children: detail.status === "open" ? (_jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { decide.mutate("dismiss"); }, loading: decide.isPending, "data-testid": "dismiss-suggestion", children: t("inventory.replenishment.dismiss") }), _jsx(Button, { onClick: () => { decide.mutate("accept"); }, loading: decide.isPending, "data-testid": "accept-suggestion", children: t("inventory.replenishment.accept") })] })) : null })] })) : null] }) })] }));
}
