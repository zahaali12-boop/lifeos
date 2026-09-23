import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
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
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, PurchaseStatus, useSuppliers } from "./shared";
const editable = (status) => status === "draft" || status === "rejected" || status === "blocked";
/** Supplier invoices (roadmap 4.4): lines picked from uninvoiced receipts and open service lines or entered as expenses, matched against tolerances, blocked breaches waiting for an override, approved, posted and reversed. */
export function InvoicesPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [tab, setTab] = useState("lines");
    const [reversal, setReversal] = useState(null);
    const [credit, setCredit] = useState(null);
    const suppliers = useSuppliers(companyId);
    const list = useQuery({
        queryKey: ["invoices", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const invoicable = useQuery({
        queryKey: ["invoicable", companyId, form?.partnerId ?? ""],
        enabled: Boolean(companyId) && Boolean(form?.partnerId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices/invoicable", { params: { query: { companyId, partnerId: form?.partnerId ?? "" } } })),
    });
    const detail = useQuery({
        queryKey: ["invoice", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices/{invoiceId}", { params: { path: { invoiceId: openId ?? "" } } })),
    });
    const supplierItems = useQuery({
        queryKey: ["open-items", companyId, detail.data?.partnerId ?? ""],
        enabled: Boolean(companyId) && detail.data?.kind === "debit_note" && detail.data.status === "posted",
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: detail.data?.partnerId ?? "", status: "live" } } })),
    });
    const settlements = useQuery({
        queryKey: ["settlements", companyId, openId],
        enabled: Boolean(companyId) && detail.data?.status === "posted" && detail.data.openItems.length > 0,
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/settlements", { params: { query: { companyId, openItemId: detail.data?.openItems[0]?.id ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["invoices"], ["invoice"], ["invoicable"], ["orders"], ["order"], ["receipts"], ["receipt"], ["returns"], ["return"], ["open-items"], ["settlements"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const body = {
                companyId,
                partnerId: f.partnerId,
                kind: f.kind,
                supplierInvoiceNumber: f.supplierInvoiceNumber || null,
                documentDate: f.documentDate || null,
                currency: f.currency || null,
                applyWht: f.applyWht,
                lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ kind: l.kind, quantity: num(l.quantity), unitPrice: num(l.unitPrice), receiptLineId: l.receiptLineId || null, orderLineId: l.orderLineId || null, landedCostChargeId: l.landedCostChargeId || null, returnLineId: l.returnLineId || null, description: l.description || null, discountPct: 0 })),
            };
            return f.id ? unwrap(await api.PUT("/api/v1/purchasing/invoices/{invoiceId}", { params: { path: { invoiceId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/invoices", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { invoiceId: input.id } };
            switch (input.action) {
                case "submit": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/submit", { params }));
                case "post": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/post", { params }));
                case "reverse": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/reverse", { params, body: { reason: input.reason ?? "" } }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/purchasing/invoices/{invoiceId}", { params }));
                    return null;
                }
            }
        },
        onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) {
            setOpenId(null);
        } await refresh(); },
        onError: fail,
    });
    const apply = useMutation({
        mutationFn: async (input) => unwrap(await api.POST("/api/v1/payables/settlements/apply", { body: input })),
        onSuccess: async () => { setProblem(null); setCredit(null); await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 130, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 110, cell: ({ row }) => t(`purchasing.kinds.${row.original.kind}`) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 190, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "reference", accessorKey: "supplierInvoiceNumber", header: t("purchasing.supplierReference"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.supplierInvoiceNumber ?? "" }) },
        { id: "date", accessorKey: "documentDate", header: t("purchasing.documentDate"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.documentDate) }) },
        { id: "due", accessorKey: "dueDate", header: t("purchasing.dueDate"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.dueDate) }) },
        { id: "total", accessorKey: "totalGross", header: t("purchasing.total"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalGross, row.original.currency) }) },
        { id: "payable", accessorKey: "totalPayable", header: t("purchasing.payable"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalPayable, row.original.currency) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ id: null, kind: "invoice", partnerId: "", supplierInvoiceNumber: "", documentDate: today(), currency: "", applyWht: true, lines: [] }); };
    const openEdit = (i) => {
        setProblem(null);
        setForm({ id: i.id, kind: i.kind, partnerId: i.partnerId, supplierInvoiceNumber: i.supplierInvoiceNumber ?? "", documentDate: i.documentDate, currency: i.currency, applyWht: Boolean(i.whtCodeId) || i.totalWht !== 0, lines: i.lines.map((l) => ({ kind: l.kind, receiptLineId: l.receiptLineId ?? "", orderLineId: l.orderLineId ?? "", landedCostChargeId: l.landedCostChargeId ?? "", returnLineId: l.returnLineId ?? "", label: l.kind === "expense" ? "" : l.kind === "charge" ? `${l.landedCostNumber ?? ""} · ${l.description ?? ""}` : `${l.itemCode ?? ""} · ${l.returnNumber ?? l.receiptNumber ?? l.orderNumber ?? ""}`, quantity: String(l.quantity), unitPrice: String(l.unitPrice), description: l.description ?? "" })) });
    };
    const addInvoicable = (line) => {
        if (!form || form.lines.some((l) => (line.kind === "receipt" ? l.receiptLineId === line.receiptLineId : line.kind === "charge" ? l.landedCostChargeId === line.landedCostChargeId : line.kind === "return" ? l.returnLineId === line.returnLineId : l.kind === "order" && l.orderLineId === line.orderLineId))) {
            return;
        }
        const label = line.kind === "charge" ? `${line.landedCostNumber ?? ""} · ${line.itemCode}` : `${line.itemCode} · ${line.returnNumber ?? line.receiptNumber ?? line.orderNumber ?? ""}`;
        setForm({ ...form, currency: form.currency || line.currency, lines: [...form.lines, { kind: line.kind, receiptLineId: line.kind === "return" ? "" : (line.receiptLineId ?? ""), orderLineId: line.orderLineId ?? "", landedCostChargeId: line.landedCostChargeId ?? "", returnLineId: line.returnLineId ?? "", label, quantity: String(line.remaining), unitPrice: String(line.unitPrice), description: "" }] });
    };
    const addExpense = () => { if (form) {
        setForm({ ...form, lines: [...form.lines, { kind: "expense", receiptLineId: "", orderLineId: "", landedCostChargeId: "", returnLineId: "", label: "", quantity: "1", unitPrice: "", description: "" }] });
    } };
    const offered = (invoicable.data ?? []).filter((line) => (form?.kind === "debit_note" ? line.kind === "return" : line.kind !== "return"));
    const patchLine = (index, change) => { if (form) {
        setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });
    } };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const i = detail.data;
    const latestMatch = i?.matches[0];
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.invoices"), description: t("purchasing.invoicesDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-invoice", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newInvoice")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "pending_approval", "blocked", "approved", "posted", "reversed", "rejected"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.invoices", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyInvoices"), emptyDescription: t("purchasing.emptyInvoicesDescription"), onOpen: (row) => { setProblem(null); setReversal(null); setTab("lines"); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("purchasing.editInvoice") : t("purchasing.newInvoice") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.supplier"), required: true, children: _jsxs(SelectField, { value: form.partnerId, onChange: (e) => { setForm({ ...form, partnerId: e.target.value, lines: form.lines.filter((l) => l.kind === "expense") }); }, required: true, disabled: Boolean(form.id), "data-testid": "invoice-supplier", children: [_jsx("option", { value: "", children: "\u2014" }), (suppliers.data ?? []).map((s) => (_jsxs("option", { value: s.partnerId, children: [s.partnerCode, " \u00B7 ", localized(s.partnerName)] }, s.partnerId)))] }) }), _jsx(Field, { label: t("purchasing.kind"), children: _jsxs(SelectField, { value: form.kind, onChange: (e) => { setForm({ ...form, kind: e.target.value, lines: e.target.value === "expense" ? form.lines.filter((l) => l.kind === "expense") : e.target.value === "debit_note" ? form.lines.filter((l) => l.kind === "expense" || l.kind === "return") : form.lines.filter((l) => l.kind !== "return") }); }, "data-testid": "invoice-kind", children: [_jsx("option", { value: "invoice", children: t("purchasing.kinds.invoice") }), _jsx("option", { value: "expense", children: t("purchasing.kinds.expense") }), _jsx("option", { value: "debit_note", children: t("purchasing.kinds.debit_note") })] }) }), _jsx(Field, { label: t("purchasing.supplierReference"), children: _jsx(TextField, { value: form.supplierInvoiceNumber, onChange: (e) => { setForm({ ...form, supplierInvoiceNumber: e.target.value }); }, dir: "ltr", "data-testid": "invoice-reference" }) }), _jsx(Field, { label: t("purchasing.documentDate"), children: _jsx(TextField, { type: "date", value: form.documentDate, onChange: (e) => { setForm({ ...form, documentDate: e.target.value }); }, dir: "ltr", "data-testid": "invoice-date" }) }), _jsx(Field, { label: t("partners.currency"), description: t("purchasing.currencyHelp"), children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, "data-testid": "invoice-currency" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: form.applyWht, onChange: (e) => { setForm({ ...form, applyWht: e.target.checked }); }, "data-testid": "invoice-wht" }), t("purchasing.applyWht")] })] }), form.kind !== "expense" && form.partnerId ? (_jsxs("div", { className: "flex flex-col gap-2 rounded-md border border-border p-3", "data-testid": "invoicable", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.invoicable") }), offered.length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.nothingInvoicable") }) : (_jsx("ul", { className: "flex flex-col gap-1 text-sm", children: offered.map((line) => (_jsxs("li", { className: "flex flex-wrap items-center gap-3", children: [_jsx(Badge, { tone: line.kind === "receipt" ? "info" : line.kind === "charge" || line.kind === "return" ? "warning" : "neutral", children: t(`purchasing.lineKinds.${line.kind}`) }), _jsx("span", { dir: "ltr", children: line.returnNumber ?? line.receiptNumber ?? line.landedCostNumber ?? line.orderNumber }), _jsxs("span", { dir: "auto", children: [line.itemCode, " \u00B7 ", localized(line.itemName)] }), _jsxs("span", { className: "tabular", dir: "ltr", children: [formatNumber(line.remaining, { maximumFractionDigits: 3 }), " ", line.uomCode, " \u00D7 ", formatNumber(line.unitPrice, { maximumFractionDigits: 4 }), " ", line.currency] }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { addInvoicable(line); }, "data-testid": `add-invoicable-${line.itemCode}${line.kind === "charge" ? "-charge" : line.kind === "return" ? "-return" : ""}`, children: t("purchasing.addLine") })] }, line.returnLineId ?? line.receiptLineId ?? line.landedCostChargeId ?? line.orderLineId))) }))] })) : null, _jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.lines") }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: addExpense, "data-testid": "add-expense-line", children: t("purchasing.addExpenseLine") })] }), form.lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.kind") }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.quantity") }), _jsx(TableHead, { children: t("purchasing.unitPrice") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: form.lines.map((line, index) => (_jsxs(TableRow, { "data-testid": "invoice-line", children: [_jsx(TableCell, { children: t(`purchasing.lineKinds.${line.kind}`) }), _jsx(TableCell, { dir: "auto", children: line.kind === "expense" ? _jsx(TextField, { "aria-label": t("purchasing.description"), value: line.description, onChange: (e) => { patchLine(index, { description: e.target.value }); }, className: "w-56", "data-testid": `invoice-description-${String(index)}` }) : line.label }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.quantity"), inputMode: "decimal", value: line.quantity, onChange: (e) => { patchLine(index, { quantity: e.target.value }); }, dir: "ltr", className: "w-20", "data-testid": `invoice-qty-${String(index)}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.unitPrice"), inputMode: "decimal", value: line.unitPrice, onChange: (e) => { patchLine(index, { unitPrice: e.target.value }); }, dir: "ltr", className: "w-28", "data-testid": `invoice-price-${String(index)}` }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setForm({ ...form, lines: form.lines.filter((_, x) => x !== index) }); }, children: t("workflow.remove") }) })] }, index))) })] })) : _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.noLines") }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, disabled: !form.partnerId || form.lines.length === 0, "data-testid": "save-invoice", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setReversal(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: i ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "invoice-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: i.number }), _jsx(PurchaseStatus, { status: i.status }), i.blockKind ? _jsx(Badge, { tone: "danger", "data-testid": "invoice-block", children: t(`purchasing.matchStatuses.${i.blockKind}`, { defaultValue: i.blockKind }) }) : null] }) }), _jsx(FormError, { message: problem?.message ?? null }), i.blockReason ? _jsx("p", { className: "text-sm text-warning", "data-testid": "block-reason", children: i.blockReason }) : null, _jsx(KeyValues, { entries: [
                                    [t("partners.supplier"), `${i.partnerCode} · ${localized(i.partnerName)}`],
                                    [t("purchasing.supplierReference"), i.supplierInvoiceNumber ?? "—"],
                                    [t("purchasing.documentDate"), formatDate(i.documentDate)],
                                    [t("purchasing.dueDate"), formatDate(i.dueDate) || "—"],
                                    [t("purchasing.terms"), [i.paymentTermsCode, i.whtCode].filter(Boolean).join(" · ") || "—"],
                                    [t("purchasing.total"), _jsxs("span", { "data-testid": "invoice-total", children: [formatMoney(i.totalGross, i.currency), i.exchangeRate !== 1 ? ` (@ ${formatNumber(i.exchangeRate, { maximumFractionDigits: 6 })})` : ""] }, "total")],
                                    [t("purchasing.withheld"), formatMoney(i.totalWht, i.currency)],
                                    [t("purchasing.payable"), _jsx("span", { "data-testid": "invoice-payable", children: formatMoney(i.totalPayable, i.currency) }, "payable")],
                                    ...(i.rejectionReason ? [[t("purchasing.rejectionReason"), i.rejectionReason]] : []),
                                    ...(i.reversalReason ? [[t("purchasing.reversalReason"), i.reversalReason]] : []),
                                ] }), _jsx(Tabs, { tabs: [{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "match", label: t("purchasing.match"), testId: "tab-match" }, { id: "payables", label: t("purchasing.payables"), testId: "tab-payables" }], value: tab, onChange: setTab }), tab === "lines" ? (_jsxs(Table, { "data-testid": "invoice-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.kind") }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.quantity") }), _jsx(TableHead, { children: t("purchasing.unitPrice") }), _jsx(TableHead, { children: t("purchasing.expectedPrice") }), _jsx(TableHead, { children: t("purchasing.net") })] }) }), _jsx(TableBody, { children: i.lines.map((l) => (_jsxs(TableRow, { "data-testid": "invoice-line-row", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsx(TableCell, { children: t(`purchasing.lineKinds.${l.kind}`) }), _jsx(TableCell, { dir: "auto", children: l.kind === "expense" ? `${l.description ?? ""} (${l.accountRole ?? ""})` : l.kind === "charge" ? `${l.landedCostNumber ?? ""} · ${l.description ?? ""}` : `${l.itemCode ?? ""} · ${l.returnNumber ?? l.receiptNumber ?? l.orderNumber ?? ""}` }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.quantity, { maximumFractionDigits: 3 }), " ", l.uomCode ?? ""] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(l.unitPrice, { maximumFractionDigits: 4 }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: l.expectedUnitPrice === null ? "" : `${formatNumber(l.expectedUnitPrice, { maximumFractionDigits: 4 })}${l.priceVariancePct === null ? "" : ` (${formatNumber(l.priceVariancePct, { maximumFractionDigits: 2 })}%)`}` }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.netAmount, i.currency) })] }, l.id))) })] })) : null, tab === "match" ? (latestMatch ? (_jsx(KeyValues, { entries: [
                                    [t("common.status"), _jsx("span", { "data-testid": "match-status", children: t(`purchasing.matchStatuses.${latestMatch.status}`, { defaultValue: latestMatch.status }) }, "s")],
                                    [t("purchasing.priceTolerance"), `${formatNumber(latestMatch.priceTolerancePct)}%`],
                                    [t("purchasing.qtyTolerance"), `${formatNumber(latestMatch.qtyTolerancePct)}%`],
                                    [t("purchasing.priceVariance"), `${formatMoney(latestMatch.priceVarianceAmount, i.currency)} (${formatNumber(latestMatch.priceVariancePct, { maximumFractionDigits: 2 })}%)`],
                                    [t("purchasing.qtyVarianceLabel"), formatNumber(latestMatch.qtyVariance, { maximumFractionDigits: 3 })],
                                    [t("purchasing.override"), latestMatch.overrideId ? "✓" : "—"],
                                    [t("purchasing.matchedAt"), formatDate(latestMatch.matchedAt)],
                                ] })) : _jsx("p", { className: "text-sm text-fg-muted", children: t("purchasing.notMatchedYet") })) : null, tab === "payables" ? (i.openItems.length === 0 ? _jsx("p", { className: "text-sm text-fg-muted", children: t("purchasing.noPayables") }) : (_jsxs(Table, { "data-testid": "invoice-open-items", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.instalment") }), _jsx(TableHead, { children: t("purchasing.dueDate") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("purchasing.remaining") }), _jsx(TableHead, { children: t("common.status") })] }) }), _jsx(TableBody, { children: i.openItems.map((o) => (_jsxs(TableRow, { "data-testid": "open-item-row", children: [_jsx(TableCell, { children: String(o.instalment) }), _jsx(TableCell, { dir: "ltr", children: formatDate(o.dueDate) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(o.originalTc, o.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", "data-testid": "open-item-remaining", children: formatMoney(o.remainingTc, o.currency) }), _jsx(TableCell, { children: _jsx(PurchaseStatus, { status: o.status }) })] }, o.id))) })] }))) : null, tab === "payables" && i.kind === "debit_note" && i.status === "posted" && i.openItems[0]?.status === "open" ? (_jsxs("div", { className: "flex flex-col gap-3 rounded-md border border-border p-3", "data-testid": "apply-credit", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.applyCredit") }), _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.applyCreditHelp") }), credit ? (_jsxs("div", { className: "grid gap-3 sm:grid-cols-3", children: [_jsx(Field, { label: t("purchasing.appliedTo"), required: true, children: _jsxs(SelectField, { value: credit.invoiceItemId, onChange: (e) => { setCredit({ ...credit, invoiceItemId: e.target.value }); }, "data-testid": "credit-target", children: [_jsx("option", { value: "", children: "\u2014" }), (supplierItems.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === i.currency).map((o) => (_jsxs("option", { value: o.id, children: [o.documentNumber, " \u00B7 ", formatMoney(o.remainingTc, o.currency)] }, o.id)))] }) }), _jsx(Field, { label: t("purchasing.creditAmount"), required: true, children: _jsx(TextField, { inputMode: "decimal", value: credit.amount, onChange: (e) => { setCredit({ ...credit, amount: e.target.value }); }, dir: "ltr", "data-testid": "credit-amount" }) }), _jsxs("div", { className: "flex items-end gap-2", children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setCredit(null); }, children: t("common.cancel") }), _jsx(Button, { type: "button", onClick: () => { const item = i.openItems[0]; if (item) {
                                                            apply.mutate({ settlingItemId: item.id, settledItemId: credit.invoiceItemId, amount: num(credit.amount) });
                                                        } }, loading: apply.isPending, disabled: !credit.invoiceItemId || num(credit.amount) <= 0, "data-testid": "confirm-apply-credit", children: t("purchasing.applyCredit") })] })] })) : _jsx(Button, { type: "button", variant: "secondary", onClick: () => { setCredit({ invoiceItemId: "", amount: String(Math.abs(Number(i.openItems[0]?.remainingTc ?? 0))) }); }, "data-testid": "start-apply-credit", children: t("purchasing.applyCredit") })] })) : null, tab === "payables" && i.status === "posted" ? (_jsxs("div", { className: "flex flex-col gap-2", "data-testid": "settlements", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.settlements") }), (settlements.data ?? []).length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.noSettlements") }) : (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.postingDate") }), _jsx(TableHead, { children: t("purchasing.kind") }), _jsx(TableHead, { children: t("purchasing.appliedTo") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("purchasing.fxGainLoss") })] }) }), _jsx(TableBody, { children: (settlements.data ?? []).map((st) => (_jsxs(TableRow, { "data-testid": "settlement-row", children: [_jsx(TableCell, { dir: "ltr", children: formatDate(st.settlementDate) }), _jsx(TableCell, { children: t(`purchasing.settlementKinds.${st.kind}`) }), _jsxs(TableCell, { dir: "ltr", children: [st.settlingDocumentNumber, " \u2192 ", st.settledDocumentNumber] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(st.amountTc, st.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(st.fxGainLossFc, i.functionalCurrency) })] }, st.id))) })] }))] })) : null, reversal !== null ? (_jsx(Field, { label: t("purchasing.reversalReason"), required: true, children: _jsx(TextField, { value: reversal, onChange: (e) => { setReversal(e.target.value); }, "data-testid": "reversal-reason" }) })) : null, _jsxs(DialogFooter, { children: [editable(i.status) ? _jsx(Button, { variant: "secondary", onClick: () => { openEdit(i); }, "data-testid": "edit-invoice", children: t("common.edit") }) : null, editable(i.status) ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: i.id, action: "delete" }); }, loading: act.isPending, "data-testid": "delete-invoice", children: t("purchasing.deleteDraft") }) : null, editable(i.status) ? _jsx(Button, { onClick: () => { act.mutate({ id: i.id, action: "submit" }); }, loading: act.isPending, "data-testid": "submit-invoice", children: t("purchasing.submit") }) : null, i.status === "approved" ? _jsx(Button, { onClick: () => { act.mutate({ id: i.id, action: "post" }); }, loading: act.isPending, "data-testid": "post-invoice", children: t("purchasing.postInvoice") }) : null, i.status === "posted" && reversal === null ? _jsx(Button, { variant: "secondary", onClick: () => { setReversal(""); }, "data-testid": "reverse-invoice", children: t("purchasing.reverse") }) : null, reversal !== null ? _jsx(Button, { onClick: () => { act.mutate({ id: i.id, action: "reverse", reason: reversal }); }, loading: act.isPending, disabled: !reversal.trim(), "data-testid": "confirm-reverse", children: t("purchasing.reverseNow") }) : null] })] })) : null }) })] }));
}
