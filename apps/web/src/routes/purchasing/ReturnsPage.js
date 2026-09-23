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
import { num, PurchaseStatus } from "./shared";
/** Supplier returns (roadmap 4.6): quantities of a posted receipt sent back at their exact cost, GRNI relieved under the return's reference, credited by the supplier's debit note, reversed while nothing has been credited. */
export function ReturnsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [reversal, setReversal] = useState(null);
    const list = useQuery({
        queryKey: ["returns", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const returnable = useQuery({
        queryKey: ["returnable", companyId],
        enabled: Boolean(companyId) && Boolean(form),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns/returnable", { params: { query: { companyId } } })),
    });
    const detail = useQuery({
        queryKey: ["return", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns/{returnId}", { params: { path: { returnId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["returns"], ["return"], ["returnable"], ["receipts"], ["receipt"], ["invoicable"], ["stock"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const body = {
                receiptId: f.receiptId,
                postingDate: f.postingDate || null,
                reason: f.reason || null,
                supplierRma: f.supplierRma || null,
                lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ receiptLineId: l.receiptLineId, quantity: num(l.quantity), lotNumber: l.lotNumber || null, serialNumbers: l.serialNumbers.split(/[\s,]+/).filter(Boolean), reason: l.reason || null })),
            };
            return f.id ? unwrap(await api.PUT("/api/v1/purchasing/returns/{returnId}", { params: { path: { returnId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/returns", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { returnId: input.id } };
            switch (input.action) {
                case "post": return unwrap(await api.POST("/api/v1/purchasing/returns/{returnId}/post", { params }));
                case "reverse": return unwrap(await api.POST("/api/v1/purchasing/returns/{returnId}/reverse", { params, body: { reason: input.reason ?? "" } }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/purchasing/returns/{returnId}", { params }));
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
        { id: "receipt", accessorKey: "receiptNumber", header: t("nav.receipts"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.receiptNumber }) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.postingDate) }) },
        { id: "rma", accessorKey: "supplierRma", header: t("purchasing.supplierRma"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.supplierRma ?? "" }) },
        { id: "cost", accessorKey: "totalCostFc", header: t("purchasing.costValue"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalCostFc, row.original.functionalCurrency) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ id: null, receiptId: "", postingDate: today(), reason: "", supplierRma: "", lines: [] }); };
    const chooseReceipt = (receiptId) => {
        if (!form) {
            return;
        }
        const lines = (returnable.data ?? []).filter((l) => l.receiptId === receiptId);
        setForm({ ...form, receiptId, lines: lines.map((l) => ({ receiptLineId: l.receiptLineId, quantity: "", lotNumber: l.lotNumber ?? "", serialNumbers: "", reason: "" })) });
    };
    const patchLine = (index, change) => { if (form) {
        setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });
    } };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const receipts = useMemo(() => {
        const seen = new Map();
        for (const l of returnable.data ?? []) {
            seen.set(l.receiptId, `${l.receiptNumber} · ${l.partnerCode}`);
        }
        return [...seen.entries()];
    }, [returnable.data]);
    const lineInfo = (receiptLineId) => returnable.data?.find((l) => l.receiptLineId === receiptLineId);
    const r = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.returns"), description: t("purchasing.returnsDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-return", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newReturn")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "posted", "reversed"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.returns", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyReturns"), emptyDescription: t("purchasing.emptyReturnsDescription"), onOpen: (row) => { setProblem(null); setReversal(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("purchasing.editReturn") : t("purchasing.newReturn") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(Field, { label: t("nav.receipts"), required: true, children: _jsxs(SelectField, { value: form.receiptId, onChange: (e) => { chooseReceipt(e.target.value); }, required: true, disabled: Boolean(form.id), "data-testid": "return-receipt", children: [_jsx("option", { value: "", children: "\u2014" }), receipts.map(([id, label]) => (_jsx("option", { value: id, children: label }, id)))] }) }), _jsx(Field, { label: t("purchasing.postingDate"), children: _jsx(TextField, { type: "date", value: form.postingDate, onChange: (e) => { setForm({ ...form, postingDate: e.target.value }); }, dir: "ltr", "data-testid": "return-date" }) }), _jsx(Field, { label: t("purchasing.supplierRma"), children: _jsx(TextField, { value: form.supplierRma, onChange: (e) => { setForm({ ...form, supplierRma: e.target.value }); }, dir: "ltr", "data-testid": "return-rma" }) }), _jsx(Field, { label: t("purchasing.returnReason"), children: _jsx(TextField, { value: form.reason, onChange: (e) => { setForm({ ...form, reason: e.target.value }); }, "data-testid": "return-reason" }) })] }), form.lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.received") }), _jsx(TableHead, { children: t("purchasing.returnedSoFar") }), _jsx(TableHead, { children: t("purchasing.returnNow") }), _jsx(TableHead, { children: t("purchasing.lot") }), _jsx(TableHead, { children: t("purchasing.serials") }), _jsx(TableHead, { children: t("purchasing.returnReason") })] }) }), _jsx(TableBody, { children: form.lines.map((line, index) => {
                                            const info = lineInfo(line.receiptLineId);
                                            const lotTracked = info?.tracking === "lot" || info?.tracking === "lot_and_serial";
                                            const serialTracked = info?.tracking === "serial" || info?.tracking === "lot_and_serial";
                                            return (_jsxs(TableRow, { "data-testid": "return-line", children: [_jsx(TableCell, { dir: "auto", children: info ? `${info.itemCode} · ${localized(info.itemName)}` : "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: info ? `${formatNumber(info.received, { maximumFractionDigits: 3 })} ${info.uomCode}` : "" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: info ? formatNumber(info.returned, { maximumFractionDigits: 3 }) : "" }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.returnNow"), inputMode: "decimal", value: line.quantity, onChange: (e) => { patchLine(index, { quantity: e.target.value }); }, dir: "ltr", className: "w-24", "data-testid": `return-qty-${String(index)}` }) }), _jsx(TableCell, { children: lotTracked ? _jsx(TextField, { "aria-label": t("purchasing.lot"), value: line.lotNumber, onChange: (e) => { patchLine(index, { lotNumber: e.target.value }); }, dir: "ltr", className: "w-28", "data-testid": `return-lot-${String(index)}` }) : null }), _jsx(TableCell, { children: serialTracked ? _jsx(TextField, { "aria-label": t("purchasing.serials"), value: line.serialNumbers, onChange: (e) => { patchLine(index, { serialNumbers: e.target.value }); }, dir: "ltr", className: "w-40", placeholder: t("purchasing.serialsHelp"), "data-testid": `return-serials-${String(index)}` }) : null }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.returnReason"), value: line.reason, onChange: (e) => { patchLine(index, { reason: e.target.value }); }, className: "w-40", "data-testid": `return-line-reason-${String(index)}` }) })] }, line.receiptLineId));
                                        }) })] })) : (_jsx("p", { className: "text-xs text-fg-muted", children: form.receiptId ? t("purchasing.nothingReturnable") : t("purchasing.chooseReceipt") })), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, disabled: !form.receiptId, "data-testid": "save-return", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setReversal(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: r ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "return-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: r.number }), _jsx(PurchaseStatus, { status: r.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("nav.receipts"), r.receiptNumber],
                                    [t("partners.supplier"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                                    [t("purchasing.warehouse"), r.warehouseCode ?? "—"],
                                    [t("purchasing.postingDate"), formatDate(r.postingDate)],
                                    [t("purchasing.supplierRma"), r.supplierRma ?? "—"],
                                    [t("purchasing.returnReason"), r.reason ?? "—"],
                                    [t("purchasing.costValue"), _jsx("span", { "data-testid": "return-value", children: formatMoney(r.totalCostFc, r.functionalCurrency) }, "value")],
                                    ...(r.reversalReason ? [[t("purchasing.reversalReason"), r.reversalReason]] : []),
                                ] }), _jsxs(Table, { "data-testid": "return-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.quantity") }), _jsx(TableHead, { children: t("purchasing.lot") }), _jsx(TableHead, { children: t("purchasing.costValue") }), _jsx(TableHead, { children: t("purchasing.credited") })] }) }), _jsx(TableBody, { children: r.lines.map((l) => (_jsxs(TableRow, { "data-testid": "return-line-row", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsxs(TableCell, { dir: "auto", children: [l.itemCode, " \u00B7 ", localized(l.itemName), l.reason ? ` — ${l.reason}` : ""] }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.quantity, { maximumFractionDigits: 3 }), " ", l.uomCode] }), _jsxs(TableCell, { dir: "ltr", children: [l.lotNumber ?? "", l.serialNumbers.length > 0 ? ` ${l.serialNumbers.join(", ")}` : ""] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(l.costAmountFc, r.functionalCurrency) }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.qtyCredited, { maximumFractionDigits: 3 }), " \u00B7 ", formatMoney(l.creditedAmountFc, r.functionalCurrency)] })] }, l.id))) })] }), reversal !== null ? (_jsx(Field, { label: t("purchasing.reversalReason"), required: true, children: _jsx(TextField, { value: reversal, onChange: (e) => { setReversal(e.target.value); }, "data-testid": "reversal-reason" }) })) : null, _jsxs(DialogFooter, { children: [r.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { setProblem(null); setForm({ id: r.id, receiptId: r.receiptId, postingDate: r.postingDate, reason: r.reason ?? "", supplierRma: r.supplierRma ?? "", lines: r.lines.map((l) => ({ receiptLineId: l.receiptLineId, quantity: String(l.quantity), lotNumber: l.lotNumber ?? "", serialNumbers: l.serialNumbers.join(" "), reason: l.reason ?? "" })) }); }, "data-testid": "edit-return", children: t("common.edit") }) : null, r.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: r.id, action: "delete" }); }, loading: act.isPending, "data-testid": "delete-return", children: t("purchasing.deleteDraft") }) : null, r.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "post" }); }, loading: act.isPending, "data-testid": "post-return", children: t("purchasing.postReturn") }) : null, r.status === "posted" && reversal === null ? _jsx(Button, { variant: "secondary", onClick: () => { setReversal(""); }, "data-testid": "reverse-return", children: t("purchasing.reverse") }) : null, reversal !== null ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "reverse", reason: reversal }); }, loading: act.isPending, disabled: !reversal.trim(), "data-testid": "confirm-reverse", children: t("purchasing.reverseNow") }) : null] })] })) : null }) })] }));
}
