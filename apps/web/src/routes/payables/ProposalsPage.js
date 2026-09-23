import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { ItemStatus, useBankAccounts } from "./shared";
/** Payment proposals (roadmap 4.7): what is due by a date in one currency, edited line by line, approved, then drafted into payments per supplier. */
export function ProposalsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [edits, setEdits] = useState({});
    const [payFrom, setPayFrom] = useState(null);
    const suppliers = useSuppliers(companyId);
    const banks = useBankAccounts(companyId);
    const list = useQuery({
        queryKey: ["proposals", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/proposals", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["proposal", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/proposals/{proposalId}", { params: { path: { proposalId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["proposals"], ["proposal"], ["payments"], ["open-items"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const create = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/payables/proposals", { body: { companyId, payThrough: f.payThrough, currency: f.currency, partnerId: f.partnerId || null, bankAccountId: f.bankAccountId || null, takeDiscounts: f.takeDiscounts } })),
        onSuccess: async (saved) => { setProblem(null); setForm(null); setEdits({}); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { proposalId: input.id } };
            switch (input.action) {
                case "lines": return unwrap(await api.PUT("/api/v1/payables/proposals/{proposalId}/lines", { params, body: { lines: Object.entries(edits).map(([lineId, e]) => ({ lineId, selected: e.selected, amountTc: e.amount ? num(e.amount) : null })) } }));
                case "approve": return unwrap(await api.POST("/api/v1/payables/proposals/{proposalId}/approve", { params }));
                case "cancel": return unwrap(await api.POST("/api/v1/payables/proposals/{proposalId}/cancel", { params }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/payables/proposals/{proposalId}", { params }));
                    return null;
                }
                case "pay": {
                    unwrap(await api.POST("/api/v1/banking/payments/from-proposal", { body: { proposalId: input.id, bankAccountId: input.bankAccountId ?? "", method: "transfer" } }));
                    return undefined;
                }
            }
        },
        onSuccess: async (result) => { setProblem(null); setEdits({}); setPayFrom(null); if (result === null) {
            setOpenId(null);
        } await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(ItemStatus, { status: row.original.status }) },
        { id: "payThrough", accessorKey: "payThrough", header: t("payables.payThrough"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.payThrough) }) },
        { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
        { id: "lines", accessorFn: (r) => r.lines.length, header: t("purchasing.lines"), size: 80, cell: ({ row }) => String(row.original.lines.length) },
        { id: "total", accessorKey: "totalTc", header: t("payables.toPay"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalTc, row.original.currency) }) },
        { id: "discount", accessorKey: "discountTc", header: t("payables.discounts"), size: 130, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.discountTc, row.original.currency) }) },
    ], [t]);
    const submit = (event) => { event.preventDefault(); if (form) {
        create.mutate(form);
    } };
    const p = detail.data;
    const edited = (lineId, fallback) => edits[lineId] ?? { selected: fallback.selected, amount: String(fallback.amount) };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.paymentProposals"), description: t("payables.proposalsDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setForm({ payThrough: today(), currency: companies.find((c) => c.id === companyId)?.functionalCurrency ?? "", partnerId: "", bankAccountId: "", takeDiscounts: true }); }, disabled: !companyId, "data-testid": "new-proposal", children: [_jsx(Plus, { "aria-hidden": "true" }), t("payables.newProposal")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "approved", "executed", "cancelled"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.paymentProposals", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("payables.emptyProposals"), emptyDescription: t("payables.emptyProposalsDescription"), onOpen: (row) => { setProblem(null); setEdits({}); setPayFrom(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("payables.newProposal") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("payables.payThrough"), required: true, children: _jsx(TextField, { type: "date", value: form.payThrough, onChange: (e) => { setForm({ ...form, payThrough: e.target.value }); }, dir: "ltr", required: true, "data-testid": "proposal-pay-through" }) }), _jsx(Field, { label: t("partners.currency"), required: true, children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, required: true, "data-testid": "proposal-currency" }) }), _jsx(Field, { label: t("partners.supplier"), children: _jsxs(SelectField, { value: form.partnerId, onChange: (e) => { setForm({ ...form, partnerId: e.target.value }); }, "data-testid": "proposal-supplier", children: [_jsx("option", { value: "", children: t("common.all") }), (suppliers.data ?? []).map((sup) => (_jsxs("option", { value: sup.partnerId, children: [sup.partnerCode, " \u00B7 ", localized(sup.partnerName)] }, sup.partnerId)))] }) }), _jsx(Field, { label: t("nav.bankAccounts"), children: _jsxs(SelectField, { value: form.bankAccountId, onChange: (e) => { setForm({ ...form, bankAccountId: e.target.value }); }, "data-testid": "proposal-bank", children: [_jsx("option", { value: "", children: "\u2014" }), (banks.data ?? []).map((b) => (_jsxs("option", { value: b.id, children: [b.code, " \u00B7 ", localized(b.name)] }, b.id)))] }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: form.takeDiscounts, onChange: (e) => { setForm({ ...form, takeDiscounts: e.target.checked }); }, "data-testid": "proposal-discounts" }), t("payables.takeDiscounts")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, "data-testid": "save-proposal", children: t("payables.propose") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: p ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "proposal-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: p.number }), _jsx(ItemStatus, { status: p.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("payables.payThrough"), formatDate(p.payThrough)],
                                    [t("partners.currency"), p.currency],
                                    [t("payables.toPay"), _jsx("span", { "data-testid": "proposal-total", children: formatMoney(p.totalTc, p.currency) }, "total")],
                                    [t("payables.discounts"), formatMoney(p.discountTc, p.currency)],
                                ] }), _jsxs(Table, { "data-testid": "proposal-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("payables.pay") }), _jsx(TableHead, { children: t("partners.supplier") }), _jsx(TableHead, { children: t("purchasing.number") }), _jsx(TableHead, { children: t("purchasing.dueDate") }), _jsx(TableHead, { children: t("purchasing.remaining") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("payables.discount") })] }) }), _jsx(TableBody, { children: p.lines.map((l, index) => {
                                            const e = edited(l.id, { selected: l.selected, amount: Number(l.amountTc) });
                                            return (_jsxs(TableRow, { "data-testid": "proposal-line", children: [_jsx(TableCell, { children: p.status === "draft" ? _jsx("input", { type: "checkbox", "aria-label": t("payables.pay"), checked: e.selected, onChange: (ev) => { setEdits({ ...edits, [l.id]: { ...e, selected: ev.target.checked } }); }, "data-testid": `line-selected-${String(index)}` }) : (l.selected ? "✓" : "") }), _jsxs(TableCell, { dir: "auto", children: [l.partnerCode, " \u00B7 ", localized(l.partnerName)] }), _jsx(TableCell, { dir: "ltr", children: l.documentNumber }), _jsx(TableCell, { dir: "ltr", children: formatDate(l.dueDate) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.remainingTc, p.currency) }), _jsx(TableCell, { children: p.status === "draft" ? _jsx(TextField, { "aria-label": t("purchasing.amount"), inputMode: "decimal", value: e.amount, onChange: (ev) => { setEdits({ ...edits, [l.id]: { ...e, amount: ev.target.value } }); }, dir: "ltr", className: "w-32", "data-testid": `line-amount-${String(index)}` }) : _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(l.amountTc, p.currency) }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.discountTc, p.currency) })] }, l.id));
                                        }) })] }), payFrom !== null ? (_jsx(Field, { label: t("nav.bankAccounts"), required: true, children: _jsxs(SelectField, { value: payFrom, onChange: (e) => { setPayFrom(e.target.value); }, "data-testid": "pay-bank", children: [_jsx("option", { value: "", children: "\u2014" }), (banks.data ?? []).map((b) => (_jsxs("option", { value: b.id, children: [b.code, " \u00B7 ", localized(b.name), " \u00B7 ", b.currency] }, b.id)))] }) })) : null, _jsxs(DialogFooter, { children: [p.status === "draft" && Object.keys(edits).length > 0 ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ action: "lines", id: p.id }); }, loading: act.isPending, "data-testid": "save-lines", children: t("common.save") }) : null, p.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ action: "delete", id: p.id }); }, loading: act.isPending, "data-testid": "delete-proposal", children: t("purchasing.deleteDraft") }) : null, p.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ action: "approve", id: p.id }); }, loading: act.isPending, "data-testid": "approve-proposal", children: t("payables.approve") }) : null, p.status === "draft" || p.status === "approved" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ action: "cancel", id: p.id }); }, loading: act.isPending, "data-testid": "cancel-proposal", children: t("common.cancel") }) : null, p.status === "approved" && payFrom === null ? _jsx(Button, { onClick: () => { setPayFrom(p.bankAccountId ?? ""); }, "data-testid": "pay-proposal", children: t("payables.payProposal") }) : null, payFrom !== null ? _jsx(Button, { onClick: () => { act.mutate({ action: "pay", id: p.id, bankAccountId: payFrom }); }, loading: act.isPending, disabled: !payFrom, "data-testid": "confirm-pay-proposal", children: t("payables.draftPayments") }) : null] })] })) : null }) })] }));
}
