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
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, PurchaseStatus, useChargeTypes, useSuppliers } from "./shared";
const bases = ["value", "weight", "volume", "quantity"];
/** Landed costs (roadmap 4.5): charges allocated to posted receipt lines by value, weight, volume or quantity, posted so what is on hand takes its share and what was sold goes to cost of sales; charge invoices settle the estimates. */
export function LandedCostsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const [tab, setTab] = useState("allocations");
    const [reversal, setReversal] = useState(null);
    const suppliers = useSuppliers(companyId);
    const chargeTypes = useChargeTypes();
    const [view, setView] = useState("documents");
    const [chargeType, setChargeType] = useState(null);
    const list = useQuery({
        queryKey: ["landed-costs", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const allocatable = useQuery({
        queryKey: ["allocatable", companyId],
        enabled: Boolean(companyId) && Boolean(form),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs/allocatable", { params: { query: { companyId } } })),
    });
    const detail = useQuery({
        queryKey: ["landed-cost", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs/{landedCostId}", { params: { path: { landedCostId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await Promise.all([["landed-costs"], ["landed-cost"], ["allocatable"], ["invoicable"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const save = useMutation({
        mutationFn: async (f) => {
            const body = {
                companyId,
                postingDate: f.postingDate || null,
                currency: f.currency || null,
                reference: f.reference || null,
                receiptLineIds: f.receiptLineIds,
                charges: f.charges.map((c) => ({ chargeTypeId: c.chargeTypeId, amount: num(c.amount), partnerId: c.partnerId || null, description: c.description || null, allocationBasis: c.allocationBasis || null })),
            };
            return f.id ? unwrap(await api.PUT("/api/v1/purchasing/landed-costs/{landedCostId}", { params: { path: { landedCostId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/landed-costs", { body }));
        },
        onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
        onError: fail,
    });
    const saveChargeType = useMutation({
        mutationFn: async (f) => {
            const body = { code: f.code, name: { en: f.name, ar: f.nameAr || f.name }, defaultAllocationBasis: f.basis, isActive: f.isActive };
            return f.id ? unwrap(await api.PUT("/api/v1/purchasing/charge-types/{chargeTypeId}", { params: { path: { chargeTypeId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/charge-types", { body }));
        },
        onSuccess: async () => { setProblem(null); setChargeType(null); await queryClient.invalidateQueries({ queryKey: ["charge-types"] }); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { landedCostId: input.id } };
            switch (input.action) {
                case "post": return unwrap(await api.POST("/api/v1/purchasing/landed-costs/{landedCostId}/post", { params }));
                case "reverse": return unwrap(await api.POST("/api/v1/purchasing/landed-costs/{landedCostId}/reverse", { params, body: { reason: input.reason ?? "" } }));
                case "delete": {
                    unwrap(await api.DELETE("/api/v1/purchasing/landed-costs/{landedCostId}", { params }));
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
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 110, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.postingDate) }) },
        { id: "reference", accessorKey: "reference", header: t("purchasing.reference"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.reference ?? "" }) },
        { id: "charges", accessorFn: (row) => row.charges.length, header: t("purchasing.charges"), size: 90, cell: ({ row }) => String(row.original.charges.length) },
        { id: "total", accessorKey: "totalAmountFc", header: t("purchasing.total"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalAmountFc, row.original.functionalCurrency) }) },
        { id: "onHand", accessorKey: "onHandPortionFc", header: t("purchasing.toStock"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.onHandPortionFc, row.original.functionalCurrency) }) },
        { id: "sold", accessorKey: "soldPortionFc", header: t("purchasing.toCogs"), size: 140, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.soldPortionFc, row.original.functionalCurrency) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ id: null, postingDate: today(), currency: "", reference: "", receiptLineIds: [], charges: [{ chargeTypeId: "", partnerId: "", amount: "", allocationBasis: "", description: "" }] }); };
    const openEdit = (d) => {
        setProblem(null);
        setForm({ id: d.id, postingDate: d.postingDate, currency: d.currency, reference: d.reference ?? "", receiptLineIds: [...new Set(d.allocations.map((a) => a.receiptLineId))], charges: d.charges.map((c) => ({ chargeTypeId: c.chargeTypeId, partnerId: c.partnerId ?? "", amount: String(c.amount), allocationBasis: c.allocationBasis, description: c.description ?? "" })) });
    };
    const patchCharge = (index, change) => { if (form) {
        setForm({ ...form, charges: form.charges.map((c, i) => (i === index ? { ...c, ...change } : c)) });
    } };
    const toggleLine = (id) => { if (form) {
        setForm({ ...form, receiptLineIds: form.receiptLineIds.includes(id) ? form.receiptLineIds.filter((x) => x !== id) : [...form.receiptLineIds, id] });
    } };
    const submit = (event) => { event.preventDefault(); if (form) {
        save.mutate(form);
    } };
    const d = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.landedCosts"), description: t("purchasing.landedCostsDescription"), actions: view === "documents" ? (_jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-landed-cost", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newLandedCost")] })) : (_jsxs(Button, { onClick: () => { setProblem(null); setChargeType({ id: null, code: "", name: "", nameAr: "", basis: "value", isActive: true }); }, "data-testid": "new-charge-type", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newChargeType")] })) }), _jsx(Tabs, { tabs: [{ id: "documents", label: t("nav.landedCosts"), testId: "view-documents" }, { id: "types", label: t("purchasing.chargeTypes"), testId: "view-charge-types" }], value: view, onChange: setView }), view === "documents" ? (_jsxs(_Fragment, { children: [_jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "posted", "reversed"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.landedCosts", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyLandedCosts"), emptyDescription: t("purchasing.emptyLandedCostsDescription"), onOpen: (row) => { setProblem(null); setReversal(null); setTab("allocations"); setOpenId(row.id); } })] })) : (_jsxs(Table, { "data-testid": "charge-types", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.code") }), _jsx(TableHead, { children: t("partners.name") }), _jsx(TableHead, { children: t("purchasing.defaultBasis") }), _jsx(TableHead, { children: t("common.status") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: (chargeTypes.data ?? []).map((c) => (_jsxs(TableRow, { "data-testid": "charge-type-row", children: [_jsx(TableCell, { dir: "ltr", children: c.code }), _jsx(TableCell, { dir: "auto", children: localized(c.name) }), _jsx(TableCell, { children: t(`purchasing.bases.${c.defaultAllocationBasis}`) }), _jsx(TableCell, { children: c.isActive ? t("common.active") : t("common.inactive") }), _jsx(TableCell, { children: _jsx(Button, { variant: "ghost", size: "sm", onClick: () => { setProblem(null); setChargeType({ id: c.id, code: c.code, name: c.name.en ?? "", nameAr: c.name.ar ?? "", basis: c.defaultAllocationBasis, isActive: c.isActive }); }, children: t("common.edit") }) })] }, c.id))) })] })), _jsx(Dialog, { open: Boolean(chargeType), onOpenChange: (isOpen) => { if (!isOpen) {
                    setChargeType(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-xl", children: chargeType ? (_jsxs("form", { onSubmit: (e) => { e.preventDefault(); saveChargeType.mutate(chargeType); }, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: chargeType.id ? chargeType.code : t("purchasing.newChargeType") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("partners.code"), required: true, children: _jsx(TextField, { value: chargeType.code, onChange: (e) => { setChargeType({ ...chargeType, code: e.target.value.toUpperCase() }); }, dir: "ltr", required: true, "data-testid": "charge-type-code" }) }), _jsx(Field, { label: t("purchasing.defaultBasis"), children: _jsx(SelectField, { value: chargeType.basis, onChange: (e) => { setChargeType({ ...chargeType, basis: e.target.value }); }, "data-testid": "charge-type-basis", children: bases.map((b) => (_jsx("option", { value: b, children: t(`purchasing.bases.${b}`) }, b))) }) }), _jsx(Field, { label: t("partners.name"), required: true, children: _jsx(TextField, { value: chargeType.name, onChange: (e) => { setChargeType({ ...chargeType, name: e.target.value }); }, required: true, "data-testid": "charge-type-name" }) }), _jsx(Field, { label: t("partners.nameAr"), children: _jsx(TextField, { value: chargeType.nameAr, onChange: (e) => { setChargeType({ ...chargeType, nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: chargeType.isActive, onChange: (e) => { setChargeType({ ...chargeType, isActive: e.target.checked }); } }), t("common.active")] })] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setChargeType(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: saveChargeType.isPending, "data-testid": "save-charge-type", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: form.id ? t("purchasing.editLandedCost") : t("purchasing.newLandedCost") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("purchasing.postingDate"), children: _jsx(TextField, { type: "date", value: form.postingDate, onChange: (e) => { setForm({ ...form, postingDate: e.target.value }); }, dir: "ltr", "data-testid": "landed-cost-date" }) }), _jsx(Field, { label: t("partners.currency"), description: t("purchasing.companyCurrencyHelp"), children: _jsx(TextField, { value: form.currency, onChange: (e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3 }) }), _jsx(Field, { label: t("purchasing.reference"), children: _jsx(TextField, { value: form.reference, onChange: (e) => { setForm({ ...form, reference: e.target.value }); }, dir: "ltr", "data-testid": "landed-cost-reference" }) })] }), _jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.charges") }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setForm({ ...form, charges: [...form.charges, { chargeTypeId: "", partnerId: "", amount: "", allocationBasis: "", description: "" }] }); }, "data-testid": "add-charge", children: t("purchasing.addCharge") })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.chargeType") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("purchasing.basis") }), _jsx(TableHead, { children: t("purchasing.expectedFrom") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: form.charges.map((c, index) => (_jsxs(TableRow, { "data-testid": "charge-row", children: [_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("purchasing.chargeType"), value: c.chargeTypeId, onChange: (e) => { patchCharge(index, { chargeTypeId: e.target.value }); }, required: true, "data-testid": `charge-type-${String(index)}`, children: [_jsx("option", { value: "", children: "\u2014" }), (chargeTypes.data ?? []).filter((x) => x.isActive).map((x) => (_jsxs("option", { value: x.id, children: [x.code, " \u00B7 ", localized(x.name)] }, x.id)))] }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.amount"), inputMode: "decimal", value: c.amount, onChange: (e) => { patchCharge(index, { amount: e.target.value }); }, dir: "ltr", className: "w-28", "data-testid": `charge-amount-${String(index)}` }) }), _jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("purchasing.basis"), value: c.allocationBasis, onChange: (e) => { patchCharge(index, { allocationBasis: e.target.value }); }, "data-testid": `charge-basis-${String(index)}`, children: [_jsx("option", { value: "", children: t("purchasing.typeDefault") }), bases.map((b) => (_jsx("option", { value: b, children: t(`purchasing.bases.${b}`) }, b)))] }) }), _jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("purchasing.expectedFrom"), value: c.partnerId, onChange: (e) => { patchCharge(index, { partnerId: e.target.value }); }, "data-testid": `charge-supplier-${String(index)}`, children: [_jsx("option", { value: "", children: "\u2014" }), (suppliers.data ?? []).map((s) => (_jsx("option", { value: s.partnerId, children: s.partnerCode }, s.partnerId)))] }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setForm({ ...form, charges: form.charges.filter((_, i) => i !== index) }); }, children: t("workflow.remove") }) })] }, index))) })] }), _jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.landOn") }), (allocatable.data ?? []).length === 0 ? _jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.nothingAllocatable") }) : (_jsx("ul", { className: "grid gap-1 text-sm sm:grid-cols-2", "data-testid": "allocatable-lines", children: (allocatable.data ?? []).map((l) => (_jsx("li", { children: _jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: form.receiptLineIds.includes(l.receiptLineId), onChange: () => { toggleLine(l.receiptLineId); }, "data-testid": `allocate-${l.receiptNumber}-${String(l.lineNo)}` }), _jsx("span", { dir: "ltr", children: l.receiptNumber }), _jsxs("span", { dir: "auto", children: [l.itemCode, " \u00B7 ", localized(l.itemName)] }), _jsxs("span", { className: "tabular text-fg-muted", dir: "ltr", children: [formatNumber(l.quantity, { maximumFractionDigits: 3 }), " ", l.uomCode, " \u00B7 ", formatNumber(l.valueFc)] })] }) }, l.receiptLineId))) })), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, disabled: form.receiptLineIds.length === 0 || form.charges.length === 0, "data-testid": "save-landed-cost", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                    setReversal(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: d ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "landed-cost-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: d.number }), _jsx(PurchaseStatus, { status: d.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("purchasing.postingDate"), formatDate(d.postingDate)],
                                    [t("purchasing.reference"), d.reference ?? "—"],
                                    [t("purchasing.total"), _jsx("span", { "data-testid": "landed-cost-total", children: formatMoney(d.totalAmountFc, d.functionalCurrency) }, "total")],
                                    [t("purchasing.toStock"), _jsx("span", { "data-testid": "landed-cost-on-hand", children: formatMoney(d.onHandPortionFc, d.functionalCurrency) }, "stock")],
                                    [t("purchasing.toCogs"), _jsx("span", { "data-testid": "landed-cost-sold", children: formatMoney(d.soldPortionFc, d.functionalCurrency) }, "cogs")],
                                    ...(d.reversalReason ? [[t("purchasing.reversalReason"), d.reversalReason]] : []),
                                ] }), _jsx(Tabs, { tabs: [{ id: "allocations", label: t("purchasing.allocationReport"), testId: "tab-allocations" }, { id: "charges", label: t("purchasing.charges"), testId: "tab-charges" }], value: tab, onChange: setTab }), tab === "allocations" ? (_jsxs(Table, { "data-testid": "allocation-rows", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.chargeType") }), _jsx(TableHead, { children: t("nav.receipts") }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.basis") }), _jsx(TableHead, { children: t("purchasing.allocated") }), _jsx(TableHead, { children: t("purchasing.toStock") }), _jsx(TableHead, { children: t("purchasing.toCogs") })] }) }), _jsx(TableBody, { children: d.allocations.map((a) => (_jsxs(TableRow, { "data-testid": "allocation-row", children: [_jsx(TableCell, { children: a.chargeTypeCode }), _jsx(TableCell, { dir: "ltr", children: a.receiptNumber }), _jsxs(TableCell, { dir: "auto", children: [a.itemCode, " \u00B7 ", localized(a.itemName), " (", formatNumber(a.receivedQuantity, { maximumFractionDigits: 3 }), " ", a.uomCode, ")"] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatNumber(a.basisValue, { maximumFractionDigits: 3 }) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(a.allocatedAmountFc, d.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(a.onHandPortionFc, d.functionalCurrency) }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(a.soldPortionFc, d.functionalCurrency) })] }, a.id))) })] })) : null, tab === "charges" ? (_jsxs(Table, { "data-testid": "charge-rows", children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.chargeType") }), _jsx(TableHead, { children: t("purchasing.amount") }), _jsx(TableHead, { children: t("purchasing.basis") }), _jsx(TableHead, { children: t("purchasing.expectedFrom") }), _jsx(TableHead, { children: t("purchasing.settlement") })] }) }), _jsx(TableBody, { children: d.charges.map((c) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(c.lineNo) }), _jsxs(TableCell, { children: [c.chargeTypeCode, c.description ? ` · ${c.description}` : ""] }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: formatMoney(c.amount, d.currency) }), _jsx(TableCell, { children: t(`purchasing.bases.${c.allocationBasis}`) }), _jsx(TableCell, { dir: "ltr", children: c.partnerCode ?? "—" }), _jsx(TableCell, { className: "tabular", dir: "ltr", children: c.isEstimate ? t("purchasing.estimated") : t("purchasing.invoicedAt", { amount: formatMoney(c.invoicedAmountFc, d.functionalCurrency) }) })] }, c.id))) })] })) : null, reversal !== null ? (_jsx(Field, { label: t("purchasing.reversalReason"), required: true, children: _jsx(TextField, { value: reversal, onChange: (e) => { setReversal(e.target.value); }, "data-testid": "reversal-reason" }) })) : null, _jsxs(DialogFooter, { children: [d.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { openEdit(d); }, "data-testid": "edit-landed-cost", children: t("common.edit") }) : null, d.status === "draft" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: d.id, action: "delete" }); }, loading: act.isPending, "data-testid": "delete-landed-cost", children: t("purchasing.deleteDraft") }) : null, d.status === "draft" ? _jsx(Button, { onClick: () => { act.mutate({ id: d.id, action: "post" }); }, loading: act.isPending, "data-testid": "post-landed-cost", children: t("purchasing.postLandedCost") }) : null, d.status === "posted" && reversal === null ? _jsx(Button, { variant: "secondary", onClick: () => { setReversal(""); }, "data-testid": "reverse-landed-cost", children: t("purchasing.reverse") }) : null, reversal !== null ? _jsx(Button, { onClick: () => { act.mutate({ id: d.id, action: "reverse", reason: reversal }); }, loading: act.isPending, disabled: !reversal.trim(), "data-testid": "confirm-reverse", children: t("purchasing.reverseNow") }) : null] })] })) : null }) })] }));
}
