import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Checkbox, DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger, EmptyState, Spinner, cn } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable } from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp, Bookmark, Columns3 } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
const ROW_HEIGHT = 40;
/**
 * Data grid v1 (ADR-0013): TanStack Table for state, TanStack Virtual for rows, column chooser, sortable headers,
 * multi-select with bulk actions, saved views (private or shared) and j/k/Enter keyboard navigation.
 */
export function DataGrid({ columns, data, rowKey, entityType, onOpen, selectable = false, bulkActions, toolbar, loading = false, emptyTitle, emptyDescription, emptyAction, height = 560, label }) {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [sorting, setSorting] = useState([]);
    const [visibility, setVisibility] = useState({});
    const [selected, setSelected] = useState(new Set());
    const [activeIndex, setActiveIndex] = useState(0);
    const containerRef = useRef(null);
    const allColumns = useMemo(() => {
        if (!selectable) {
            return columns;
        }
        const selectColumn = {
            id: "__select",
            enableSorting: false,
            enableHiding: false,
            size: 40,
            header: () => (_jsx(Checkbox, { "aria-label": t("grid.selectAll"), checked: selected.size === 0 ? false : selected.size === data.length ? true : "indeterminate", onCheckedChange: (checked) => { setSelected(checked === true ? new Set(data.map(rowKey)) : new Set()); } })),
            cell: ({ row }) => {
                const key = rowKey(row.original);
                return (_jsx(Checkbox, { "aria-label": t("grid.selectRow"), checked: selected.has(key), onCheckedChange: (checked) => {
                        setSelected((prev) => {
                            const next = new Set(prev);
                            if (checked === true) {
                                next.add(key);
                            }
                            else {
                                next.delete(key);
                            }
                            return next;
                        });
                    }, onClick: (event) => { event.stopPropagation(); } }));
            },
        };
        return [selectColumn, ...columns];
    }, [columns, data, rowKey, selectable, selected, t]);
    const table = useReactTable({
        data,
        columns: allColumns,
        state: { sorting, columnVisibility: visibility },
        onSortingChange: setSorting,
        onColumnVisibilityChange: setVisibility,
        getCoreRowModel: getCoreRowModel(),
        getSortedRowModel: getSortedRowModel(),
    });
    const rows = table.getRowModel().rows;
    const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => containerRef.current, estimateSize: () => ROW_HEIGHT, overscan: 12 });
    // Saved views: list, apply, save (private or shared), delete.
    const views = useQuery({
        queryKey: ["views", entityType],
        enabled: Boolean(entityType),
        queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/views", { params: { query: { entityType: entityType ?? "" } } })),
    });
    const saveView = useMutation({
        mutationFn: async (input) => unwrap(await api.POST("/api/v1/collaboration/views", { body: { entityType: entityType ?? "", name: input.name, shared: input.shared, isDefault: false, definition: { columns: visibility, sort: sorting } } })),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["views", entityType] }),
    });
    const deleteView = useMutation({
        mutationFn: async (id) => unwrap(await api.DELETE("/api/v1/collaboration/views/{viewId}", { params: { path: { viewId: id } } })),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["views", entityType] }),
    });
    const applyView = useCallback((definition) => {
        const view = definition;
        setVisibility(view.columns ?? {});
        setSorting(view.sort ?? []);
    }, []);
    const clearSelection = useCallback(() => { setSelected(new Set()); }, []);
    useEffect(() => {
        if (activeIndex >= rows.length) {
            setActiveIndex(Math.max(0, rows.length - 1));
        }
    }, [activeIndex, rows.length]);
    const onKeyDown = (event) => {
        if (event.key === "j" || event.key === "ArrowDown") {
            event.preventDefault();
            const next = Math.min(rows.length - 1, activeIndex + 1);
            setActiveIndex(next);
            virtualizer.scrollToIndex(next);
        }
        else if (event.key === "k" || event.key === "ArrowUp") {
            event.preventDefault();
            const next = Math.max(0, activeIndex - 1);
            setActiveIndex(next);
            virtualizer.scrollToIndex(next);
        }
        else if (event.key === "Enter" && rows[activeIndex] && onOpen) {
            event.preventDefault();
            onOpen(rows[activeIndex].original);
        }
        else if (event.key === " " && selectable && rows[activeIndex]) {
            event.preventDefault();
            const key = rowKey(rows[activeIndex].original);
            setSelected((prev) => {
                const next = new Set(prev);
                if (next.has(key)) {
                    next.delete(key);
                }
                else {
                    next.add(key);
                }
                return next;
            });
        }
    };
    const selectedKeys = [...selected];
    const gridTemplate = table.getVisibleLeafColumns().map((column) => (column.id === "__select" ? "40px" : `minmax(${column.getSize() < 150 ? column.getSize() : 150}px, ${column.id === "__select" ? "40px" : "1fr"})`)).join(" ");
    return (_jsxs("div", { className: "flex flex-col gap-2", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2", children: [toolbar, selectedKeys.length > 0 && bulkActions ? (_jsxs("div", { className: "flex items-center gap-2 rounded-md bg-accent-soft px-2 py-1 text-sm text-accent", role: "status", children: [_jsx("span", { children: t("grid.selected", { count: selectedKeys.length }) }), bulkActions(selectedKeys, clearSelection)] })) : null, _jsxs("div", { className: "ms-auto flex items-center gap-1", children: [entityType ? (_jsxs(DropdownMenu, { children: [_jsx(DropdownMenuTrigger, { asChild: true, children: _jsxs(Button, { variant: "secondary", size: "sm", children: [_jsx(Bookmark, { "aria-hidden": "true" }), t("grid.views")] }) }), _jsxs(DropdownMenuContent, { align: "end", className: "min-w-56", children: [_jsx(DropdownMenuLabel, { children: t("grid.savedViews") }), (views.data ?? []).map((view) => (_jsxs(DropdownMenuItem, { onSelect: () => { applyView(view.definition); }, children: [_jsx("span", { className: "truncate", children: view.name }), view.shared ? _jsx("span", { className: "ms-auto text-xs text-fg-subtle", children: t("grid.shared") }) : null] }, view.id))), (views.data ?? []).length === 0 ? _jsx("div", { className: "px-2 py-1.5 text-xs text-fg-muted", children: t("grid.noViews") }) : null, _jsx(DropdownMenuSeparator, {}), _jsx(DropdownMenuItem, { onSelect: () => {
                                                    const name = window.prompt(t("grid.viewNamePrompt"));
                                                    if (name?.trim()) {
                                                        saveView.mutate({ name: name.trim(), shared: false });
                                                    }
                                                }, children: t("grid.saveView") }), _jsx(DropdownMenuItem, { onSelect: () => {
                                                    const name = window.prompt(t("grid.viewNamePrompt"));
                                                    if (name?.trim()) {
                                                        saveView.mutate({ name: name.trim(), shared: true });
                                                    }
                                                }, children: t("grid.saveSharedView") }), (views.data ?? []).length > 0 ? (_jsxs(_Fragment, { children: [_jsx(DropdownMenuSeparator, {}), (views.data ?? []).map((view) => (_jsx(DropdownMenuItem, { className: "text-danger", onSelect: () => { deleteView.mutate(view.id); }, children: t("grid.deleteView", { name: view.name }) }, `delete-${view.id}`)))] })) : null] })] })) : null, _jsxs(DropdownMenu, { children: [_jsx(DropdownMenuTrigger, { asChild: true, children: _jsxs(Button, { variant: "secondary", size: "sm", "aria-label": t("grid.columns"), children: [_jsx(Columns3, { "aria-hidden": "true" }), _jsx("span", { className: "hidden sm:inline", children: t("grid.columns") })] }) }), _jsxs(DropdownMenuContent, { align: "end", children: [_jsx(DropdownMenuLabel, { children: t("grid.visibleColumns") }), table.getAllLeafColumns().filter((column) => column.getCanHide()).map((column) => (_jsx(DropdownMenuCheckboxItem, { checked: column.getIsVisible(), onCheckedChange: (value) => { column.toggleVisibility(value); }, children: typeof column.columnDef.header === "string" ? column.columnDef.header : column.id }, column.id)))] })] })] })] }), loading ? (_jsx("div", { className: "flex h-40 items-center justify-center", children: _jsx(Spinner, { label: t("common.loading") }) })) : data.length === 0 ? (_jsx(EmptyState, { title: emptyTitle, description: emptyDescription, action: emptyAction })) : (_jsxs("div", { ref: containerRef, role: "grid", "aria-label": t(label), "aria-rowcount": rows.length, tabIndex: 0, onKeyDown: onKeyDown, className: "overflow-auto rounded-md border border-border bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-focus", style: { maxHeight: height }, "data-testid": "data-grid", children: [_jsx("div", { role: "rowgroup", className: "sticky top-0 z-[1] bg-surface-sunken", children: table.getHeaderGroups().map((headerGroup) => (_jsx("div", { role: "row", className: "grid border-b border-border", style: { gridTemplateColumns: gridTemplate }, children: headerGroup.headers.map((header) => {
                                const sorted = header.column.getIsSorted();
                                return (_jsx("div", { role: "columnheader", "aria-sort": sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : undefined, className: "flex h-10 items-center px-3 text-xs font-semibold uppercase tracking-wide text-fg-muted", children: header.isPlaceholder ? null : header.column.getCanSort() ? (_jsxs("button", { type: "button", className: "flex items-center gap-1 rounded-sm hover:text-fg", onClick: header.column.getToggleSortingHandler(), children: [flexRender(header.column.columnDef.header, header.getContext()), sorted === "asc" ? _jsx(ArrowUp, { className: "size-3", "aria-hidden": "true" }) : sorted === "desc" ? _jsx(ArrowDown, { className: "size-3", "aria-hidden": "true" }) : null] })) : (flexRender(header.column.columnDef.header, header.getContext())) }, header.id));
                            }) }, headerGroup.id))) }), _jsx("div", { role: "rowgroup", style: { height: virtualizer.getTotalSize(), position: "relative" }, children: virtualizer.getVirtualItems().map((virtualRow) => {
                            const row = rows[virtualRow.index];
                            if (!row) {
                                return null;
                            }
                            const key = rowKey(row.original);
                            const isActive = virtualRow.index === activeIndex;
                            return (_jsx("div", { role: "row", "aria-rowindex": virtualRow.index + 1, "aria-selected": selectable ? selected.has(key) : undefined, "data-state": selected.has(key) ? "selected" : undefined, "data-active": isActive || undefined, className: cn("absolute inset-x-0 grid cursor-default items-center border-b border-border text-sm hover:bg-surface-sunken/60 data-[active]:bg-accent-soft/50 data-[state=selected]:bg-selection", onOpen && "cursor-pointer"), style: { height: virtualRow.size, transform: `translateY(${virtualRow.start}px)`, gridTemplateColumns: gridTemplate }, onClick: () => { setActiveIndex(virtualRow.index); }, onDoubleClick: () => onOpen?.(row.original), children: row.getVisibleCells().map((cell) => (_jsx("div", { role: "gridcell", className: "truncate px-3", children: flexRender(cell.column.columnDef.cell, cell.getContext()) }, cell.id))) }, key));
                        }) })] })), _jsx("p", { className: "text-xs text-fg-subtle", "aria-live": "polite", children: t("grid.rowCount", { count: rows.length }) })] }));
}
