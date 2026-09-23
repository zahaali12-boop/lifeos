import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, num, optionalNum, PurchaseStatus, useSuppliers } from "./shared";
/** Purchase requisitions (roadmap 4.2): what a department needs, submitted through the workflow, then turned into one order per supplier. */
export function RequisitionsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const [status, setStatus] = useState("");
    const [problem, setProblem] = useState(null);
    const [form, setForm] = useState(null);
    const [openId, setOpenId] = useState(null);
    const suppliers = useSuppliers(companyId);
    const list = useQuery({
        queryKey: ["requisitions", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/requisitions", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["requisition", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/requisitions/{requisitionId}", { params: { path: { requisitionId: openId ?? "" } } })),
    });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["requisitions"] });
        await queryClient.invalidateQueries({ queryKey: ["requisition"] });
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const create = useMutation({
        mutationFn: async (f) => unwrap(await api.POST("/api/v1/purchasing/requisitions", {
            body: {
                companyId,
                neededBy: f.neededBy || null,
                justification: f.justification || null,
                lines: f.lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null, estimatedPrice: optionalNum(l.price), suggestedSupplierId: l.supplierId || null })),
            },
        })),
        onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
        onError: fail,
    });
    const act = useMutation({
        mutationFn: async (input) => {
            const params = { path: { requisitionId: input.id } };
            switch (input.action) {
                case "submit": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/submit", { params }));
                case "cancel": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/cancel", { params }));
                case "orders": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/orders", { params, body: {} }));
            }
        },
        onSuccess: async (result, input) => {
            setProblem(null);
            if (input.action === "orders" && "orders" in result) {
                setCreatedOrders(result.orders.map((o) => o.number));
            }
            await refresh();
            await queryClient.invalidateQueries({ queryKey: ["orders"] });
        },
        onError: fail,
    });
    const [createdOrders, setCreatedOrders] = useState([]);
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => _jsx(PurchaseStatus, { status: row.original.status }) },
        { id: "requester", accessorKey: "requesterName", header: t("purchasing.requester"), size: 160, cell: ({ row }) => row.original.requesterName ?? "" },
        { id: "neededBy", accessorKey: "neededBy", header: t("purchasing.neededBy"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.neededBy) }) },
        { id: "lines", accessorFn: (row) => row.lines.length, header: t("purchasing.lines"), size: 80, cell: ({ row }) => String(row.original.lines.length) },
        { id: "total", accessorKey: "totalEstimated", header: t("purchasing.estimatedTotal"), size: 150, cell: ({ row }) => _jsx("span", { className: "tabular", dir: "ltr", children: formatMoney(row.original.totalEstimated, row.original.currency) }) },
        { id: "updated", accessorKey: "updatedAt", header: t("common.updated"), size: 160, cell: ({ row }) => _jsx("span", { dir: "ltr", children: formatDate(row.original.updatedAt) }) },
    ], [t]);
    const openNew = () => { setProblem(null); setForm({ neededBy: "", justification: "", lines: [emptyLine()] }); };
    const submit = (event) => { event.preventDefault(); if (form) {
        create.mutate(form);
    } };
    const r = detail.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.requisitions"), description: t("purchasing.requisitionsDescription"), actions: _jsxs(Button, { onClick: openNew, disabled: !companyId, "data-testid": "new-requisition", children: [_jsx(Plus, { "aria-hidden": "true" }), t("purchasing.newRequisition")] }) }), _jsxs("div", { className: "mb-3 flex flex-wrap items-end gap-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: [_jsx("option", { value: "", children: t("common.all") }), ["draft", "pending_approval", "approved", "ordered", "rejected", "cancelled"].map((s) => (_jsx("option", { value: s, children: t(`purchasing.statuses.${s}`) }, s)))] }) })] }), _jsx(DataGrid, { label: "nav.requisitions", columns: columns, data: list.data ?? [], rowKey: (row) => row.id, loading: list.isPending && Boolean(companyId), emptyTitle: t("purchasing.emptyRequisitions"), emptyDescription: t("purchasing.emptyRequisitionsDescription"), onOpen: (row) => { setProblem(null); setCreatedOrders([]); setOpenId(row.id); } }), _jsx(Dialog, { open: Boolean(form), onOpenChange: (isOpen) => { if (!isOpen) {
                    setForm(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: form ? (_jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("purchasing.newRequisition") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("purchasing.neededBy"), children: _jsx(TextField, { type: "date", value: form.neededBy, onChange: (e) => { setForm({ ...form, neededBy: e.target.value }); }, dir: "ltr", "data-testid": "requisition-needed-by" }) }), _jsx(Field, { label: t("purchasing.justification"), children: _jsx(TextareaField, { value: form.justification, onChange: (e) => { setForm({ ...form, justification: e.target.value }); }, rows: 2, "data-testid": "requisition-justification" }) })] }), _jsx(LinesEditor, { lines: form.lines, onChange: (lines) => { setForm({ ...form, lines }); }, priceLabel: t("purchasing.estimatedPrice"), suppliers: suppliers.data ?? [], showDescription: true }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setForm(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, "data-testid": "save-requisition", children: t("common.save") })] })] })) : null }) }), _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    setOpenId(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: r ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "requisition-detail", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "flex items-center gap-3 text-lg font-semibold", children: [_jsx("span", { dir: "ltr", children: r.number }), _jsx(PurchaseStatus, { status: r.status })] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(KeyValues, { entries: [
                                    [t("purchasing.requester"), r.requesterName ?? "—"],
                                    [t("purchasing.neededBy"), formatDate(r.neededBy) || "—"],
                                    [t("purchasing.justification"), r.justification ?? "—"],
                                    [t("purchasing.estimatedTotal"), formatMoney(r.totalEstimated, r.currency)],
                                    ...(r.rejectionReason ? [[t("purchasing.rejectionReason"), r.rejectionReason]] : []),
                                ] }), _jsx(LinesTable, { lines: r.lines.map((l) => ({ id: l.id, lineNo: l.lineNo, itemCode: l.itemCode, description: l.description, quantity: l.quantity, uomCode: l.uomCode, unitPrice: l.estimatedPrice, status: l.status })), currency: r.currency, testId: "requisition-lines" }), createdOrders.length > 0 ? _jsx("p", { className: "text-sm", "data-testid": "created-orders", children: t("purchasing.ordersCreated", { numbers: createdOrders.join(", ") }) }) : null, _jsxs(DialogFooter, { children: [r.status === "draft" || r.status === "rejected" ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "submit" }); }, loading: act.isPending, "data-testid": "submit-requisition", children: t("purchasing.submit") }) : null, r.status === "approved" ? _jsx(Button, { onClick: () => { act.mutate({ id: r.id, action: "orders" }); }, loading: act.isPending, "data-testid": "create-orders", children: t("purchasing.createOrders") }) : null, r.status === "draft" || r.status === "pending_approval" || r.status === "approved" || r.status === "rejected" ? _jsx(Button, { variant: "secondary", onClick: () => { act.mutate({ id: r.id, action: "cancel" }); }, loading: act.isPending, "data-testid": "cancel-requisition", children: t("purchasing.cancelDocument") }) : null] })] })) : null }) })] }));
}
