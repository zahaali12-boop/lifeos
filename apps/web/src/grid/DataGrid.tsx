import { Button, Checkbox, DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger, EmptyState, Input, Spinner, cn } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { flexRender, getCoreRowModel, getExpandedRowModel, getFilteredRowModel, getGroupedRowModel, getSortedRowModel, useReactTable, type Column, type ColumnDef, type ExpandedState, type GroupingState, type Row, type RowData, type SortingState, type VisibilityState } from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp, Bookmark, ChevronDown, ChevronRight, Columns3, Download, ListTree, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { sumDecimals } from "../lib/decimal";
import { saveFile } from "../lib/download";
import { formatNumber, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";

declare module "@tanstack/react-table" {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars -- the generic parameters must match the library's declaration
  interface ColumnMeta<TData extends RowData, TValue> {
    /** How an export types the column's cells; inferred from the values when not given. */
    exportType?: "text" | "number" | "date";
    /** A measure: summed exactly on group rows (money and quantities are decimals, never floats) and never grouped by. */
    aggregate?: "sum";
    /** Renders a group row's sum; a money column names its currency here. Plain number formatting otherwise. */
    formatAggregate?: (sum: string) => string;
  }
}

type ExportFormat = "csv" | "xlsx";
type ExportValue = string | number | null;

/** A ready-made grouping a screen offers ("by customer", "by month"): the fields to group by and, optionally, the columns and sort that suit it. */
export interface GridPreset {
  id: string;
  label: string;
  grouping: string[];
  columns?: VisibilityState;
  sort?: SortingState;
}

export interface DataGridProps<T> {
  columns: ColumnDef<T, unknown>[];
  data: T[];
  rowKey: (row: T) => string;
  /** Enables saved views (columns, sort, grouping, search and the screen's own filters) through the collaboration API for this entity type. */
  entityType?: string;
  onOpen?: (row: T) => void;
  selectable?: boolean;
  /** Rendered in the toolbar when rows are selected; receives the selected keys. */
  bulkActions?: (selected: string[], clear: () => void) => ReactNode;
  toolbar?: ReactNode;
  loading?: boolean;
  emptyTitle: string;
  emptyDescription?: string;
  emptyAction?: ReactNode;
  /** Pixel height of the scrolling body; the grid virtualizes rows so 100k rows render smoothly. */
  height?: number;
  /** Translation key used for the accessible name of the grid (and the export's file name). */
  label: string;
  /** The kind of record listed, checked against the role's export rules; defaults to the saved-views entity type. */
  documentType?: string;
  /** Hides the export menu (lists whose rows are not records, or that export through a report of their own). */
  exportable?: boolean;
  /** Columns hidden until chosen (a wide analysis shows a few by default and keeps the rest in the chooser). */
  initialColumns?: VisibilityState;
  /** Fields grouped by when the grid first renders. */
  initialGrouping?: string[];
  /** Ready-made groupings listed first in the group-by menu. */
  presets?: GridPreset[];
  /** Offers the group-by menu (any column with a value can be grouped by; measures are summed). */
  groupable?: boolean;
  /** Offers the quick filter box that keeps the rows whose text in any column contains what is typed. */
  searchable?: boolean;
  /** The screen's own filters (dates, status) to keep in a saved view, and how to put them back when the view is applied. */
  viewExtras?: () => Record<string, unknown>;
  onApplyView?: (extras: Record<string, unknown> | undefined) => void;
}

/** A cell's value as a spreadsheet should hold it: numbers stay numbers, texts in the reader's language, lists joined. */
function exportValue(value: unknown, yes: string, no: string): ExportValue {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value === "number" || typeof value === "string") {
    return value;
  }
  if (typeof value === "boolean") {
    return value ? yes : no;
  }
  if (Array.isArray(value)) {
    return value.map((v) => exportValue(v, yes, no)).filter((v) => v !== null).join(", ");
  }
  if (typeof value === "object") {
    const text = value as Record<string, unknown>;
    if (typeof text.en === "string" || typeof text.ar === "string") {
      return localized(text as Record<string, string>);
    }
  }
  return JSON.stringify(value);
}

function inferType(values: ExportValue[]): "text" | "number" | "date" {
  const present = values.filter((v) => v !== null && v !== "");
  if (present.length === 0) {
    return "text";
  }
  if (present.every((v) => typeof v === "number")) {
    return "number";
  }
  return present.every((v) => typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v)) ? "date" : "text";
}

