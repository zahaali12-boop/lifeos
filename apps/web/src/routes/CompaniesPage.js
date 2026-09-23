import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Input } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatDate, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { rememberRecent } from "../shell/CommandPalette";
import { FormError, PageHeader, SelectField, TextField } from "./common";
const defaultPolicies = { defaultLanguage: "en", costingMethod: "average", costingScope: "company", revenueRecognitionPoint: "invoice", taxRoundingMode: "line", roundingMode: "half_away", negativeStockPolicy: "block", bankRevaluationMode: "permanent", fiscalCalendarId: null, businessCalendarId: null, tradeName: null, registrationNumbers: null, address: null };
const empty = { code: "", legalNameEn: "", legalNameAr: "", country: "IQ", functionalCurrency: "IQD", reportingCurrency: "", timeZone: "Asia/Baghdad", isActive: true, customFields: {}, policies: defaultPolicies };
function asRecord(value) {
    return typeof value === "object" && value !== null && !Array.isArray(value) ? value : {};
}
function toForm(company) {
    return {
        code: company.code,
        legalNameEn: company.legalName.en ?? "",
        legalNameAr: company.legalName.ar ?? "",
        country: company.country,
        functionalCurrency: company.functionalCurrency,
        reportingCurrency: company.reportingCurrency ?? "",
        timeZone: company.timeZone,
        isActive: company.isActive,
        customFields: asRecord(company.customFields),
        policies: {
            defaultLanguage: company.defaultLanguage,
            costingMethod: company.costingMethod,
            costingScope: company.costingScope,
            revenueRecognitionPoint: company.revenueRecognitionPoint,
            taxRoundingMode: company.taxRoundingMode,
            roundingMode: company.roundingMode,
            negativeStockPolicy: company.negativeStockPolicy,
            bankRevaluationMode: company.bankRevaluationMode,
            fiscalCalendarId: company.fiscalCalendarId,
            businessCalendarId: company.businessCalendarId,
            tradeName: company.tradeName,
            registrationNumbers: company.registrationNumbers,
            address: company.address,
        },
    };
}
/** Renders one custom-field control from its definition; values are validated by the API (custom_field.* problems map back here). */
function CustomFieldControl({ definition, value, onChange }) {
    const control = { name: `cf-${definition.key}` };
    switch (definition.type) {
        case "boolean":
            return (_jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: Boolean(value), onChange: (e) => { onChange(e.target.checked); }, ...control }), localized(definition.label)] }));
        case "select":
            return (_jsxs(SelectField, { value: typeof value === "string" ? value : "", onChange: (e) => { onChange(e.target.value || undefined); }, ...control, children: [_jsx("option", { value: "", children: "\u2014" }), definition.options.map((option) => (_jsx("option", { value: option.value, children: localized(option.label) }, option.value)))] }));
        case "number":
            return _jsx(TextField, { type: "number", inputMode: "decimal", value: typeof value === "number" || typeof value === "string" ? String(value) : "", onChange: (e) => { onChange(e.target.value === "" ? undefined : Number(e.target.value)); }, ...control, dir: "ltr" });
        case "date":
            return _jsx(TextField, { type: "date", value: typeof value === "string" ? value : "", onChange: (e) => { onChange(e.target.value || undefined); }, ...control, dir: "ltr" });
        default:
            return _jsx(TextField, { value: typeof value === "string" ? value : "", onChange: (e) => { onChange(e.target.value || undefined); }, ...control });
    }
}
export function CompaniesPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const search = useSearch({ strict: false });
    const [query, setQuery] = useState(search.q ?? "");
    const [editing, setEditing] = useState(null);
    /** Opens a company for editing with its current version (the ETag of GET), so the save can send If-Match. */
    const openCompany = async (company) => {
        setProblem(null);
        const result = await api.GET("/api/v1/organization/companies/{companyId}", { params: { path: { companyId: company.id } } });
        setEditing({ id: company.id, form: toForm(result.data ?? company), version: result.response.headers.get("etag") ?? undefined });
    };
    const [problem, setProblem] = useState(null);
    const filter = query.trim() ? `code like '${query.trim().replace(/'/g, "''")}'` : undefined;
    const companies = useQuery({
        queryKey: ["companies", filter ?? ""],
        queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies", { params: { query: filter ? { filter } : {} } })),
    });
    const currencies = useQuery({ queryKey: ["currencies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/currencies")) });
    const fields = useQuery({ queryKey: ["custom-fields", "company"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields", { params: { query: { entityType: "company" } } })) });
    useEffect(() => {
        if (search.new) {
            setEditing({ id: null, form: empty });
        }
        else if (search.open && companies.data) {
            const company = companies.data.find((c) => c.id === search.open);
            if (company) {
                void openCompany(company);
            }
        }
    }, [search.new, search.open, companies.data]);
    const save = useMutation({
        mutationFn: async (input) => {
            const body = {
                ...input.form.policies,
                code: input.form.code,
                legalName: { en: input.form.legalNameEn, ...(input.form.legalNameAr ? { ar: input.form.legalNameAr } : {}) },
                country: input.form.country,
                functionalCurrency: input.form.functionalCurrency,
                reportingCurrency: input.form.reportingCurrency || null,
                timeZone: input.form.timeZone,
                isActive: input.form.isActive,
                customFields: input.form.customFields,
            };
            if (input.id) {
                // The version ETag from GET goes back as If-Match: a concurrent change answers 412 instead of being overwritten.
                return unwrap(await api.PUT("/api/v1/organization/companies/{companyId}", { params: { path: { companyId: input.id } }, body, headers: input.version ? { "If-Match": input.version } : {} }));
            }
            return unwrap(await api.POST("/api/v1/organization/companies", { body }));
        },
        onSuccess: async (company) => {
            setEditing(null);
            setProblem(null);
            rememberRecent({ to: `/companies?open=${company.id}`, label: `${company.code} · ${localized(company.legalName)}` });
            await queryClient.invalidateQueries({ queryKey: ["companies"] });
            void navigate({ to: "/companies", search: {} });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("companies.code"), size: 120 },
        { id: "legalName", accessorFn: (row) => localized(row.legalName), header: t("companies.legalName"), size: 260 },
        { id: "country", accessorKey: "country", header: t("companies.country"), size: 90 },
        { id: "functionalCurrency", accessorKey: "functionalCurrency", header: t("companies.functionalCurrency"), size: 110 },
        { id: "timeZone", accessorKey: "timeZone", header: t("companies.timeZone"), size: 160 },
        { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
        { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 140, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ], [t]);
    const submit = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const form = editing?.form;
    const isEdit = editing?.id != null;
    const setForm = (patch) => {
        setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev));
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.companies"), description: t("companies.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: empty }); }, "data-testid": "new-company", children: [_jsx(Plus, { "aria-hidden": "true" }), t("companies.new")] }) }), _jsx(DataGrid, { label: "nav.companies", columns: columns, data: companies.data ?? [], rowKey: (row) => row.id, entityType: "company", loading: companies.isPending, onOpen: (company) => { void openCompany(company); }, emptyTitle: t("companies.emptyTitle"), emptyDescription: t("companies.emptyDescription"), emptyAction: _jsx(Button, { onClick: () => { setEditing({ id: null, form: empty }); }, children: t("companies.new") }), toolbar: _jsx(Input, { type: "search", placeholder: t("companies.searchPlaceholder"), value: query, onChange: (e) => { setQuery(e.target.value); }, className: "w-56", "aria-label": t("common.search") }) }), _jsx(Dialog, { open: editing !== null, onOpenChange: (open) => { if (!open) {
                    setEditing(null);
                    void navigate({ to: "/companies", search: {} });
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "sm:max-w-2xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: isEdit ? t("companies.edit") : t("companies.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("companies.code"), required: true, error: problem?.fields.code, children: _jsx(TextField, { value: form.code, onChange: (e) => { setForm({ code: e.target.value.toUpperCase() }); }, required: true, dir: "ltr", name: "code" }) }), _jsx(Field, { label: t("companies.country"), required: true, error: problem?.fields.country, description: t("companies.countryHint"), children: _jsx(TextField, { value: form.country, onChange: (e) => { setForm({ country: e.target.value.toUpperCase() }); }, required: true, maxLength: 2, dir: "ltr", name: "country" }) }), _jsx(Field, { label: t("companies.legalNameEn"), required: true, error: problem?.fields.legalName, children: _jsx(TextField, { value: form.legalNameEn, onChange: (e) => { setForm({ legalNameEn: e.target.value }); }, required: true, name: "legalNameEn" }) }), _jsx(Field, { label: t("companies.legalNameAr"), children: _jsx(TextField, { value: form.legalNameAr, onChange: (e) => { setForm({ legalNameAr: e.target.value }); }, name: "legalNameAr", dir: "rtl" }) }), _jsx(Field, { label: t("companies.functionalCurrency"), required: true, error: problem?.fields.functionalCurrency ?? problem?.fields.currency, description: isEdit ? t("companies.currencyLocked") : undefined, children: _jsx(SelectField, { value: form.functionalCurrency, onChange: (e) => { setForm({ functionalCurrency: e.target.value }); }, disabled: isEdit, name: "functionalCurrency", children: (currencies.data ?? []).filter((c) => c.isActive).map((currency) => (_jsxs("option", { value: currency.code, children: [currency.code, " \u2014 ", localized(currency.name)] }, currency.code))) }) }), _jsx(Field, { label: t("companies.reportingCurrency"), error: problem?.fields.reportingCurrency, children: _jsxs(SelectField, { value: form.reportingCurrency, onChange: (e) => { setForm({ reportingCurrency: e.target.value }); }, name: "reportingCurrency", children: [_jsx("option", { value: "", children: "\u2014" }), (currencies.data ?? []).filter((c) => c.isActive).map((currency) => (_jsx("option", { value: currency.code, children: currency.code }, currency.code)))] }) }), _jsx(Field, { label: t("companies.timeZone"), required: true, error: problem?.fields.timeZone, children: _jsx(TextField, { value: form.timeZone, onChange: (e) => { setForm({ timeZone: e.target.value }); }, required: true, dir: "ltr", name: "timeZone" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end pb-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.isActive, onChange: (e) => { setForm({ isActive: e.target.checked }); }, name: "isActive" }), t("common.active")] })] }), (fields.data ?? []).filter((f) => f.active).length > 0 ? (_jsxs("fieldset", { className: "grid gap-4 rounded-md border border-border p-4 sm:grid-cols-2", children: [_jsx("legend", { className: "px-1 text-sm font-medium", children: t("customFields.title") }), (fields.data ?? []).filter((f) => f.active).map((definition) => (_jsx(Field, { label: localized(definition.label), required: definition.required, error: problem?.fields[`customFields.${definition.key}`], description: localized(definition.description) || undefined, children: _jsx(CustomFieldControl, { definition: definition, value: form.customFields[definition.key], onChange: (next) => { setForm({ customFields: { ...form.customFields, [definition.key]: next } }); } }) }, definition.id)))] })) : null, _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-company", children: t("common.save") })] })] })) : null }) })] }));
}
