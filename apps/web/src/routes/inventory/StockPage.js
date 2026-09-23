import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Input } from "@quicker/ui";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { Field, PageHeader, TextField } from "../common";
import { CompanyFilter, Qty, Tabs, WarehouseSelect, findItemByCode, useCompanyContext, useWarehouses } from "./shared";
/** Stock (roadmap 3.2): what is on hand, reserved and available by item and warehouse; the balances by bin, lot and serial; the ledger of every movement. */
export function StockPage() {
    const { t } = useTranslation();
    const { companies, companyId, setCompanyId } = useCompanyContext();
    const warehouses = useWarehouses(companyId);
    const [tab, setTab] = useState("stock");
    const [warehouseId, setWarehouseId] = useState("");
    const [query, setQuery] = useState("");
    const [itemCode, setItemCode] = useState("");
    const [onlyAvailable, setOnlyAvailable] = useState(false);
    const [from, setFrom] = useState("");
    const [to, setTo] = useState("");
    const item = useQuery({ queryKey: ["item-by-code", itemCode], enabled: itemCode.trim().length > 0, queryFn: () => findItemByCode(itemCode) });
    const itemId = item.data?.id;
    const stock = useInfiniteQuery({
        queryKey: ["stock", companyId, warehouseId, query, onlyAvailable],
        enabled: Boolean(companyId) && tab === "stock",
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/inventory/stock", { params: { query: { companyId, limit: 200, onlyAvailable, ...(warehouseId ? { warehouseId } : {}), ...(query.trim() ? { q: query.trim() } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
        initialPageParam: "",
        getNextPageParam: (last) => last.nextCursor ?? undefined,
    });
    const balances = useQuery({
        queryKey: ["balances", companyId, warehouseId, itemId ?? ""],
        enabled: Boolean(companyId) && tab === "balances",
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/balances", { params: { query: { companyId, ...(warehouseId ? { warehouseId } : {}), ...(itemId ? { itemId } : {}) } } })),
    });
    const ledger = useInfiniteQuery({
        queryKey: ["ledger", companyId, warehouseId, itemId ?? "", from, to],
        enabled: Boolean(companyId) && tab === "ledger",
        queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/inventory/stock/ledger", { params: { query: { companyId, limit: 200, ...(warehouseId ? { warehouseId } : {}), ...(itemId ? { itemId } : {}), ...(from ? { from } : {}), ...(to ? { to } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
        initialPageParam: "",
        getNextPageParam: (last) => last.nextCursor ?? undefined,
    });
    const stockColumns = useMemo(() => [
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 140, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 240 },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
        { id: "onHand", accessorKey: "onHand", header: t("inventory.stock.onHand"), size: 110, cell: ({ row }) => _jsx(Qty, { value: row.original.onHand, uom: row.original.baseUom }) },
        { id: "reserved", accessorKey: "reserved", header: t("inventory.stock.reserved"), size: 100, cell: ({ row }) => _jsx(Qty, { value: row.original.reserved }) },
        { id: "hold", accessorKey: "qualityHold", header: t("inventory.stock.qualityHold"), size: 100, cell: ({ row }) => _jsx(Qty, { value: row.original.qualityHold }) },
        { id: "available", accessorKey: "available", header: t("inventory.stock.available"), size: 110, cell: ({ row }) => _jsx(Qty, { value: row.original.available }) },
        { id: "last", accessorKey: "lastMovementAt", header: t("inventory.stock.lastMovement"), size: 160, cell: ({ row }) => formatDateTime(row.original.lastMovementAt) },
    ], [t]);
    const balanceColumns = useMemo(() => [
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "variant", accessorKey: "variantSku", header: t("inventory.stock.variant"), size: 120 },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
        { id: "bin", accessorKey: "binCode", header: t("inventory.warehouses.bin"), size: 90 },
        { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 120, cell: ({ row }) => (row.original.lotNumber ? `${row.original.lotNumber}${row.original.lotExpiresOn ? ` · ${formatDate(row.original.lotExpiresOn)}` : ""}` : "") },
        { id: "serial", accessorKey: "serialNumber", header: t("inventory.stock.serial"), size: 140 },
        { id: "onHand", accessorKey: "onHand", header: t("inventory.stock.onHand"), size: 110, cell: ({ row }) => _jsx(Qty, { value: row.original.onHand, uom: row.original.baseUom }) },
        { id: "reserved", accessorKey: "reserved", header: t("inventory.stock.reserved"), size: 100, cell: ({ row }) => _jsx(Qty, { value: row.original.reserved }) },
        { id: "available", accessorKey: "available", header: t("inventory.stock.available"), size: 110, cell: ({ row }) => _jsx(Qty, { value: row.original.available }) },
    ], [t]);
    const ledgerColumns = useMemo(() => [
        { id: "date", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
        { id: "seq", accessorKey: "sequence", header: "#", size: 80, cell: ({ row }) => String(row.original.sequence) },
        { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.itemCode }) },
        { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
        { id: "type", accessorKey: "entryType", header: t("inventory.stock.entryType"), size: 150, cell: ({ row }) => t(`inventory.entryTypes.${row.original.entryType}`, { defaultValue: row.original.entryType }) },
        { id: "quantity", accessorKey: "quantity", header: t("inventory.quantity"), size: 120, cell: ({ row }) => _jsx(Qty, { value: row.original.quantity, uom: row.original.baseUom }) },
        { id: "entered", accessorKey: "enteredQuantity", header: t("inventory.stock.entered"), size: 120, cell: ({ row }) => _jsx(Qty, { value: row.original.enteredQuantity, uom: row.original.enteredUom }) },
        { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 110 },
        { id: "serial", accessorKey: "serialNumber", header: t("inventory.stock.serial"), size: 130 },
        { id: "source", accessorKey: "sourceDocumentType", header: t("inventory.stock.source"), size: 150 },
    ], [t]);
    const stockRows = stock.data?.pages.flatMap((p) => p.items) ?? [];
    const ledgerRows = ledger.data?.pages.flatMap((p) => p.items) ?? [];
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.stock"), description: t("inventory.stock.description") }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-4", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(WarehouseSelect, { warehouses: warehouses.data ?? [], value: warehouseId, onChange: setWarehouseId, allowAll: true }), tab === "stock" ? (_jsx(Field, { label: t("common.search"), children: _jsx(Input, { type: "search", value: query, onChange: (e) => { setQuery(e.target.value); }, placeholder: t("inventory.stock.searchPlaceholder"), "aria-label": t("common.search"), "data-testid": "stock-search" }) })) : (_jsx(Field, { label: t("inventory.itemCode"), description: item.isSuccess && item.data === null && itemCode.trim() ? t("inventory.itemUnknown") : undefined, children: _jsx(TextField, { value: itemCode, onChange: (e) => { setItemCode(e.target.value.toUpperCase()); }, dir: "ltr", "data-testid": "stock-item-code" }) })), tab === "stock" ? (_jsxs("label", { className: "flex items-center gap-2 self-end pb-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: onlyAvailable, onChange: (e) => { setOnlyAvailable(e.target.checked); } }), t("inventory.stock.onlyAvailable")] })) : null, tab === "ledger" ? (_jsxs(_Fragment, { children: [_jsx(Field, { label: t("accounting.from"), children: _jsx(TextField, { type: "date", value: from, onChange: (e) => { setFrom(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.to"), children: _jsx(TextField, { type: "date", value: to, onChange: (e) => { setTo(e.target.value); }, dir: "ltr" }) })] })) : null] }), _jsx(Tabs, { value: tab, onChange: setTab, tabs: [
                    { id: "stock", label: t("inventory.stock.byItem"), testId: "tab-stock" },
                    { id: "balances", label: t("inventory.stock.balances"), testId: "tab-balances" },
                    { id: "ledger", label: t("inventory.stock.ledger"), testId: "tab-ledger" },
                ] }), tab === "stock" ? (_jsxs(_Fragment, { children: [_jsx(DataGrid, { label: "nav.stock", columns: stockColumns, data: stockRows, rowKey: (row) => `${row.itemId}:${row.warehouseId}`, loading: stock.isPending && Boolean(companyId), emptyTitle: t("inventory.stock.emptyTitle"), emptyDescription: t("inventory.stock.emptyDescription") }), stock.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void stock.fetchNextPage(); }, loading: stock.isFetchingNextPage, children: t("common.loadMore") })) : null] })) : null, tab === "balances" ? _jsx(DataGrid, { label: "inventory.stock.balances", columns: balanceColumns, data: balances.data ?? [], rowKey: (row) => `${row.itemId}:${row.variantId ?? ""}:${row.warehouseId}:${row.binId ?? ""}:${row.lotId ?? ""}:${row.serialId ?? ""}`, loading: balances.isPending && Boolean(companyId), emptyTitle: t("inventory.stock.emptyTitle"), emptyDescription: t("inventory.stock.emptyDescription") }) : null, tab === "ledger" ? (_jsxs(_Fragment, { children: [_jsx(DataGrid, { label: "inventory.stock.ledger", columns: ledgerColumns, data: ledgerRows, rowKey: (row) => row.id, loading: ledger.isPending && Boolean(companyId), emptyTitle: t("inventory.stock.noMovements"), emptyDescription: t("inventory.stock.noMovementsHint") }), ledger.hasNextPage ? (_jsx(Button, { variant: "secondary", className: "mt-3", onClick: () => { void ledger.fetchNextPage(); }, loading: ledger.isFetchingNextPage, children: t("common.loadMore") })) : null] })) : null] }));
}
