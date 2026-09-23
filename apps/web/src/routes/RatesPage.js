import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatDate, formatNumber } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
export function RatesPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [open, setOpen] = useState(false);
    const [form, setForm] = useState({ rateType: "spot", fromCurrency: "USD", toCurrency: "IQD", validFrom: new Date().toISOString().slice(0, 10), rate: "", reason: "" });
    const [problem, setProblem] = useState(null);
    const rates = useQuery({ queryKey: ["rates"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rates", { params: { query: { limit: 500 } } })) });
    const rateTypes = useQuery({ queryKey: ["rate-types"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rate-types")) });
    const currencies = useQuery({ queryKey: ["currencies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/currencies")) });
    const providers = useQuery({ queryKey: ["rate-providers"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/rates/providers")) });
    const save = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/organization/rates", { body: { rateType: form.rateType, fromCurrency: form.fromCurrency, toCurrency: form.toCurrency, validFrom: form.validFrom, rate: form.rate, reason: form.reason || null } })),
        onSuccess: async () => {
            setOpen(false);
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["rates"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const importRates = useMutation({
        mutationFn: async (provider) => unwrap(await api.POST("/api/v1/organization/rates/import", { body: { provider, rateType: "spot" } })),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["rates"] }),
    });
    const columns = useMemo(() => [
        { id: "validFrom", accessorKey: "validFrom", header: t("rates.validFrom"), size: 120, cell: ({ row }) => formatDate(row.original.validFrom) },
        { id: "rateType", accessorKey: "rateType", header: t("rates.type"), size: 100 },
        { id: "pair", accessorFn: (row) => `${row.fromCurrency} → ${row.toCurrency}`, header: t("rates.pair"), size: 120 },
        { id: "rate", accessorKey: "rate", header: t("rates.rate"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", children: formatNumber(row.original.rate, { maximumFractionDigits: 6 }) }) },
        { id: "source", accessorKey: "source", header: t("rates.source"), size: 120 },
        { id: "reason", accessorKey: "reason", header: t("common.reason"), size: 200 },
    ], [t]);
    const submit = (event) => {
        event.preventDefault();
        save.mutate();
    };
    const activeCurrencies = (currencies.data ?? []).filter((c) => c.isActive);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.rates"), description: t("rates.description"), actions: _jsxs(_Fragment, { children: [(providers.data ?? []).map((provider) => (_jsxs(Button, { variant: "secondary", loading: importRates.isPending, onClick: () => { importRates.mutate(provider); }, children: [_jsx(Download, { "aria-hidden": "true" }), t("rates.import", { provider })] }, provider))), _jsxs(Button, { onClick: () => { setProblem(null); setOpen(true); }, children: [_jsx(Plus, { "aria-hidden": "true" }), t("rates.new")] })] }) }), importRates.data ? (_jsx("p", { className: "mb-3 text-sm text-fg-muted", role: "status", children: t("rates.imported", { count: importRates.data.imported }) })) : null, _jsx(DataGrid, { label: "nav.rates", columns: columns, data: rates.data ?? [], rowKey: (row) => row.id, loading: rates.isPending, emptyTitle: t("rates.emptyTitle"), emptyDescription: t("rates.emptyDescription") }), _jsx(Dialog, { open: open, onOpenChange: setOpen, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: _jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("rates.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("rates.type"), required: true, error: problem?.fields.rateType, children: _jsx(SelectField, { value: form.rateType, onChange: (e) => { setForm({ ...form, rateType: e.target.value }); }, children: (rateTypes.data ?? []).map((type) => (_jsx("option", { value: type.code, children: type.code }, type.code))) }) }), _jsx(Field, { label: t("rates.validFrom"), required: true, error: problem?.fields.validFrom, children: _jsx(TextField, { type: "date", value: form.validFrom, onChange: (e) => { setForm({ ...form, validFrom: e.target.value }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("rates.from"), required: true, error: problem?.fields.fromCurrency, children: _jsx(SelectField, { value: form.fromCurrency, onChange: (e) => { setForm({ ...form, fromCurrency: e.target.value }); }, children: activeCurrencies.map((c) => (_jsx("option", { value: c.code, children: c.code }, c.code))) }) }), _jsx(Field, { label: t("rates.to"), required: true, error: problem?.fields.toCurrency, children: _jsx(SelectField, { value: form.toCurrency, onChange: (e) => { setForm({ ...form, toCurrency: e.target.value }); }, children: activeCurrencies.map((c) => (_jsx("option", { value: c.code, children: c.code }, c.code))) }) }), _jsx(Field, { label: t("rates.rate"), required: true, error: problem?.fields.rate, children: _jsx(TextField, { inputMode: "decimal", value: form.rate, onChange: (e) => { setForm({ ...form, rate: e.target.value }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("common.reason"), children: _jsx(TextField, { value: form.reason, onChange: (e) => { setForm({ ...form, reason: e.target.value }); } }) })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setOpen(false); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, children: t("common.save") })] })] }) }) })] }));
}