function headerText<T>(column: Column<T>): string {
  const header = column.columnDef.header;
  return typeof header === "string" && header.trim() ? header : column.id;
}

/** The raw value a column reads from a record, for grouping keys: its accessor function, or its (possibly dotted) key. */
function readValue<T>(column: ColumnDef<T, unknown>, original: T): unknown {
  if ("accessorFn" in column) {
    return column.accessorFn(original, 0);
  }
  if ("accessorKey" in column && typeof column.accessorKey === "string") {
    return column.accessorKey.split(".").reduce<unknown>((value, key) => (value !== null && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined), original);
  }
  return undefined;
}

/** The leaf rows of a (possibly grouped) row model in the order they are shown. */
function leavesOf<T>(rows: Row<T>[]): Row<T>[] {
  return rows.flatMap((row) => (row.getIsGrouped() ? leavesOf(row.subRows) : [row]));
}

const compareNumbers = <T,>(a: Row<T>, b: Row<T>, columnId: string): number => Number(a.getValue(columnId) ?? 0) - Number(b.getValue(columnId) ?? 0);

/** What a saved view keeps, within the keys the collaboration API allows (columns, sort, groupBy, filters). */
interface ViewDefinition {
  columns?: Record<string, boolean>;
  sort?: { id: string; desc: boolean }[];
  groupBy?: string[];
  /** The quick filter's text and the screen's own filters, as the screen hands them over. */
  filters?: { search?: string; screen?: Record<string, unknown> | undefined };
}

const ROW_HEIGHT = 40;

/**
 * Data grid v1 (ADR-0013): TanStack Table for state, TanStack Virtual for rows, column chooser, sortable headers,
 * multi-select with bulk actions, saved views (private or shared) and j/k/Enter keyboard navigation. Group by any
 * fields in order with exact sums on the group rows (founder programme item 1f; the grid is then a treegrid, whose rows
 * may say their level and whether they are open), and a quick filter over every column.
 */
