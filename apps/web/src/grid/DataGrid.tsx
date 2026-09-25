import { Button, Checkbox, DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger, EmptyState, Spinner, cn } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable, type Column, type ColumnDef, type RowData, type SortingState, type VisibilityState } from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp, Bookmark, Columns3, Download } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { saveFile } from "../lib/download";
import { localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";

declare module "@tanstack/react-table" {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars -- the generic parameters must match the library's declaration
  interface ColumnMeta<TData extends RowData, TValue> {
    /** How an export types the column's cells; inferred from the values when not given. */
    exportType?: "text" | "number" | "date";
  }
}

type ExportFormat = "csv" | "xlsx";
type ExportValue = string | number | null;

export interface DataGridProps<T> {
  columns: ColumnDef<T, unknown>[];
  data: T[];
  rowKey: (row: T) => string;
  /** Enables saved views (columns and sort) through the collaboration API for this entity type. */
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

interface ViewDefinition {
  columns?: Record<string, boolean>;
  sort?: { id: string; desc: boolean }[];
}

const ROW_HEIGHT = 40;

/**
 * Data grid v1 (ADR-0013): TanStack Table for state, TanStack Virtual for rows, column chooser, sortable headers,
 * multi-select with bulk actions, saved views (private or shared) and j/k/Enter keyboard navigation.
 */
export function DataGrid<T>({ columns, data, rowKey, entityType, onOpen, selectable = false, bulkActions, toolbar, loading = false, emptyTitle, emptyDescription, emptyAction, height = 560, label, documentType, exportable = true }: DataGridProps<T>) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [sorting, setSorting] = useState<SortingState>([]);
  const [visibility, setVisibility] = useState<VisibilityState>({});
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [activeIndex, setActiveIndex] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);

  const allColumns = useMemo<ColumnDef<T, unknown>[]>(() => {
    if (!selectable) {
      return columns;
    }
    const selectColumn: ColumnDef<T, unknown> = {
      id: "__select",
      enableSorting: false,
      enableHiding: false,
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
    mutationFn: async (input: { name: string; shared: boolean }) =>
      unwrap(await api.POST("/api/v1/collaboration/views", { body: { entityType: entityType ?? "", name: input.name, shared: input.shared, isDefault: false, definition: { columns: visibility, sort: sorting } satisfies ViewDefinition } })),
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
  }, []);

  const clearSelection = useCallback(() => { setSelected(new Set()); }, []);

  // Export what the grid shows: its visible data columns in their order, the selected rows (or all) in the current sort.
  const exportTable = useMutation({
    mutationFn: async (format: ExportFormat) => {
      const exported = table.getVisibleLeafColumns().filter((column) => column.id !== "__select" && Boolean(column.accessorFn));
      const source = selected.size > 0 ? rows.filter((row) => selected.has(rowKey(row.original))) : rows;
      const values = source.map((row) => exported.map((column) => exportValue(row.getValue(column.id), t("common.yes"), t("common.no"))));
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
    } else if (event.key === "Enter" && rows[activeIndex] && onOpen) {
      event.preventDefault();
      onOpen(rows[activeIndex].original);
    } else if (event.key === " " && selectable && rows[activeIndex]) {
      event.preventDefault();
      const key = rowKey(rows[activeIndex].original);
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
  const gridTemplate = table.getVisibleLeafColumns().map((column) => (column.id === "__select" ? "40px" : `minmax(${column.getSize() < 150 ? column.getSize() : 150}px, ${column.id === "__select" ? "40px" : "1fr"})`)).join(" ");

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
        <div className="ms-auto flex items-center gap-1">
          {exportable && rows.length > 0 ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="secondary" size="sm" loading={exportTable.isPending} aria-label={t("grid.export")} data-testid="grid-export">
                  <Download aria-hidden="true" />
                  <span className="hidden sm:inline">{t("grid.export")}</span>
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>{selected.size > 0 ? t("grid.exportSelected", { count: selected.size }) : t("grid.exportAll", { count: rows.length })}</DropdownMenuLabel>
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
                <Button variant="secondary" size="sm">
                  <Bookmark aria-hidden="true" />
                  {t("grid.views")}
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="min-w-56">
                <DropdownMenuLabel>{t("grid.savedViews")}</DropdownMenuLabel>
                {(views.data ?? []).map((view) => (
                  <DropdownMenuItem key={view.id} onSelect={() => { applyView(view.definition); }}>
                    <span className="truncate">{view.name}</span>
                    {view.shared ? <span className="ms-auto text-xs text-fg-subtle">{t("grid.shared")}</span> : null}
                  </DropdownMenuItem>
                ))}
                {(views.data ?? []).length === 0 ? <div className="px-2 py-1.5 text-xs text-fg-muted">{t("grid.noViews")}</div> : null}
                <DropdownMenuSeparator />
                <DropdownMenuItem
                  onSelect={() => {
                    const name = window.prompt(t("grid.viewNamePrompt"));
                    if (name?.trim()) {
                      saveView.mutate({ name: name.trim(), shared: false });
                    }
                  }}
                >
                  {t("grid.saveView")}
                </DropdownMenuItem>
                <DropdownMenuItem
                  onSelect={() => {
                    const name = window.prompt(t("grid.viewNamePrompt"));
                    if (name?.trim()) {
                      saveView.mutate({ name: name.trim(), shared: true });
                    }
                  }}
                >
                  {t("grid.saveSharedView")}
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
            <DropdownMenuContent align="end">
              <DropdownMenuLabel>{t("grid.visibleColumns")}</DropdownMenuLabel>
              {table.getAllLeafColumns().filter((column) => column.getCanHide()).map((column) => (
                <DropdownMenuCheckboxItem key={column.id} checked={column.getIsVisible()} onCheckedChange={(value) => { column.toggleVisibility(value); }}>
                  {typeof column.columnDef.header === "string" ? column.columnDef.header : column.id}
                </DropdownMenuCheckboxItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>

      {loading ? (
        <div className="flex h-40 items-center justify-center">
          <Spinner label={t("common.loading")} />
        </div>
      ) : data.length === 0 ? (
        <EmptyState title={emptyTitle} description={emptyDescription} action={emptyAction} />
      ) : (
        <div
          ref={containerRef}
          role="grid"
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
              const key = rowKey(row.original);
              const isActive = virtualRow.index === activeIndex;
              return (
                <div
                  key={key}
                  role="row"
                  aria-rowindex={virtualRow.index + 1}
                  aria-selected={selectable ? selected.has(key) : undefined}
                  data-state={selected.has(key) ? "selected" : undefined}
                  data-active={isActive || undefined}
                  className={cn("absolute inset-x-0 grid cursor-default items-center border-b border-border text-sm hover:bg-surface-sunken/60 data-[active]:bg-accent-soft/50 data-[state=selected]:bg-selection", onOpen && "cursor-pointer")}
                  style={{ height: virtualRow.size, transform: `translateY(${virtualRow.start}px)`, gridTemplateColumns: gridTemplate }}
                  onClick={() => { setActiveIndex(virtualRow.index); }}
                  onDoubleClick={() => onOpen?.(row.original)}
                >
                  {row.getVisibleCells().map((cell) => (
                    <div key={cell.id} role="gridcell" className="truncate px-3">
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </div>
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      )}
      <p className="text-xs text-fg-subtle" aria-live="polite">
        {t("grid.rowCount", { count: rows.length })}
      </p>
      {exportTable.isError ? (
        <p className="text-xs text-danger" role="alert" data-testid="grid-export-error">
          {toFormProblem(exportTable.error, t("grid.exportFailed")).message}
        </p>
      ) : null}
    </div>
  );
}
