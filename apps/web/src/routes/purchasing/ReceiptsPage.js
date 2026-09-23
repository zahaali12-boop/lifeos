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
import { CompanyFilter, KeyValues, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { num, PurchaseStatus } from "./shared";
/** Goods receipts (roadmap 4.3): open order lines received within the supplier's tolerance, posted into stock at the expected cost against GRNI, reversed as a whole. */
export function ReceiptsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [reversal, setReversal] = useState(null);
    const warehouses = useWarehouses(companyId);
    const list = useQuery({
        queryKey: ["receipts", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const receivable = useQuery({
        queryKey: ["receivable", companyId],
        enabled: Boolean(companyId) && Boolean(form),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/receivable", { params: { query: { companyId } } })),
    });
    const detail = useQuery({
        queryKey: ["receipt", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/{receiptId}", { params: { path: { receiptId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["receipts"], ["receipt"], ["receivable"], ["orders"], ["order"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const body = {
                orderId: f.orderId,
                warehouseId: f.warehouseId || null,
                postingDate: f.postingDate || null,
                supplierDeliveryNote: f.supplierDeliveryNote || null,
                lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ orderLineId: l.orderLineId, quantity: num(l.quantity), lotNumber: l.lotNumber || null, expiresOn: l.expiresOn || null, serialNumbers: l.serialNumbers.split(/[\s,]+/).filter(Boolean) })),
            };
            return f.id ? unwrap(await api.PUT("/api/v1/purchasing/receipts/{receiptId}", { params: { path: { receiptId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/receipts", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { receiptId: input.id } };
            switch (input.action) {
                case "post": return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/post", { params }));
                case "reverse": return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/reverse", { params, body: { reason: input.reason ?? "" } }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/purchasing/receipts/{receiptId}", { params }));
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
        { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "order", accessorKey: "orderNumber", header: t("nav.purchaseOrders"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.orderNumber }) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.postingDate) }) },
        { id: "note", accessorKey: "supplierDeliveryNote", header: t("purchasing.deliveryNote"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.supplierDeliveryNote ?? "" }) },
        { id: "value", accessorKey: "totalExpectedCost", header: t("purchasing.expectedValue"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalExpectedCost, row.original.functionalCurrency) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ id: null, orderId: "", warehouseId: "", postingDate: today(), supplierDeliveryNote: "", lines: [] }); };
    const chooseOrder = (orderId) => {
        if (!form) {
            return;
        }
        const lines = (receivable.data ?? []).filter((l) => l.orderId === orderId);
        const only = (warehouses.data ?? []).length === 1 ? warehouses.data?.[0]?.id : undefined;
        setForm({ ...form, orderId, warehouseId: lines[0]?.warehouseId ?? (form.warehouseId || only) ?? "", lines: lines.map((l) => ({ orderLineId: l.orderLineId, quantity: String(l.remaining), lotNumber: "", expiresOn: "", serialNumbers: "" })) });
    };
    const patchLine = (index, change) => { if (form) {
        setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });
    } };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const orders = useMemo(() => {
        const seen = new Map();
        for (const l of receivable.data ?? []) {
            seen.set(l.orderId, l.orderNumber);
        }
        return [...seen.entries()];
    }, [receivable.data]);
    const lineInfo = (orderLineId) => receivable.data?.find((l) => l.orderLineId === orderLineId);
    const r = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.receipts"), description: t("purchasing.receiptsDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-receipt", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newReceipt")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "posted", "reversed"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.receipts", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyReceipts"), emptyDescription: t("purchasing.emptyReceiptsDescription"), onOpen: (row) => { setProblem(null); setReversal(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("purchasing.editReceipt") : t("purchasing.newReceipt") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(Field, { label: t("nav.purchaseOrders"), required: true, children: _jsxs(SelectField, { value: form.orderId, onChange: (e) => { chooseOrder(e.target.value); }, required: true, disabled: Boolean(form.id), "data-testid": "receipt-order", children: [_jsx("option", { value: "", children: "\u2014" }), orders.map(([id, number]) => (_jsx("option", { value: id, children: number }, id)))] }) }), _jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: form.warehouseId, onChange: (id) => { setForm({ ...form, warehouseId: id }); }, label: t("purchasing.receiveInto"), testId: "receipt-warehouse", required: true }), _jsx(Field, { label: t("purchasing.postingDate"), children: _jsx(TextField, { type: "date", value: form.postingDate, onChange: (e) => { setForm({ ...form, postingDate: e.target.value }); }, dir: "ltr", "data-testid": "receipt-date" }) }), _jsx(Field, { label: t("purchasing.deliveryNote"), children: _jsx(TextField, { value: form.supplierDeliveryNote, onChange: (e) => { setForm({ ...form, supplierDeliveryNote: e.target.value }); }, dir: "ltr", "data-testid": "receipt-delivery-note" }) })] }), form.lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.ordered") }), _jsx(TableHead, { children: t("purchasing.receivedSoFar") }), _jsx(TableHead, { children: t("purchasing.maxReceivable") }), _jsx(TableHead, { children: t("purchasing.receiveNow") }), _jsx(TableHead, { children: t("purchasing.lot") }), _jsx(TableHead, { children: t("purchasing.expiresOn") }), _jsx(TableHead, { children: t("purchasing.serials") })] }) }), _jsx(TableBody, { children: form.lines.map((line, index) => {
                                            const info = lineInfo(line.orderLineId);
                                            const lotTracked = info?.tracking === "lot" || info?.tracking === "lot_and_serial";
                                            const serialTracked = info?.tracking === "serial" || info?.tracking === "lot_and_serial";
                                            return (_jsxs(TableRow, { "data-testid": "receipt-line", children: [_jsx(TableCell, { dir: "auto", children: info ? `${info.itemCode} · ${localized(info.itemName)}` : "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: info ? `${formatNumber(info.ordered, { maximumFractionDigits: 3 })} ${info.uomCode}` : "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: info ? formatNumber(info.received, { maximumFractionDigits: 3 }) : "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: info ? formatNumber(info.maxReceivable, { maximumFractionDigits: 3 }) : "" }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.receiveNow"), inputMode: "decimal", value: line.quantity, onChange: (e) => { patchLine(index, { quantity: e.target.value }); }, dir: "ltr", className: "w-24", "data-testid": `receive-qty-${String(index)}` }) }), _jsx(TableCell, { children: lotTracked ? _jsx(TextField, { "aria-label": t("purchasing.lot"), value: line.lotNumber, onChange: (e) => { patchLine(index, { lotNumber: e.target.value }); }, dir: "ltr", className: "w-28", "data-testid": `receive-lot-${String(index)}` }) : null }), _jsx(TableCell, { children: lotTracked ? _jsx(TextField, { "aria-label": t("purchasing.expiresOn"), type: "date", value: line.expiresOn, onChange: (e) => { patchLine(index, { expiresOn: e.target.value }); }, dir: "ltr", "data-testid": `receive-expiry-${String(index)}` }) : null }), _jsx(TableCell, { children: serialTracked ? _jsx(TextField, { "aria-label": t("purchasing.serials"), value: line.serialNumbers, onChange: (e) => { patchLine(index, { serialNumbers: e.target.value }); }, dir: "ltr", className: "w-40", placeholder: t("purchasing.serialsHelp"), "data-testid": `receive-serials-${String(index)}` }) : null })] }, line.orderLineId));
                                        }) })] })) : (_jsx("p", { className: "text-xs text-fg-muted", children: form.orderId ? t("purchasing.nothingReceivable") : t("purchasing.chooseOrder") })), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, disabled: !form.orderId, "data-testid": "save-receipt", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setReversal(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: r ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "receipt-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: r.number }), _jsx(PurchaseStatus, { status: r.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("nav.purchaseOrders"), r.orderNumber],
                                    [t("partners.supplier"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                                    [t("purchasing.receiveInto"), r.warehouseCode ?? "—"],
                                    [t("purchasing.postingDate"), formatDate(r.postingDate)],
                                    [t("purchasing.deliveryNote"), r.supplierDeliveryNote ?? "—"],
                                    [t("purchasing.expectedValue"), _jsxs("span", { "data-testid": "receipt-value", children: [formatMoney(r.totalExpectedCost, r.functionalCurrency), r.exchangeRate !== 1 ? ` (${r.currency} @ ${formatNumber(r.exchangeRate, { maximumFractionDigits: 6 })})` : ""] }, "value")],
                                    ...(r.reversalReason ? [[t("purchasing.reversalReason"), r.reversalReason]] : []),
                                ] }), _jsxs(Table, { "data-testid": "receipt-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.quantity") }), _jsx(TableHead, { children: t("purchasing.lot") }), _jsx(TableHead, { children: t("purchasing.expectedUnitCost") }), _jsx(TableHead, { children: t("purchasing.expectedValue") })] }) }), _jsx(TableBody, { children: r.lines.map((l) => (_jsxs(TableRow, { "data-testid": "receipt-line-row", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsxs(TableCell, { dir: "auto", children: [l.itemCode, " \u00B7 ", localized(l.itemName)] }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.quantity, { maximumFractionDigits: 3 }), " ", l.uomCode] }), _jsxs(TableCell, { dir: "ltr", children: [l.lotNumber ?? "", l.serialNumbers.length > 0 ? ` ${l.serialNumbers.join(", ")}` : ""] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(l.expectedUnitCost, { maximumFractionDigits: 4 }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.expectedCostAmount, r.functionalCurrency) })] }, l.id))) })] }), reversal !== null ? (_jsx(Field, { label: t("purchasing.reversalReason"), required: true, children: _jsx(TextField, { value: reversal, onChange: (e) => { setReversal(e.target.value); }, "data-testid": "reversal-reason" }) })) : null, _jsxs(DialogFooter, { children: [r.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setForm({ id: r.id, orderId: r.orderId, warehouseId: r.warehouseId, postingDate: r.postingDate, supplierDeliveryNote: r.supplierDeliveryNote ?? "", lines: r.lines.map((l) => ({ orderLineId: l.orderLineId, quantity: String(l.quantity), lotNumber: l.lotNumber ?? "", expiresOn: l.expiresOn ?? "", serialNumbers: l.serialNumbers.join(" ") })) }); }, "data-testid": "edit-receipt", children: t("common.edit") }) : null, r.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: r.id, action: "delete" }); }, loading: act.isPending, "data-testid": "delete-receipt", children: t("purchasing.deleteDraft") }) : null, r.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "post" }); }, loading: act.isPending, "data-testid": "post-receipt", children: t("purchasing.postReceipt") }) : null, r.status === "posted" && reversal === null ? _jsx(Button, { variant: "secondary", onClick: () => { setReversal(""); }, "data-testid": "reverse-receipt", children: t("purchasing.reverse") }) : null, reversal !== null ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "reverse", reason: reversal }); }, loading: act.isPending, disabled: !reversal.trim(), "data-testid": "confirm-reverse", children: t("purchasing.reverseNow") }) : null] })] })) : null }) })] }));
}
