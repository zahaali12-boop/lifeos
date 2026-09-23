import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, orderLineBodies, PurchaseStatus, useAgreements, useSuppliers } from "./shared";
const editable = (status) => status === "draft" || status === "rejected";
const changeable = (status) => status === "approved" || status === "sent";
/** Purchase orders (roadmap 4.2): drafted in the supplier's currency, submitted through the workflow, sent by email, changed through revisions, cancelled or closed. */
export function PurchaseOrdersPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [tab, setTab] = useState("lines");
    const [sendTo, setSendTo] = useState(null);
    const suppliers = useSuppliers(companyId);
    const warehouses = useWarehouses(companyId);
    const agreements = useAgreements(companyId);
    const list = useQuery({
        queryKey: ["orders", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["order", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders/{orderId}", { params: { path: { orderId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["orders"] });
        await queryClient.invalidateQueries({ queryKey: ["order"] });
        await queryClient.invalidateQueries({ queryKey: ["agreements"] });
        await queryClient.invalidateQueries({ queryKey: ["agreement"] });
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const order = { companyId, partnerId: f.partnerId, currency: f.currency || null, expectedDate: f.expectedDate || null, warehouseId: f.warehouseId || null, agreementId: f.agreementId || null, notes: f.notes || null, lines: orderLineBodies(f.lines) };
            if (!f.id) {
                return unwrap(await api.POST("/api/v1/purchasing/orders", { body: order }));
            }
            return f.change
                ? unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/change", { params: { path: { orderId: f.id } }, body: { order, reason: f.reason } }))
                : unwrap(await api.PUT("/api/v1/purchasing/orders/{orderId}", { params: { path: { orderId: f.id } }, body: order }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { orderId: input.id } };
            switch (input.action) {
                case "submit": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/submit", { params }));
                case "send": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/send", { params, body: { to: input.to ?? null, message: input.message ?? null } }));
                case "cancel": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/cancel", { params, body: { message: input.message ?? null } }));
                case "close": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/close", { params }));
            }
        },
        onSuccess: async () => { setProblem(null); setSendTo(null); await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsxs("span", { dir: "ltr", children: [row.original.number, Number(row.original.revision) > 1 ? ` · r${String(row.original.revision)}` : ""] }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "date", accessorKey: "orderDate", header: t("purchasing.orderDate"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.orderDate) }) },
        { id: "expected", accessorKey: "expectedDate", header: t("purchasing.expectedDate"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.expectedDate) }) },
        { id: "total", accessorKey: "totalGross", header: t("purchasing.total"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalGross, row.original.currency) }) },
    ], [t]);
    const openForm = (o, change = false) => {
        setProblem(null);
        setForm(o
            ? { id: o.id, partnerId: o.partnerId, currency: o.currency, expectedDate: o.expectedDate ?? "", warehouseId: o.warehouseId ?? "", agreementId: o.agreementId ?? "", notes: o.notes ?? "", change, reason: "", lines: o.lines.map((l) => ({ itemCode: l.itemCode, description: l.description ?? "", quantity: String(l.quantity), uom: l.uomCode, price: String(l.unitPrice), supplierId: "", blanketLineId: l.blanketLineId ?? "" })) }
            : { id: null, partnerId: "", currency: "", expectedDate: "", warehouseId: "", agreementId: "", notes: "", change: false, reason: "", lines: [emptyLine()] });
    };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const o = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.purchaseOrders"), description: t("purchasing.ordersDescription"), actions: _jsxs(Button, { onClick: () => { openForm(null); }, disabled: !companyId, "data-testid": "new-order", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newOrder")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "pending_approval", "approved", "sent", "partially_received", "received", "closed", "rejected", "cancelled"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.purchaseOrders", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyOrders"), emptyDescription: t("purchasing.emptyOrdersDescription"), onOpen: (row) => { setProblem(null); setTab("lines"); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? (form.change ? t("purchasing.changeOrder") : t("purchasing.editOrder")) : t("purchasing.newOrder") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("partners.supplier"), required: true, children: _jsxs(SelectField, { value: form.partnerId, onChange: (e) => { setForm({ ...form, partnerId: e.target.value }); }, required: true, "data-testid": "order-supplier", children: [_jsx("option", { value: "", children: "\u2014" }), (suppliers.data ?? []).map((s) => (_jsxs("option", { value: s.partnerId, children: [s.partnerCode, " \u00B7 ", localized(s.partnerName)] }, s.partnerId)))] }) }), _jsx(Field, { label: t("partners.currency"), description: t("purchasing.currencyHelp"), children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, "data-testid": "order-currency" }) }), _jsx(Field, { label: t("purchasing.expectedDate"), children: _jsx(TextField, { type: "date", value: form.expectedDate, onChange: (e) => { setForm({ ...form, expectedDate: e.target.value }); }, dir: "ltr", "data-testid": "order-expected" }) }), _jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: form.warehouseId, onChange: (id) => { setForm({ ...form, warehouseId: id }); }, label: t("purchasing.deliverTo"), testId: "order-warehouse" }), _jsx(Field, { label: t("nav.agreements"), children: _jsxs(SelectField, { value: form.agreementId, onChange: (e) => { setForm({ ...form, agreementId: e.target.value }); }, "data-testid": "order-agreement", children: [_jsx("option", { value: "", children: "\u2014" }), (agreements.data ?? []).filter((a) => a.status === "active" && a.partnerId === form.partnerId).map((a) => (_jsx("option", { value: a.id, children: a.number }, a.id)))] }) }), _jsx(Field, { label: t("purchasing.notes"), children: _jsx(TextareaField, { value: form.notes, onChange: (e) => { setForm({ ...form, notes: e.target.value }); }, rows: 2 }) }), form.change ? (_jsx(Field, { label: t("purchasing.changeReason"), required: true, children: _jsx(TextField, { value: form.reason, onChange: (e) => { setForm({ ...form, reason: e.target.value }); }, required: true, "data-testid": "change-reason" }) })) : null] }), _jsx(LinesEditor, { lines: form.lines, onChange: (lines) => { setForm({ ...form, lines }); }, showDescription: true }), form.agreementId ? (_jsx("div", { className: "grid gap-2 sm:grid-cols-2", children: form.lines.map((line, index) => (_jsx(Field, { label: t("purchasing.blanketLineFor", { line: String(index + 1) }), children: _jsxs(SelectField, { value: line.blanketLineId, onChange: (e) => { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, blanketLineId: e.target.value } : l)) }); }, "data-testid": `line-blanket-${String(index)}`, children: [_jsx("option", { value: "", children: "\u2014" }), (agreements.data?.find((a) => a.id === form.agreementId)?.lines ?? []).map((b) => (_jsxs("option", { value: b.id, children: [b.itemCode, " \u00B7 ", formatNumber(b.remainingQty, { maximumFractionDigits: 3 }), " ", b.uomCode] }, b.id)))] }) }, index))) })) : null, _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-order", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setSendTo(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: o ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "order-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: o.number }), _jsx("span", { className: "text-sm font-normal text-fg-muted", "data-testid": "order-revision", children: t("purchasing.revisionN", { n: String(o.revision) }) }), _jsx(PurchaseStatus, { status: o.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("partners.supplier"), `${o.partnerCode} · ${localized(o.partnerName)}`],
                                    [t("purchasing.orderDate"), formatDate(o.orderDate)],
                                    [t("purchasing.expectedDate"), formatDate(o.expectedDate) || "—"],
                                    [t("purchasing.terms"), [o.paymentTermsCode, o.deliveryTermsCode].filter(Boolean).join(" · ") || "—"],
                                    [t("purchasing.total"), _jsxs("span", { "data-testid": "order-total", children: [formatMoney(o.totalGross, o.currency), o.exchangeRate !== 1 ? ` (${formatNumber(o.totalGrossRc)} @ ${formatNumber(o.exchangeRate, { maximumFractionDigits: 6 })})` : ""] }, "total")],
                                    ...(o.sentTo ? [[t("purchasing.sentTo"), `${o.sentTo} · ${formatDateTime(o.sentAt)}`]] : []),
                                    ...(o.rejectionReason ? [[t("purchasing.rejectionReason"), o.rejectionReason]] : []),
                                ] }), _jsx(Tabs, { tabs: [{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "revisions", label: t("purchasing.revisions"), testId: "tab-revisions" }, { id: "commitments", label: t("purchasing.commitments"), testId: "tab-commitments" }], value: tab, onChange: setTab }), tab === "lines" ? _jsx(LinesTable, { lines: o.lines, currency: o.currency, testId: "order-lines" }) : null, tab === "revisions" ? (o.revisions.length === 0 ? _jsx("p", { className: "text-sm text-fg-muted", children: t("purchasing.noRevisions") }) : (_jsxs(Table, { "data-testid": "order-revisions", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.revision") }), _jsx(TableHead, { children: t("purchasing.changeReason") }), _jsx(TableHead, { children: t("purchasing.changedAt") }), _jsx(TableHead, { children: t("purchasing.total") })] }) }), _jsx(TableBody, { children: o.revisions.map((r) => (_jsxs(TableRow, { "data-testid": "revision-row", children: [_jsx(TableCell, { children: String(r.revision) }), _jsx(TableCell, { dir: "auto", children: r.reason ?? "" }), _jsx(TableCell, { dir: "ltr", children: formatDateTime(r.changedAt) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: typeof r.snapshot === "object" && r.snapshot !== null && "totalGross" in r.snapshot ? formatMoney(String(r.snapshot.totalGross), o.currency) : "" })] }, r.revision))) })] }))) : null, tab === "commitments" ? (o.commitments.length === 0 ? _jsx("p", { className: "text-sm text-fg-muted", children: t("purchasing.noCommitments") }) : (_jsxs(Table, { "data-testid": "order-commitments", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.accountRole") }), _jsx(TableHead, { children: t("purchasing.period") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("common.status") })] }) }), _jsx(TableBody, { children: o.commitments.map((c) => (_jsxs(TableRow, { "data-testid": "commitment-row", children: [_jsx(TableCell, { children: c.accountRole }), _jsx(TableCell, { dir: "ltr", children: c.periodKey }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(c.amountFc, c.currency) }), _jsx(TableCell, { children: _jsx(PurchaseStatus, { status: c.status }) })] }, c.id))) })] }))) : null, sendTo ? (_jsxs("div", { className: "grid gap-3 rounded-md border border-border p-3 sm:grid-cols-2", "data-testid": "send-panel", children: [_jsx(Field, { label: t("purchasing.sendTo"), description: t("purchasing.sendToHelp"), children: _jsx(TextField, { type: "email", value: sendTo.to, onChange: (e) => { setSendTo({ ...sendTo, to: e.target.value }); }, dir: "ltr", "data-testid": "send-to" }) }), _jsx(Field, { label: t("purchasing.message"), children: _jsx(TextField, { value: sendTo.message, onChange: (e) => { setSendTo({ ...sendTo, message: e.target.value }); }, "data-testid": "send-message" }) })] })) : null, _jsxs(DialogFooter, { children: [editable(o.status) ? _jsx(Button, { variant: "secondary", onClick: () => { openForm(o); }, "data-testid": "edit-order", children: t("common.edit") }) : null, editable(o.status) ? _jsx(Button, { onClick: () => { act.mutate({ id: o.id, action: "submit" }); }, loading: act.isPending, "data-testid": "submit-order", children: t("purchasing.submit") }) : null, changeable(o.status) ? _jsx(Button, { variant: "secondary", onClick: () => { openForm(o, true); }, "data-testid": "change-order", children: t("purchasing.changeOrder") }) : null, changeable(o.status) && !sendTo ? _jsx(Button, { onClick: () => { setSendTo({ to: "", message: "" }); }, "data-testid": "send-order", children: t("purchasing.send") }) : null, sendTo ? _jsx(Button, { onClick: () => { act.mutate({ id: o.id, action: "send", ...(sendTo.to ? { to: sendTo.to } : {}), ...(sendTo.message ? { message: sendTo.message } : {}) }); }, loading: act.isPending, "data-testid": "confirm-send", children: t("purchasing.sendNow") }) : null, o.status !== "cancelled" && o.status !== "closed" && o.status !== "received" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: o.id, action: "cancel" }); }, loading: act.isPending, "data-testid": "cancel-order", children: t("purchasing.cancelDocument") }) : null, o.status === "partially_received" || o.status === "sent" || o.status === "approved" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: o.id, action: "close" }); }, loading: act.isPending, "data-testid": "close-order", children: t("purchasing.close") }) : null] })] })) : null }) })] }));
}
