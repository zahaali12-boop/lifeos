import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses } from "./shared";
const kinds = ["standard", "in_transit", "consignment", "quarantine", "virtual"];
const binKinds = ["storage", "receiving", "shipping", "quarantine", "returns"];
const empty = { code: "", nameEn: "", nameAr: "", kind: "standard", binsEnabled: false, allowNegativeStock: "", isActive: true };
function toForm(w) {
    return { code: w.code, nameEn: w.name.en ?? "", nameAr: w.name.ar ?? "", kind: w.kind, binsEnabled: w.binsEnabled, allowNegativeStock: w.allowNegativeStock === null ? "" : w.allowNegativeStock ? "yes" : "no", isActive: w.isActive };
}
/** Warehouses and bins (roadmap 3.2): the places stock lives, per company, with their bins in pick order. */
export function WarehousesPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [editing, setEditing] = useState(null);
    const [bin, setBin] = useState({ code: "", zone: "", kind: "storage", pickSequence: "0" });
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const detail = warehouses.data?.find((w) => w.id === openId);
    const bins = useQuery({
        queryKey: ["bins", openId],
        enabled: Boolean(openId) && detail?.binsEnabled === true,
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: openId ?? "" } } })),
    });
    const open = (id) => { void navigate({ to: "/inventory/warehouses", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (input) => {
            const f = input.form;
            const body = { companyId, code: f.code, name: { en: f.nameEn, ...(f.nameAr ? { ar: f.nameAr } : {}) }, kind: f.kind, binsEnabled: f.binsEnabled, allowNegativeStock: f.allowNegativeStock === "" ? null : f.allowNegativeStock === "yes", isActive: f.isActive };
            return input.id
                ? unwrap(await api.PUT("/api/v1/inventory/warehouses/{warehouseId}", { params: { path: { warehouseId: input.id } }, body }))
                : unwrap(await api.POST("/api/v1/inventory/warehouses", { body }));
        },
        onSuccess: async (saved) => {
            setEditing(null);
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["warehouses"] });
            open(saved.id);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const addBin = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: openId ?? "" } }, body: { code: bin.code, zone: bin.zone || null, kind: bin.kind, pickSequence: Number(bin.pickSequence) || 0, isActive: true } })),
        onSuccess: async () => {
            setBin({ code: "", zone: "", kind: "storage", pickSequence: "0" });
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["bins", openId] });
            await queryClient.invalidateQueries({ queryKey: ["warehouses"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("inventory.items.code"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("inventory.items.name"), size: 260 },
        { id: "kind", accessorKey: "kind", header: t("inventory.warehouses.kind"), size: 130, cell: ({ row }) => t(`inventory.warehouses.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
        { id: "bins", accessorKey: "binCount", header: t("inventory.warehouses.bins"), size: 90, cell: ({ row }) => (row.original.binsEnabled ? String(row.original.binCount) : "—") },
        { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
        { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 130, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ], [t]);
    const form = editing?.form;
    const isEdit = editing?.id != null;
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
    const submit = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.warehouses"), description: t("inventory.warehouses.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: empty }); }, disabled: !companyId, "data-testid": "new-warehouse", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.warehouses.new")] }) }), _jsx("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: _jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }) }), _jsx(DataGrid, { label: "nav.warehouses", columns: columns, data: warehouses.data ?? [], rowKey: (row) => row.id, loading: warehouses.isPending && Boolean(companyId), onOpen: (row) => { open(row.id); }, emptyTitle: t("inventory.warehouses.emptyTitle"), emptyDescription: t("inventory.warehouses.emptyDescription") }), _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.code} · ${localized(detail.name)}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "warehouse-detail", children: [_jsxs("div", { className: "flex flex-wrap gap-2 text-sm", children: [_jsx(Badge, { children: t(`inventory.warehouses.kinds.${detail.kind}`, { defaultValue: detail.kind }) }), _jsx(Badge, { tone: detail.binsEnabled ? "accent" : "neutral", children: detail.binsEnabled ? t("inventory.warehouses.binsEnabled") : t("inventory.warehouses.noBins") }), detail.allowNegativeStock !== null ? _jsx(Badge, { tone: "warning", children: detail.allowNegativeStock ? t("inventory.warehouses.negativeAllowed") : t("inventory.warehouses.negativeBlocked") }) : null] }), detail.binsEnabled ? (_jsxs(_Fragment, { children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.warehouses.bin") }), _jsx(TableHead, { children: t("inventory.warehouses.zone") }), _jsx(TableHead, { children: t("inventory.warehouses.kind") }), _jsx(TableHead, { className: "text-end", children: t("inventory.warehouses.pickSequence") })] }) }), _jsx(TableBody, { children: (bins.data ?? []).map((b) => (_jsxs(TableRow, { "data-testid": "bin-row", children: [_jsx(TableCell, { dir: "ltr", children: b.code }), _jsx(TableCell, { children: b.zone ?? "" }), _jsx(TableCell, { children: t(`inventory.warehouses.binKinds.${b.kind}`, { defaultValue: b.kind }) }), _jsx(TableCell, { className: "text-end", children: String(b.pickSequence) })] }, b.id))) })] }), _jsxs("form", { className: "grid items-end gap-3 sm:grid-cols-5", onSubmit: (e) => { e.preventDefault(); addBin.mutate(); }, children: [_jsx(Field, { label: t("inventory.warehouses.bin"), required: true, children: _jsx(TextField, { value: bin.code, onChange: (e) => { setBin({ ...bin, code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", "data-testid": "bin-code" }) }), _jsx(Field, { label: t("inventory.warehouses.zone"), children: _jsx(TextField, { value: bin.zone, onChange: (e) => { setBin({ ...bin, zone: e.target.value.toUpperCase() }); }, dir: "ltr" }) }), _jsx(Field, { label: t("inventory.warehouses.kind"), children: _jsx(SelectField, { value: bin.kind, onChange: (e) => { setBin({ ...bin, kind: e.target.value }); }, children: binKinds.map((k) => (_jsx("option", { value: k, children: t(`inventory.warehouses.binKinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("inventory.warehouses.pickSequence"), children: _jsx(TextField, { inputMode: "numeric", value: bin.pickSequence, onChange: (e) => { setBin({ ...bin, pickSequence: e.target.value }); }, dir: "ltr" }) }), _jsx(Button, { type: "submit", variant: "secondary", loading: addBin.isPending, "data-testid": "add-bin", children: t("inventory.warehouses.addBin") })] })] })) : null, _jsx(FormError, { message: problem?.message ?? null }), _jsx(DialogFooter, { children: _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }, "data-testid": "edit-warehouse", children: t("inventory.warehouses.edit") }) })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: isEdit ? t("inventory.warehouses.edit") : t("inventory.warehouses.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("inventory.items.code"), required: true, error: problem?.fields.code, children: _jsx(TextField, { value: form.code, onChange: (e) => { setForm({ code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", "data-testid": "warehouse-code" }) }), _jsx(Field, { label: t("inventory.warehouses.kind"), children: _jsx(SelectField, { value: form.kind, onChange: (e) => { setForm({ kind: e.target.value }); }, children: kinds.map((k) => (_jsx("option", { value: k, children: t(`inventory.warehouses.kinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("inventory.items.nameEn"), required: true, error: problem?.fields.name, children: _jsx(TextField, { value: form.nameEn, onChange: (e) => { setForm({ nameEn: e.target.value }); }, required: true, "data-testid": "warehouse-name-en" }) }), _jsx(Field, { label: t("inventory.items.nameAr"), children: _jsx(TextField, { value: form.nameAr, onChange: (e) => { setForm({ nameAr: e.target.value }); }, dir: "rtl" }) }), _jsx(Field, { label: t("inventory.warehouses.negativeStock"), children: _jsxs(SelectField, { value: form.allowNegativeStock, onChange: (e) => { setForm({ allowNegativeStock: e.target.value }); }, children: [_jsx("option", { value: "", children: t("inventory.warehouses.negativeCompany") }), _jsx("option", { value: "no", children: t("inventory.warehouses.negativeBlocked") }), _jsx("option", { value: "yes", children: t("inventory.warehouses.negativeAllowed") })] }) }), _jsxs("div", { className: "flex flex-col gap-2 self-end pb-2 text-sm", children: [_jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.binsEnabled, onChange: (e) => { setForm({ binsEnabled: e.target.checked }); }, "data-testid": "warehouse-bins" }), t("inventory.warehouses.binsEnabled")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.isActive, onChange: (e) => { setForm({ isActive: e.target.checked }); } }), t("common.active")] })] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-warehouse", children: t("common.save") })] })] })) : null }) })] }));
}
