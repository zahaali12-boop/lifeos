import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
/** Entity types that carry custom fields today; each host registers itself on the API as it lands. */
const hosts = ["company"];
const types = ["text", "number", "date", "boolean", "select", "multi_select", "reference"];
const emptyForm = { key: "", labelEn: "", labelAr: "", type: "text", required: false, indexed: false, options: "", min: "", max: "", maxLength: "", pattern: "", referenceType: "" };
export function CustomFieldsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [entityType, setEntityType] = useState("company");
    const [editing, setEditing] = useState(null);
    const [problem, setProblem] = useState(null);
    const fields = useQuery({ queryKey: ["custom-fields", entityType], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields", { params: { query: { entityType } } })) });
    const save = useMutation({
        mutationFn: async (input) => {
            const f = input.form;
            const body = {
                entityType,
                key: f.key,
                label: { en: f.labelEn, ...(f.labelAr ? { ar: f.labelAr } : {}) },
                type: f.type,
                required: f.required,
                indexed: f.indexed,
                options: f.type === "select" || f.type === "multi_select" ? f.options.split(",").map((o) => o.trim()).filter(Boolean).map((value) => ({ value, label: { en: value } })) : null,
                rules: { min: f.min ? Number(f.min) : null, max: f.max ? Number(f.max) : null, maxLength: f.maxLength ? Number(f.maxLength) : null, pattern: f.pattern || null, referenceType: f.referenceType || null },
                position: 0,
                active: true,
                description: null,
            };
            return input.id ? unwrap(await api.PUT("/api/v1/collaboration/custom-fields/{fieldId}", { params: { path: { fieldId: input.id } }, body })) : unwrap(await api.POST("/api/v1/collaboration/custom-fields", { body }));
        },
        onSuccess: async () => {
            setEditing(null);
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["custom-fields"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const remove = useMutation({
        mutationFn: async (id) => unwrap(await api.DELETE("/api/v1/collaboration/custom-fields/{fieldId}", { params: { path: { fieldId: id } } })),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["custom-fields"] }),
    });
    const columns = useMemo(() => [
        { id: "key", accessorKey: "key", header: t("customFields.key"), size: 160, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.key }) },
        { id: "label", accessorFn: (row) => localized(row.label), header: t("customFields.label"), size: 220 },
        { id: "type", accessorKey: "type", header: t("customFields.type"), size: 120 },
        { id: "required", accessorKey: "required", header: t("customFields.required"), size: 100, cell: ({ row }) => (row.original.required ? t("common.yes") : t("common.no")) },
        { id: "indexed", accessorKey: "indexed", header: t("customFields.indexed"), size: 100, cell: ({ row }) => (row.original.indexed ? _jsx(Badge, { tone: "accent", children: t("common.yes") }) : t("common.no")) },
        { id: "active", accessorKey: "active", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.active ? "success" : "neutral", children: row.original.active ? t("common.active") : t("common.inactive") }) },
    ], [t]);
    const openEdit = (field) => {
        setProblem(null);
        setEditing({
            id: field.id,
            form: {
                key: field.key,
                labelEn: field.label.en ?? "",
                labelAr: field.label.ar ?? "",
                type: field.type,
                required: field.required,
                indexed: field.indexed,
                options: field.options.map((o) => o.value).join(", "),
                min: field.rules.min == null ? "" : String(field.rules.min),
                max: field.rules.max == null ? "" : String(field.rules.max),
                maxLength: field.rules.maxLength == null ? "" : String(field.rules.maxLength),
                pattern: field.rules.pattern ?? "",
                referenceType: field.rules.referenceType ?? "",
            },
        });
    };
    const submit = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const form = editing?.form;
    const isEdit = editing?.id != null;
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.customFields"), description: t("customFields.description"), actions: _jsxs(_Fragment, { children: [_jsx(SelectField, { value: entityType, onChange: (e) => { setEntityType(e.target.value); }, "aria-label": t("customFields.entityType"), className: "w-48", children: hosts.map((host) => (_jsx("option", { value: host, children: t(`entities.${host}`) }, host))) }), _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: emptyForm }); }, "data-testid": "new-custom-field", children: [_jsx(Plus, { "aria-hidden": "true" }), t("customFields.new")] })] }) }), _jsx(DataGrid, { label: "nav.customFields", columns: columns, data: fields.data ?? [], rowKey: (row) => row.id, loading: fields.isPending, onOpen: openEdit, selectable: true, emptyTitle: t("customFields.emptyTitle"), emptyDescription: t("customFields.emptyDescription"), bulkActions: (selected, clear) => (_jsx(Button, { size: "sm", variant: "danger", onClick: () => { selected.forEach((id) => { remove.mutate(id); }); clear(); }, children: t("common.delete") })) }), _jsx(Dialog, { open: editing !== null, onOpenChange: (open) => { if (!open) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "sm:max-w-2xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: isEdit ? t("customFields.edit") : t("customFields.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("customFields.key"), required: true, description: t("customFields.keyHint"), error: problem?.fields.key, children: _jsx(TextField, { value: form.key, onChange: (e) => { setForm({ key: e.target.value }); }, required: true, disabled: isEdit, dir: "ltr", pattern: "[a-z][a-z0-9_]{0,39}" }) }), _jsx(Field, { label: t("customFields.type"), required: true, error: problem?.fields.type, children: _jsx(SelectField, { value: form.type, onChange: (e) => { setForm({ type: e.target.value }); }, disabled: isEdit, children: types.map((type) => (_jsx("option", { value: type, children: t(`customFields.types.${type}`) }, type))) }) }), _jsx(Field, { label: t("customFields.labelEn"), required: true, error: problem?.fields.label, children: _jsx(TextField, { value: form.labelEn, onChange: (e) => { setForm({ labelEn: e.target.value }); }, required: true }) }), _jsx(Field, { label: t("customFields.labelAr"), children: _jsx(TextField, { value: form.labelAr, onChange: (e) => { setForm({ labelAr: e.target.value }); }, dir: "rtl" }) }), form.type === "select" || form.type === "multi_select" ? (_jsx(Field, { label: t("customFields.options"), required: true, description: t("customFields.optionsHint"), error: problem?.fields.options, className: "sm:col-span-2", children: _jsx(TextField, { value: form.options, onChange: (e) => { setForm({ options: e.target.value }); }, required: true, dir: "ltr" }) })) : null, form.type === "number" ? (_jsxs(_Fragment, { children: [_jsx(Field, { label: t("customFields.min"), error: problem?.fields.rules, children: _jsx(TextField, { inputMode: "decimal", value: form.min, onChange: (e) => { setForm({ min: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("customFields.max"), children: _jsx(TextField, { inputMode: "decimal", value: form.max, onChange: (e) => { setForm({ max: e.target.value }); }, dir: "ltr" }) })] })) : null, form.type === "text" ? (_jsxs(_Fragment, { children: [_jsx(Field, { label: t("customFields.maxLength"), children: _jsx(TextField, { inputMode: "numeric", value: form.maxLength, onChange: (e) => { setForm({ maxLength: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("customFields.pattern"), error: problem?.fields.rules, children: _jsx(TextField, { value: form.pattern, onChange: (e) => { setForm({ pattern: e.target.value }); }, dir: "ltr" }) })] })) : null, form.type === "reference" ? (_jsx(Field, { label: t("customFields.referenceType"), required: true, error: problem?.fields.rules, children: _jsx(TextField, { value: form.referenceType, onChange: (e) => { setForm({ referenceType: e.target.value }); }, required: true, dir: "ltr" }) })) : null, _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.required, onChange: (e) => { setForm({ required: e.target.checked }); } }), t("customFields.required")] }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.indexed, onChange: (e) => { setForm({ indexed: e.target.checked }); } }), t("customFields.indexedHint")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-custom-field", children: t("common.save") })] })] })) : null }) })] }));
}
