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
import { emptyLine, LinesEditor, num, PurchaseStatus, useAgreements, useSuppliers } from "./shared";
/** Blanket purchase agreements (roadmap 4.2): agreed quantities and prices per item for a period, released by purchase orders and capped. */
export function AgreementsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const suppliers = useSuppliers(companyId);
    const list = useAgreements(companyId);
    const detail = useQuery({
        queryKey: ["agreement", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/agreements/{agreementId}", { params: { path: { agreementId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["agreements"] });
        await queryClient.invalidateQueries({ queryKey: ["agreement"] });
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const create = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/purchasing/agreements", {
            body: {
                companyId,
                partnerId: f.partnerId,
                validFrom: f.validFrom,
                validTo: f.validTo,
                currency: f.currency || null,
                committedAmount: num(f.committedAmount),
                lines: f.lines.map((l) => ({ itemCode: l.itemCode, agreedQty: num(l.quantity), uom: l.uom || null, agreedPrice: num(l.price) })),
            },
        })),
        onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { agreementId: input.id } };
            switch (input.action) {
                case "activate": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/activate", { params }));
                case "close": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/close", { params }));
                case "cancel": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/cancel", { params }));
            }
        },
        onSuccess: async () => { setProblem(null); await refresh(); },
        onError: fail,
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => _jsxs("span", { dir: "auto", children: [row.original.partnerCode, " \u00B7 ", localized(row.original.partnerName)] }) },
        { id: "from", accessorKey: "validFrom", header: t("purchasing.validFrom"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.validFrom) }) },
        { id: "to", accessorKey: "validTo", header: t("purchasing.validTo"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.validTo) }) },
        { id: "released", accessorKey: "releasedAmount", header: t("purchasing.releasedAmount"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.releasedAmount, row.original.currency) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ partnerId: "", validFrom: today(), validTo: "", currency: "", committedAmount: "0", lines: [emptyLine()] }); };
    const submit = (event) => { event.preventDefault(); if (form) {
        create.mutate(form);
    } };
    const a = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.agreements"), description: t("purchasing.agreementsDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-agreement", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newAgreement")] }) }), _jsx("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: _jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }) }), _jsx(DataGrid, { label: "nav.agreements", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyAgreements"), emptyDescription: t("purchasing.emptyAgreementsDescription"), onOpen: (row) => { setProblem(null); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("purchasing.newAgreement") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("partners.supplier"), required: true, children: _jsxs(SelectField, { value: form.partnerId, onChange: (e) => { setForm({ ...form, partnerId: e.target.value }); }, required: true, "data-testid": "agreement-supplier", children: [_jsx("option", { value: "", children: "\u2014" }), (suppliers.data ?? []).map((s) => (_jsxs("option", { value: s.partnerId, children: [s.partnerCode, " \u00B7 ", localized(s.partnerName)] }, s.partnerId)))] }) }), _jsx(Field, { label: t("purchasing.validFrom"), required: true, children: _jsx(TextField, { type: "date", value: form.validFrom, onChange: (e) => { setForm({ ...form, validFrom: e.target.value }); }, dir: "ltr", required: true, "data-testid": "agreement-from" }) }), _jsx(Field, { label: t("purchasing.validTo"), required: true, children: _jsx(TextField, { type: "date", value: form.validTo, onChange: (e) => { setForm({ ...form, validTo: e.target.value }); }, dir: "ltr", required: true, "data-testid": "agreement-to" }) }), _jsx(Field, { label: t("partners.currency"), description: t("purchasing.currencyHelp"), children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) }), _jsx(Field, { label: t("purchasing.committedAmount"), description: t("purchasing.committedAmountHelp"), children: _jsx(TextField, { inputMode: "decimal", value: form.committedAmount, onChange: (e) => { setForm({ ...form, committedAmount: e.target.value }); }, dir: "ltr" }) })] }), _jsx(LinesEditor, { lines: form.lines, onChange: (lines) => { setForm({ ...form, lines }); }, priceLabel: t("purchasing.agreedPrice") }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, "data-testid": "save-agreement", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: a ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "agreement-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: a.number }), _jsx(PurchaseStatus, { status: a.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("partners.supplier"), `${a.partnerCode} · ${localized(a.partnerName)}`],
                                    [t("purchasing.validity"), `${formatDate(a.validFrom)} → ${formatDate(a.validTo)}`],
                                    [t("purchasing.committedAmount"), Number(a.committedAmount) > 0 ? formatMoney(a.committedAmount, a.currency) : t("purchasing.uncapped")],
                                    [t("purchasing.releasedAmount"), formatMoney(a.releasedAmount, a.currency)],
                                ] }), _jsxs(Table, { "data-testid": "agreement-lines", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.agreedQty") }), _jsx(TableHead, { children: t("purchasing.agreedPrice") }), _jsx(TableHead, { children: t("purchasing.releasedQty") }), _jsx(TableHead, { children: t("purchasing.remainingQty") })] }) }), _jsx(TableBody, { children: a.lines.map((l) => (_jsxs(TableRow, { "data-testid": "agreement-line", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsxs(TableCell, { dir: "auto", children: [l.itemCode, " \u00B7 ", localized(l.itemName)] }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.agreedQty, { maximumFractionDigits: 3 }), " ", l.uomCode] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(l.agreedPrice, { maximumFractionDigits: 4 }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(l.releasedQty, { maximumFractionDigits: 3 }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", "data-testid": "remaining-qty", children: formatNumber(l.remainingQty, { maximumFractionDigits: 3 }) })] }, l.id))) })] }), _jsxs(DialogFooter, { children: [a.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ id: a.id, action: "activate" }); }, loading: act.isPending, "data-testid": "activate-agreement", children: t("purchasing.activate") }) : null, a.status === "active" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: a.id, action: "close" }); }, loading: act.isPending, "data-testid": "close-agreement", children: t("purchasing.close") }) : null, a.status === "draft" || a.status === "active" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: a.id, action: "cancel" }); }, loading: act.isPending, "data-testid": "cancel-agreement", children: t("purchasing.cancelDocument") }) : null] })] })) : null }) })] }));
}
