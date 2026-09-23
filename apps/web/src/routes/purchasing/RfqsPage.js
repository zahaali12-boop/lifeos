import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, num, PurchaseStatus, useSuppliers } from "./shared";
/** Requests for quotation (roadmap 4.2): lines sent to invited suppliers, their quotes recorded, compared by landed price in the company's currency then lead time, and one awarded into a purchase order. */
export function RfqsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [tab, setTab] = useState("lines");
    const [quote, setQuote] = useState(null);
    const [comparison, setComparison] = useState(null);
    const [awarded, setAwarded] = useState(null);
    const suppliers = useSuppliers(companyId);
    const list = useQuery({
        queryKey: ["rfqs", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/rfqs", { params: { query: { companyId } } })),
    });
    const detail = useQuery({
        queryKey: ["rfq", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/rfqs/{rfqId}", { params: { path: { rfqId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["rfqs"] });
        await queryClient.invalidateQueries({ queryKey: ["rfq"] });
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const create = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/purchasing/rfqs", {
            body: { companyId, title: f.title || null, dueOn: f.dueOn || null, partnerIds: f.partnerIds, lines: f.lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null })) },
        })),
        onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { rfqId: input.id } };
            switch (input.action) {
                case "send": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/send", { params, body: {} }));
                case "close": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/close", { params }));
                case "cancel": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/cancel", { params }));
                case "compare": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/compare", { params }));
                case "award": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/award", { params, body: { quoteId: input.quoteId ?? "" } }));
            }
        },
        onSuccess: async (result, input) => {
            setProblem(null);
            if (input.action === "compare" && "rankings" in result) {
                setComparison(result);
            }
            if (input.action === "award" && "number" in result) {
                setAwarded(result.number);
                await queryClient.invalidateQueries({ queryKey: ["orders"] });
            }
            await refresh();
        },
        onError: fail,
    });
    const recordQuote = useMutation({
        mutationFn: async (input) => unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/quotes", {
            params: { path: { rfqId: input.id } },
            body: { partnerId: input.form.partnerId, currency: input.form.currency, leadTimeDays: num(input.form.leadTimeDays), freightAmount: num(input.form.freightAmount), otherCharges: 0, lines: Object.entries(input.form.prices).filter(([, price]) => price.trim()).map(([rfqLineId, price]) => ({ rfqLineId, unitPrice: num(price) })) },
        })),
        onSuccess: async () => { setProblem(null); setQuote(null); setComparison(null); await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "title", accessorKey: "title", header: t("purchasing.title"), size: 220, cell: ({ row }) => _jsx("span", { dir: "auto", children: row.original.title ?? "" }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "due", accessorKey: "dueOn", header: t("purchasing.dueOn"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.dueOn) }) },
        { id: "suppliers", accessorFn: (row) => row.suppliers.length, header: t("purchasing.suppliersInvited"), size: 110, cell: ({ row }) => String(row.original.suppliers.length) },
        { id: "quotes", accessorFn: (row) => row.quotes.length, header: t("purchasing.quotes"), size: 90, cell: ({ row }) => String(row.original.quotes.length) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ title: "", dueOn: "", partnerIds: [], lines: [emptyLine()] }); };
    const submit = (event) => { event.preventDefault(); if (form) {
        create.mutate(form);
    } };
    const r = detail.data;
    const openQuote = (partnerId) => {
        if (!r) {
            return;
        }
        const account = suppliers.data?.find((s) => s.partnerId === partnerId);
        setProblem(null);
        setQuote({ partnerId, currency: account?.currency ?? "", leadTimeDays: String(account?.leadTimeDays ?? 0), freightAmount: "0", prices: Object.fromEntries(r.lines.map((l) => [l.id, ""])) });
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.rfqs"), description: t("purchasing.rfqsDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-rfq", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newRfq")] }) }), _jsx("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: _jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }) }), _jsx(DataGrid, { label: "nav.rfqs", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyRfqs"), emptyDescription: t("purchasing.emptyRfqsDescription"), onOpen: (row) => { setProblem(null); setTab("lines"); setComparison(null); setAwarded(null); setQuote(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("purchasing.newRfq") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("purchasing.title"), children: _jsx(TextField, { value: form.title, onChange: (e) => { setForm({ ...form, title: e.target.value }); }, "data-testid": "rfq-title" }) }), _jsx(Field, { label: t("purchasing.dueOn"), children: _jsx(TextField, { type: "date", value: form.dueOn, onChange: (e) => { setForm({ ...form, dueOn: e.target.value }); }, dir: "ltr", "data-testid": "rfq-due" }) })] }), _jsxs("fieldset", { className: "flex flex-col gap-1", children: [_jsx("legend", { className: "text-sm font-semibold", children: t("purchasing.inviteSuppliers") }), _jsx("div", { className: "flex flex-wrap gap-3", children: (suppliers.data ?? []).map((s) => (_jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.partnerIds.includes(s.partnerId), onChange: (e) => { setForm({ ...form, partnerIds: e.target.checked ? [...form.partnerIds, s.partnerId] : form.partnerIds.filter((id) => id !== s.partnerId) }); }, "data-testid": `invite-${s.partnerCode}` }), _jsxs("span", { dir: "auto", children: [s.partnerCode, " \u00B7 ", localized(s.partnerName)] })] }, s.partnerId))) })] }), _jsx(LinesEditor, { lines: form.lines, onChange: (lines) => { setForm({ ...form, lines }); }, showPrice: false, showDescription: true }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, "data-testid": "save-rfq", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: r ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "rfq-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: r.number }), _jsx(PurchaseStatus, { status: r.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [[t("purchasing.title"), r.title ?? "—"], [t("purchasing.dueOn"), formatDate(r.dueOn) || "—"]] }), _jsx(Tabs, { tabs: [{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "suppliers", label: t("purchasing.suppliersInvited"), testId: "tab-suppliers" }, { id: "quotes", label: t("purchasing.quotes"), testId: "tab-quotes" }], value: tab, onChange: setTab }), tab === "lines" ? _jsx(LinesTable, { lines: r.lines, testId: "rfq-lines" }) : null, tab === "suppliers" ? (_jsxs(Table, { "data-testid": "rfq-suppliers", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.supplier") }), _jsx(TableHead, { children: t("partners.email") }), _jsx(TableHead, { children: t("purchasing.sentAt") }), _jsx(TableHead, { children: t("common.status") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: r.suppliers.map((s) => (_jsxs(TableRow, { "data-testid": "rfq-supplier-row", children: [_jsxs(TableCell, { dir: "auto", children: [s.partnerCode, " \u00B7 ", localized(s.partnerName)] }), _jsx(TableCell, { dir: "ltr", children: s.contactEmail ?? "—" }), _jsx(TableCell, { dir: "ltr", children: formatDateTime(s.sentAt) }), _jsx(TableCell, { children: _jsx(PurchaseStatus, { status: s.status }) }), _jsx(TableCell, { children: (r.status === "sent" || r.status === "draft") && !s.quoteId ? (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { openQuote(s.partnerId); }, "data-testid": `record-quote-${s.partnerCode}`, children: t("purchasing.recordQuote") })) : null })] }, s.id))) })] })) : null, tab === "quotes" ? (_jsxs("div", { className: "flex flex-col gap-3", children: [r.quotes.length === 0 ? _jsx("p", { className: "text-sm text-fg-muted", children: t("purchasing.noQuotes") }) : (_jsxs(Table, { "data-testid": "rfq-quotes", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.supplier") }), _jsx(TableHead, { children: t("partners.currency") }), _jsx(TableHead, { children: t("purchasing.goodsTotal") }), _jsx(TableHead, { children: t("purchasing.landedTotal") }), _jsx(TableHead, { children: t("purchasing.leadTimeDays") }), _jsx(TableHead, { children: t("purchasing.rank") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: r.quotes.map((q) => {
                                                    const ranking = comparison?.rankings.find((x) => x.quoteId === q.id);
                                                    return (_jsxs(TableRow, { "data-testid": "quote-row", children: [_jsxs(TableCell, { dir: "auto", children: [q.partnerCode, " \u00B7 ", localized(q.partnerName)] }), _jsx(TableCell, { dir: "ltr", children: q.currency }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(q.goodsTotal, q.currency) }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatMoney(q.landedTotal, q.currency), ranking ? ` = ${formatMoney(ranking.landedTotalRc, comparison?.currency ?? q.currency)}` : ""] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: String(q.leadTimeDays) }), _jsx(TableCell, { className: "tabular", dir: "ltr", "data-testid": "quote-rank", children: ranking ? String(ranking.rank) : "" }), _jsx(TableCell, { children: r.status === "sent" ? _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { act.mutate({ id: r.id, action: "award", quoteId: q.id }); }, loading: act.isPending, "data-testid": `award-${q.partnerCode}`, children: t("purchasing.award") }) : null })] }, q.id));
                                                }) })] })), comparison ? _jsx("p", { className: "text-xs text-fg-muted", "data-testid": "comparison-note", children: t("purchasing.comparisonNote", { currency: comparison.currency, date: formatDate(comparison.rateDate) }) }) : null, awarded ? _jsx("p", { className: "text-sm", "data-testid": "awarded-order", children: t("purchasing.awardedInto", { number: awarded }) }) : null] })) : null, quote ? (_jsxs("form", { onSubmit: (e) => { e.preventDefault(); recordQuote.mutate({ id: r.id, form: quote }); }, className: "flex flex-col gap-3 rounded-md border border-border p-3", "data-testid": "quote-form", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.recordQuoteFor", { supplier: r.suppliers.find((s) => s.partnerId === quote.partnerId)?.partnerCode ?? "" }) }), _jsxs("div", { className: "grid gap-3 sm:grid-cols-3", children: [_jsx(Field, { label: t("partners.currency"), required: true, children: _jsx(TextField, { value: quote.currency, onChange: (e) => { setQuote({ ...quote, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, required: true, "data-testid": "quote-currency" }) }), _jsx(Field, { label: t("purchasing.leadTimeDays"), children: _jsx(TextField, { type: "number", min: 0, value: quote.leadTimeDays, onChange: (e) => { setQuote({ ...quote, leadTimeDays: e.target.value }); }, dir: "ltr", "data-testid": "quote-lead-time" }) }), _jsx(Field, { label: t("purchasing.freight"), children: _jsx(TextField, { inputMode: "decimal", value: quote.freightAmount, onChange: (e) => { setQuote({ ...quote, freightAmount: e.target.value }); }, dir: "ltr", "data-testid": "quote-freight" }) }), r.lines.map((l, index) => (_jsx(Field, { label: `${String(l.lineNo)}. ${l.itemCode ?? l.description ?? ""} × ${formatNumber(l.quantity, { maximumFractionDigits: 3 })} ${l.uomCode}`, children: _jsx(TextField, { inputMode: "decimal", value: quote.prices[l.id] ?? "", onChange: (e) => { setQuote({ ...quote, prices: { ...quote.prices, [l.id]: e.target.value } }); }, dir: "ltr", "data-testid": `quote-price-${String(index)}` }) }, l.id)))] }), _jsxs("div", { className: "flex justify-end gap-2", children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setQuote(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: recordQuote.isPending, "data-testid": "save-quote", children: t("purchasing.saveQuote") })] })] })) : null, _jsxs(DialogFooter, { children: [r.status === "draft" || (r.status === "sent" && r.suppliers.some((s) => !s.sentAt)) ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "send" }); }, loading: act.isPending, "data-testid": "send-rfq", children: t("purchasing.sendRfq") }) : null, r.quotes.length > 0 && r.status === "sent" ? _jsx(Button, { variant: "secondary", onClick: () => { setTab("quotes"); act.mutate({ id: r.id, action: "compare" }); }, loading: act.isPending, "data-testid": "compare-quotes", children: t("purchasing.compare") }) : null, r.status === "sent" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: r.id, action: "close" }); }, loading: act.isPending, "data-testid": "close-rfq", children: t("purchasing.close") }) : null, r.status === "draft" || r.status === "sent" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: r.id, action: "cancel" }); }, loading: act.isPending, "data-testid": "cancel-rfq", children: t("purchasing.cancelDocument") }) : null] })] })) : null }) })] }));
}
