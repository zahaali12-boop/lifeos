import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { today } from "../accounting/shared";
import { DocStatus, KeyValues, Qty, Tabs, findItemByCode } from "./shared";
/** An active lot past its expiry date is expired already (stock rules block it by date); the daily job only records it. */
function lotStatus(lot) {
    return lot.status === "active" && lot.expiresOn && lot.expiresOn < today() ? "expired" : lot.status;
}
const lotStatuses = ["active", "quarantine", "recalled", "expired", "consumed"];
const serialStatuses = ["in_stock", "in_transit", "sold", "returned", "in_repair", "scrapped", "consumed", "consigned"];
/** Lots and serials (roadmap 3.5): where each lot is, its expiry and status, its trace forward and back; every serial's timeline. */
export function TrackingPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const [tab, setTab] = useState(search.tab === "serials" ? "serials" : "lots");
    const [itemCode, setItemCode] = useState("");
    const [status, setStatus] = useState("");
    const [query, setQuery] = useState("");
    const [nextStatus, setNextStatus] = useState("");
    const [reason, setReason] = useState("");
    const [problem, setProblem] = useState(null);
    const openLot = search.lot;
    const openSerial = search.serial;
    const item = useQuery({ queryKey: ["item-by-code", itemCode], enabled: itemCode.trim().length > 0, queryFn: () => findItemByCode(itemCode) });
    const itemId = item.data?.id;
    const lots = useQuery({
        queryKey: ["lots", itemId ?? "", status, query],
        enabled: tab === "lots",
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/lots", { params: { query: { ...(itemId ? { itemId } : {}), ...(status ? { status } : {}), ...(query.trim() ? { q: query.trim() } : {}) } } })),
    });
    const serials = useQuery({
        queryKey: ["serials", itemId ?? "", status, query],
        enabled: tab === "serials",
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials", { params: { query: { ...(itemId ? { itemId } : {}), ...(status ? { status } : {}), ...(query.trim() ? { q: query.trim() } : {}) } } })),
    });
    const trace = useQuery({
        queryKey: ["lot-trace", openLot],
        enabled: Boolean(openLot),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/lots/{lotId}/trace", { params: { path: { lotId: openLot ?? "" } } })),
    });
    const history = useQuery({
        queryKey: ["serial-history", openSerial],
        enabled: Boolean(openSerial),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials/{serialId}/history", { params: { path: { serialId: openSerial ?? "" } } })),
    });
    const go = (patch) => { void navigate({ to: "/inventory/tracking", search: { tab, ...patch } }); };
    const changeLot = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/lots/{lotId}/status", { params: { path: { lotId: openLot ?? "" } }, body: { status: nextStatus, reason: reason || null, recallReference: nextStatus === "recalled" ? reason || null : null } })),
        onSuccess: async () => {
            setProblem(null);
            setReason("");
            await queryClient.invalidateQueries({ queryKey: ["lots"] });
            await queryClient.invalidateQueries({ queryKey: ["lot-trace", openLot] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const changeSerial = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/serials/{serialId}/status", { params: { path: { serialId: openSerial ?? "" } }, body: { status: nextStatus, note: reason || null } })),
        onSuccess: async () => {
            setProblem(null);
            setReason("");
            await queryClient.invalidateQueries({ queryKey: ["serials"] });
            await queryClient.invalidateQueries({ queryKey: ["serial-history", openSerial] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const lotColumns = useMemo(() => [
        { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.lotNumber }) },
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "mfg", accessorKey: "manufacturedOn", header: t("inventory.tracking.manufactured"), size: 120, cell: ({ row }) => formatDate(row.original.manufacturedOn) },
        { id: "exp", accessorKey: "expiresOn", header: t("inventory.expiresOn"), size: 120, cell: ({ row }) => formatDate(row.original.expiresOn) },
        { id: "supplierLot", accessorKey: "supplierLot", header: t("inventory.tracking.supplierLot"), size: 130 },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(DocStatus, { status: lotStatus(row.original) }) },
    ], [t]);
    const serialColumns = useMemo(() => [
        { id: "serial", accessorKey: "serialNumber", header: t("inventory.stock.serial"), size: 160, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.serialNumber }) },
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 120 },
        { id: "warehouse", accessorKey: "currentWarehouseCode", header: t("inventory.warehouse"), size: 120 },
        { id: "warranty", accessorKey: "warrantyUntil", header: t("inventory.tracking.warranty"), size: 120, cell: ({ row }) => formatDate(row.original.warrantyUntil) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(DocStatus, { status: row.original.status }) },
    ], [t]);
    const lotDetail = trace.data;
    const serialDetail = history.data;
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.tracking"), description: t("inventory.tracking.description") }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("inventory.itemCode"), description: item.isSuccess && item.data === null && itemCode.trim() ? t("inventory.itemUnknown") : undefined, children: _jsx(TextField, { value: itemCode, onChange: (e) => { setItemCode(e.target.value.toUpperCase()); }, dir: "ltr", "data-testid": "tracking-item" }) }), _jsx(Field, { label: t("common.status"), children: _jsxs(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, children: [_jsx("option", { value: "", children: t("accounting.anyStatus") }), (tab === "lots" ? lotStatuses : serialStatuses).map((s) => (_jsx("option", { value: s, children: t(`inventory.statuses.${s}`) }, s)))] }) }), _jsx(Field, { label: t("common.search"), children: _jsx(Input, { type: "search", value: query, onChange: (e) => { setQuery(e.target.value); }, "aria-label": t("common.search"), "data-testid": "tracking-search" }) })] }), _jsx(Tabs, { value: tab, onChange: (id) => { setTab(id); setStatus(""); }, tabs: [{ id: "lots", label: t("inventory.tracking.lots"), testId: "tab-lots" }, { id: "serials", label: t("inventory.tracking.serials"), testId: "tab-serials" }] }), tab === "lots" ? _jsx(DataGrid, { label: "inventory.tracking.lots", columns: lotColumns, data: lots.data ?? [], rowKey: (row) => row.id, loading: lots.isPending, onOpen: (row) => { go({ lot: row.id }); }, emptyTitle: t("inventory.tracking.noLots"), emptyDescription: t("inventory.tracking.noLotsHint") }) : null, tab === "serials" ? _jsx(DataGrid, { label: "inventory.tracking.serials", columns: serialColumns, data: serials.data ?? [], rowKey: (row) => row.id, loading: serials.isPending, onOpen: (row) => { go({ serial: row.id }); }, emptyTitle: t("inventory.tracking.noSerials"), emptyDescription: t("inventory.tracking.noSerialsHint") }) : null, _jsx(Dialog, { open: Boolean(openLot), onOpenChange: (isOpen) => { if (!isOpen) {
                    go({});
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: lotDetail ? `${lotDetail.lot.itemCode} · ${lotDetail.lot.lotNumber}` : t("common.loading") }) }), lotDetail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "lot-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: lotStatus(lotDetail.lot) }), lotDetail.lot.expiresOn ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.expiresOn"), ": ", formatDate(lotDetail.lot.expiresOn)] }) : null, lotDetail.lot.statusReason ? _jsx("span", { className: "text-fg-muted", children: lotDetail.lot.statusReason }) : null, lotDetail.lot.recallReference ? _jsxs("span", { className: "text-danger", children: [t("inventory.tracking.recall"), ": ", lotDetail.lot.recallReference] }) : null] }), _jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.stock.onHand") }), _jsx(KeyValues, { entries: lotDetail.onHand.map((b) => [b.warehouseCode, _jsx(Qty, { value: b.onHand }, b.warehouseId)]) })] }), _jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.tracking.movements") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.date") }), _jsx(TableHead, { children: t("inventory.stock.entryType") }), _jsx(TableHead, { children: t("inventory.warehouse") }), _jsx(TableHead, { className: "text-end", children: t("inventory.quantity") }), _jsx(TableHead, { children: t("inventory.stock.source") })] }) }), _jsx(TableBody, { children: [...lotDetail.inbound, ...lotDetail.outbound].map((m) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: formatDate(m.postingDate) }), _jsx(TableCell, { children: t(`inventory.entryTypes.${m.entryType}`, { defaultValue: m.entryType }) }), _jsx(TableCell, { children: m.warehouseCode }), _jsx(TableNumberCell, { children: _jsx(Qty, { value: m.quantity }) }), _jsxs(TableCell, { children: [m.sourceDocumentType, m.serialNumber ? ` · ${m.serialNumber}` : ""] })] }, m.sleId))) })] })] }), lotDetail.shippedTo.length > 0 ? (_jsxs("section", { children: [_jsx("h3", { className: "mb-1 text-sm font-semibold", children: t("inventory.tracking.shippedTo") }), _jsx(KeyValues, { entries: lotDetail.shippedTo.map((p) => [p.partnerId, `${String(p.quantity)} · ${formatDate(p.lastShippedOn)}`]) })] })) : null, _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-3 sm:grid-cols-3", children: [_jsx(Field, { label: t("inventory.tracking.newStatus"), children: _jsxs(SelectField, { value: nextStatus, onChange: (e) => { setNextStatus(e.target.value); }, "data-testid": "lot-status", children: [_jsx("option", { value: "", children: "\u2014" }), lotStatuses.map((s) => (_jsx("option", { value: s, children: t(`inventory.statuses.${s}`) }, s)))] }) }), _jsx(Field, { label: t("common.reason"), children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); }, "data-testid": "lot-reason" }) }), _jsx("div", { className: "self-end", children: _jsx(Button, { onClick: () => { changeLot.mutate(); }, disabled: !nextStatus, loading: changeLot.isPending, "data-testid": "lot-apply", children: t("inventory.tracking.apply") }) })] })] })) : null] }) }), _jsx(Dialog, { open: Boolean(openSerial), onOpenChange: (isOpen) => { if (!isOpen) {
                    go({});
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: serialDetail ? `${serialDetail.serial.itemCode} · ${serialDetail.serial.serialNumber}` : t("common.loading") }) }), serialDetail ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "serial-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(DocStatus, { status: serialDetail.serial.status }), serialDetail.serial.currentWarehouseCode ? _jsx("span", { className: "text-fg-muted", children: serialDetail.serial.currentWarehouseCode }) : null, serialDetail.serial.lotNumber ? _jsxs("span", { className: "text-fg-muted", children: [t("inventory.stock.lot"), ": ", serialDetail.serial.lotNumber] }) : null] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("inventory.tracking.when") }), _jsx(TableHead, { children: t("inventory.tracking.event") }), _jsx(TableHead, { children: t("common.status") }), _jsx(TableHead, { children: t("inventory.warehouse") }), _jsx(TableHead, { children: t("inventory.stock.source") })] }) }), _jsx(TableBody, { children: serialDetail.events.map((e) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: formatDateTime(e.at) }), _jsx(TableCell, { children: e.entryType ? t(`inventory.entryTypes.${e.entryType}`, { defaultValue: e.entryType }) : t(`inventory.tracking.kinds.${e.kind}`, { defaultValue: e.kind }) }), _jsx(TableCell, { children: t(`inventory.statuses.${e.toStatus}`, { defaultValue: e.toStatus }) }), _jsx(TableCell, { children: e.warehouseCode ?? "" }), _jsx(TableCell, { children: e.sourceDocumentType ?? e.note ?? "" })] }, e.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-3 sm:grid-cols-3", children: [_jsx(Field, { label: t("inventory.tracking.newStatus"), children: _jsxs(SelectField, { value: nextStatus, onChange: (e) => { setNextStatus(e.target.value); }, children: [_jsx("option", { value: "", children: "\u2014" }), serialStatuses.map((s) => (_jsx("option", { value: s, children: t(`inventory.statuses.${s}`) }, s)))] }) }), _jsx(Field, { label: t("inventory.tracking.note"), children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); } }) }), _jsx("div", { className: "self-end", children: _jsx(Button, { onClick: () => { changeSerial.mutate(); }, disabled: !nextStatus, loading: changeSerial.isPending, children: t("inventory.tracking.apply") }) })] }), _jsx(DialogFooter, {})] })) : null] }) })] }));
}
