import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";
const statuses = ["", "draft", "shipped", "partially_received", "received", "cancelled"];
const emptyLine = { itemCode: "", quantity: "", uom: "", fromBinId: "", toBinId: "", lotNumber: "", serialNumbers: "" };
function toRequest(companyId, f) {
    return {
        companyId,
        fromWarehouseId: f.fromWarehouseId,
        toWarehouseId: f.toWarehouseId,
        transitWarehouseId: f.kind === "two_step" && f.transitWarehouseId ? f.transitWarehouseId : null,
        kind: f.kind,
        reference: f.reference || null,
        lines: f.lines
            .filter((l) => l.itemCode.trim())
            .map((l) => ({ itemCode: l.itemCode.trim(), quantity: l.quantity || "0", uom: l.uom || null, fromBinId: l.fromBinId || null, toBinId: l.toBinId || null, lotNumber: l.lotNumber || null, serialNumbers: l.serialNumbers.trim() ? l.serialNumbers.split(/[\s,;]+/).filter(Boolean) : null })),
    };
}
/** Transfers (roadmap 3.4): one-step moves and two-step moves through an in-transit warehouse, shipped and received with shortages accounted for. */
export function TransfersPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [status, setStatus] = useState("");
    const [editing, setEditing] = useState(null);
    const [date, setDate] = useState(today());
    const [problem, setProblem] = useState(null);
    const openId = search.open;
    const fromWarehouse = warehouses.data?.find((w) => w.id === editing?.fromWarehouseId);
    const toWarehouse = warehouses.data?.find((w) => w.id === editing?.toWarehouseId);
    const fromBins = useQuery({
        queryKey: ["bins", fromWarehouse?.id ?? ""],
        enabled: fromWarehouse?.binsEnabled === true,
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: fromWarehouse?.id ?? "" } } })),
    });
    const toBins = useQuery({
        queryKey: ["bins", toWarehouse?.id ?? ""],
        enabled: toWarehouse?.binsEnabled === true,
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: toWarehouse?.id ?? "" } } })),
    });
    const transfers = useQuery({
        queryKey: ["transfers", companyId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/transfers", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
    });
    const transfer = useQuery({
        queryKey: ["transfer", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/transfers/{transferId}", { params: { path: { transferId: openId ?? "" } } })),
    });
    const refresh = async (id) => {
        await queryClient.invalidateQueries({ queryKey: ["transfers"] });
        await queryClient.invalidateQueries({ queryKey: ["stock"] });
        if (id) {
            await queryClient.invalidateQueries({ queryKey: ["transfer", id] });
        }
    };
    const open = (id) => { void navigate({ to: "/inventory/transfers", search: id ? { open: id } : {} }); };
    const save = useMutation({
        mutationFn: async (form) => unwrap(await api.POST("/api/v1/inventory/transfers", { body: toRequest(companyId, form) })),
        onSuccess: async (saved) => {
            setEditing(null);
            setProblem(null);
            await refresh(saved.id);
            open(saved.id);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const act = useMutation({
        mutationFn: async (action) => {
            const transferId = openId ?? "";
            switch (action) {
                case "ship":
                    return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/ship", { params: { path: { transferId } }, body: { shipDate: date || null } }));
                case "receive":
                    return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/receive", { params: { path: { transferId } }, body: { receiveDate: date || null } }));
                case "cancel":
                    return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/cancel", { params: { path: { transferId } } }));
            }
        },
        onSuccess: async () => {
            setProblem(null);
            await refresh(openId);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.number }) },
        { id: "from", accessorKey: "fromWarehouseCode", header: t("inventory.transfers.from"), size: 120 },
        { id: "to", accessorKey: "toWarehouseCode", header: t("inventory.transfers.to"), size: 120 },
        { id: "kind", accessorKey: "kind", header: t("inventory.transfers.kind"), size: 110, cell: ({ row }) => t(`inventory.transfers.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
        { id: "lines", accessorFn: (row) => row.lines.length, header: t("inventory.lines"), size: 80 },
        { id: "shipDate", accessorKey: "shipDate", header: t("inventory.transfers.shipped"), size: 110, cell: ({ row }) => formatDate(row.original.shipDate) },
        { id: "receiveDate", accessorKey: "receiveDate", header: t("inventory.transfers.received"), size: 110, cell: ({ row }) => formatDate(row.original.receiveDate) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 150, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    const detail = transfer.data;
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
    const updateLine = (index, patch) => {
        if (editing) {
            setForm({ lines: editing.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
        }
    };
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const transits = (warehouses.data ?? []).filter((w) => w.kind === "in_transit");
    const stockWarehouses = (warehouses.data ?? []).filter((w) => w.kind !== "in_transit");
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.transfers"), description: t("inventory.transfers.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setEditing({ fromWarehouseId: stockWarehouses[0]?.id ?? "", toWarehouseId: stockWarehouses[1]?.id ?? "", transitWarehouseId: transits[0]?.id ?? "", kind: transits.length > 0 ? "two_step" : "one_step", reference: "", lines: [{ ...emptyLine }] }); }, disabled: !companyId, "data-testid": "new-transfer", children: [_jsx(Plus, { "aria-hidden": "true" }), t("inventory.transfers.new")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: statuses.map((s) => (_jsx("option", { value: s, children: s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) })] }), _jsx(DataGrid, { label: "nav.transfers", columns: columns, data: transfers.data ?? [], rowKey: (row) => row.id, loading: transfers.isPending && Boolean(companyId), emptyTitle: t("inventory.transfers.emptyTitle"), emptyDescription: t("inventory.transfers.emptyDescription"), onOpen: (row) => { open(row.id); } }), _jsx(Dialog, { open: Boolean(openId) && !editing, onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: detail ? `${detail.number} · ${detail.fromWarehouseCode} → ${detail.toWarehouseCode}` : t("common.loading") }) }), detail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "transfer-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: detail.status }), _jsx("span", { children: t(`inventory.transfers.kinds.${detail.kind}`, { defaultValue: detail.kind }) }), detail.transitWarehouseCode ? _jsx("span", { className: "text-fg-muted", children: t("inventory.transfers.via", { warehouse: detail.transitWarehouseCode }) }) : null, detail.shipDate ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.transfers.shipped"), ": ", formatDate(detail.shipDate)] }) : null, detail.receiveDate ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.transfers.received"), ": ", formatDate(detail.receiveDate)] }) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("inventory.item") }), _jsx(TableHead, { className: "text-end", children: t("inventory.transfers.requested") }), _jsx(TableHead, { className: "text-end", children: t("inventory.transfers.shipped") }), _jsx(TableHead, { className: "text-end", children: t("inventory.transfers.received") }), _jsx(TableHead, { className: "text-end", children: t("inventory.transfers.shortage") })] }) }), _jsx(TableBody, { children: detail.lines.map((line) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: String(line.lineNo) }), _jsxs(TableCell, { children: [_jsx("span", { dir: "ltr", children: line.itemCode }), " ", localized(line.itemName), line.lotNumber ? _jsxs("span", { className: "text-fg-muted", children: [" \u00B7 ", line.lotNumber] }) : null] }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.qtyRequested, uom: line.uomCode }) }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.qtyShipped }) }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.qtyReceived }) }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: line.qtyShortage }) })] }, line.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), detail.status === "draft" || detail.status === "shipped" || detail.status === "partially_received" ? (_jsx(Field, { label: t("accounting.date"), children: _jsx(TextField, { type: "date", value: date, onChange: (e) => { setDate(e.target.value); }, dir: "ltr", "data-testid": "transfer-date" }) })) : null, _jsxs(DialogFooter, { children: [detail.status === "draft" ? (_jsx(Button, { onClick: () => { act.mutate("ship"); }, loading: act.isPending, "data-testid": "ship-transfer", children: t("inventory.transfers.ship") })) : null, detail.status === "shipped" || detail.status === "partially_received" ? (_jsx(Button, { onClick: () => { act.mutate("receive"); }, loading: act.isPending, "data-testid": "receive-transfer", children: t("inventory.transfers.receive") })) : null, detail.status !== "received" && detail.status !== "cancelled" ? (_jsx(Button, { variant: "secondary", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, children: t("common.cancel") })) : null] })] })) : null] }) }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("inventory.transfers.new") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-4", children: [_jsx(WarehouseSelect, { warehouses: stockWarehouses, value: editing.fromWarehouseId, onChange: (id) => { setForm({ fromWarehouseId: id }); }, label: t("inventory.transfers.from"), required: true, testId: "transfer-from" }), _jsx(WarehouseSelect, { warehouses: stockWarehouses, value: editing.toWarehouseId, onChange: (id) => { setForm({ toWarehouseId: id }); }, label: t("inventory.transfers.to"), required: true, testId: "transfer-to" }), _jsx(Field, { label: t("inventory.transfers.kind"), children: _jsxs(SelectField, { value: editing.kind, onChange: (e) => { setForm({ kind: e.target.value }); }, "data-testid": "transfer-kind", children: [_jsx("option", { value: "one_step", children: t("inventory.transfers.kinds.one_step") }), _jsx("option", { value: "two_step", children: t("inventory.transfers.kinds.two_step") })] }) }), editing.kind === "two_step" ? _jsx(WarehouseSelect, { warehouses: transits, value: editing.transitWarehouseId, onChange: (id) => { setForm({ transitWarehouseId: id }); }, label: t("inventory.transfers.transit"), testId: "transfer-transit" }) : (_jsx(Field, { label: t("accounting.reference"), children: _jsx(TextField, { value: editing.reference, onChange: (e) => { setForm({ reference: e.target.value }); } }) }))] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.itemCode") }), _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { children: t("inventory.uom") }), fromWarehouse?.binsEnabled ? _jsx(TableHead, { children: t("inventory.transfers.fromBin") }) : null, toWarehouse?.binsEnabled ? _jsx(TableHead, { children: t("inventory.transfers.toBin") }) : null, _jsx(TableHead, { children: t("inventory.stock.lot") }), _jsx(TableHead, { children: t("inventory.serials") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: editing.lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.itemCode"), value: line.itemCode, onChange: (e) => { updateLine(index, { itemCode: e.target.value.toUpperCase() }); }, dir: "ltr", "data-testid": `line-item-${index}` }) }), _jsx(TableNumberCell, { children: _jsx(TextField, { "aria-label": t("inventory.quantity"), inputMode: "decimal", value: line.quantity, onChange: (e) => { updateLine(index, { quantity: e.target.value }); }, dir: "ltr", className: "text-end", "data-testid": `line-qty-${index}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.uom"), value: line.uom, onChange: (e) => { updateLine(index, { uom: e.target.value.toUpperCase() }); }, dir: "ltr", className: "w-20" }) }), fromWarehouse?.binsEnabled ? (_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("inventory.transfers.fromBin"), value: line.fromBinId, onChange: (e) => { updateLine(index, { fromBinId: e.target.value }); }, "data-testid": `line-from-bin-${index}`, children: [_jsx("option", { value: "", children: "\u2014" }), (fromBins.data ?? []).map((b) => (_jsx("option", { value: b.id, children: b.code }, b.id)))] }) })) : null, toWarehouse?.binsEnabled ? (_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("inventory.transfers.toBin"), value: line.toBinId, onChange: (e) => { updateLine(index, { toBinId: e.target.value }); }, "data-testid": `line-to-bin-${index}`, children: [_jsx("option", { value: "", children: "\u2014" }), (toBins.data ?? []).map((b) => (_jsx("option", { value: b.id, children: b.code }, b.id)))] }) })) : null, _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.stock.lot"), value: line.lotNumber, onChange: (e) => { updateLine(index, { lotNumber: e.target.value }); }, dir: "ltr" }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("inventory.serials"), value: line.serialNumbers, onChange: (e) => { updateLine(index, { serialNumbers: e.target.value }); }, dir: "ltr" }) }), _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "icon", "aria-label": t("accounting.removeLine"), onClick: () => { setForm({ lines: editing.lines.filter((_, i) => i !== index) }); }, children: _jsx(Trash2, { "aria-hidden": "true" }) }) })] }, index))) })] }), _jsx("div", { children: _jsxs(Button, { type: "button", variant: "secondary", onClick: () => { setForm({ lines: [...editing.lines, { ...emptyLine }] }); }, "data-testid": "add-line", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.addLine")] }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-transfer", children: t("common.save") })] })] })) : null }) })] }));
}