export function DataGrid<T>({ columns, data, rowKey, entityType, onOpen, selectable = false, bulkActions, toolbar, loading = false, emptyTitle, emptyDescription, emptyAction, height = 560, label, documentType, exportable = true, initialColumns, initialGrouping, presets, groupable = true, searchable = true, viewExtras, onApplyView }: DataGridProps<T>) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [sorting, setSorting] = useState<SortingState>([]);
  const [visibility, setVisibility] = useState<VisibilityState>(initialColumns ?? {});
  const [grouping, setGrouping] = useState<GroupingState>(initialGrouping ?? []);
  const [expanded, setExpanded] = useState<ExpandedState>({});
  const [globalFilter, setGlobalFilter] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [activeIndex, setActiveIndex] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const yes = t("common.yes");
  const no = t("common.no");

  const allColumns = useMemo<ColumnDef<T, unknown>[]>(() => {
    // Every column groups by the text it shows (a name in the reader's language, not its object) and a measure sums exactly.
    const prepared = columns.map((column): ColumnDef<T, unknown> => {
      const measure = column.meta?.aggregate === "sum";
      return {
        ...column,
        getGroupingValue: column.getGroupingValue ?? ((original: T) => exportValue(readValue(column, original), yes, no) ?? ""),
        ...(measure ? { enableGrouping: false, aggregationFn: (columnId: string, leafRows: Row<T>[]) => sumDecimals(leafRows.map((row) => row.getValue(columnId))), sortingFn: column.sortingFn ?? compareNumbers } : {}),
      };
    });
    if (!selectable) {
      return prepared;
    }
    const selectColumn: ColumnDef<T, unknown> = {
      id: "__select",
      enableSorting: false,
      enableHiding: false,
      enableGrouping: false,
      enableGlobalFilter: false,
      size: 40,
      header: () => (
        <Checkbox
          aria-label={t("grid.selectAll")}
          checked={selected.size === 0 ? false : selected.size === data.length ? true : "indeterminate"}
          onCheckedChange={(checked) => { setSelected(checked === true ? new Set(data.map(rowKey)) : new Set()); }}
        />
      ),
      cell: ({ row }) => {
        const key = rowKey(row.original);
        return (
          <Checkbox
            aria-label={t("grid.selectRow")}
            checked={selected.has(key)}
            onCheckedChange={(checked) => {
              setSelected((prev) => {
                const next = new Set(prev);
                if (checked === true) {
                  next.add(key);
                } else {
                  next.delete(key);
                }
                return next;
              });
            }}
            onClick={(event) => { event.stopPropagation(); }}
          />
        );
      },
    };
    return [selectColumn, ...prepared];
  }, [columns, data, no, rowKey, selectable, selected, t, yes]);

  const table = useReactTable({
    data,
    columns: allColumns,
    state: { sorting, columnVisibility: visibility, grouping, expanded, globalFilter },
    onSortingChange: setSorting,
    onColumnVisibilityChange: setVisibility,
    onGroupingChange: (updater) => {
      setGrouping(updater);
      setExpanded({});
    },
    onExpandedChange: setExpanded,
    onGlobalFilterChange: setGlobalFilter,
    getCoreRowModel: getCoreRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getGroupedRowModel: getGroupedRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getExpandedRowModel: getExpandedRowModel(),
    getColumnCanGlobalFilter: (column) => column.columnDef.enableGlobalFilter !== false && Boolean(column.accessorFn),
    globalFilterFn: (row, columnId, filterValue: string) => {
      const text = exportValue(row.getValue(columnId), yes, no);
      return text !== null && String(text).toLowerCase().includes(filterValue.toLowerCase());
    },
    groupedColumnMode: false,
  });

  const rows = table.getRowModel().rows;
  const leafCount = table.getFilteredRowModel().rows.length;
  const groupCount = grouping.length > 0 ? table.getGroupedRowModel().rows.length : 0;
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => containerRef.current, estimateSize: () => ROW_HEIGHT, overscan: 12 });
  const groupableColumns = table.getAllLeafColumns().filter((column) => column.getCanGroup());

  const applyPreset = useCallback((preset: GridPreset) => {
    setGrouping(preset.grouping);
    setExpanded({});
    if (preset.columns) {
      setVisibility(preset.columns);
    }
    if (preset.sort) {
      setSorting(preset.sort);
    }
  }, []);

  // Saved views: list, apply (the default one on arrival), save (private, shared or as the default), delete.
  const views = useQuery({
    queryKey: ["views", entityType],
    enabled: Boolean(entityType),
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/views", { params: { query: { entityType: entityType ?? "" } } })),
  });
  const saveView = useMutation({
    mutationFn: async (input: { name: string; shared: boolean; isDefault: boolean }) =>
      unwrap(await api.POST("/api/v1/collaboration/views", { body: { entityType: entityType ?? "", name: input.name, shared: input.shared, isDefault: input.isDefault, definition: { columns: visibility, sort: sorting, groupBy: grouping, filters: { search: globalFilter, screen: viewExtras?.() } } satisfies ViewDefinition } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["views", entityType] }),
  });
  const deleteView = useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/collaboration/views/{viewId}", { params: { path: { viewId: id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["views", entityType] }),
  });
  const applyView = useCallback((definition: unknown) => {
    const view = definition as ViewDefinition;
    setVisibility(view.columns ?? {});
    setSorting(view.sort ?? []);
    setGrouping(view.groupBy ?? []);
    setExpanded({});
    setGlobalFilter(view.filters?.search ?? "");
    onApplyView?.(view.filters?.screen);
  }, [onApplyView]);
  const appliedDefault = useRef(false);
  useEffect(() => {
    const preset = views.data?.find((view) => view.isDefault);
    if (preset && !appliedDefault.current) {
      appliedDefault.current = true;
      applyView(preset.definition);
    }
  }, [applyView, views.data]);
  const promptAndSave = (shared: boolean, isDefault: boolean): void => {
    const name = window.prompt(t("grid.viewNamePrompt"));
    if (name?.trim()) {
      saveView.mutate({ name: name.trim(), shared, isDefault });
    }
  };

  const clearSelection = useCallback(() => { setSelected(new Set()); }, []);

  // Export what the grid shows: its visible data columns in their order, the selected rows (or all) in the current
  // sort; the lines themselves when grouped, in their groups' order.
  const exportTable = useMutation({
    mutationFn: async (format: ExportFormat) => {
      const exported = table.getVisibleLeafColumns().filter((column) => column.id !== "__select" && Boolean(column.accessorFn));
      const leaves = leavesOf(table.getSortedRowModel().rows);
      const source = selected.size > 0 ? leaves.filter((row) => selected.has(rowKey(row.original))) : leaves;
      const values = source.map((row) => exported.map((column) => exportValue(row.getValue(column.id), yes, no)));
      const name = t(label);
      const result = await api.POST("/api/v1/exports/table", {
        body: {
          format,
          name,
          documentType: documentType ?? entityType ?? null,
          rightToLeft: document.documentElement.dir === "rtl",
          columns: exported.map((column, index) => ({ header: headerText(column), type: column.columnDef.meta?.exportType ?? inferType(values.map((row) => row[index] ?? null)) })),
          rows: values,
        },
        parseAs: "blob",
      });
      saveFile(unwrap(result), result.response.headers, `${name}.${format}`);
    },
  });

  useEffect(() => {
    if (activeIndex >= rows.length) {
      setActiveIndex(Math.max(0, rows.length - 1));
    }
  }, [activeIndex, rows.length]);

  const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>): void => {
    const active = rows[activeIndex];
    if (event.key === "j" || event.key === "ArrowDown") {
      event.preventDefault();
      const next = Math.min(rows.length - 1, activeIndex + 1);
      setActiveIndex(next);
      virtualizer.scrollToIndex(next);
    } else if (event.key === "k" || event.key === "ArrowUp") {
      event.preventDefault();
      const next = Math.max(0, activeIndex - 1);
      setActiveIndex(next);
      virtualizer.scrollToIndex(next);
    } else if (event.key === "Enter" && active?.getIsGrouped()) {
      event.preventDefault();
      active.toggleExpanded();
    } else if (event.key === "ArrowRight" && active?.getIsGrouped() && !active.getIsExpanded()) {
      event.preventDefault();
      active.toggleExpanded(true);
    } else if (event.key === "ArrowLeft" && active?.getIsGrouped() && active.getIsExpanded()) {
      event.preventDefault();
      active.toggleExpanded(false);
    } else if (event.key === "Enter" && active && onOpen) {
      event.preventDefault();
      onOpen(active.original);
    } else if (event.key === " " && selectable && active && !active.getIsGrouped()) {
      event.preventDefault();
      const key = rowKey(active.original);
      setSelected((prev) => {
        const next = new Set(prev);
        if (next.has(key)) {
          next.delete(key);
        } else {
          next.add(key);
        }
        return next;
      });
    }
  };

  const selectedKeys = [...selected];
  const visibleColumns = table.getVisibleLeafColumns();
  const firstDataColumnId = visibleColumns.find((column) => column.id !== "__select")?.id;
  const gridTemplate = visibleColumns.map((column) => (column.id === "__select" ? "40px" : `minmax(${column.getSize() < 150 ? column.getSize() : 150}px, ${column.id === "__select" ? "40px" : "1fr"})`)).join(" ");

  /** A group row: its field and value with the count, spanning the columns up to the first measure; each measure's exact sum; the rest blank. */
  const renderGroupRow = (row: Row<T>): ReactNode => {
    const groupColumnId = row.groupingColumnId ?? "";
    let start = visibleColumns.findIndex((column) => column.id === groupColumnId);
    if (start < 0) {
      start = Math.max(0, visibleColumns.findIndex((column) => column.id !== "__select"));
    }
    let span = 1;
    while (start + span < visibleColumns.length && visibleColumns[start + span]?.columnDef.meta?.aggregate === undefined) {
      span += 1;
    }
    const groupCell = row.getAllCells().find((cell) => cell.column.id === groupColumnId);
    const groupColumn = groupCell?.column;
    const value = groupCell ? flexRender(groupCell.column.columnDef.cell, groupCell.getContext()) : null;
    const empty = row.groupingValue === "" || row.groupingValue === null || row.groupingValue === undefined;
    return visibleColumns.map((column, index) => {
      if (index === start) {
        return (
          <div key={column.id} role="gridcell" className="flex min-w-0 items-center" style={{ gridColumn: `span ${String(span)}`, paddingInlineStart: `${String(row.depth * 16)}px` }}>
            <button
              type="button"
              className="flex h-full min-w-0 items-center gap-1 px-3 text-start"
              aria-expanded={row.getIsExpanded()}
              onClick={(event) => { event.stopPropagation(); row.toggleExpanded(); }}
              data-testid="grid-group-row"
            >
              {row.getIsExpanded() ? <ChevronDown className="size-4 shrink-0 text-fg-muted" aria-hidden="true" /> : <ChevronRight className="size-4 shrink-0 text-fg-muted rtl:-scale-x-100" aria-hidden="true" />}
              <span className="truncate font-medium">
                {groupColumn ? `${headerText(groupColumn)}: ` : ""}
                {empty ? <span className="text-fg-muted">{t("grid.noValue")}</span> : value}
              </span>
              <span className="shrink-0 text-xs text-fg-muted">{t("grid.groupCount", { count: row.getLeafRows().length })}</span>
            </button>
          </div>
        );
      }
      if (index > start && index < start + span) {
        return null;
      }
      if (column.columnDef.meta?.aggregate) {
        const sum = String(row.getValue(column.id) ?? "0");
        return (
          <div key={column.id} role="gridcell" className="truncate px-3 font-medium tabular" dir="ltr" data-testid={`grid-group-sum-${column.id}`}>
            {column.columnDef.meta.formatAggregate?.(sum) ?? formatNumber(sum)}
          </div>
        );
      }
      return <div key={column.id} role="gridcell" />;
    });
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        {toolbar}
        {selectedKeys.length > 0 && bulkActions ? (
          <div className="flex items-center gap-2 rounded-md bg-accent-soft px-2 py-1 text-sm text-accent" role="status">
            <span>{t("grid.selected", { count: selectedKeys.length })}</span>
            {bulkActions(selectedKeys, clearSelection)}
          </div>
        ) : null}
        <div className="ms-auto flex flex-wrap items-center gap-1">
          {searchable && data.length > 0 ? (
            <Input type="search" value={globalFilter} onChange={(event) => { setGlobalFilter(event.target.value); }} placeholder={t("grid.search")} aria-label={t("grid.search")} className="h-8 w-40 sm:w-52" data-testid="grid-search" />
          ) : null}
          {groupable && groupableColumns.length > 0 && data.length > 0 ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant={grouping.length > 0 ? "primary" : "secondary"} size="sm" aria-label={t("grid.groupBy")} data-testid="grid-group-by">
                  <ListTree aria-hidden="true" />
                  <span className="hidden sm:inline">{grouping.length > 0 ? t("grid.groupedBy", { count: grouping.length }) : t("grid.groupBy")}</span>
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="max-h-96 min-w-56 overflow-y-auto">
                {presets && presets.length > 0 ? (
                  <>
                    <DropdownMenuLabel>{t("grid.presets")}</DropdownMenuLabel>
                    {presets.map((preset) => (
                      <DropdownMenuItem key={preset.id} onSelect={() => { applyPreset(preset); }} data-testid={`grid-preset-${preset.id}`}>
                        {preset.label}
                      </DropdownMenuItem>
                    ))}
                    <DropdownMenuSeparator />
                  </>
                ) : null}
                <DropdownMenuLabel>{t("grid.groupFields")}</DropdownMenuLabel>
                {groupableColumns.map((column) => (
                  <DropdownMenuCheckboxItem key={column.id} checked={column.getIsGrouped()} onCheckedChange={() => { column.toggleGrouping(); }} onSelect={(event) => { event.preventDefault(); }} data-testid={`grid-group-${column.id}`}>
                    {headerText(column)}
                    {column.getIsGrouped() ? <span className="ms-auto ps-2 text-xs text-fg-subtle">{column.getGroupedIndex() + 1}</span> : null}
                  </DropdownMenuCheckboxItem>
                ))}
                {grouping.length > 0 ? (
                  <>
                    <DropdownMenuSeparator />
                    <DropdownMenuItem onSelect={() => { setGrouping([]); setExpanded({}); }} data-testid="grid-group-clear">{t("grid.clearGrouping")}</DropdownMenuItem>
                  </>
                ) : null}
              </DropdownMenuContent>
            </DropdownMenu>
          ) : null}
          {exportable && leafCount > 0 ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="secondary" size="sm" loading={exportTable.isPending} aria-label={t("grid.export")} data-testid="grid-export">
                  <Download aria-hidden="true" />
                  <span className="hidden sm:inline">{t("grid.export")}</span>
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>{selected.size > 0 ? t("grid.exportSelected", { count: selected.size }) : t("grid.exportAll", { count: leafCount })}</DropdownMenuLabel>
                <DropdownMenuItem onSelect={() => { exportTable.mutate("xlsx"); }} data-testid="grid-export-xlsx">
                  {t("grid.exportXlsx")}
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => { exportTable.mutate("csv"); }} data-testid="grid-export-csv">
                  {t("grid.exportCsv")}
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          ) : null}
          {entityType ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="secondary" size="sm" data-testid="grid-views">
                  <Bookmark aria-hidden="true" />
                  {t("grid.views")}
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="min-w-56">
                <DropdownMenuLabel>{t("grid.savedViews")}</DropdownMenuLabel>
                {(views.data ?? []).map((view) => (
                  <DropdownMenuItem key={view.id} onSelect={() => { applyView(view.definition); }} data-testid="grid-view">
                    <span className="truncate">{view.name}</span>
                    {view.isDefault ? <span className="ms-auto text-xs text-fg-subtle">{t("grid.default")}</span> : view.shared ? <span className="ms-auto text-xs text-fg-subtle">{t("grid.shared")}</span> : null}
                  </DropdownMenuItem>
                ))}
                {(views.data ?? []).length === 0 ? <div className="px-2 py-1.5 text-xs text-fg-muted">{t("grid.noViews")}</div> : null}
                <DropdownMenuSeparator />
                <DropdownMenuItem onSelect={() => { promptAndSave(false, false); }} data-testid="grid-save-view">
                  {t("grid.saveView")}
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => { promptAndSave(true, false); }}>
                  {t("grid.saveSharedView")}
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => { promptAndSave(false, true); }}>
                  {t("grid.saveDefaultView")}
                </DropdownMenuItem>
                {(views.data ?? []).length > 0 ? (
                  <>
                    <DropdownMenuSeparator />
                    {(views.data ?? []).map((view) => (
                      <DropdownMenuItem key={`delete-${view.id}`} className="text-danger" onSelect={() => { deleteView.mutate(view.id); }}>
                        {t("grid.deleteView", { name: view.name })}
                      </DropdownMenuItem>
                    ))}
                  </>
                ) : null}
              </DropdownMenuContent>
            </DropdownMenu>
          ) : null}
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="secondary" size="sm" aria-label={t("grid.columns")}>
                <Columns3 aria-hidden="true" />
                <span className="hidden sm:inline">{t("grid.columns")}</span>
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="max-h-96 overflow-y-auto">
              <DropdownMenuLabel>{t("grid.visibleColumns")}</DropdownMenuLabel>
              {table.getAllLeafColumns().filter((column) => column.getCanHide()).map((column) => (
                <DropdownMenuCheckboxItem key={column.id} checked={column.getIsVisible()} onCheckedChange={(value) => { column.toggleVisibility(value); }} onSelect={(event) => { event.preventDefault(); }}>
                  {typeof column.columnDef.header === "string" ? column.columnDef.header : column.id}
                </DropdownMenuCheckboxItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>
      {grouping.length > 0 ? (
        <div className="flex flex-wrap items-center gap-1 text-xs" data-testid="grid-grouping">
          <span className="text-fg-muted">{t("grid.groupBy")}:</span>
          {grouping.map((id, index) => {
            const column = table.getColumn(id);
            const name = column ? headerText(column) : id;
            return (
              <span key={id} className="inline-flex items-center gap-1 rounded-full border border-border bg-surface-sunken ps-2 pe-1 text-fg">
                <span>{index + 1}. {name}</span>
                <button type="button" className="rounded-full p-0.5 hover:bg-surface" aria-label={t("grid.removeGroup", { name })} onClick={() => { setGrouping((prev) => prev.filter((g) => g !== id)); setExpanded({}); }}>
                  <X className="size-3" aria-hidden="true" />
                </button>
              </span>
            );
          })}
          <Button variant="ghost" size="sm" onClick={() => { table.toggleAllRowsExpanded(true); }} data-testid="grid-expand-all">{t("grid.expandAll")}</Button>
          <Button variant="ghost" size="sm" onClick={() => { table.toggleAllRowsExpanded(false); }} data-testid="grid-collapse-all">{t("grid.collapseAll")}</Button>
        </div>
      ) : null}

      {loading ? (
        <div className="flex h-40 items-center justify-center">
          <Spinner label={t("common.loading")} />
        </div>
      ) : data.length === 0 ? (
        <EmptyState title={emptyTitle} description={emptyDescription} action={emptyAction} />
      ) : (
        <div
          ref={containerRef}
          role={grouping.length > 0 ? "treegrid" : "grid"}
          aria-label={t(label)}
          aria-rowcount={rows.length}
          tabIndex={0}
          onKeyDown={onKeyDown}
          className="overflow-auto rounded-md border border-border bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-focus"
          style={{ maxHeight: height }}
          data-testid="data-grid"
        >
          <div role="rowgroup" className="sticky top-0 z-[1] bg-surface-sunken">
            {table.getHeaderGroups().map((headerGroup) => (
              <div key={headerGroup.id} role="row" className="grid border-b border-border" style={{ gridTemplateColumns: gridTemplate }}>
                {headerGroup.headers.map((header) => {
                  const sorted = header.column.getIsSorted();
                  return (
                    <div key={header.id} role="columnheader" aria-sort={sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : undefined} className="flex h-10 items-center px-3 text-xs font-semibold uppercase tracking-wide text-fg-muted">
                      {header.isPlaceholder ? null : header.column.getCanSort() ? (
                        <button type="button" className="flex items-center gap-1 rounded-sm hover:text-fg" onClick={header.column.getToggleSortingHandler()}>
                          {flexRender(header.column.columnDef.header, header.getContext())}
                          {sorted === "asc" ? <ArrowUp className="size-3" aria-hidden="true" /> : sorted === "desc" ? <ArrowDown className="size-3" aria-hidden="true" /> : null}
                        </button>
                      ) : (
                        flexRender(header.column.columnDef.header, header.getContext())
                      )}
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
          <div role="rowgroup" style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
            {virtualizer.getVirtualItems().map((virtualRow) => {
              const row = rows[virtualRow.index];
              if (!row) {
                return null;
              }
              const grouped = row.getIsGrouped();
              const key = grouped ? row.id : rowKey(row.original);
              const isActive = virtualRow.index === activeIndex;
              return (
                <div
                  key={key}
                  role="row"
                  aria-rowindex={virtualRow.index + 1}
                  aria-level={grouping.length > 0 ? row.depth + 1 : undefined}
                  aria-expanded={grouped ? row.getIsExpanded() : undefined}
                  aria-selected={selectable && !grouped ? selected.has(key) : undefined}
                  data-state={!grouped && selected.has(key) ? "selected" : undefined}
                  data-active={isActive || undefined}
                  data-group={grouped || undefined}
                  className={cn("absolute inset-x-0 grid cursor-default items-center border-b border-border text-sm hover:bg-surface-sunken/60 data-[active]:bg-accent-soft/50 data-[state=selected]:bg-selection", grouped && "bg-surface-sunken/40", onOpen && !grouped && "cursor-pointer")}
                  style={{ height: virtualRow.size, transform: `translateY(${virtualRow.start}px)`, gridTemplateColumns: gridTemplate }}
                  onClick={() => { setActiveIndex(virtualRow.index); if (grouped) { row.toggleExpanded(); } }}
                  onDoubleClick={() => { if (!grouped) { onOpen?.(row.original); } }}
                >
                  {grouped ? renderGroupRow(row) : row.getVisibleCells().map((cell) => (
                    <div key={cell.id} role="gridcell" className="truncate px-3" style={grouping.length > 0 && cell.column.id === firstDataColumnId ? { paddingInlineStart: `${String(12 + row.depth * 16)}px` } : undefined}>
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </div>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      )}
      <p className="text-xs text-fg-subtle" aria-live="polite" data-testid="grid-row-count">
        {t("grid.rowCount", { count: leafCount })}
        {grouping.length > 0 ? ` · ${t("grid.groups", { count: groupCount })}` : ""}
      </p>
      {exportTable.isError ? (
        <p className="text-xs text-danger" role="alert" data-testid="grid-export-error">
          {toFormProblem(exportTable.error, t("grid.exportFailed")).message}
        </p>
      ) : null}
      {saveView.isError ? (
        <p className="text-xs text-danger" role="alert" data-testid="grid-view-error">
          {toFormProblem(saveView.error, t("grid.saveViewFailed")).message}
        </p>
      ) : null}
    </div>
  );
}
