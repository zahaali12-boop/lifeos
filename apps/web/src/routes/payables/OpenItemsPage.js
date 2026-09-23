import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { ItemStatus, kindLabel, useOpenItems } from "./shared";
/** Payable open items (roadmap 4.7): what is owed per supplier, aged at any date from the items and their settlements; holds; credits, advances and payments on account applied to invoices. */
export function OpenItemsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [partnerId, setPartnerId] = useState("");
    const [status, setStatus] = useState("live");
    const [tab, setTab] = useState("items");
    const [asOf, setAsOf] = useState(today());
    const [openId, setOpenId] = useState(null);
    const [problem, setProblem] = useState(null);
    const [hold, setHold] = useState(null);
    const [apply, setApply] = useState(null);
    const suppliers = useSuppliers(companyId);
    const list = useOpenItems(companyId, partnerId, status);
    const aging = useQuery({
        queryKey: ["aging", companyId, asOf, partnerId],
        enabled: Boolean(companyId) && tab === "aging",
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/aging", { params: { query: { companyId, asOf, ...(partnerId ? { partnerId } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["open-item", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/{itemId}", { params: { path: { itemId: openId ?? "" } } })),
    });
    const settlements = useQuery({
        queryKey: ["settlements", companyId, openId],
        enabled: Boolean(openId) && Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/settlements", { params: { query: { companyId, openItemId: openId ?? "" } } })),
    });
    const targets = useOpenItems(companyId, detail.data?.item.partnerId ?? "", "live");
    const refresh = async () => {
        await Promise.all([["open-items"], ["open-item"], ["aging"], ["settlements"], ["invoice"], ["invoices"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const act = useMutation({
        mutationFn: async (input) => {
            switch (input.action) {
                case "hold": return unwrap(await api.POST("/api/v1/payables/open-items/{itemId}/hold", { params: { path: { itemId: input.id } }, body: { reason: input.reason ?? "" } }));
                case "release": return unwrap(await api.POST("/api/v1/payables/open-items/{itemId}/release", { params: { path: { itemId: input.id } } }));
                case "apply": return unwrap(await api.POST("/api/v1/payables/settlements/apply", { body: { settlingItemId: input.id, settledItemId: input.settledItemId ?? "", amount: input.amount ?? 0 } }));
                case "reverse": return unwrap(await api.POST("/api/v1/payables/settlements/{settlementId}/reverse", { params: { path: { settlementId: input.id } }, body: { reason: input.reason ?? "" } }));
            }
        },
        onSuccess: async () => { setProblem(null); setHold(null); setApply(null); await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "document", accessorFn: (r) => r.item.documentNumber, header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsxs("span", { dir: "ltr", children: [row.original.item.documentNumber, Number(row.original.item.instalment) > 1 ? ` / ${String(row.original.item.instalment)}` : ""] }) },
        { id: "kind", accessorFn: (r) => r.item.kind, header: t("purchasing.kind"), size: 130, cell: ({ row }) => kindLabel(t, row.original.item.kind) },
        { id: "status", accessorFn: (r) => r.item.status, header: t("common.status"), size: 130, cell: ({ row }) => _jsxs("span", { className: "flex items-center gap-1", children: [_jsx(ItemStatus, { status: row.original.item.status }), row.original.item.paymentBlocked ? _jsx(Badge, { tone: "danger", children: t("payables.held") }) : null] }) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "due", accessorFn: (r) => r.item.dueDate, header: t("purchasing.dueDate"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.item.dueDate) }) },
        { id: "original", accessorFn: (r) => r.item.originalTc, header: t("purchasing.amount"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.item.originalTc, row.original.item.currency) }) },
        { id: "remaining", accessorFn: (r) => r.item.remainingTc, header: t("purchasing.remaining"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.item.remainingTc, row.original.item.currency) }) },
        { id: "remainingFc", accessorFn: (r) => r.item.remainingFc, header: t("payables.remainingFc"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.item.remainingFc, row.original.functionalCurrency) }) },
    ], [t]);
    const d = detail.data;
    const item = d?.item;
    const canApply = item !== undefined && Number(item.originalTc) < 0 && (item.status === "open" || item.status === "partially_settled");
    const canHold = item !== undefined && (item.status === "open" || item.status === "partially_settled");
    const bucket = (value, currency) => (Number(value) === 0 ? "" : formatMoney(value, currency));
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.payables"), description: t("payables.description") }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("partners.supplier"), children: _jsxs(SelectField, { value: partnerId, onChange: (e) => { setPartnerId(e.target.value); }, "data-testid": "supplier-filter", children: [_jsx("option", { value: "", children: t("common.all") }), (suppliers.data ?? []).map((sup) => (_jsxs("option", { value: sup.partnerId, children: [sup.partnerCode, " \u00B7 ", localized(sup.partnerName)] }, sup.partnerId)))] }) }), tab === "items" ? (_jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "live", children: t("payables.live") }), ["open", "partially_settled", "settled", "reversed"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })) : (_jsx(Field, { label: t("payables.asOf"), children: _jsx(TextField, { type: "date", value: asOf, onChange: (e) => { setAsOf(e.target.value); }, dir: "ltr", "data-testid": "aging-as-of" }) }))] }), _jsx(Tabs, { tabs: [{ id: "items", label: t("payables.openItems"), testId: "tab-items" }, { id: "aging", label: t("payables.aging"), testId: "tab-aging" }], value: tab, onChange: setTab }), tab === "items" ? (_jsx(DataGrid, { label: "nav.payables", columns: columns, data: list.data ?? [], rowKey: (row) => row.item.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("payables.emptyItems"), emptyDescription: t("payables.emptyItemsDescription"), onOpen: (row) => { setProblem(null); setHold(null); setApply(null); setOpenId(row.item.id); } })) : (_jsx("div", { className: "mt-3", "data-testid": "aging-report", children: aging.data ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.supplier") }), _jsx(TableHead, { children: t("payables.notDue") }), _jsx(TableHead, { children: "1\u201330" }), _jsx(TableHead, { children: "31\u201360" }), _jsx(TableHead, { children: "61\u201390" }), _jsx(TableHead, { children: "90+" }), _jsx(TableHead, { children: t("payables.total") }), _jsx(TableHead, { children: t("payables.advances") })] }) }), _jsxs(TableBody, { children: [aging.data.rows.map((r) => (_jsxs(TableRow, { "data-testid": "aging-row", children: [_jsxs(TableCell, { dir: "auto", children: [r.partnerCode, " \u00B7 ", localized(r.partnerName)] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.notDue, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.days1To30, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.days31To60, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.days61To90, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.over90, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular font-semibold", dir: "ltr", children: formatMoney(r.totalFc, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(r.advancesFc, aging.data.functionalCurrency) })] }, r.partnerId))), _jsxs(TableRow, { "data-testid": "aging-totals", children: [_jsx(TableCell, { className: "font-semibold", children: t("payables.total") }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.notDue, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.days1To30, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.days31To60, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.days61To90, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.over90, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular font-semibold", dir: "ltr", "data-testid": "aging-total", children: formatMoney(aging.data.totals.totalFc, aging.data.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: bucket(aging.data.totals.advancesFc, aging.data.functionalCurrency) })] })] })] })) : _jsx("p", { className: "text-sm text-fg-muted", children: companyId ? t("common.loading") : t("payables.chooseCompany") }) })), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: d && item ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "open-item-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: item.documentNumber }), _jsx(ItemStatus, { status: item.status }), item.paymentBlocked ? _jsx(Badge, { tone: "danger", children: t("payables.held") }) : null] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("purchasing.kind"), kindLabel(t, item.kind)],
                                    [t("partners.supplier"), `${d.partnerCode} · ${localized(d.partnerName)}`],
                                    [t("purchasing.postingDate"), formatDate(item.postingDate)],
                                    [t("purchasing.dueDate"), formatDate(item.dueDate)],
                                    [t("purchasing.amount"), formatMoney(item.originalTc, item.currency)],
                                    [t("purchasing.remaining"), _jsx("span", { "data-testid": "item-remaining", children: formatMoney(item.remainingTc, item.currency) }, "rem")],
                                    [t("payables.remainingFc"), formatMoney(item.remainingFc, d.functionalCurrency)],
                                    ...(item.blockReason ? [[t("payables.holdReason"), item.blockReason]] : []),
                                ] }), _jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.settlements") }), (settlements.data ?? []).length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.noSettlements") }) : (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.postingDate") }), _jsx(TableHead, { children: t("purchasing.kind") }), _jsx(TableHead, { children: t("purchasing.appliedTo") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("purchasing.fxGainLoss") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: (settlements.data ?? []).map((st) => (_jsxs(TableRow, { "data-testid": "settlement-row", children: [_jsx(TableCell, { dir: "ltr", children: formatDate(st.settlementDate) }), _jsx(TableCell, { children: t(`purchasing.settlementKinds.${st.kind}`) }), _jsxs(TableCell, { dir: "ltr", children: [st.settlingDocumentNumber, " \u2192 ", st.settledDocumentNumber] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(st.amountTc, st.currency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(st.fxGainLossFc, d.functionalCurrency) }), _jsx(TableCell, { children: st.status === "posted" && !st.reversesSettlementId && (st.kind === "credit_application" || st.kind === "advance_application") ? _jsx(Button, { variant: "ghost", size: "sm", onClick: () => { act.mutate({ action: "reverse", id: st.id, reason: t("payables.unappliedReason") }); }, "data-testid": "unapply", children: t("payables.unapply") }) : null })] }, st.id))) })] })), hold !== null ? (_jsx(Field, { label: t("payables.holdReason"), required: true, children: _jsx(TextField, { value: hold, onChange: (e) => { setHold(e.target.value); }, "data-testid": "hold-reason" }) })) : null, apply ? (_jsxs("div", { className: "grid gap-3 sm:grid-cols-2", "data-testid": "apply-form", children: [_jsx(Field, { label: t("purchasing.appliedTo"), required: true, children: _jsxs(SelectField, { value: apply.settledItemId, onChange: (e) => { setApply({ ...apply, settledItemId: e.target.value }); }, "data-testid": "apply-target", children: [_jsx("option", { value: "", children: "\u2014" }), (targets.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === item.currency).map((o) => (_jsxs("option", { value: o.id, children: [o.documentNumber, " \u00B7 ", formatMoney(o.remainingTc, o.currency)] }, o.id)))] }) }), _jsx(Field, { label: t("purchasing.creditAmount"), required: true, children: _jsx(TextField, { inputMode: "decimal", value: apply.amount, onChange: (e) => { setApply({ ...apply, amount: e.target.value }); }, dir: "ltr", "data-testid": "apply-amount" }) })] })) : null, _jsxs(DialogFooter, { children: [canHold && !item.paymentBlocked && hold === null ? _jsx(Button, { variant: "secondary", onClick: () => { setHold(""); }, "data-testid": "hold-item", children: t("payables.hold") }) : null, hold !== null ? _jsx(Button, { onClick: () => { act.mutate({ action: "hold", id: item.id, reason: hold }); }, loading: act.isPending, disabled: !hold.trim(), "data-testid": "confirm-hold", children: t("payables.hold") }) : null, item.paymentBlocked ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ action: "release", id: item.id }); }, loading: act.isPending, "data-testid": "release-item", children: t("payables.release") }) : null, canApply && !apply ? _jsx(Button, { onClick: () => { setApply({ settledItemId: "", amount: String(Math.abs(Number(item.remainingTc))) }); }, "data-testid": "apply-item", children: t("purchasing.applyCredit") }) : null, apply ? _jsx(Button, { onClick: () => { act.mutate({ action: "apply", id: item.id, settledItemId: apply.settledItemId, amount: num(apply.amount) }); }, loading: act.isPending, disabled: !apply.settledItemId || num(apply.amount) <= 0, "data-testid": "confirm-apply", children: t("purchasing.applyCredit") }) : null] })] })) : null }) })] }));
}
