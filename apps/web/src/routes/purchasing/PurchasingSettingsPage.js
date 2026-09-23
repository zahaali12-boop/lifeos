import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Tabs, useCompanyContext } from "../inventory/shared";
import { num, useDeliveryTerms, usePaymentTerms, useSupplierGroups, useSupplierPostingGroups, useWhtCodes } from "./shared";
const emptySimple = () => ({ id: null, code: "", name: "", nameAr: "", isActive: true, postingGroupId: "", paymentTermsId: "", deliveryTermsId: "", ratePct: "0", withholdAt: "payment", thresholdAmount: "", thresholdCurrency: "" });
const names = (f) => ({ en: f.name, ar: f.nameAr || f.name });
/** Purchasing settings (roadmap 4.1): payment terms with instalments and a due-date preview, delivery terms, supplier groups and withholding tax codes. */
export function PurchasingSettingsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companyId } = useCompanyContext();
    const [tab, setTab] = useState("payment");
    const [problem, setProblem] = useState(null);
    const [terms, setTerms] = useState(null);
    const [simple, setSimple] = useState(null);
    const [preview, setPreview] = useState({ invoiceDate: today(), amount: "1000", currency: "USD" });
    const [schedule, setSchedule] = useState(null);
    const paymentTerms = usePaymentTerms();
    const deliveryTerms = useDeliveryTerms();
    const groups = useSupplierGroups();
    const whtCodes = useWhtCodes();
    const postingGroups = useSupplierPostingGroups();
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const refresh = async (key) => { await queryClient.invalidateQueries({ queryKey: [key] }); };
    const saveTerms = useMutation({
        mutationFn: async (f) => {
            const body = { code: f.code, name: names(f), dueBasis: f.dueBasis, dueDays: num(f.dueDays), earlyDiscountPct: num(f.earlyDiscountPct), earlyDiscountDays: num(f.earlyDiscountDays), businessDaysOnly: f.businessDaysOnly, isActive: f.isActive, lines: f.lines.map((l) => ({ sequence: num(l.sequence), percentage: num(l.percentage), days: num(l.days) })) };
            return f.id ? unwrap(await api.PUT("/api/v1/partners/payment-terms/{termsId}", { params: { path: { termsId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/payment-terms", { body }));
        },
        onSuccess: async () => { setProblem(null); setTerms(null); await refresh("payment-terms"); },
        onError: fail,
    });
    const previewSchedule = useMutation({
        mutationFn: async (termsId) => unwrap(await api.POST("/api/v1/partners/payment-terms/{termsId}/schedule", { params: { path: { termsId } }, body: { companyId, invoiceDate: preview.invoiceDate, amount: num(preview.amount), currency: preview.currency } })),
        onSuccess: (result) => { setProblem(null); setSchedule(result); },
        onError: fail,
    });
    const saveSimple = useMutation({
        mutationFn: async (input) => {
            const f = input.form;
            switch (input.kind) {
                case "delivery": {
                    const body = { code: f.code, name: names(f), isActive: f.isActive };
                    return f.id ? unwrap(await api.PUT("/api/v1/partners/delivery-terms/{termsId}", { params: { path: { termsId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/delivery-terms", { body }));
                }
                case "groups": {
                    const body = { code: f.code, name: names(f), isActive: f.isActive, postingGroupId: f.postingGroupId || null, paymentTermsId: f.paymentTermsId || null, deliveryTermsId: f.deliveryTermsId || null };
                    return f.id ? unwrap(await api.PUT("/api/v1/partners/supplier-groups/{groupId}", { params: { path: { groupId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/supplier-groups", { body }));
                }
                case "wht": {
                    const body = { code: f.code, name: names(f), isActive: f.isActive, ratePct: num(f.ratePct), withholdAt: f.withholdAt, thresholdAmount: f.thresholdAmount ? num(f.thresholdAmount) : null, thresholdCurrency: f.thresholdAmount ? f.thresholdCurrency || null : null };
                    return f.id ? unwrap(await api.PUT("/api/v1/partners/wht-codes/{whtCodeId}", { params: { path: { whtCodeId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/wht-codes", { body }));
                }
            }
        },
        onSuccess: async (_, input) => {
            setProblem(null);
            setSimple(null);
            await refresh(input.kind === "delivery" ? "delivery-terms" : input.kind === "groups" ? "supplier-groups" : "wht-codes");
        },
        onError: fail,
    });
    const termsColumns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "basis", accessorKey: "dueBasis", header: t("partners.dueBasis"), size: 180, cell: ({ row }) => t(`partners.dueBases.${row.original.dueBasis}`, { defaultValue: row.original.dueBasis }) },
        { id: "days", accessorKey: "dueDays", header: t("partners.dueDays"), size: 90, cell: ({ row }) => (row.original.lines.length > 0 ? row.original.lines.map((l) => `${String(l.percentage)}%/${String(l.days)}d`).join(" · ") : String(row.original.dueDays)) },
        { id: "discount", accessorKey: "earlyDiscountPct", header: t("partners.earlyDiscountPct"), size: 120, cell: ({ row }) => (Number(row.original.earlyDiscountPct) > 0 ? `${String(row.original.earlyDiscountPct)}% / ${String(row.original.earlyDiscountDays)}d` : "") },
        { id: "system", accessorKey: "isSystem", header: t("partners.system"), size: 80, cell: ({ row }) => (row.original.isSystem ? "✓" : "") },
        { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
    ], [t]);
    const deliveryColumns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 300, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "system", accessorKey: "isSystem", header: t("partners.system"), size: 80, cell: ({ row }) => (row.original.isSystem ? "✓" : "") },
        { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
    ], [t]);
    const groupColumns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "terms", accessorKey: "paymentTermsId", header: t("partners.paymentTerms"), size: 120, cell: ({ row }) => paymentTerms.data?.find((p) => p.id === row.original.paymentTermsId)?.code ?? "" },
        { id: "delivery", accessorKey: "deliveryTermsId", header: t("partners.deliveryTerms"), size: 120, cell: ({ row }) => deliveryTerms.data?.find((d) => d.id === row.original.deliveryTermsId)?.code ?? "" },
        { id: "suppliers", accessorKey: "suppliers", header: t("partners.suppliersCount"), size: 100, cell: ({ row }) => String(row.original.suppliers) },
        { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
    ], [t, paymentTerms.data, deliveryTerms.data]);
    const whtColumns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "rate", accessorKey: "ratePct", header: t("partners.ratePct"), size: 90, cell: ({ row }) => formatNumber(row.original.ratePct, { maximumFractionDigits: 3 }) },
        { id: "at", accessorKey: "withholdAt", header: t("partners.withholdAt"), size: 110, cell: ({ row }) => t(`partners.withholdAts.${row.original.withholdAt}`, { defaultValue: row.original.withholdAt }) },
        { id: "threshold", accessorKey: "thresholdAmount", header: t("partners.threshold"), size: 160, cell: ({ row }) => (row.original.thresholdAmount === null ? "" : `${formatNumber(row.original.thresholdAmount)} ${row.original.thresholdCurrency ?? ""}`) },
        { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
    ], [t]);
    const openTerms = (p) => {
        setProblem(null);
        setSchedule(null);
        setTerms(p
            ? { id: p.id, code: p.code, name: p.name.en ?? "", nameAr: p.name.ar ?? "", dueBasis: p.dueBasis, dueDays: String(p.dueDays), earlyDiscountPct: String(p.earlyDiscountPct), earlyDiscountDays: String(p.earlyDiscountDays), businessDaysOnly: p.businessDaysOnly, lines: p.lines.map((l) => ({ sequence: String(l.sequence), percentage: String(l.percentage), days: String(l.days) })), isActive: p.isActive }
            : { id: null, code: "", name: "", nameAr: "", dueBasis: "invoice_date", dueDays: "30", earlyDiscountPct: "0", earlyDiscountDays: "0", businessDaysOnly: false, lines: [], isActive: true });
    };
    const openSimple = (kind, row) => {
        setProblem(null);
        const base = emptySimple();
        if (row) {
            base.id = row.id;
            base.code = row.code;
            base.name = row.name.en ?? "";
            base.nameAr = row.name.ar ?? "";
            base.isActive = row.isActive;
            if ("postingGroupId" in row) {
                base.postingGroupId = row.postingGroupId ?? "";
                base.paymentTermsId = row.paymentTermsId ?? "";
                base.deliveryTermsId = row.deliveryTermsId ?? "";
            }
            if ("ratePct" in row) {
                base.ratePct = String(row.ratePct);
                base.withholdAt = row.withholdAt;
                base.thresholdAmount = row.thresholdAmount === null ? "" : String(row.thresholdAmount);
                base.thresholdCurrency = row.thresholdCurrency ?? "";
            }
        }
        setSimple({ kind, form: base });
    };
    const setTermsForm = (patch) => { setTerms((prev) => (prev ? { ...prev, ...patch } : prev)); };
    const setSimpleForm = (patch) => { setSimple((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
    const submitTerms = (event) => { event.preventDefault(); if (terms) {
        saveTerms.mutate(terms);
    } };
    const submitSimple = (event) => { event.preventDefault(); if (simple) {
        saveSimple.mutate(simple);
    } };
    const newLabel = tab === "payment" ? t("partners.newPaymentTerms") : tab === "delivery" ? t("partners.newDeliveryTerms") : tab === "groups" ? t("partners.newGroup") : t("partners.newWhtCode");
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.purchasingSettings"), description: t("partners.settingsDescription"), actions: _jsxs(Button, { onClick: () => { if (tab === "payment") {
                        openTerms(null);
                    }
                    else {
                        openSimple(tab, null);
                    } }, "data-testid": "new-setting", children: [_jsx(Plus, { "aria-hidden": "true" }), newLabel] }) }), _jsx(Tabs, { tabs: [
                    { id: "payment", label: t("partners.tabs.payment"), testId: "tab-payment" },
                    { id: "delivery", label: t("partners.tabs.delivery"), testId: "tab-delivery" },
                    { id: "groups", label: t("partners.tabs.groups"), testId: "tab-groups" },
                    { id: "wht", label: t("partners.tabs.wht"), testId: "tab-wht" },
                ], value: tab, onChange: setTab }), tab === "payment" ? _jsx(DataGrid, { label: "partners.tabs.payment", columns: termsColumns, data: paymentTerms.data ?? [], rowKey: (row) => row.id, loading: paymentTerms.isPending, emptyTitle: t("partners.emptyTerms"), emptyDescription: t("partners.emptyTermsDescription"), onOpen: openTerms }) : null, tab === "delivery" ? _jsx(DataGrid, { label: "partners.tabs.delivery", columns: deliveryColumns, data: deliveryTerms.data ?? [], rowKey: (row) => row.id, loading: deliveryTerms.isPending, emptyTitle: t("partners.emptyTerms"), emptyDescription: t("partners.emptyTermsDescription"), onOpen: (row) => { openSimple("delivery", row); } }) : null, tab === "groups" ? _jsx(DataGrid, { label: "partners.tabs.groups", columns: groupColumns, data: groups.data ?? [], rowKey: (row) => row.id, loading: groups.isPending, emptyTitle: t("partners.emptyTerms"), emptyDescription: t("partners.emptyTermsDescription"), onOpen: (row) => { openSimple("groups", row); } }) : null, tab === "wht" ? _jsx(DataGrid, { label: "partners.tabs.wht", columns: whtColumns, data: whtCodes.data ?? [], rowKey: (row) => row.id, loading: whtCodes.isPending, emptyTitle: t("partners.emptyTerms"), emptyDescription: t("partners.emptyTermsDescription"), onOpen: (row) => { openSimple("wht", row); } }) : null, _jsx(Dialog, { open: Boolean(terms), onOpenChange: (isOpen) => { if (!isOpen) {
                    setTerms(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: terms ? (_jsxs("form", { onSubmit: submitTerms, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: terms.id ? terms.code : t("partners.newPaymentTerms") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("partners.code"), required: true, children: _jsx(TextField, { value: terms.code, onChange: (e) => { setTermsForm({ code: e.target.value }); }, dir: "ltr", required: true, "data-testid": "terms-code" }) }), _jsx(Field, { label: t("partners.name"), required: true, children: _jsx(TextField, { value: terms.name, onChange: (e) => { setTermsForm({ name: e.target.value }); }, required: true, "data-testid": "terms-name" }) }), _jsx(Field, { label: t("partners.nameAr"), children: _jsx(TextField, { value: terms.nameAr, onChange: (e) => { setTermsForm({ nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), _jsx(Field, { label: t("partners.dueBasis"), children: _jsx(SelectField, { value: terms.dueBasis, onChange: (e) => { setTermsForm({ dueBasis: e.target.value }); }, "data-testid": "terms-basis", children: ["invoice_date", "end_of_month", "delivery"].map((b) => (_jsx("option", { value: b, children: t(`partners.dueBases.${b}`) }, b))) }) }), _jsx(Field, { label: t("partners.dueDays"), children: _jsx(TextField, { type: "number", min: 0, value: terms.dueDays, onChange: (e) => { setTermsForm({ dueDays: e.target.value }); }, dir: "ltr", "data-testid": "terms-days" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: terms.businessDaysOnly, onChange: (e) => { setTermsForm({ businessDaysOnly: e.target.checked }); } }), t("partners.businessDaysOnly")] }), _jsx(Field, { label: t("partners.earlyDiscountPct"), children: _jsx(TextField, { inputMode: "decimal", value: terms.earlyDiscountPct, onChange: (e) => { setTermsForm({ earlyDiscountPct: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.earlyDiscountDays"), children: _jsx(TextField, { type: "number", min: 0, value: terms.earlyDiscountDays, onChange: (e) => { setTermsForm({ earlyDiscountDays: e.target.value }); }, dir: "ltr" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: terms.isActive, onChange: (e) => { setTermsForm({ isActive: e.target.checked }); } }), t("common.active")] })] }), _jsxs("div", { className: "flex flex-col gap-2", children: [_jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("partners.instalments") }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setTermsForm({ lines: [...terms.lines, { sequence: String(terms.lines.length + 1), percentage: "", days: "" }] }); }, "data-testid": "add-instalment", children: t("partners.addInstalment") })] }), _jsx("p", { className: "text-xs text-fg-muted", children: t("partners.instalmentsHelp") }), terms.lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.sequence") }), _jsx(TableHead, { children: t("partners.percentage") }), _jsx(TableHead, { children: t("partners.days") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: terms.lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("partners.sequence"), type: "number", min: 1, value: line.sequence, onChange: (e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, sequence: e.target.value } : l)) }); }, dir: "ltr", className: "w-20" }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("partners.percentage"), inputMode: "decimal", value: line.percentage, onChange: (e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, percentage: e.target.value } : l)) }); }, dir: "ltr", className: "w-24", "data-testid": `instalment-pct-${String(index)}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("partners.days"), type: "number", min: 0, value: line.days, onChange: (e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, days: e.target.value } : l)) }); }, dir: "ltr", className: "w-24", "data-testid": `instalment-days-${String(index)}` }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setTermsForm({ lines: terms.lines.filter((_, i) => i !== index) }); }, children: t("workflow.remove") }) })] }, index))) })] })) : null] }), terms.id ? (_jsxs("div", { className: "flex flex-col gap-2 rounded-md border border-border p-3", "data-testid": "schedule-preview", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("partners.preview") }), _jsx("p", { className: "text-xs text-fg-muted", children: t("partners.previewHelp") }), _jsxs("div", { className: "grid gap-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.invoiceDate"), children: _jsx(TextField, { type: "date", value: preview.invoiceDate, onChange: (e) => { setPreview({ ...preview, invoiceDate: e.target.value }); }, dir: "ltr", "data-testid": "preview-date" }) }), _jsx(Field, { label: t("partners.amount"), children: _jsx(TextField, { inputMode: "decimal", value: preview.amount, onChange: (e) => { setPreview({ ...preview, amount: e.target.value }); }, dir: "ltr", "data-testid": "preview-amount" }) }), _jsx(Field, { label: t("partners.currency"), children: _jsx(TextField, { value: preview.currency, onChange: (e) => { setPreview({ ...preview, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "button", variant: "secondary", onClick: () => { if (terms.id) {
                                                        previewSchedule.mutate(terms.id);
                                                    } }, loading: previewSchedule.isPending, disabled: !companyId, "data-testid": "preview-schedule", children: t("partners.preview") }) })] }), schedule ? (_jsx("ul", { className: "text-sm", "data-testid": "schedule-lines", children: schedule.instalments.map((i) => (_jsxs("li", { className: "flex gap-3", children: [_jsx("span", { className: "tabular", dir: "ltr", children: formatDate(i.dueOn) }), _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(i.amount, preview.currency || "USD") }), _jsxs("span", { className: "text-fg-muted", children: [String(i.percentage), "%"] })] }, i.sequence))) })) : null] })) : null, _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setTerms(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: saveTerms.isPending, "data-testid": "save-terms", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(simple), onOpenChange: (isOpen) => { if (!isOpen) {
                    setSimple(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: simple ? (_jsxs("form", { onSubmit: submitSimple, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: simple.form.id ? simple.form.code : newLabel }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("partners.code"), required: true, children: _jsx(TextField, { value: simple.form.code, onChange: (e) => { setSimpleForm({ code: e.target.value }); }, dir: "ltr", required: true, "data-testid": "setting-code" }) }), _jsx(Field, { label: t("partners.name"), required: true, children: _jsx(TextField, { value: simple.form.name, onChange: (e) => { setSimpleForm({ name: e.target.value }); }, required: true, "data-testid": "setting-name" }) }), _jsx(Field, { label: t("partners.nameAr"), children: _jsx(TextField, { value: simple.form.nameAr, onChange: (e) => { setSimpleForm({ nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), simple.kind === "groups" ? (_jsxs(_Fragment, { children: [_jsx(Field, { label: t("partners.postingGroup"), children: _jsxs(SelectField, { value: simple.form.postingGroupId, onChange: (e) => { setSimpleForm({ postingGroupId: e.target.value }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (postingGroups.data ?? []).map((g) => (_jsxs("option", { value: g.id, children: [g.code, " \u00B7 ", localized(g.name)] }, g.id)))] }) }), _jsx(Field, { label: t("partners.paymentTerms"), children: _jsxs(SelectField, { value: simple.form.paymentTermsId, onChange: (e) => { setSimpleForm({ paymentTermsId: e.target.value }); }, "data-testid": "group-payment-terms", children: [_jsx("option", { value: "", children: "\u2014" }), (paymentTerms.data ?? []).filter((p) => p.isActive).map((p) => (_jsx("option", { value: p.id, children: p.code }, p.id)))] }) }), _jsx(Field, { label: t("partners.deliveryTerms"), children: _jsxs(SelectField, { value: simple.form.deliveryTermsId, onChange: (e) => { setSimpleForm({ deliveryTermsId: e.target.value }); }, "data-testid": "group-delivery-terms", children: [_jsx("option", { value: "", children: "\u2014" }), (deliveryTerms.data ?? []).filter((d) => d.isActive).map((d) => (_jsx("option", { value: d.id, children: d.code }, d.id)))] }) })] })) : null, simple.kind === "wht" ? (_jsxs(_Fragment, { children: [_jsx(Field, { label: t("partners.ratePct"), required: true, children: _jsx(TextField, { inputMode: "decimal", value: simple.form.ratePct, onChange: (e) => { setSimpleForm({ ratePct: e.target.value }); }, dir: "ltr", required: true, "data-testid": "wht-rate" }) }), _jsx(Field, { label: t("partners.withholdAt"), children: _jsx(SelectField, { value: simple.form.withholdAt, onChange: (e) => { setSimpleForm({ withholdAt: e.target.value }); }, children: ["invoice", "payment"].map((w) => (_jsx("option", { value: w, children: t(`partners.withholdAts.${w}`) }, w))) }) }), _jsx(Field, { label: t("partners.threshold"), children: _jsx(TextField, { inputMode: "decimal", value: simple.form.thresholdAmount, onChange: (e) => { setSimpleForm({ thresholdAmount: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.thresholdCurrency"), children: _jsx(TextField, { value: simple.form.thresholdCurrency, onChange: (e) => { setSimpleForm({ thresholdCurrency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) })] })) : null, _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: simple.form.isActive, onChange: (e) => { setSimpleForm({ isActive: e.target.checked }); } }), t("common.active")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setSimple(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: saveSimple.isPending, "data-testid": "save-setting", children: t("common.save") })] })] })) : null }) })] }));
}
