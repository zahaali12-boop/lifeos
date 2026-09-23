import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Checkbox, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, cn } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanySelect, today, useCompanies, useCompanySelection } from "./shared";
const types = ["asset", "liability", "equity", "revenue", "expense"];
const subledgers = ["", "AR", "AP", "INV", "FA", "BANK", "PDC", "GRNI", "IC", "WHT"];
const rules = ["required", "optional", "blocked"];
const emptyAccount = { code: "", nameEn: "", nameAr: "", type: "asset", parentCode: "", isHeader: false, isControl: false, subledgerType: "", allowManualPosting: true, defaultRole: "", currencyRestriction: "", isActive: true };
function toForm(account) {
    return {
        code: account.code,
        nameEn: account.name.en ?? "",
        nameAr: account.name.ar ?? "",
        type: account.type,
        parentCode: account.parentCode ?? "",
        isHeader: account.isHeader,
        isControl: account.isControl,
        subledgerType: account.subledgerType ?? "",
        allowManualPosting: account.allowManualPosting,
        defaultRole: account.defaultRole ?? "",
        currencyRestriction: account.currencyRestriction ?? "",
        isActive: account.isActive,
    };
}
function toRequest(form) {
    return {
        code: form.code.trim(),
        name: { en: form.nameEn, ...(form.nameAr ? { ar: form.nameAr } : {}) },
        type: form.type,
        parentCode: form.parentCode || null,
        subtype: "",
        isHeader: form.isHeader,
        isControl: form.isControl,
        subledgerType: form.isControl && form.subledgerType ? form.subledgerType : null,
        currencyRestriction: form.currencyRestriction || null,
        allowManualPosting: form.allowManualPosting,
        revalueFx: false,
        defaultRole: form.defaultRole || null,
        isActive: form.isActive,
    };
}
/** The chart of accounts of a company as a tree, the account editor, and the dimension rules of an account. */
export function ChartPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const companies = useCompanies();
    const [companyId, setCompanyId] = useCompanySelection(companies.data);
    const company = companies.data?.find((c) => c.id === companyId);
    const chartId = company?.chartId ?? null;
    const [filter, setFilter] = useState("");
    const [selectedId, setSelectedId] = useState(null);
    const [editing, setEditing] = useState(null);
    const [problem, setProblem] = useState(null);
    const [template, setTemplate] = useState("IFRS_SME");
    const [ruleDraft, setRuleDraft] = useState({ dimensionCode: "", rule: "required" });
    const templates = useQuery({ queryKey: ["chart-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/chart-templates")) });
    const chart = useQuery({
        queryKey: ["chart", chartId],
        enabled: Boolean(chartId),
        queryFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}", { params: { path: { chartId: chartId ?? "" }, query: { expand: "accounts" } } })),
    });
    const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
    const accountRules = useQuery({
        queryKey: ["dimension-rules", selectedId],
        enabled: Boolean(selectedId),
        queryFn: async () => unwrap(await api.GET("/api/v1/accounting/accounts/{accountId}/dimension-rules", { params: { path: { accountId: selectedId ?? "" } } })),
    });
    const createChart = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/charts/from-template", { body: { templateCode: template, code: `CH-${company?.code ?? "MAIN"}-${today().replaceAll("-", "")}`, companyId, shared: false } })),
        onSuccess: async () => {
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["companies"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const saveAccount = useMutation({
        mutationFn: async (input) => input.id
            ? unwrap(await api.PUT("/api/v1/accounting/accounts/{accountId}", { params: { path: { accountId: input.id } }, body: toRequest(input.form) }))
            : unwrap(await api.POST("/api/v1/accounting/charts/{chartId}/accounts", { params: { path: { chartId: chartId ?? "" } }, body: toRequest(input.form) })),
        onSuccess: async (saved) => {
            setEditing(null);
            setProblem(null);
            setSelectedId(saved.id);
            await queryClient.invalidateQueries({ queryKey: ["chart", chartId] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const saveRules = useMutation({
        mutationFn: async (next) => unwrap(await api.PUT("/api/v1/accounting/accounts/{accountId}/dimension-rules", { params: { path: { accountId: selectedId ?? "" } }, body: next.map((r) => ({ dimensionCode: r.dimensionCode, rule: r.rule, defaultValueId: r.defaultValueId })) })),
        onSuccess: async () => {
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["dimension-rules", selectedId] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const accounts = useMemo(() => {
        const all = chart.data?.accounts ?? [];
        const needle = filter.trim().toLowerCase();
        return needle ? all.filter((a) => a.code.includes(needle) || localized(a.name).toLowerCase().includes(needle)) : all;
    }, [chart.data, filter]);
    const selected = chart.data?.accounts?.find((a) => a.id === selectedId) ?? null;
    const submit = (event) => {
        event.preventDefault();
        if (editing) {
            saveAccount.mutate(editing);
        }
    };
    const setForm = (patch) => {
        if (editing) {
            setEditing({ ...editing, form: { ...editing.form, ...patch } });
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("accounting.chart"), description: t("accounting.chartDescription"), actions: chartId ? (_jsxs(Button, { onClick: () => { setProblem(null); setEditing({ id: null, form: { ...emptyAccount, parentCode: selected?.isHeader ? selected.code : "", type: selected?.type ?? "asset" } }); }, "data-testid": "new-account", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.newAccount")] })) : null }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: (id) => { setCompanyId(id); setSelectedId(null); } }), chartId ? (_jsx(Field, { label: t("common.search"), children: _jsx(Input, { type: "search", value: filter, onChange: (e) => { setFilter(e.target.value); }, "aria-label": t("common.search") }) })) : null] }), _jsx(FormError, { message: problem && !editing ? problem.message : null }), company && !chartId ? (_jsxs("form", { className: "flex flex-wrap items-end gap-3 rounded-lg border border-border bg-surface p-4", onSubmit: (event) => {
                    event.preventDefault();
                    createChart.mutate();
                }, children: [_jsx("p", { className: "w-full text-sm text-fg-muted", children: t("accounting.noChartYet", { company: company.code }) }), _jsx(Field, { label: t("accounting.template"), children: _jsx(SelectField, { value: template, onChange: (e) => { setTemplate(e.target.value); }, "data-testid": "chart-template", children: (templates.data ?? []).map((tpl) => (_jsxs("option", { value: tpl.code, children: [tpl.code, " \u00B7 ", localized(tpl.name)] }, tpl.code))) }) }), _jsx(Button, { type: "submit", loading: createChart.isPending, "data-testid": "create-chart", children: t("accounting.createChart") })] })) : null, chartId ? (_jsxs("div", { className: "grid gap-4 lg:grid-cols-[2fr_1fr]", children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.accountCode") }), _jsx(TableHead, { children: t("accounting.accountName") }), _jsx(TableHead, { children: t("accounting.type") }), _jsx(TableHead, { children: t("accounting.flags") })] }) }), _jsx(TableBody, { children: accounts.map((account) => (_jsxs(TableRow, { className: cn("cursor-pointer", selectedId === account.id && "bg-selection"), onClick: () => { setSelectedId(account.id); }, "data-testid": "account-row", "aria-selected": selectedId === account.id, children: [_jsx(TableCell, { dir: "ltr", style: { paddingInlineStart: `${12 + Number(account.level) * 16}px` }, className: cn(account.isHeader && "font-semibold"), children: _jsx("button", { type: "button", className: "underline-offset-2 hover:underline", onClick: () => { setSelectedId(account.id); }, children: account.code }) }), _jsx(TableCell, { className: cn(account.isHeader && "font-semibold"), children: localized(account.name) }), _jsx(TableCell, { children: t(`accounting.types.${account.type}`) }), _jsxs(TableCell, { className: "space-x-1", children: [account.isHeader ? _jsx(Badge, { tone: "neutral", children: t("accounting.header") }) : null, account.isControl ? _jsx(Badge, { tone: "accent", children: account.subledgerType ?? t("accounting.control") }) : null, !account.allowManualPosting && !account.isHeader ? _jsx(Badge, { tone: "neutral", children: t("accounting.documentsOnly") }) : null, !account.isActive ? _jsx(Badge, { tone: "danger", children: t("common.inactive") }) : null] })] }, account.id))) })] }), _jsx("aside", { className: "flex flex-col gap-3 rounded-lg border border-border bg-surface p-4", "aria-label": t("accounting.accountDetails"), children: selected ? (_jsxs(_Fragment, { children: [_jsxs("div", { children: [_jsxs("h2", { className: "text-base font-semibold", dir: "auto", children: [_jsx("span", { dir: "ltr", children: selected.code }), " ", localized(selected.name)] }), _jsxs("p", { className: "text-sm text-fg-muted", children: [t(`accounting.types.${selected.type}`), selected.defaultRole ? ` · ${selected.defaultRole}` : ""] })] }), _jsxs("div", { className: "flex flex-wrap gap-2", children: [_jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setEditing({ id: selected.id, form: toForm(selected) }); }, "data-testid": "edit-account", children: t("accounting.edit") }), !selected.isHeader ? (_jsx(Button, { variant: "secondary", asChild: true, children: _jsx(Link, { to: "/accounting/ledger", search: { companyId, accountId: selected.id, to: today() }, children: t("accounting.ledger") }) })) : null] }), !selected.isHeader ? (_jsxs("section", { className: "flex flex-col gap-2", "data-testid": "dimension-rules", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("accounting.dimensionRules") }), (accountRules.data ?? []).length === 0 ? _jsx("p", { className: "text-sm text-fg-muted", children: t("accounting.noDimensionRules") }) : null, _jsx("ul", { className: "flex flex-col gap-1 text-sm", children: (accountRules.data ?? []).map((rule) => (_jsxs("li", { className: "flex items-center justify-between gap-2", children: [_jsxs("span", { children: [_jsx("span", { dir: "ltr", children: rule.dimensionCode }), " \u00B7 ", t(`accounting.rules.${rule.rule}`)] }), _jsx(Button, { variant: "ghost", size: "sm", onClick: () => { saveRules.mutate((accountRules.data ?? []).filter((r) => r.dimensionId !== rule.dimensionId)); }, children: t("common.delete") })] }, rule.dimensionId))) }), _jsxs("form", { className: "flex flex-wrap items-end gap-2", onSubmit: (event) => {
                                                event.preventDefault();
                                                if (ruleDraft.dimensionCode) {
                                                    saveRules.mutate([...(accountRules.data ?? []).filter((r) => r.dimensionCode !== ruleDraft.dimensionCode), { dimensionId: "", dimensionCode: ruleDraft.dimensionCode, rule: ruleDraft.rule, defaultValueId: null }]);
                                                }
                                            }, children: [_jsx(Field, { label: t("accounting.dimension"), children: _jsxs(SelectField, { value: ruleDraft.dimensionCode, onChange: (e) => { setRuleDraft({ ...ruleDraft, dimensionCode: e.target.value }); }, "data-testid": "rule-dimension", children: [_jsx("option", { value: "", children: "\u2014" }), (dimensions.data ?? []).map((d) => (_jsx("option", { value: d.code, children: localized(d.name) }, d.id)))] }) }), _jsx(Field, { label: t("accounting.rule"), children: _jsx(SelectField, { value: ruleDraft.rule, onChange: (e) => { setRuleDraft({ ...ruleDraft, rule: e.target.value }); }, children: rules.map((r) => (_jsx("option", { value: r, children: t(`accounting.rules.${r}`) }, r))) }) }), _jsx(Button, { type: "submit", variant: "secondary", loading: saveRules.isPending, "data-testid": "add-rule", children: t("accounting.addRule") })] })] })) : null] })) : (_jsx("p", { className: "text-sm text-fg-muted", children: t("accounting.selectAccount") })) })] })) : null, _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: editing ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: editing.id ? t("accounting.editAccount") : t("accounting.newAccount") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("accounting.accountCode"), required: true, error: problem?.fields.code, children: _jsx(TextField, { value: editing.form.code, onChange: (e) => { setForm({ code: e.target.value }); }, required: true, dir: "ltr", "data-testid": "account-code" }) }), _jsx(Field, { label: t("accounting.parentCode"), error: problem?.fields.parentCode, children: _jsx(TextField, { value: editing.form.parentCode, onChange: (e) => { setForm({ parentCode: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.nameEn"), required: true, error: problem?.fields.name, children: _jsx(TextField, { value: editing.form.nameEn, onChange: (e) => { setForm({ nameEn: e.target.value }); }, required: true, "data-testid": "account-name-en" }) }), _jsx(Field, { label: t("accounting.nameAr"), children: _jsx(TextField, { value: editing.form.nameAr, onChange: (e) => { setForm({ nameAr: e.target.value }); }, dir: "rtl" }) }), _jsx(Field, { label: t("accounting.type"), required: true, children: _jsx(SelectField, { value: editing.form.type, onChange: (e) => { setForm({ type: e.target.value }); }, children: types.map((type) => (_jsx("option", { value: type, children: t(`accounting.types.${type}`) }, type))) }) }), _jsx(Field, { label: t("accounting.subledger"), error: problem?.fields.subledgerType, children: _jsx(SelectField, { value: editing.form.subledgerType, onChange: (e) => { setForm({ subledgerType: e.target.value, isControl: Boolean(e.target.value) }); }, children: subledgers.map((s) => (_jsx("option", { value: s, children: s || t("accounting.notControl") }, s))) }) }), _jsx(Field, { label: t("accounting.defaultRole"), error: problem?.fields.defaultRole, children: _jsx(TextField, { value: editing.form.defaultRole, onChange: (e) => { setForm({ defaultRole: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.currencyRestriction"), error: problem?.fields.currencyRestriction, children: _jsx(TextField, { value: editing.form.currencyRestriction, onChange: (e) => { setForm({ currencyRestriction: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) })] }), _jsxs("div", { className: "flex flex-wrap gap-6 text-sm", children: [_jsxs("label", { className: "flex items-center gap-2", children: [_jsx(Checkbox, { checked: editing.form.isHeader, onCheckedChange: (checked) => { setForm({ isHeader: checked === true }); } }), t("accounting.header")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx(Checkbox, { checked: editing.form.allowManualPosting, onCheckedChange: (checked) => { setForm({ allowManualPosting: checked === true }); } }), t("accounting.allowManualPosting")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx(Checkbox, { checked: editing.form.isActive, onCheckedChange: (checked) => { setForm({ isActive: checked === true }); } }), t("common.active")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: saveAccount.isPending, "data-testid": "save-account", children: t("common.save") })] })] })) : null }) })] }));
}
