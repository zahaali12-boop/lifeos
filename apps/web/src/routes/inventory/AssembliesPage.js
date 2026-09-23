import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";
const statuses = ["", "draft", "posted", "cancelled"];
/** Assemblies (roadmap 3.4): build an item from components (from its bill of materials or listed by hand); the output is worth what the components cost. */
export function AssembliesPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [status, setStatus] = useState("");
    const [editing, setEditing] = useState(null);
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const assemblies = useQuery({
        queryKey: ["assemblies", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/assemblies", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const assembly = useQuery({
        queryKey: ["assembly", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/assemblies/{assemblyId}", { params: { path: { assemblyId: openId ?? "" } } })),
    });
    const refresh = async (id) => {
        await queryClient.invalidateQueries({ queryKey: ["assemblies"] });
        await queryClient.invalidateQueries({ queryKey: ["stock"] });
        if (id) {
            await queryClient.invalidateQueries({ queryKey: ["assembly", id] });
        }
    };
    const open = (id) => { void navigate({ to: "/inventory/assemblies", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/inventory/assemblies", {
            body: {
                companyId,
                warehouseId: f.warehouseId,
                outputItemCode: f.outputItemCode.trim(),
                outputQuantity: f.outputQuantity || "0",
                outputUom: f.outputUom || null,
                postingDate: f.postingDate || null,
                reference: f.reference || null,
                lines: f.lines.filter((l) => l.itemCode.trim()).length > 0 ? f.lines.filter((l) => l.itemCode.trim()).map((l) => ({ itemCode: l.itemCode.trim(), quantity: l.quantity || "0", uom: l.uom || null })) : null,
            },
        })),
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
            const assemblyId = openId ?? "";
            return action === "post"
                ? unwrap(await api.POST("/api/v1/inventory/assemblies/{assemblyId}/post", { params: { path: { assemblyId } } }))
                : unwrap(await api.POST("/api/v1/inventory/assemblies/{assemblyId}/cancel", { params: { path: { assemblyId } } }));
        },
        onSuccess: async () => {
            setProblem(null);
            await refresh(openId);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "date", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "output", accessorKey: "outputItemCode", header: t("inventory.assemblies.output"), size: 200, cell: ({ row }) => `${row.original.outputItemCode} · ${localized(row.original.outputItemName)}` },
        { id: "qty", accessorKey: "outputQuantity", header: t("inventory.quantity"), size: 110, cell: ({ row }) => _jsx(Qty, { value: row.original.outputQuantity, uom: row.original.outputUomCode }) },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
        { id: "cost", accessorKey: "outputCost", header: t("inventory.costAmount"), size: 130, cell: ({ row }) => _jsx(Amount, { value: row.original.outputCost }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    const detail = assembly.data;
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.assemblies"), description: t("inventory.assemblies.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ warehouseId: warehouses.data?.[0]?.id ?? "", outputItemCode: "", outputQuantity: "1", outputUom: "", postingDate: today(), reference: "", lines: [{ itemCode: "", quantity: "", uom: "" }] }); }, disabled: !companyId, "data-testid": "new-assembly", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.assemblies.new")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) })] }), _jsx(DataGrid, { label: "nav.assemblies", columns: columns, data: assemblies.data ?? [], rowKey: (row) => row.id, loading: assemblies.isPending && Boolean(companyId), emptyTitle: t("inventory.assemblies.emptyTitle"), emptyDescription: t("inventory.assemblies.emptyDescription"), onOpen: (row) => { open(row.id); } }), _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.number} · ${detail.outputItemCode}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "assembly-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: detail.status }), _jsxs("span", { children: [_jsx(Qty, { value: detail.outputQuantity, uom: detail.outputUomCode }), " ", localized(detail.outputItemName)] }), _jsx("span", { className: "text-fg-muted", children: detail.warehouseCode }), detail.outputCost !== null ? (_jsxs("span", { className: "text-fg-muted", children: [t("inventory.costAmount"), ": ", _jsx(Amount, { value: detail.outputCost })] })) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("inventory.assemblies.component") }), _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { className: "text-end", children: t("inventory.costAmount") })] }) }), _jsx(TableBody, { children: detail.lines.map((line) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { children: [_jsx("span", { dir: "ltr", children: line.itemCode }), " ", localized(line.itemName)] }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.quantity, uom: line.uomCode }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.costAmount }) })] }, line.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(DialogFooter, { children: detail.status === "draft" ? (_jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, children: t("common.cancel") }), _jsx(Button, { onClick: () => { act.mutate("post"); }, loading: act.isPending, "data-testid": "post-assembly", children: t("accounting.post") })] })) : null })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("inventory.assemblies.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: editing.warehouseId, onChange: (id) => { setForm({ warehouseId: id }); }, required: true }), _jsx(Field, { label: t("inventory.assemblies.output"), required: true, children: _jsx(TextField, { value: editing.outputItemCode, onChange: (e) => { setForm({ outputItemCode: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", "data-testid": "assembly-output" }) }), _jsx(Field, { label: t("inventory.quantity"), required: true, children: _jsx(TextField, { inputMode: "decimal", value: editing.outputQuantity, onChange: (e) => { setForm({ outputQuantity: e.target.value }); }, required: true, dir: "ltr", "data-testid": "assembly-qty" }) }), _jsx(Field, { label: t("accounting.postingDate"), children: _jsx(TextField, { type: "date", value: editing.postingDate, onChange: (e) => { setForm({ postingDate: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.reference"), children: _jsx(TextField, { value: editing.reference, onChange: (e) => { setForm({ reference: e.target.value }); } }) })] }), _jsx("p", { className: "text-sm text-fg-muted", children: t("inventory.assemblies.linesHint") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.assemblies.component") }), _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { children: t("inventory.uom") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: editing.lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.assemblies.component"), value: line.itemCode, onChange: (e) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, itemCode: e.target.value.toUpperCase() } : l)) }); }, dir: "ltr", "data-testid": `line-item-${index}` }) }), _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("inventory.quantity"), inputMode: "decimal", value: line.quantity, onChange: (e) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, quantity: e.target.value } : l)) }); }, dir: "ltr", className: "text-end", "data-testid": `line-qty-${index}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.uom"), value: line.uom, onChange: (e) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, uom: e.target.value.toUpperCase() } : l)) }); }, dir: "ltr", className: "w-20" }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "icon", "aria-label": t("accounting.removeLine"), onClick: () => { setForm({ lines: editing.lines.filter((_, i) => i !== index) }); }, children: _jsx(Trash2, { "aria-hidden": "true" }) }) })] }, index))) })] }), _jsx("div", { children: _jsxs(Button, { type: "button", variant: "secondary", onClick: () => { setForm({ lines: [...editing.lines, { itemCode: "", quantity: "", uom: "" }] }); }, "data-testid": "add-line", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.addLine")] }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-assembly", children: t("common.save") })] })] })) : null }) })] }));
}
