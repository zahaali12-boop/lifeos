import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { ItemStatus, useBankAccounts, useOpenItems } from "../payables/shared";
import { num, useSuppliers } from "../purchasing/shared";
/** Supplier payments and advances (roadmap 4.7): invoice items settled at their booked value with discounts, withholding at payment, charges and realised FX; the rest on account; reversible. */
export function PaymentsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [reversal, setReversal] = useState(null);
    const suppliers = useSuppliers(companyId);
    const banks = useBankAccounts(companyId);
    const payable = useOpenItems(companyId, form?.partnerId ?? "", "live");
    const list = useQuery({
        queryKey: ["payments", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/banking/payments", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["payment", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/banking/payments/{paymentId}", { params: { path: { paymentId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["payments"], ["payment"], ["open-items"], ["open-item"], ["bank-accounts"], ["bank-account"], ["proposals"], ["proposal"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const body = {
                companyId,
                partnerId: f.partnerId,
                bankAccountId: f.bankAccountId,
                kind: f.kind,
                paymentDate: f.paymentDate || null,
                method: f.method,
                reference: f.reference || null,
                currency: f.currency || null,
                exchangeRate: f.exchangeRate ? num(f.exchangeRate) : null,
                lines: f.kind === "supplier_advance" ? [] : f.lines.filter((l) => num(l.amount) > 0).map((l) => ({ openItemId: l.openItemId, amount: num(l.amount) })),
                onAccount: num(f.onAccount),
                charges: num(f.charges),
                bankAmount: f.bankAmount ? num(f.bankAmount) : null,
                applyWht: true,
            };
            return f.id ? unwrap(await api.PUT("/api/v1/banking/payments/{paymentId}", { params: { path: { paymentId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/payments", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { paymentId: input.id } };
            switch (input.action) {
                case "post": return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params }));
                case "reverse": return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/reverse", { params, body: { reason: input.reason ?? "" } }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/banking/payments/{paymentId}", { params }));
                    return null;
                }
            }
        },
        onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) {
            setOpenId(null);
        } await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(ItemStatus, { status: row.original.status }) },
        { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 130, cell: ({ row }) => t(`banking.paymentKinds.${row.original.kind}`) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.bankAccountCode }) },
        { id: "date", accessorKey: "paymentDate", header: t("banking.paymentDate"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.paymentDate) }) },
        { id: "amount", accessorKey: "amountTc", header: t("purchasing.amount"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.amountTc, row.original.currency) }) },
    ], [t]);
    const blank = () => ({ id: null, kind: "supplier_payment", partnerId: "", bankAccountId: "", paymentDate: today(), method: "transfer", reference: "", currency: "", exchangeRate: "", onAccount: "", charges: "", bankAmount: "", lines: [] });
    const patchLine = (index, amount) => { if (form) {
        setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, amount } : l)) });
    } };
    const addItem = (id) => {
        const row = (payable.data ?? []).find((o) => o.item.id === id);
        if (!form || !row || form.lines.some((l) => l.openItemId === id)) {
            return;
        }
        setForm({ ...form, currency: row.item.currency, lines: [...form.lines, { openItemId: id, label: `${row.item.documentNumber} · ${formatMoney(row.item.remainingTc, row.item.currency)}`, remaining: Number(row.item.remainingTc), amount: String(row.item.remainingTc) }] });
    };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const bank = form ? (banks.data ?? []).find((b) => b.id === form.bankAccountId) : undefined;
    const supplierCurrency = form ? ((suppliers.data ?? []).find((s) => s.partnerId === form.partnerId)?.currency ?? "") : "";
    const paymentCurrency = form ? (form.currency !== "" ? form.currency : supplierCurrency) : "";
    const needsBankAmount = bank !== undefined && paymentCurrency !== "" && bank.currency !== paymentCurrency;
    const p = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.payments"), description: t("banking.paymentsDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setForm(blank()); }, disabled: !companyId, "data-testid": "new-payment", children: [_jsx(Plus, { "aria-hidden": "true" }), t("banking.newPayment")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "posted", "reversed"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.payments", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("banking.emptyPayments"), emptyDescription: t("banking.emptyPaymentsDescription"), onOpen: (row) => { setProblem(null); setReversal(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("banking.editPayment") : t("banking.newPayment") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(Field, { label: t("purchasing.kind"), children: _jsxs(SelectField, { value: form.kind, onChange: (e) => { setForm({ ...form, kind: e.target.value, lines: e.target.value === "supplier_advance" ? [] : form.lines }); }, "data-testid": "payment-kind", children: [_jsx("option", { value: "supplier_payment", children: t("banking.paymentKinds.supplier_payment") }), _jsx("option", { value: "supplier_advance", children: t("banking.paymentKinds.supplier_advance") })] }) }), _jsx(Field, { label: t("partners.supplier"), required: true, children: _jsxs(SelectField, { value: form.partnerId, onChange: (e) => { setForm({ ...form, partnerId: e.target.value, lines: [], currency: "" }); }, required: true, disabled: Boolean(form.id), "data-testid": "payment-supplier", children: [_jsx("option", { value: "", children: "\u2014" }), (suppliers.data ?? []).map((sup) => (_jsxs("option", { value: sup.partnerId, children: [sup.partnerCode, " \u00B7 ", localized(sup.partnerName)] }, sup.partnerId)))] }) }), _jsx(Field, { label: t("nav.bankAccounts"), required: true, children: _jsxs(SelectField, { value: form.bankAccountId, onChange: (e) => { setForm({ ...form, bankAccountId: e.target.value }); }, required: true, "data-testid": "payment-bank", children: [_jsx("option", { value: "", children: "\u2014" }), (banks.data ?? []).map((b) => (_jsxs("option", { value: b.id, children: [b.code, " \u00B7 ", b.currency] }, b.id)))] }) }), _jsx(Field, { label: t("banking.paymentDate"), children: _jsx(TextField, { type: "date", value: form.paymentDate, onChange: (e) => { setForm({ ...form, paymentDate: e.target.value }); }, dir: "ltr", "data-testid": "payment-date" }) }), _jsx(Field, { label: t("banking.method"), children: _jsx(SelectField, { value: form.method, onChange: (e) => { setForm({ ...form, method: e.target.value }); }, "data-testid": "payment-method", children: ["transfer", "cash", "cheque"].map((m) => (_jsx("option", { value: m, children: t(`banking.methods.${m}`) }, m))) }) }), _jsx(Field, { label: t("banking.reference"), children: _jsx(TextField, { value: form.reference, onChange: (e) => { setForm({ ...form, reference: e.target.value }); }, dir: "ltr", "data-testid": "payment-reference" }) }), form.kind === "supplier_advance" || form.lines.length === 0 ? (_jsx(Field, { label: t("partners.currency"), description: t("banking.currencyHelp"), children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, "data-testid": "payment-currency" }) })) : null, _jsx(Field, { label: t("banking.exchangeRate"), description: t("banking.exchangeRateHelp"), children: _jsx(TextField, { inputMode: "decimal", value: form.exchangeRate, onChange: (e) => { setForm({ ...form, exchangeRate: e.target.value }); }, dir: "ltr", "data-testid": "payment-rate" }) }), _jsx(Field, { label: form.kind === "supplier_advance" ? t("banking.advanceAmount") : t("banking.onAccount"), required: form.kind === "supplier_advance", children: _jsx(TextField, { inputMode: "decimal", value: form.onAccount, onChange: (e) => { setForm({ ...form, onAccount: e.target.value }); }, dir: "ltr", "data-testid": "payment-on-account" }) }), _jsx(Field, { label: t("banking.charges"), description: bank ? bank.currency : undefined, children: _jsx(TextField, { inputMode: "decimal", value: form.charges, onChange: (e) => { setForm({ ...form, charges: e.target.value }); }, dir: "ltr", "data-testid": "payment-charges" }) }), needsBankAmount ? (_jsx(Field, { label: t("banking.bankAmount"), description: t("banking.bankAmountHelp", { currency: bank.currency }), required: true, children: _jsx(TextField, { inputMode: "decimal", value: form.bankAmount, onChange: (e) => { setForm({ ...form, bankAmount: e.target.value }); }, dir: "ltr", required: true, "data-testid": "payment-bank-amount" }) })) : null] }), form.kind === "supplier_payment" && form.partnerId ? (_jsxs("div", { className: "flex flex-col gap-2 rounded-md border border-border p-3", "data-testid": "payable-items", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("banking.openInvoices") }), (payable.data ?? []).filter((o) => Number(o.item.originalTc) > 0 && !o.item.paymentBlocked).length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("banking.nothingOpen") }) : (_jsx("ul", { className: "flex flex-col gap-1 text-sm", children: (payable.data ?? []).filter((o) => Number(o.item.originalTc) > 0 && !o.item.paymentBlocked).map((o) => (_jsxs("li", { className: "flex flex-wrap items-center gap-3", children: [_jsx("span", { dir: "ltr", children: o.item.documentNumber }), _jsx("span", { dir: "ltr", children: formatDate(o.item.dueDate) }), _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(o.item.remainingTc, o.item.currency) }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { addItem(o.item.id); }, "data-testid": `add-item-${o.item.documentNumber}`, children: t("purchasing.addLine") })] }, o.item.id))) }))] })) : null, form.lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.number") }), _jsx(TableHead, { children: t("purchasing.remaining") }), _jsx(TableHead, { children: t("banking.payNow") })] }) }), _jsx(TableBody, { children: form.lines.map((line, index) => (_jsxs(TableRow, { "data-testid": "payment-line", children: [_jsx(TableCell, { dir: "ltr", children: line.label }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(line.remaining) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("banking.payNow"), inputMode: "decimal", value: line.amount, onChange: (e) => { patchLine(index, e.target.value); }, dir: "ltr", className: "w-32", "data-testid": `pay-amount-${String(index)}` }) })] }, line.openItemId))) })] })) : null, _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, disabled: !form.partnerId || !form.bankAccountId, "data-testid": "save-payment", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setReversal(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: p ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "payment-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: p.number }), _jsx(ItemStatus, { status: p.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("purchasing.kind"), t(`banking.paymentKinds.${p.kind}`)],
                                    [t("partners.supplier"), `${p.partnerCode} · ${localized(p.partnerName)}`],
                                    [t("nav.bankAccounts"), `${p.bankAccountCode} · ${p.bankCurrency}`],
                                    [t("banking.paymentDate"), formatDate(p.paymentDate)],
                                    [t("banking.method"), `${t(`banking.methods.${p.method}`)}${p.reference ? ` · ${p.reference}` : ""}`],
                                    [t("purchasing.amount"), _jsxs("span", { "data-testid": "payment-amount", children: [formatMoney(p.amountTc, p.currency), Number(p.exchangeRate) !== 1 ? ` @ ${formatNumber(p.exchangeRate, { maximumFractionDigits: 6 })}` : ""] }, "amount")],
                                    [t("payables.discount"), formatMoney(p.discountTc, p.currency)],
                                    [t("purchasing.withheld"), formatMoney(p.whtTc, p.currency)],
                                    [t("banking.onAccount"), formatMoney(p.onAccountTc, p.currency)],
                                    [t("banking.charges"), formatMoney(p.chargesBank, p.bankCurrency)],
                                    [t("banking.bankAmount"), _jsx("span", { "data-testid": "payment-bank-amount-value", children: formatMoney(p.bankAmount, p.bankCurrency) }, "bank")],
                                    ...(p.reversalReason ? [[t("purchasing.reversalReason"), p.reversalReason]] : []),
                                ] }), p.lines.length > 0 ? (_jsxs(Table, { "data-testid": "payment-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.number") }), _jsx(TableHead, { children: t("purchasing.dueDate") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("payables.discount") }), _jsx(TableHead, { children: t("purchasing.withheld") }), _jsx(TableHead, { children: t("banking.cash") })] }) }), _jsx(TableBody, { children: p.lines.map((l) => (_jsxs(TableRow, { "data-testid": "payment-line-row", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsxs(TableCell, { dir: "ltr", children: [l.documentNumber, Number(l.instalment) > 1 ? ` / ${String(l.instalment)}` : ""] }), _jsx(TableCell, { dir: "ltr", children: formatDate(l.dueDate) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.amountTc, p.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.discountTc, p.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.whtTc, p.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.cashTc, p.currency) })] }, l.id))) })] })) : null, reversal !== null ? (_jsx(Field, { label: t("purchasing.reversalReason"), required: true, children: _jsx(TextField, { value: reversal, onChange: (e) => { setReversal(e.target.value); }, "data-testid": "reversal-reason" }) })) : null, _jsxs(DialogFooter, { children: [p.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setForm({ id: p.id, kind: p.kind, partnerId: p.partnerId, bankAccountId: p.bankAccountId, paymentDate: p.paymentDate, method: p.method, reference: p.reference ?? "", currency: p.currency, exchangeRate: "", onAccount: p.onAccountTc ? String(p.onAccountTc) : "", charges: p.chargesBank ? String(p.chargesBank) : "", bankAmount: p.bankCurrency !== p.currency ? String(p.bankAmount) : "", lines: p.lines.map((l) => ({ openItemId: l.openItemId, label: `${l.documentNumber} · ${formatMoney(l.itemRemainingTc, p.currency)}`, remaining: Number(l.itemRemainingTc), amount: String(l.amountTc) })) }); }, "data-testid": "edit-payment", children: t("common.edit") }) : null, p.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: p.id, action: "delete" }); }, loading: act.isPending, "data-testid": "delete-payment", children: t("purchasing.deleteDraft") }) : null, p.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ id: p.id, action: "post" }); }, loading: act.isPending, "data-testid": "post-payment", children: t("banking.postPayment") }) : null, p.status === "posted" && reversal === null ? _jsx(Button, { variant: "secondary", onClick: () => { setReversal(""); }, "data-testid": "reverse-payment", children: t("purchasing.reverse") }) : null, reversal !== null ? _jsx(Button, { onClick: () => { act.mutate({ id: p.id, action: "reverse", reason: reversal }); }, loading: act.isPending, disabled: !reversal.trim(), "data-testid": "confirm-reverse", children: t("purchasing.reverseNow") }) : null] })] })) : null }) })] }));
}
