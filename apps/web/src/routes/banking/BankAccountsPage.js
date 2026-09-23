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
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
/** Bank, cash and petty-cash accounts (roadmap 4.7): each on its control account with the bank transactions as its subledger. */
export function BankAccountsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const list = useBankAccounts(companyId);
    const detail = useQuery({
        queryKey: ["bank-account", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts/{bankAccountId}", { params: { path: { bankAccountId: openId ?? "" } } })),
    });
    const transactions = useQuery({
        queryKey: ["bank-transactions", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts/{bankAccountId}/transactions", { params: { path: { bankAccountId: openId ?? "" } } })),
    });
    const save = useMutation({
        mutationFn: async (f) => {
            const body = { companyId, code: f.code, name: { en: f.nameEn, ar: f.nameAr }, kind: f.kind, currency: f.currency, bankName: f.bankName || null, branchName: f.branchName || null, accountNumber: f.accountNumber || null, iban: f.iban || null, swift: f.swift || null, isActive: f.isActive };
            return f.id ? unwrap(await api.PUT("/api/v1/banking/bank-accounts/{bankAccountId}", { params: { path: { bankAccountId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/bank-accounts", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await Promise.all([["bank-accounts"], ["bank-account"]].map((key) => queryClient.invalidateQueries({ queryKey: key }))); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "code", header: t("banking.code"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.code }) },
        { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 110, cell: ({ row }) => t(`banking.kinds.${row.original.kind}`) },
        { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
        { id: "gl", accessorKey: "glAccountCode", header: t("banking.glAccount"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.glAccountCode }) },
        { id: "balance", accessorKey: "balanceTc", header: t("banking.balance"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.balanceTc, row.original.currency) }) },
        { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ], [t]);
    const blank = () => ({ id: null, code: "", nameEn: "", nameAr: "", kind: "bank", currency: companies.find((c) => c.id === companyId)?.functionalCurrency ?? "", bankName: "", branchName: "", accountNumber: "", iban: "", swift: "", isActive: true });
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const a = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.bankAccounts"), description: t("banking.accountsDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setForm(blank()); }, disabled: !companyId, "data-testid": "new-bank-account", children: [_jsx(Plus, { "aria-hidden": "true" }), t("banking.newAccount")] }) }), _jsx("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: _jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }) }), _jsx(DataGrid, { label: "nav.bankAccounts", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("banking.emptyAccounts"), emptyDescription: t("banking.emptyAccountsDescription"), onOpen: (row) => { setProblem(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("banking.editAccount") : t("banking.newAccount") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("banking.code"), required: true, children: _jsx(TextField, { value: form.code, onChange: (e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }, dir: "ltr", required: true, "data-testid": "bank-code" }) }), _jsx(Field, { label: t("purchasing.kind"), required: true, children: _jsx(SelectField, { value: form.kind, onChange: (e) => { setForm({ ...form, kind: e.target.value }); }, "data-testid": "bank-kind", children: ["bank", "cash", "petty_cash"].map((k) => (_jsx("option", { value: k, children: t(`banking.kinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("banking.nameEn"), required: true, children: _jsx(TextField, { value: form.nameEn, onChange: (e) => { setForm({ ...form, nameEn: e.target.value }); }, dir: "ltr", required: true, "data-testid": "bank-name-en" }) }), _jsx(Field, { label: t("banking.nameAr"), required: true, children: _jsx(TextField, { value: form.nameAr, onChange: (e) => { setForm({ ...form, nameAr: e.target.value }); }, dir: "rtl", required: true, "data-testid": "bank-name-ar" }) }), _jsx(Field, { label: t("partners.currency"), required: true, children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, required: true, "data-testid": "bank-currency" }) }), _jsx(Field, { label: t("banking.bankName"), children: _jsx(TextField, { value: form.bankName, onChange: (e) => { setForm({ ...form, bankName: e.target.value }); }, "data-testid": "bank-bank-name" }) }), _jsx(Field, { label: t("banking.iban"), children: _jsx(TextField, { value: form.iban, onChange: (e) => { setForm({ ...form, iban: e.target.value }); }, dir: "ltr", "data-testid": "bank-iban" }) }), _jsx(Field, { label: t("banking.swift"), children: _jsx(TextField, { value: form.swift, onChange: (e) => { setForm({ ...form, swift: e.target.value }); }, dir: "ltr", "data-testid": "bank-swift" }) }), _jsx(Field, { label: t("banking.accountNumber"), children: _jsx(TextField, { value: form.accountNumber, onChange: (e) => { setForm({ ...form, accountNumber: e.target.value }); }, dir: "ltr", "data-testid": "bank-account-number" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: form.isActive, onChange: (e) => { setForm({ ...form, isActive: e.target.checked }); }, "data-testid": "bank-active" }), t("common.active")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-bank-account", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: a ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "bank-account-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: a.code }), " \u00B7 ", _jsx("span", { dir: "auto", children: localized(a.name) })] }) }), _jsx(KeyValues, { entries: [
                                    [t("purchasing.kind"), t(`banking.kinds.${a.kind}`)],
                                    [t("partners.currency"), a.currency],
                                    [t("banking.glAccount"), a.glAccountCode],
                                    [t("banking.bankName"), a.bankName ?? "—"],
                                    [t("banking.iban"), a.iban ?? "—"],
                                    [t("banking.balance"), _jsxs("span", { "data-testid": "bank-balance", children: [formatMoney(a.balanceTc, a.currency), a.currency !== a.functionalCurrency ? ` (${formatMoney(a.balanceFc, a.functionalCurrency)})` : ""] }, "bal")],
                                ] }), _jsx("h3", { className: "text-sm font-semibold", children: t("banking.transactions") }), (transactions.data ?? []).length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("banking.noTransactions") }) : (_jsxs(Table, { "data-testid": "bank-transactions", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.postingDate") }), _jsx(TableHead, { children: t("purchasing.kind") }), _jsx(TableHead, { children: t("banking.reference") }), _jsx(TableHead, { children: t("purchasing.amount") })] }) }), _jsx(TableBody, { children: (transactions.data ?? []).map((tx) => (_jsxs(TableRow, { "data-testid": "bank-transaction-row", children: [_jsx(TableCell, { dir: "ltr", children: formatDate(tx.postingDate) }), _jsx(TableCell, { children: t(`banking.transactionKinds.${tx.kind}`) }), _jsx(TableCell, { dir: "ltr", children: tx.reference ?? "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(tx.amountTc, a.currency) })] }, tx.id))) })] })), _jsx(DialogFooter, { children: _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setForm({ id: a.id, code: a.code, nameEn: a.name.en ?? "", nameAr: a.name.ar ?? "", kind: a.kind, currency: a.currency, bankName: a.bankName ?? "", branchName: a.branchName ?? "", accountNumber: "", iban: a.iban ?? "", swift: a.swift ?? "", isActive: a.isActive }); }, "data-testid": "edit-bank-account", children: t("common.edit") }) })] })) : null }) })] }));
}
