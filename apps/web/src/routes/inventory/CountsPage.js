import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useReasonCodes, useWarehouses } from "./shared";
const statuses = ["", "planned", "frozen", "counting", "review", "approved", "posted", "cancelled"];
const scopes = ["full", "cycle", "bins", "items"];
/** Counts (roadmap 3.6): plan, freeze, count (here or on the scanner), recount, review the variances with reasons, approve and post. */
export function CountsPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const reasons = useReasonCodes("count");
    const [status, setStatus] = useState("");
    const [editing, setEditing] = useState(null);
    const [entries, setEntries] = useState({});
    const [lineReasons, setLineReasons] = useState({});
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const counts = useQuery({
        queryKey: ["counts", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const sheet = useQuery({
        queryKey: ["count-sheet", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{countId}/sheet", { params: { path: { countId: openId ?? "" } } })),
    });
    const refresh = async (id) => {
        await queryClient.invalidateQueries({ queryKey: ["counts"] });
        if (id) {
            await queryClient.invalidateQueries({ queryKey: ["count-sheet", id] });
        }
    };
    const open = (id) => { setEntries({}); void navigate({ to: "/inventory/counts", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/inventory/counts", { body: { companyId, warehouseId: f.warehouseId, scope: f.scope, postingDate: f.postingDate || null, blind: f.blind, blockMovements: f.blockMovements, notes: f.notes || null } })),
        onSuccess: async (saved) => {
            setEditing(null);
            setProblem(null);
            await refresh(saved.id);
            open(saved.id);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const act = useMutation({
        mutationFn: async (action) => {
            const countId = openId ?? "";
            switch (action) {
                case "freeze":
                    return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/freeze", { params: { path: { countId } } }));
                case "review":
                    return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/review", { params: { path: { countId } } }));
                case "approve":
                    return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/approve", { params: { path: { countId } } }));
                case "post":
                    return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/post", { params: { path: { countId } } }));
                case "cancel":
                    return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/cancel", { params: { path: { countId } } }));
            }
        },
        onSuccess: async () => {
            setProblem(null);
            await refresh(openId);
            await queryClient.invalidateQueries({ queryKey: ["stock"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const enter = useMutation({
        mutationFn: async () => {
            const lines = sheet.data?.lines ?? [];
            const body = { entries: Object.entries(entries).filter(([, v]) => v.trim() !== "").map(([lineId, countedQty]) => ({ lineId, countedQty, itemId: lines.find((l) => l.id === lineId)?.itemId ?? null })) };
            return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/entries", { params: { path: { countId: openId ?? "" } }, body }));
        },
        onSuccess: async () => {
            setProblem(null);
            setEntries({});
            await refresh(openId);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const recount = useMutation({
        mutationFn: async (lineId) => unwrap(await api.POST("/api/v1/inventory/counts/{countId}/lines/{lineId}/recount", { params: { path: { countId: openId ?? "", lineId } } })),
        onSuccess: async () => { await refresh(openId); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const setReason = useMutation({
        mutationFn: async (input) => unwrap(await api.PUT("/api/v1/inventory/counts/{countId}/lines/{lineId}/reason", { params: { path: { countId: openId ?? "", lineId: input.lineId } }, body: { reasonCode: input.reasonCode || null } })),
        onSuccess: async () => { await refresh(openId); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
        { id: "scope", accessorKey: "scope", header: t("inventory.counts.scope"), size: 100, cell: ({ row }) => t(`inventory.counts.scopes.${row.original.scope}`, { defaultValue: row.original.scope }) },
        { id: "date", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "lines", accessorKey: "lineCount", header: t("inventory.lines"), size: 80, cell: ({ row }) => `${String(row.original.countedLines)}/${String(row.original.lineCount)}` },
        { id: "variances", accessorKey: "varianceLines", header: t("inventory.counts.variances"), size: 100, cell: ({ row }) => String(row.original.varianceLines) },
        { id: "value", accessorKey: "varianceValue", header: t("inventory.counts.varianceValue"), size: 140, cell: ({ row }) => _jsx(Amount, { value: row.original.varianceValue }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    const detail = sheet.data;
    const count = detail?.count;
    const counting = count?.status === "frozen" || count?.status === "counting";
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.counts"), description: t("inventory.counts.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ warehouseId: warehouses.data?.[0]?.id ?? "", scope: "full", postingDate: today(), blind: false, blockMovements: false, notes: "" }); }, disabled: !companyId, "data-testid": "new-count", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.counts.new")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) })] }), _jsx(DataGrid, { label: "nav.counts", columns: columns, data: counts.data ?? [], rowKey: (row) => row.id, loading: counts.isPending && Boolean(companyId), emptyTitle: t("inventory.counts.emptyTitle"), emptyDescription: t("inventory.counts.emptyDescription"), onOpen: (row) => { open(row.id); } }), _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: count ? `${count.number} · ${count.warehouseCode}` : t("common.loading") }) }), detail && count ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "count-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: count.status }), _jsx("span", { children: t(`inventory.counts.scopes.${count.scope}`, { defaultValue: count.scope }) }), count.blind ? _jsx("span", { className: "text-fg-muted", children: t("inventory.counts.blind") }) : null, count.blockMovements ? _jsx("span", { className: "text-fg-muted", children: t("inventory.counts.blockMovements") }) : null, count.frozenAt ? _jsx("span", { className: "text-fg-muted", children: t("inventory.counts.frozenAt", { when: formatDate(count.frozenAt) }) }) : null, _jsxs("span", { className: "text-fg-muted", children: [t("inventory.counts.varianceValue"), ": ", _jsx(Amount, { value: count.varianceValue })] })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("inventory.item") }), _jsx(TableHead, { children: t("inventory.warehouses.bin") }), _jsx(TableHead, { children: t("inventory.stock.lot") }), _jsx(TableHead, { className: "text-end", children: t("inventory.counts.expected") }), _jsx(TableHead, { className: "text-end", children: t("inventory.counts.counted") }), _jsx(TableHead, { className: "text-end", children: t("inventory.counts.variance") }), _jsx(TableHead, { children: t("inventory.reason") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: detail.lines.map((line) => (_jsxs(TableRow, { "data-testid": "count-line", children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { children: [_jsx("span", { dir: "ltr", children: line.itemCode }), " ", localized(line.itemName), line.serialNumber ? _jsxs("span", { className: "text-fg-muted", children: [" \u00B7 ", line.serialNumber] }) : null] }), _jsx(TableCell, { children: line.binCode ?? "" }), _jsx(TableCell, { children: line.lotNumber ?? "" }), _jsx(TableNumberCell, { children: count.blind && counting ? "•••" : _jsx(Qty, { value: line.expectedQty, uom: line.baseUom }) }), _jsx(TableNumberCell, { children: counting ? (_jsx(TextField, { "aria-label": t("inventory.counts.counted"), inputMode: "decimal", value: entries[line.id] ?? (line.countedQty === null ? "" : String(line.countedQty)), onChange: (e) => { setEntries({ ...entries, [line.id]: e.target.value }); }, dir: "ltr", className: "w-24 text-end", "data-testid": `counted-${line.lineNo}` })) : (_jsx(Qty, { value: line.countedQty })) }), _jsxs(TableNumberCell, { children: [_jsx(Qty, { value: line.varianceQty }), line.recountRequested ? _jsx("span", { className: "ms-1 text-warning", children: "\u21BB" }) : null] }), _jsx(TableCell, { children: count.status === "review" && Number(line.varianceQty) !== 0 ? (_jsxs(SelectField, { "aria-label": t("inventory.reason"), value: lineReasons[line.id] ?? line.reasonCode ?? "", onChange: (e) => { setLineReasons({ ...lineReasons, [line.id]: e.target.value }); setReason.mutate({ lineId: line.id, reasonCode: e.target.value }); }, "data-testid": `reason-${line.lineNo}`, children: [_jsx("option", { value: "", children: "\u2014" }), (reasons.data ?? []).map((r) => (_jsx("option", { value: r.code, children: r.code }, r.id)))] })) : (line.reasonCode ?? "") }), _jsx(TableCell, { children: count.status === "review" || counting ? (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { recount.mutate(line.id); }, children: t("inventory.counts.recount") })) : null })] }, line.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs(DialogFooter, { children: [count.status === "planned" ? (_jsx(Button, { onClick: () => { act.mutate("freeze"); }, loading: act.isPending, "data-testid": "freeze-count", children: t("inventory.counts.freeze") })) : null, counting ? (_jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { enter.mutate(); }, loading: enter.isPending, disabled: Object.keys(entries).length === 0, "data-testid": "save-entries", children: t("inventory.counts.saveEntries") }), _jsx(Button, { onClick: () => { act.mutate("review"); }, loading: act.isPending, "data-testid": "review-count", children: t("inventory.counts.review") })] })) : null, count.status === "review" ? (_jsx(Button, { onClick: () => { act.mutate("approve"); }, loading: act.isPending, "data-testid": "approve-count", children: t("accounting.approve") })) : null, count.status === "approved" ? (_jsx(Button, { onClick: () => { act.mutate("post"); }, loading: act.isPending, "data-testid": "post-count", children: t("accounting.post") })) : null, count.status !== "posted" && count.status !== "cancelled" ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, children: t("common.cancel") })) : null] })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("inventory.counts.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: editing.warehouseId, onChange: (id) => { setForm({ warehouseId: id }); }, required: true, testId: "count-warehouse" }), _jsx(Field, { label: t("inventory.counts.scope"), children: _jsx(SelectField, { value: editing.scope, onChange: (e) => { setForm({ scope: e.target.value }); }, children: scopes.map((s) => (_jsx("option", { value: s, children: t(`inventory.counts.scopes.${s}`) }, s))) }) }), _jsx(Field, { label: t("accounting.postingDate"), children: _jsx(TextField, { type: "date", value: editing.postingDate, onChange: (e) => { setForm({ postingDate: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("inventory.counts.notes"), children: _jsx(TextField, { value: editing.notes, onChange: (e) => { setForm({ notes: e.target.value }); } }) }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: editing.blind, onChange: (e) => { setForm({ blind: e.target.checked }); } }), t("inventory.counts.blind")] }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: editing.blockMovements, onChange: (e) => { setForm({ blockMovements: e.target.checked }); } }), t("inventory.counts.blockMovements")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-count", children: t("common.save") })] })] })) : null }) })] }));
}
