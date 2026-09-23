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
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useReasonCodes, useWarehouses } from "./shared";
const kinds = ["positive", "negative", "scrap", "opening"];
const statuses = ["", "draft", "pending_approval", "approved", "posted", "rejected", "cancelled"];
const emptyLine = { itemCode: "", binId: "", quantity: "", uom: "", unitCost: "", reasonCode: "", note: "", lotNumber: "", expiresOn: "", serialNumbers: "" };
function toForm(a) {
    return {
        warehouseId: a.warehouseId,
        kind: a.kind,
        postingDate: a.postingDate,
        reference: a.reference ?? "",
        notes: a.notes ?? "",
        lines: a.lines.map((l) => ({ itemCode: l.itemCode, binId: l.binId ?? "", quantity: String(l.quantity), uom: l.uomCode, unitCost: l.unitCost === null ? "" : String(l.unitCost), reasonCode: l.reasonCode, note: l.note ?? "", lotNumber: l.lotNumber ?? "", expiresOn: l.expiresOn ?? "", serialNumbers: l.serialNumbers.join(", ") })),
    };
}
function toRequest(companyId, f) {
    return {
        companyId,
        warehouseId: f.warehouseId,
        kind: f.kind,
        postingDate: f.postingDate || null,
        reference: f.reference || null,
        notes: f.notes || null,
        lines: f.lines
            .filter((l) => l.itemCode.trim())
            .map((l) => ({
            itemCode: l.itemCode.trim(),
            binId: l.binId || null,
            quantity: l.quantity || "0",
            uom: l.uom || null,
            unitCost: l.unitCost || null,
            reasonCode: l.reasonCode || null,
            note: l.note || null,
            lotNumber: l.lotNumber || null,
            expiresOn: l.expiresOn || null,
            serialNumbers: l.serialNumbers.trim() ? l.serialNumbers.split(/[\s,;]+/).filter(Boolean) : null,
        })),
    };
}
/** Adjustments (roadmap 3.4): positive, negative, scrap and opening documents with reason codes; approval when the company asks for it; posting through the engines. */
export function AdjustmentsPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const reasons = useReasonCodes();
    const [status, setStatus] = useState("");
    const [editing, setEditing] = useState(null);
    const [reason, setReason] = useState("");
    const [problem, setProblem] = useState(null);
    const [reasonsOpen, setReasonsOpen] = useState(false);
    const openId = search.open;
    const formWarehouse = warehouses.data?.find((w) => w.id === editing?.form.warehouseId);
    const bins = useQuery({
        queryKey: ["bins", formWarehouse?.id ?? ""],
        enabled: formWarehouse?.binsEnabled === true,
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: formWarehouse?.id ?? "" } } })),
    });
    const adjustments = useQuery({
        queryKey: ["adjustments", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/adjustments", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const adjustment = useQuery({
        queryKey: ["adjustment", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/adjustments/{adjustmentId}", { params: { path: { adjustmentId: openId ?? "" } } })),
    });
    const refresh = async (id) => {
        await queryClient.invalidateQueries({ queryKey: ["adjustments"] });
        if (id) {
            await queryClient.invalidateQueries({ queryKey: ["adjustment", id] });
        }
    };
    const open = (id) => { void navigate({ to: "/inventory/adjustments", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (input) => input.id
            ? unwrap(await api.PUT("/api/v1/inventory/adjustments/{adjustmentId}", { params: { path: { adjustmentId: input.id } }, body: toRequest(companyId, input.form) }))
            : unwrap(await api.POST("/api/v1/inventory/adjustments", { body: toRequest(companyId, input.form) })),
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
            const adjustmentId = openId ?? "";
            switch (action) {
                case "submit":
                    return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/submit", { params: { path: { adjustmentId } } }));
                case "approve":
                    return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/approve", { params: { path: { adjustmentId } } }));
                case "reject":
                    return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/reject", { params: { path: { adjustmentId } }, body: { reason } }));
                case "cancel":
                    return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/cancel", { params: { path: { adjustmentId } } }));
            }
        },
        onSuccess: async () => {
            setProblem(null);
            setReason("");
            await refresh(openId);
            await queryClient.invalidateQueries({ queryKey: ["stock"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "kind", accessorKey: "kind", header: t("inventory.adjustments.kind"), size: 110, cell: ({ row }) => t(`inventory.adjustments.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
        { id: "lines", accessorFn: (row) => row.lines.length, header: t("inventory.lines"), size: 80 },
        { id: "reference", accessorKey: "reference", header: t("accounting.reference"), size: 160 },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    const detail = adjustment.data;
    const editable = detail?.status === "draft" || detail?.status === "rejected";
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
    const updateLine = (index, patch) => {
        if (!editing) {
            return;
        }
        setForm({ lines: editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
    };
    const newForm = () => ({ warehouseId: warehouses.data?.[0]?.id ?? "", kind: "positive", postingDate: today(), reference: "", notes: "", lines: [{ ...emptyLine }] });
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.adjustments"), description: t("inventory.adjustments.description"), actions: _jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { setReasonsOpen(true); }, "data-testid": "reason-codes", children: t("inventory.reasons.title") }), _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: newForm() }); }, disabled: !companyId, "data-testid": "new-adjustment", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.adjustments.new")] })] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) })] }), _jsx(DataGrid, { label: "nav.adjustments", columns: columns, data: adjustments.data ?? [], rowKey: (row) => row.id, loading: adjustments.isPending && Boolean(companyId), emptyTitle: t("inventory.adjustments.emptyTitle"), emptyDescription: t("inventory.adjustments.emptyDescription"), onOpen: (row) => { open(row.id); } }), _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "adjustment-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: detail.status }), _jsx("span", { children: t(`inventory.adjustments.kinds.${detail.kind}`, { defaultValue: detail.kind }) }), _jsx("span", { className: "text-fg-muted", children: detail.warehouseCode }), detail.reference ? _jsx("span", { className: "text-fg-muted", children: detail.reference }) : null, detail.rejectionReason ? _jsx("span", { className: "text-danger", children: t("accounting.rejectedBecause", { reason: detail.rejectionReason }) }) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("inventory.item") }), _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { className: "text-end", children: t("inventory.unitCost") }), _jsx(TableHead, { className: "text-end", children: t("inventory.costAmount") }), _jsx(TableHead, { children: t("inventory.reason") }), _jsx(TableHead, { children: t("inventory.stock.lot") })] }) }), _jsx(TableBody, { children: detail.lines.map((line) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { children: [_jsx("span", { dir: "ltr", children: line.itemCode }), " ", localized(line.itemName)] }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.quantity, uom: line.uomCode }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.unitCost }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: line.costAmount }) }), _jsxs(TableCell, { children: [line.reasonCode, line.note ? ` · ${line.note}` : ""] }), _jsxs(TableCell, { children: [line.lotNumber ?? "", line.serialNumbers.length > 0 ? ` ${line.serialNumbers.join(", ")}` : ""] })] }, line.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), detail.status === "pending_approval" ? (_jsx(Field, { label: t("common.reason"), children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); } }) })) : null, _jsxs(DialogFooter, { children: [editable ? (_jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }, children: t("accounting.edit") })) : null, editable ? (_jsx(Button, { onClick: () => { act.mutate("submit"); }, loading: act.isPending, "data-testid": "submit-adjustment", children: t("inventory.adjustments.submit") })) : null, detail.status === "pending_approval" ? (_jsxs(_Fragment, { children: [_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("reject"); }, loading: act.isPending, children: t("accounting.reject") }), _jsx(Button, { onClick: () => { act.mutate("approve"); }, loading: act.isPending, "data-testid": "approve-adjustment", children: t("accounting.approve") })] })) : null, detail.status !== "posted" && detail.status !== "cancelled" ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, children: t("common.cancel") })) : null] })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: editing.id ? t("inventory.adjustments.edit") : t("inventory.adjustments.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: editing.form.warehouseId, onChange: (id) => { setForm({ warehouseId: id }); }, required: true, testId: "adjustment-warehouse" }), _jsx(Field, { label: t("inventory.adjustments.kind"), children: _jsx(SelectField, { value: editing.form.kind, onChange: (e) => { setForm({ kind: e.target.value }); }, "data-testid": "adjustment-kind", children: kinds.map((k) => (_jsx("option", { value: k, children: t(`inventory.adjustments.kinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("accounting.postingDate"), required: true, error: problem?.fields.postingDate, children: _jsx(TextField, { type: "date", value: editing.form.postingDate, onChange: (e) => { setForm({ postingDate: e.target.value }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.reference"), children: _jsx(TextField, { value: editing.form.reference, onChange: (e) => { setForm({ reference: e.target.value }); } }) })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.itemCode") }), formWarehouse?.binsEnabled ? _jsx(TableHead, { children: t("inventory.warehouses.bin") }) : null, _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { children: t("inventory.uom") }), _jsx(TableHead, { className: "text-end", children: t("inventory.unitCost") }), _jsx(TableHead, { children: t("inventory.reason") }), _jsx(TableHead, { children: t("inventory.stock.lot") }), _jsx(TableHead, { children: t("inventory.expiresOn") }), _jsx(TableHead, { children: t("inventory.serials") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: editing.form.lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.itemCode"), value: line.itemCode, onChange: (e) => { updateLine(index, { itemCode: e.target.value.toUpperCase() }); }, dir: "ltr", "data-testid": `line-item-${index}` }) }), formWarehouse?.binsEnabled ? (_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("inventory.warehouses.bin"), value: line.binId, onChange: (e) => { updateLine(index, { binId: e.target.value }); }, "data-testid": `line-bin-${index}`, children: [_jsx("option", { value: "", children: "\u2014" }), (bins.data ?? []).map((b) => (_jsx("option", { value: b.id, children: b.code }, b.id)))] }) })) : null, _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("inventory.quantity"), inputMode: "decimal", value: line.quantity, onChange: (e) => { updateLine(index, { quantity: e.target.value }); }, dir: "ltr", className: "text-end", "data-testid": `line-qty-${index}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.uom"), value: line.uom, onChange: (e) => { updateLine(index, { uom: e.target.value.toUpperCase() }); }, dir: "ltr", className: "w-20" }) }), _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("inventory.unitCost"), inputMode: "decimal", value: line.unitCost, onChange: (e) => { updateLine(index, { unitCost: e.target.value }); }, dir: "ltr", className: "text-end", "data-testid": `line-cost-${index}` }) }), _jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("inventory.reason"), value: line.reasonCode, onChange: (e) => { updateLine(index, { reasonCode: e.target.value }); }, "data-testid": `line-reason-${index}`, children: [_jsx("option", { value: "", children: "\u2014" }), (reasons.data ?? []).map((r) => (_jsxs("option", { value: r.code, children: [r.code, " \u00B7 ", localized(r.name)] }, r.id)))] }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.stock.lot"), value: line.lotNumber, onChange: (e) => { updateLine(index, { lotNumber: e.target.value }); }, dir: "ltr", className: "w-28" }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.expiresOn"), type: "date", value: line.expiresOn, onChange: (e) => { updateLine(index, { expiresOn: e.target.value }); }, dir: "ltr" }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.serials"), value: line.serialNumbers, onChange: (e) => { updateLine(index, { serialNumbers: e.target.value }); }, dir: "ltr", className: "w-32" }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "icon", "aria-label": t("accounting.removeLine"), onClick: () => { setForm({ lines: editing.form.lines.filter((_, i) => i !== index) }); }, children: _jsx(Trash2, { "aria-hidden": "true" }) }) })] }, index))) })] }), _jsx("div", { children: _jsxs(Button, { type: "button", variant: "secondary", onClick: () => { setForm({ lines: [...editing.form.lines, { ...emptyLine }] }); }, "data-testid": "add-line", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.addLine")] }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-adjustment", children: t("common.save") })] })] })) : null }) }), _jsx(ReasonCodesDialog, { open: reasonsOpen, onOpenChange: setReasonsOpen })] }));
}
const appliesTo = ["adjustment", "count", "return", "scrap", "write_off", "shortage"];
/** Reason codes: why stock was adjusted, scrapped, short or counted differently; a reason may override the account the movement offsets. */
function ReasonCodesDialog({ open, onOpenChange }) {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const reasons = useReasonCodes();
    const [form, setForm] = useState({ code: "", en: "", ar: "", appliesTo: "adjustment", requiresNote: false });
    const [problem, setProblem] = useState(null);
    const add = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/reason-codes", { body: { code: form.code, name: { en: form.en, ...(form.ar ? { ar: form.ar } : {}) }, appliesTo: form.appliesTo, requiresNote: form.requiresNote, isActive: true } })),
        onSuccess: async () => { setForm({ code: "", en: "", ar: "", appliesTo: "adjustment", requiresNote: false }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["reason-codes"] }); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    return (_jsx(Dialog, { open: open, onOpenChange: onOpenChange, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("inventory.reasons.title") }) }), _jsx("ul", { className: "max-h-48 overflow-y-auto text-sm", children: (reasons.data ?? []).map((r) => (_jsxs("li", { "data-testid": "reason-row", children: [_jsx("span", { dir: "ltr", children: r.code }), " \u00B7 ", localized(r.name), " ", _jsxs("span", { className: "text-fg-subtle", children: ["(", t(`inventory.reasons.appliesTo.${r.appliesTo}`, { defaultValue: r.appliesTo }), ")"] })] }, r.id))) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("form", { className: "grid gap-3 sm:grid-cols-2", onSubmit: (e) => { e.preventDefault(); add.mutate(); }, children: [_jsx(Field, { label: t("inventory.items.code"), required: true, children: _jsx(TextField, { value: form.code, onChange: (e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", "data-testid": "reason-code" }) }), _jsx(Field, { label: t("inventory.reasons.applies"), children: _jsx(SelectField, { value: form.appliesTo, onChange: (e) => { setForm({ ...form, appliesTo: e.target.value }); }, children: appliesTo.map((k) => (_jsx("option", { value: k, children: t(`inventory.reasons.appliesTo.${k}`) }, k))) }) }), _jsx(Field, { label: t("inventory.items.nameEn"), required: true, children: _jsx(TextField, { value: form.en, onChange: (e) => { setForm({ ...form, en: e.target.value }); }, required: true, "data-testid": "reason-name-en" }) }), _jsx(Field, { label: t("inventory.items.nameAr"), children: _jsx(TextField, { value: form.ar, onChange: (e) => { setForm({ ...form, ar: e.target.value }); }, dir: "rtl" }) }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.requiresNote, onChange: (e) => { setForm({ ...form, requiresNote: e.target.checked }); } }), t("inventory.reasons.requiresNote")] }), _jsx("div", { className: "text-end", children: _jsx(Button, { type: "submit", variant: "secondary", loading: add.isPending, "data-testid": "save-reason", children: t("inventory.reasons.add") }) })] })] }) }));
}
