import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { ArrowDown, ArrowUp, Code, Columns3, Copy, Download, Play, Plus, Save, Sigma, Trash, X } from "lucide-react";
import { useCallback, useEffect, useId, useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { recordRoute } from "../../lib/documents";
import { saveFile } from "../../lib/download";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { ReportChart } from "./ReportChart";
import {
  aggregations, calculatedTypes, cellText, chartData, chartTypes, comparisons, drillText, measureText, operators, pivot, reportKinds, useCatalogue,
  type CatalogueField, type ChartOptions, type Pivot, type QueryDefinition, type ReportSummary, type ResultRow, type ShareRequest,
} from "./shared";

interface ValueDraft {
  field: string;
  aggregation: string;
}

interface FilterDraft {
  id: number;
  field: string;
  op: string;
  value: string;
}

interface CalculatedDraft {
  key: string;
  labelEn: string;
  labelAr: string;
  expression: string;
  type: string;
}

interface ChartDraft {
  type: string;
  stacked: boolean;
  horizontal: boolean;
  categoryField: string;
  seriesField: string;
  measure: string;
}

/** What the builder edits: the query, how it is shown, and how it draws. */
interface Draft {
  model: string;
  rows: string[];
  columns: string[];
  values: ValueDraft[];
  filters: FilterDraft[];
  sortField: string;
  sortDescending: boolean;
  topN: string;
  calculated: CalculatedDraft[];
  comparison: string;
  from: string;
  to: string;
  kind: string;
  chart: ChartDraft;
}

/** The saved report around the draft: its name, who else sees it, and what the signed-in member may do with it. */
interface Meta {
  id: string | null;
  nameEn: string;
  nameAr: string;
  descriptionEn: string;
  descriptionAr: string;
  shares: ShareRequest[];
  isOwner: boolean;
  canEdit: boolean;
  updatedAt: string | null;
}

const emptyChart: ChartDraft = { type: "bar", stacked: false, horizontal: false, categoryField: "", seriesField: "", measure: "" };
const emptyDraft = (model: string): Draft => ({ model, rows: [], columns: [], values: [], filters: [], sortField: "", sortDescending: true, topN: "", calculated: [], comparison: "", from: "", to: "", kind: "pivot", chart: emptyChart });
const emptyMeta: Meta = { id: null, nameEn: "", nameAr: "", descriptionEn: "", descriptionAr: "", shares: [], isOwner: true, canEdit: true, updatedAt: null };

function filterText(value: unknown): string {
  if (value === null || value === undefined) {
    return "";
  }
  if (Array.isArray(value)) {
    return value.map((v) => cellText(v)).join(", ");
  }
  return cellText(value);
}

function draftOf(r: ReportSummary): Draft {
  const d = r.definition;
  return {
    model: d.model,
    rows: d.rows ?? [],
    columns: d.columns ?? [],
    values: (d.values ?? []).map((v) => ({ field: v.field, aggregation: v.aggregation ?? "" })),
    filters: (d.filters ?? []).map((f, i) => ({ id: i + 1, field: f.field, op: f.op, value: filterText(f.value) })),
    sortField: d.sort?.[0]?.field ?? "",
    sortDescending: d.sort?.[0]?.descending ?? true,
    topN: d.topN ? String(d.topN) : "",
    calculated: (d.calculated ?? []).map((c) => ({ key: c.key, labelEn: c.label.en ?? "", labelAr: c.label.ar ?? "", expression: c.expression, type: c.type })),
    comparison: d.comparison ?? "",
    from: d.from ?? "",
    to: d.to ?? "",
    kind: r.kind,
    chart: { type: r.chart.type, stacked: r.chart.stacked, horizontal: r.chart.horizontal, categoryField: r.chart.categoryField ?? "", seriesField: r.chart.seriesField ?? "", measure: r.chart.measures?.[0] ?? "" },
  };
}

function metaOf(r: ReportSummary): Meta {
  return {
    id: r.id,
    nameEn: r.name.en ?? "",
    nameAr: r.name.ar ?? "",
    descriptionEn: r.description.en ?? "",
    descriptionAr: r.description.ar ?? "",
    shares: r.shares.map((s) => ({ shareWith: s.shareWith, targetId: s.targetId, access: s.access })),
    isOwner: r.isOwner,
    canEdit: r.canEdit,
    updatedAt: r.updatedAt,
  };
}

/** A filter's value as the API takes it: a list for in/not in, two bounds for between, nothing for the null tests, the text otherwise. */
function filterValue(f: FilterDraft): unknown {
  const text = f.value.trim();
  switch (f.op) {
    case "is_null":
    case "not_null":
      return undefined;
    case "in":
    case "not_in":
      return text.split(",").map((v) => v.trim()).filter(Boolean);
    case "between":
      return text.split(/\s*(?:,|\.\.)\s*/).map((v) => v.trim()).filter(Boolean).slice(0, 2);
    default:
      return text;
  }
}

function toDefinition(draft: Draft, companyId: string): QueryDefinition {
  return {
    model: draft.model,
    companyId,
    rows: draft.rows,
    columns: draft.columns,
    values: draft.values.map((v) => ({ field: v.field, aggregation: v.aggregation || null })),
    filters: draft.filters.filter((f) => f.field && f.op).map((f) => ({ field: f.field, op: f.op, value: filterValue(f) })),
    sort: draft.sortField ? [{ field: draft.sortField, descending: draft.sortDescending }] : [],
    topN: draft.topN.trim() ? Number(draft.topN) : null,
    calculated: draft.calculated.map((c) => ({ key: c.key, label: { en: c.labelEn, ar: c.labelAr || c.labelEn }, expression: c.expression, type: c.type })),
    comparison: draft.comparison || null,
    from: draft.from || null,
    to: draft.to || null,
  };
}

function toChartOptions(chart: ChartDraft): ChartOptions {
  return { type: chart.type, stacked: chart.stacked, horizontal: chart.horizontal, categoryField: chart.categoryField || null, seriesField: chart.seriesField || null, measures: chart.measure ? [chart.measure] : null };
}

function Zone({ title, testId, hint, children }: { title: string; testId: string; hint: string; children: ReactNode[] }) {
  return (
    <div className="rounded-md border border-border bg-surface p-2" data-testid={testId}>
      <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-fg-muted">{title}</div>
      <div className="flex flex-wrap items-center gap-1">{children.length > 0 ? children : <span className="text-xs text-fg-subtle">{hint}</span>}</div>
    </div>
  );
}

function Chip({ children, onRemove, removeLabel, testId }: { children: ReactNode; onRemove: () => void; removeLabel: string; testId: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-surface-sunken ps-3 pe-1 py-0.5 text-sm" data-testid={testId}>
      {children}
      <button type="button" onClick={onRemove} aria-label={removeLabel} className="rounded-full p-1 text-fg-muted hover:bg-border hover:text-fg">
        <X className="size-3" aria-hidden="true" />
      </button>
    </span>
  );
}

/** The result as a table: row dimensions down the side, column dimensions across the top, a cell per measure, totals where they add up; a measure cell drills to the lines behind it. */
function ResultTable({ table, label, canDrill, onDrill, id, caption }: { table: Pivot; label: (key: string) => string; canDrill: boolean; onDrill: (keys: Record<string, string>, title: string) => void; id: string; caption: string }) {
  const { t } = useTranslation();
  const grouped = table.columnDims.length > 0;
  return (
    <Table id={id} data-testid="report-table">
      <caption className="sr-only">{caption}</caption>
      <TableHeader>
        <TableRow>
          {table.rowDims.map((d) => <TableHead key={d} rowSpan={grouped ? 2 : 1}>{label(d)}</TableHead>)}
          {grouped
            ? table.columns.map((c) => <TableHead key={c.key} colSpan={table.measures.length} className="text-center">{c.label || t("reports.blank")}</TableHead>)
            : table.measures.map((m) => <TableHead key={m.key} className="text-end">{localized(m.label)}</TableHead>)}
        </TableRow>
        {grouped ? (
          <TableRow>
            {table.columns.flatMap((c) => table.measures.map((m) => <TableHead key={`${c.key}|${m.key}`} className="text-end">{localized(m.label)}</TableHead>))}
          </TableRow>
        ) : null}
      </TableHeader>
      <TableBody>
        {table.rows.map((row, index) => (
          <TableRow key={row.key} data-testid={`report-row-${index + 1}`}>
            {row.cells.map((cell, i) => <TableCell key={table.rowDims[i] ?? i} dir="auto">{cell || t("reports.blank")}</TableCell>)}
            {table.columns.flatMap((c) => table.measures.map((m) => {
              const value = row.values[c.key]?.[m.key] ?? null;
              const text = measureText(value, m.type);
              const title = [...row.cells, c.label].filter(Boolean).join(" · ");
              return (
                <TableNumberCell key={`${c.key}|${m.key}`} dir="ltr">
                  {canDrill && value !== null ? (
                    <button type="button" className="rounded px-1 text-accent underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-focus" onClick={() => { onDrill({ ...row.keys, ...c.keys }, title); }} title={t("reports.drillHint")}>
                      {text}
                    </button>
                  ) : text}
                </TableNumberCell>
              );
            }))}
          </TableRow>
        ))}
        {table.rows.length > 1 ? (
          <TableRow className="bg-surface-sunken font-semibold" data-testid="report-totals">
            <TableCell colSpan={Math.max(1, table.rowDims.length)}>{t("reports.total")}</TableCell>
            {table.columns.flatMap((c) => table.measures.map((m) => <TableNumberCell key={`${c.key}|${m.key}`} dir="ltr">{measureText(table.totals[c.key]?.[m.key] ?? null, m.type)}</TableNumberCell>))}
          </TableRow>
        ) : null}
        {table.rows.length === 0 ? <TableRow><TableCell colSpan={table.rowDims.length + table.columns.length * table.measures.length}>{t("reports.noRows")}</TableCell></TableRow> : null}
      </TableBody>
    </Table>
  );
}

/** The report builder (founder programme item 9b): a model, its fields dragged into rows, columns and values by a click, filters, a date range, comparisons, calculated fields, the result as a table, a pivot or a chart, drill to the documents, the SQL behind it, saved and shared, exported. */
export function ReportBuilderPage() {
  const search = useSearch({ strict: false });
  const reportId = search.report ?? null;
  return <Builder key={reportId ?? `new:${search.model ?? ""}`} reportId={reportId} initialModel={search.model ?? ""} />;
}

function Builder({ reportId, initialModel }: { reportId: string | null; initialModel: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const can = useCan();
  const canManage = can("reporting.report.manage");
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const catalogue = useCatalogue();
  const [draft, setDraft] = useState<Draft>(() => emptyDraft(initialModel));
  const [meta, setMeta] = useState<Meta>(emptyMeta);
  const [executed, setExecuted] = useState<QueryDefinition | null>(null);
  const [loadedId, setLoadedId] = useState<string | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [fieldSearch, setFieldSearch] = useState("");
  const [dialog, setDialog] = useState<"save" | "explain" | "calculated" | null>(null);
  const [drill, setDrill] = useState<{ keys: Record<string, string>; title: string } | null>(null);
  const [calculatedForm, setCalculatedForm] = useState<CalculatedDraft>({ key: "", labelEn: "", labelAr: "", expression: "", type: "number" });
  const tableId = useId();

  // A saved report loads into the builder once and runs as saved; the company it was saved for is selected.
  const saved = useQuery({
    queryKey: ["report", reportId],
    enabled: Boolean(reportId),
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/reports/{reportId}", { params: { path: { reportId: reportId ?? "" } } })),
  });
  useEffect(() => {
    if (saved.data && saved.data.id !== loadedId) {
      setLoadedId(saved.data.id);
      setDraft(draftOf(saved.data));
      setMeta(metaOf(saved.data));
      if (saved.data.companyId) {
        setCompanyId(saved.data.companyId);
      }
      setExecuted({ ...saved.data.definition, companyId: saved.data.companyId ?? saved.data.definition.companyId });
    }
  }, [saved.data, loadedId, setCompanyId]);

  // Without a model chosen, the first model the member may read.
  const firstModel = catalogue.data?.[0];
  useEffect(() => {
    if (!draft.model && firstModel && !reportId) {
      setDraft((d) => ({ ...d, model: firstModel.code }));
    }
  }, [draft.model, firstModel, reportId]);

  const model = catalogue.data?.find((m) => m.code === draft.model);
  const fields = useMemo(() => model?.fields ?? [], [model]);
  const fieldByKey = useMemo(() => new Map(fields.map((f) => [f.key, f])), [fields]);
  const label = useCallback((key: string): string => {
    const f = fieldByKey.get(key);
    if (f) {
      return localized(f.label);
    }
    const c = draft.calculated.find((x) => x.key === key);
    return c ? localized({ en: c.labelEn, ar: c.labelAr }) : key;
  }, [fieldByKey, draft.calculated]);
  const dims = useMemo(() => fields.filter((f) => f.role === "dimension"), [fields]);
  const chosenDims = [...draft.rows, ...draft.columns];
  const folders = useMemo(() => {
    const needle = fieldSearch.trim().toLowerCase();
    const groups = new Map<string, CatalogueField[]>();
    for (const f of fields) {
      if (needle && !localized(f.label).toLowerCase().includes(needle) && !f.key.includes(needle)) {
        continue;
      }
      const list = groups.get(f.folder) ?? [];
      list.push(f);
      groups.set(f.folder, list);
    }
    return [...groups.entries()];
  }, [fields, fieldSearch]);

  const result = useQuery({
    queryKey: ["report-run", executed],
    enabled: executed !== null,
    retry: false,
    queryFn: async () => unwrap(await api.POST("/api/v1/reporting/query", { body: executed ?? { model: draft.model, companyId } })),
  });
  const runProblem = result.error ? toFormProblem(result.error, t("reports.runFailed")) : null;

  const table = useMemo(() => {
    if (!result.data || !executed) {
      return null;
    }
    const rows = executed.rows ?? [];
    const columns = executed.columns ?? [];
    return draft.kind === "table" ? pivot(result.data, [...rows, ...columns], []) : pivot(result.data, rows, columns);
  }, [result.data, executed, draft.kind]);
  const chartOptions = useMemo(() => toChartOptions(draft.chart), [draft.chart]);
  const chart = useMemo(() => (result.data && executed ? chartData(result.data, chartOptions, [...(executed.rows ?? []), ...(executed.columns ?? [])], { other: t("reports.other"), blank: t("reports.blank") }) : null), [result.data, executed, chartOptions, t]);
  const chartType = chart?.measure?.type;
  const chartFormat = useCallback((value: number): string => measureText(value, chartType), [chartType]);

  const run = (): void => { setProblem(null); setExecuted(toDefinition(draft, companyId)); };
  const changeModel = (code: string): void => { setDraft({ ...emptyDraft(code), from: draft.from, to: draft.to, kind: draft.kind, chart: emptyChart }); setExecuted(null); };
  const addField = (f: CatalogueField): void => {
    if (f.role === "measure") {
      if (!draft.values.some((v) => v.field === f.key)) {
        setDraft({ ...draft, values: [...draft.values, { field: f.key, aggregation: f.aggregation ?? "sum" }] });
      }
    } else if (!chosenDims.includes(f.key)) {
      setDraft({ ...draft, rows: [...draft.rows, f.key] });
    }
  };
  const addColumn = (key: string): void => {
    if (!chosenDims.includes(key)) {
      setDraft({ ...draft, columns: [...draft.columns, key] });
    }
  };
  const removeDim = (key: string): void => { setDraft({ ...draft, rows: draft.rows.filter((r) => r !== key), columns: draft.columns.filter((c) => c !== key), sortField: draft.sortField === key ? "" : draft.sortField }); };
  const removeValue = (key: string): void => { setDraft({ ...draft, values: draft.values.filter((v) => v.field !== key), sortField: draft.sortField === key ? "" : draft.sortField }); };
  const addFilter = (): void => { setDraft({ ...draft, filters: [...draft.filters, { id: Date.now(), field: dims[0]?.key ?? "", op: "eq", value: "" }] }); };
  const setFilter = (id: number, patch: Partial<FilterDraft>): void => { setDraft({ ...draft, filters: draft.filters.map((f) => (f.id === id ? { ...f, ...patch } : f)) }); };

  // The lines behind a cell: the row's keys plus the column's, as the API's drill takes them.
  const drillQuery = useQuery({
    queryKey: ["report-drill", executed, drill?.keys],
    enabled: Boolean(drill && executed),
    retry: false,
    queryFn: async () => {
      if (!drill || !executed) {
        throw new Error("nothing to drill");
      }
      return unwrap(await api.POST("/api/v1/reporting/query/drill", { body: { definition: executed, keys: drill.keys, limit: 200 } }));
    },
  });
  const explain = useQuery({
    queryKey: ["report-explain", executed],
    enabled: dialog === "explain" && executed !== null,
    retry: false,
    queryFn: async () => unwrap(await api.POST("/api/v1/reporting/query/explain", { body: executed ?? { model: draft.model, companyId } })),
  });

  const save = useMutation({
    mutationFn: async (asCopy: boolean) => {
      const body = {
        name: { en: meta.nameEn, ar: meta.nameAr },
        description: { en: meta.descriptionEn, ar: meta.descriptionAr },
        kind: draft.kind,
        chart: chartOptions,
        definition: toDefinition(draft, companyId),
        companyId,
        shares: meta.isOwner || asCopy ? meta.shares : null,
      };
      return meta.id && !asCopy
        ? unwrap(await api.PUT("/api/v1/reporting/reports/{reportId}", { params: { path: { reportId: meta.id } }, body }))
        : unwrap(await api.POST("/api/v1/reporting/reports", { body }));
    },
    onSuccess: async (r) => {
      setProblem(null);
      setDialog(null);
      queryClient.setQueryData(["report", r.id], r);
      await queryClient.invalidateQueries({ queryKey: ["reports"] });
      if (r.id !== meta.id) {
        void navigate({ to: "/reports/builder", search: { report: r.id }, replace: true });
      } else {
        setMeta(metaOf(r));
      }
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const remove = useMutation({
    mutationFn: async () => {
      if (meta.id) {
        unwrap(await api.DELETE("/api/v1/reporting/reports/{reportId}", { params: { path: { reportId: meta.id } } }));
      }
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["reports"] });
      void navigate({ to: "/reports" });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const exportTable = useMutation({
    mutationFn: async (format: "csv" | "xlsx") => {
      if (!table) {
        return;
      }
      const measureLabel = (key: string): string => {
        const m = table.measures.find((x) => x.key === key);
        return m ? localized(m.label) : key;
      };
      const columns = [
        ...table.rowDims.map((d) => ({ header: label(d), type: "text" })),
        ...table.columns.flatMap((c) => table.measures.map((m) => ({ header: c.label ? `${c.label} · ${measureLabel(m.key)}` : measureLabel(m.key), type: "number" }))),
      ];
      const rows = table.rows.map((r) => [...r.cells, ...table.columns.flatMap((c) => table.measures.map((m) => r.values[c.key]?.[m.key] ?? null))]);
      const name = localized({ en: meta.nameEn, ar: meta.nameAr }) || t("nav.reports");
      const response = await api.POST("/api/v1/exports/table", { body: { format, name, documentType: null, rightToLeft: document.documentElement.dir === "rtl", columns, rows }, parseAs: "blob" });
      saveFile(unwrap(response), response.response.headers, `${name}.${format}`);
    },
  });

  const submitSave = (event: FormEvent): void => {
    event.preventDefault();
    save.mutate(false);
  };
  const addCalculated = (event: FormEvent): void => {
    event.preventDefault();
    const key = calculatedForm.key.trim().toLowerCase().replace(/[^a-z0-9_]+/g, "_");
    if (!key || !calculatedForm.expression.trim()) {
      return;
    }
    setDraft({ ...draft, calculated: [...draft.calculated.filter((c) => c.key !== key), { ...calculatedForm, key }] });
    setCalculatedForm({ key: "", labelEn: "", labelAr: "", expression: "", type: "number" });
    setDialog(null);
  };

  const roles = useQuery({ queryKey: ["roles"], enabled: dialog === "save" && meta.isOwner, queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
  const members = useQuery({ queryKey: ["members"], enabled: dialog === "save" && meta.isOwner, queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
  const setShare = (index: number, patch: Partial<ShareRequest>): void => { setMeta({ ...meta, shares: meta.shares.map((s, i) => (i === index ? { ...s, ...patch } : s)) }); };

  const sortable = [...chosenDims, ...draft.values.map((v) => v.field), ...draft.calculated.map((c) => c.key)];
  const title = meta.id ? localized({ en: meta.nameEn, ar: meta.nameAr }) || t("reports.untitled") : t("reports.newReport");
  const resultCaption = result.data ? t("reports.resultCaption", { rows: result.data.rowCount, currency: result.data.currency }) : "";

  return (
    <div>
      <PageHeader
        title={title}
        description={meta.id ? t("reports.builderSavedDescription", { when: meta.updatedAt ? formatDateTime(meta.updatedAt) : "" }) : t("reports.builderDescription")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { void navigate({ to: "/reports" }); }} data-testid="back-to-reports">{t("reports.allReports")}</Button>
            <Button onClick={run} disabled={!companyId || !draft.model} data-testid="run-report"><Play aria-hidden="true" /> {t("reports.run")}</Button>
            {canManage ? (
              <Button variant="secondary" onClick={() => { setProblem(null); setDialog("save"); }} data-testid="save-report">
                {meta.canEdit ? <Save aria-hidden="true" /> : <Copy aria-hidden="true" />} {meta.canEdit ? t("common.save") : t("reports.saveCopy")}
              </Button>
            ) : null}
            {meta.id && meta.isOwner && canManage ? (
              <Button variant="danger" onClick={() => { if (window.confirm(t("reports.confirmDelete"))) { remove.mutate(); } }} loading={remove.isPending} data-testid="delete-report"><Trash aria-hidden="true" /> {t("common.delete")}</Button>
            ) : null}
          </>
        }
      />
      <FormError message={problem?.message ?? runProblem?.message ?? null} />

      <div className="flex flex-col gap-4 lg:flex-row">
        <aside className="w-full shrink-0 rounded-md border border-border bg-surface p-3 lg:w-72" aria-label={t("reports.fields")}>
          <Field label={t("reports.model")}>
            <SelectField value={draft.model} onChange={(e) => { changeModel(e.target.value); }} data-testid="report-model">
              {(catalogue.data ?? []).map((m) => <option key={m.code} value={m.code}>{localized(m.name)}</option>)}
            </SelectField>
          </Field>
          {model ? <p className="mt-1 text-xs text-fg-muted">{localized(model.description)}</p> : null}
          <div className="mt-3">
            <TextField value={fieldSearch} onChange={(e) => { setFieldSearch(e.target.value); }} placeholder={t("reports.findField")} aria-label={t("reports.findField")} data-testid="field-search" />
          </div>
          <div className="mt-2 max-h-[32rem] overflow-y-auto pe-1">
            {folders.map(([folder, items]) => (
              <div key={folder} className="mt-3">
                <div className="text-xs font-semibold uppercase tracking-wide text-fg-muted">{t(`reports.folders.${folder}`, { defaultValue: folder })}</div>
                <ul className="mt-1 flex flex-col">
                  {items.map((f) => {
                    const chosen = f.role === "measure" ? draft.values.some((v) => v.field === f.key) : chosenDims.includes(f.key);
                    return (
                      <li key={f.key} className="flex items-center gap-1">
                        <button
                          type="button"
                          onClick={() => { addField(f); }}
                          disabled={chosen}
                          title={f.description ? localized(f.description) : undefined}
                          className="flex min-w-0 flex-1 items-center gap-1.5 rounded px-1.5 py-1 text-start text-sm hover:bg-surface-sunken disabled:text-fg-subtle"
                          data-testid={`field-${f.key}`}
                        >
                          {f.role === "measure" ? <Sigma className="size-3.5 shrink-0 text-fg-muted" aria-hidden="true" /> : <span className="size-3.5 shrink-0 rounded-sm border border-border-strong" aria-hidden="true" />}
                          <span className="truncate" dir="auto">{localized(f.label)}</span>
                        </button>
                        {f.role === "dimension" ? (
                          <button type="button" onClick={() => { addColumn(f.key); }} disabled={chosen} aria-label={t("reports.addToColumns", { field: localized(f.label) })} className="rounded p-1 text-fg-muted hover:bg-surface-sunken hover:text-fg disabled:opacity-40" data-testid={`field-${f.key}-column`}>
                            <Columns3 className="size-3.5" aria-hidden="true" />
                          </button>
                        ) : null}
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
          </div>
        </aside>

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-end gap-3">
            <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
            <Field label={t("reports.from")}>
              <TextField type="date" value={draft.from} onChange={(e) => { setDraft({ ...draft, from: e.target.value }); }} dir="ltr" data-testid="report-from" />
            </Field>
            <Field label={t("reports.to")}>
              <TextField type="date" value={draft.to} onChange={(e) => { setDraft({ ...draft, to: e.target.value }); }} dir="ltr" data-testid="report-to" />
            </Field>
            <Field label={t("reports.comparison")}>
              <SelectField value={draft.comparison} onChange={(e) => { setDraft({ ...draft, comparison: e.target.value }); }} data-testid="report-comparison">
                <option value="">{t("reports.noComparison")}</option>
                {comparisons.map((c) => <option key={c} value={c}>{t(`reports.comparisons.${c}`)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("reports.topN")}>
              <TextField type="number" inputMode="numeric" min={1} value={draft.topN} onChange={(e) => { setDraft({ ...draft, topN: e.target.value }); }} dir="ltr" className="w-24" data-testid="report-top-n" />
            </Field>
            <Field label={t("reports.sortBy")}>
              <div className="flex items-center gap-1">
                <SelectField value={draft.sortField} onChange={(e) => { setDraft({ ...draft, sortField: e.target.value }); }} data-testid="report-sort">
                  <option value="">{t("reports.defaultSort")}</option>
                  {sortable.map((key) => <option key={key} value={key}>{label(key)}</option>)}
                </SelectField>
                <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, sortDescending: !draft.sortDescending }); }} aria-label={draft.sortDescending ? t("reports.descending") : t("reports.ascending")} data-testid="report-sort-direction">
                  {draft.sortDescending ? <ArrowDown aria-hidden="true" /> : <ArrowUp aria-hidden="true" />}
                </Button>
              </div>
            </Field>
          </div>

          <div className="mt-3 grid gap-2 md:grid-cols-3">
            <Zone title={t("reports.rows")} testId="zone-rows" hint={t("reports.rowsHint")}>
              {draft.rows.map((key) => <Chip key={key} onRemove={() => { removeDim(key); }} removeLabel={t("reports.remove", { field: label(key) })} testId={`row-${key}`}>{label(key)}</Chip>)}
            </Zone>
            <Zone title={t("reports.columns")} testId="zone-columns" hint={t("reports.columnsHint")}>
              {draft.columns.map((key) => <Chip key={key} onRemove={() => { removeDim(key); }} removeLabel={t("reports.remove", { field: label(key) })} testId={`column-${key}`}>{label(key)}</Chip>)}
            </Zone>
            <Zone title={t("reports.values")} testId="zone-values" hint={t("reports.valuesHint")}>
              {[
                ...draft.values.map((v) => (
                  <Chip key={v.field} onRemove={() => { removeValue(v.field); }} removeLabel={t("reports.remove", { field: label(v.field) })} testId={`value-${v.field}`}>
                    {label(v.field)}
                    <select
                      value={v.aggregation}
                      onChange={(e) => { setDraft({ ...draft, values: draft.values.map((x) => (x.field === v.field ? { ...x, aggregation: e.target.value } : x)) }); }}
                      aria-label={t("reports.aggregationOf", { field: label(v.field) })}
                      className="ms-1 rounded border border-border bg-surface px-1 py-0.5 text-xs"
                      data-testid={`aggregation-${v.field}`}
                    >
                      {aggregations.map((a) => <option key={a} value={a}>{t(`reports.aggregations.${a}`)}</option>)}
                    </select>
                  </Chip>
                )),
                ...draft.calculated.map((c) => (
                  <Chip key={c.key} onRemove={() => { setDraft({ ...draft, calculated: draft.calculated.filter((x) => x.key !== c.key) }); }} removeLabel={t("reports.remove", { field: label(c.key) })} testId={`calculated-${c.key}`}>
                    <Sigma className="size-3 text-fg-muted" aria-hidden="true" /> {label(c.key)} <code className="text-xs text-fg-muted" dir="ltr">= {c.expression}</code>
                  </Chip>
                )),
                <Button key="add-calculated" variant="ghost" size="sm" onClick={() => { setDialog("calculated"); }} data-testid="add-calculated"><Plus aria-hidden="true" /> {t("reports.calculated")}</Button>,
              ]}
            </Zone>
          </div>

          <div className="mt-2 rounded-md border border-border bg-surface p-2" data-testid="zone-filters">
            <div className="flex items-center justify-between">
              <div className="text-xs font-semibold uppercase tracking-wide text-fg-muted">{t("reports.filters")}</div>
              <Button variant="ghost" size="sm" onClick={addFilter} disabled={dims.length === 0} data-testid="add-filter"><Plus aria-hidden="true" /> {t("reports.addFilter")}</Button>
            </div>
            {draft.filters.map((f, index) => {
              const needsValue = f.op !== "is_null" && f.op !== "not_null";
              return (
                <div key={f.id} className="mt-2 flex flex-wrap items-end gap-2" data-testid={`filter-${index + 1}`}>
                  <Field label={t("reports.field")}>
                    <SelectField value={f.field} onChange={(e) => { setFilter(f.id, { field: e.target.value }); }} data-testid={`filter-${index + 1}-field`}>
                      {dims.map((d) => <option key={d.key} value={d.key}>{localized(d.label)}</option>)}
                    </SelectField>
                  </Field>
                  <Field label={t("reports.operator")}>
                    <SelectField value={f.op} onChange={(e) => { setFilter(f.id, { op: e.target.value }); }} data-testid={`filter-${index + 1}-op`}>
                      {operators.map((op) => <option key={op} value={op}>{t(`reports.operators.${op}`)}</option>)}
                    </SelectField>
                  </Field>
                  {needsValue ? (
                    <Field label={t("reports.value")} description={f.op === "in" || f.op === "not_in" ? t("reports.listHint") : f.op === "between" ? t("reports.betweenHint") : undefined}>
                      <TextField value={f.value} onChange={(e) => { setFilter(f.id, { value: e.target.value }); }} data-testid={`filter-${index + 1}-value`} />
                    </Field>
                  ) : null}
                  <Button variant="secondary" size="sm" onClick={() => { setDraft({ ...draft, filters: draft.filters.filter((x) => x.id !== f.id) }); }} aria-label={t("reports.removeFilter")} data-testid={`filter-${index + 1}-remove`}>
                    <X aria-hidden="true" />
                  </Button>
                </div>
              );
            })}
          </div>

          <div className="mt-4 flex flex-wrap items-center justify-between gap-2">
            <Tabs tabs={reportKinds.map((k) => ({ id: k, label: t(`reports.kinds.${k}`), testId: `kind-${k}` }))} value={draft.kind} onChange={(kind) => { setDraft({ ...draft, kind }); }} />
            <div className="mb-4 flex flex-wrap items-center gap-2">
              <Button variant="secondary" size="sm" onClick={() => { setDialog("explain"); }} disabled={!executed} data-testid="show-query"><Code aria-hidden="true" /> {t("reports.showQuery")}</Button>
              <Button variant="secondary" size="sm" onClick={() => { exportTable.mutate("csv"); }} disabled={!table} loading={exportTable.isPending} data-testid="export-csv"><Download aria-hidden="true" /> CSV</Button>
              <Button variant="secondary" size="sm" onClick={() => { exportTable.mutate("xlsx"); }} disabled={!table} loading={exportTable.isPending} data-testid="export-xlsx"><Download aria-hidden="true" /> Excel</Button>
            </div>
          </div>

          {draft.kind === "chart" ? (
            <div className="mb-3 flex flex-wrap items-end gap-3 rounded-md border border-border bg-surface p-2" data-testid="chart-options">
              <Field label={t("reports.chartType")}>
                <SelectField value={draft.chart.type} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, type: e.target.value } }); }} data-testid="chart-type">
                  {chartTypes.map((c) => <option key={c} value={c}>{t(`reports.chartTypes.${c}`)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("reports.categories")}>
                <SelectField value={draft.chart.categoryField} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, categoryField: e.target.value } }); }} data-testid="chart-category">
                  <option value="">{t("reports.firstDimension")}</option>
                  {chosenDims.map((key) => <option key={key} value={key}>{label(key)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("reports.series")}>
                <SelectField value={draft.chart.seriesField} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, seriesField: e.target.value } }); }} data-testid="chart-series">
                  <option value="">{t("reports.oneSeriesPerMeasure")}</option>
                  {chosenDims.map((key) => <option key={key} value={key}>{label(key)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("reports.measure")}>
                <SelectField value={draft.chart.measure} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, measure: e.target.value } }); }} data-testid="chart-measure">
                  <option value="">{t("reports.firstMeasure")}</option>
                  {draft.values.map((v) => <option key={v.field} value={v.field}>{label(v.field)}</option>)}
                  {draft.calculated.map((c) => <option key={c.key} value={c.key}>{label(c.key)}</option>)}
                </SelectField>
              </Field>
              <label className="flex h-9 items-center gap-2 text-sm">
                <input type="checkbox" checked={draft.chart.stacked} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, stacked: e.target.checked } }); }} data-testid="chart-stacked" />
                {t("reports.stacked")}
              </label>
              <label className="flex h-9 items-center gap-2 text-sm">
                <input type="checkbox" checked={draft.chart.horizontal} onChange={(e) => { setDraft({ ...draft, chart: { ...draft.chart, horizontal: e.target.checked } }); }} data-testid="chart-horizontal" />
                {t("reports.horizontal")}
              </label>
            </div>
          ) : null}

          {executed === null ? (
            <div className="rounded-md border border-dashed border-border p-8 text-center text-sm text-fg-muted" data-testid="report-empty">{t("reports.pressRun")}</div>
          ) : result.isPending ? (
            <div className="p-8 text-center text-sm text-fg-muted" aria-live="polite">{t("common.loading")}</div>
          ) : table && result.data ? (
            <div className={result.isFetching ? "opacity-60 transition-opacity" : "transition-opacity"}>
              {draft.kind === "chart" && chart ? (
                chart.series.length > 0 ? (
                  <div className="mb-3 rounded-md border border-border bg-surface p-3">
                    <ReportChart data={chart} options={chartOptions} format={chartFormat} describedBy={tableId} label={title} />
                  </div>
                ) : (
                  <p className="mb-3 text-sm text-fg-muted" data-testid="chart-needs-fields">{t("reports.chartNeedsFields")}</p>
                )
              ) : null}
              <ResultTable table={table} label={label} canDrill={Boolean(model?.canDrill)} onDrill={(keys, cellTitle) => { setDrill({ keys, title: cellTitle }); }} id={tableId} caption={resultCaption} />
              <p className="mt-2 flex flex-wrap gap-x-3 text-xs text-fg-muted" data-testid="report-footer">
                <span>{t("reports.rowsCount", { count: Number(result.data.rowCount) })}</span>
                {result.data.truncated ? <span className="text-warning">{t("reports.truncated")}</span> : null}
                <span>{t("reports.currencyNote", { currency: result.data.currency })}</span>
                {result.data.projectedUpTo ? <span>{t("reports.projectedUpTo", { when: formatDateTime(result.data.projectedUpTo) })}</span> : null}
                <span>{t("reports.took", { ms: Number(result.data.durationMs) })}</span>
                {company ? <span>{localized(company.legalName)}</span> : null}
              </p>
            </div>
          ) : null}
        </div>
      </div>

      <Dialog open={dialog === "save"} onOpenChange={(isOpen) => { if (!isOpen) { setDialog(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          <form onSubmit={submitSave} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{meta.id && meta.canEdit ? t("reports.saveReport") : t("reports.saveCopy")}</DialogTitle>
            </DialogHeader>
            <FormError message={problem?.message ?? null} />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("reports.nameEn")} required>
                <TextField value={meta.nameEn} onChange={(e) => { setMeta({ ...meta, nameEn: e.target.value }); }} dir="ltr" required data-testid="report-name-en" />
              </Field>
              <Field label={t("reports.nameAr")} required>
                <TextField value={meta.nameAr} onChange={(e) => { setMeta({ ...meta, nameAr: e.target.value }); }} dir="rtl" required data-testid="report-name-ar" />
              </Field>
              <Field label={t("reports.descriptionEn")}>
                <TextField value={meta.descriptionEn} onChange={(e) => { setMeta({ ...meta, descriptionEn: e.target.value }); }} dir="ltr" data-testid="report-description-en" />
              </Field>
              <Field label={t("reports.descriptionAr")}>
                <TextField value={meta.descriptionAr} onChange={(e) => { setMeta({ ...meta, descriptionAr: e.target.value }); }} dir="rtl" data-testid="report-description-ar" />
              </Field>
            </div>
            {meta.isOwner || !meta.id ? (
              <fieldset className="rounded-md border border-border p-3">
                <legend className="px-1 text-sm font-medium">{t("reports.sharing")}</legend>
                <p className="text-xs text-fg-muted">{t("reports.sharingHelp")}</p>
                {meta.shares.map((s, index) => (
                  <div key={index} className="mt-2 flex flex-wrap items-end gap-2" data-testid={`share-${index + 1}`}>
                    <Field label={t("reports.shareWith")}>
                      <SelectField value={s.shareWith} onChange={(e) => { setShare(index, { shareWith: e.target.value, targetId: null }); }} data-testid={`share-${index + 1}-with`}>
                        <option value="tenant">{t("reports.shareTargets.tenant")}</option>
                        <option value="role">{t("reports.shareTargets.role")}</option>
                        <option value="membership">{t("reports.shareTargets.membership")}</option>
                      </SelectField>
                    </Field>
                    {s.shareWith === "role" ? (
                      <Field label={t("reports.role")}>
                        <SelectField value={s.targetId ?? ""} onChange={(e) => { setShare(index, { targetId: e.target.value || null }); }} required data-testid={`share-${index + 1}-target`}>
                          <option value="">—</option>
                          {(roles.data ?? []).map((r) => <option key={r.id} value={r.id}>{localized(r.name)}</option>)}
                        </SelectField>
                      </Field>
                    ) : null}
                    {s.shareWith === "membership" ? (
                      <Field label={t("reports.member")}>
                        <SelectField value={s.targetId ?? ""} onChange={(e) => { setShare(index, { targetId: e.target.value || null }); }} required data-testid={`share-${index + 1}-target`}>
                          <option value="">—</option>
                          {(members.data ?? []).map((m) => <option key={m.membershipId} value={m.membershipId}>{m.displayName} · {m.email}</option>)}
                        </SelectField>
                      </Field>
                    ) : null}
                    <Field label={t("reports.access.title")}>
                      <SelectField value={s.access} onChange={(e) => { setShare(index, { access: e.target.value }); }} data-testid={`share-${index + 1}-access`}>
                        <option value="view">{t("reports.shareAccess.view")}</option>
                        <option value="edit">{t("reports.shareAccess.edit")}</option>
                      </SelectField>
                    </Field>
                    <Button variant="secondary" size="sm" onClick={() => { setMeta({ ...meta, shares: meta.shares.filter((_, i) => i !== index) }); }} aria-label={t("reports.removeShare")} data-testid={`share-${index + 1}-remove`}>
                      <X aria-hidden="true" />
                    </Button>
                  </div>
                ))}
                <Button variant="ghost" size="sm" className="mt-2" onClick={() => { setMeta({ ...meta, shares: [...meta.shares, { shareWith: "tenant", targetId: null, access: "view" }] }); }} data-testid="add-share"><Plus aria-hidden="true" /> {t("reports.addShare")}</Button>
              </fieldset>
            ) : null}
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setDialog(null); }}>{t("common.cancel")}</Button>
              {meta.id && meta.canEdit ? <Button type="button" variant="secondary" onClick={() => { save.mutate(true); }} loading={save.isPending} data-testid="save-as-copy">{t("reports.saveCopy")}</Button> : null}
              <Button type="submit" loading={save.isPending} data-testid="confirm-save-report">{t("common.save")}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={dialog === "calculated"} onOpenChange={(isOpen) => { if (!isOpen) { setDialog(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-xl">
          <form onSubmit={addCalculated} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("reports.calculatedField")}</DialogTitle>
            </DialogHeader>
            <p className="text-sm text-fg-muted">{t("reports.calculatedHelp")}</p>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("reports.key")} required description={t("reports.keyHelp")}>
                <TextField value={calculatedForm.key} onChange={(e) => { setCalculatedForm({ ...calculatedForm, key: e.target.value }); }} dir="ltr" required data-testid="calculated-key" />
              </Field>
              <Field label={t("reports.type")}>
                <SelectField value={calculatedForm.type} onChange={(e) => { setCalculatedForm({ ...calculatedForm, type: e.target.value }); }} data-testid="calculated-type">
                  {calculatedTypes.map((c) => <option key={c} value={c}>{t(`reports.types.${c}`)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("reports.labelEn")} required>
                <TextField value={calculatedForm.labelEn} onChange={(e) => { setCalculatedForm({ ...calculatedForm, labelEn: e.target.value }); }} dir="ltr" required data-testid="calculated-label-en" />
              </Field>
              <Field label={t("reports.labelAr")}>
                <TextField value={calculatedForm.labelAr} onChange={(e) => { setCalculatedForm({ ...calculatedForm, labelAr: e.target.value }); }} dir="rtl" data-testid="calculated-label-ar" />
              </Field>
            </div>
            <Field label={t("reports.expression")} required description={t("reports.expressionHelp", { keys: draft.values.map((v) => v.field).join(", ") || "net_amount" })}>
              <TextField value={calculatedForm.expression} onChange={(e) => { setCalculatedForm({ ...calculatedForm, expression: e.target.value }); }} dir="ltr" required className="font-mono" data-testid="calculated-expression" />
            </Field>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setDialog(null); }}>{t("common.cancel")}</Button>
              <Button type="submit" data-testid="confirm-calculated">{t("common.add")}</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={dialog === "explain"} onOpenChange={(isOpen) => { if (!isOpen) { setDialog(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("reports.showQuery")}</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-fg-muted">{t("reports.queryHelp")}</p>
          {explain.data ? (
            <div className="flex flex-col gap-3" data-testid="query-explanation">
              <pre dir="ltr" className="max-h-96 overflow-auto rounded-md bg-surface-sunken p-3 text-xs leading-relaxed whitespace-pre-wrap" data-testid="query-sql">{explain.data.sqlWithValues}</pre>
              <dl className="grid gap-x-4 gap-y-1 text-xs sm:grid-cols-[auto_1fr]">
                <dt className="text-fg-muted">{t("reports.joins")}</dt>
                <dd dir="ltr">{explain.data.joins.length > 0 ? explain.data.joins.join(", ") : "—"}</dd>
                <dt className="text-fg-muted">{t("reports.scope")}</dt>
                <dd>{explain.data.scope.companies.length === 0 && explain.data.scope.branches.length === 0 && explain.data.scope.warehouses.length === 0 ? t("reports.scopeAll") : t("reports.scopeLimited", { companies: explain.data.scope.companies.length, branches: explain.data.scope.branches.length, warehouses: explain.data.scope.warehouses.length })}</dd>
              </dl>
            </div>
          ) : explain.error ? <FormError message={toFormProblem(explain.error, t("reports.runFailed")).message} /> : <p className="text-sm text-fg-muted">{t("common.loading")}</p>}
        </DialogContent>
      </Dialog>

      <Dialog open={drill !== null} onOpenChange={(isOpen) => { if (!isOpen) { setDrill(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("reports.drillTitle")}</DialogTitle>
          </DialogHeader>
          <p className="text-sm text-fg-muted" dir="auto" data-testid="drill-subject">{drill?.title}</p>
          {drillQuery.data ? (
            <div className="max-h-[32rem] overflow-auto">
              <Table data-testid="drill-table">
                <TableHeader>
                  <TableRow>
                    {drillQuery.data.columns.map((c) => <TableHead key={c.key} className={c.type === "money" || c.type === "quantity" || c.type === "number" ? "text-end" : undefined}>{localized(c.label)}</TableHead>)}
                    <TableHead>{t("reports.document")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(drillQuery.data.rows as ResultRow[]).map((row, index) => {
                    const type = cellText(row.document_type);
                    const id = cellText(row.document_id);
                    const route = type && id ? recordRoute(type, id) : null;
                    return (
                      <TableRow key={index} data-testid={`drill-row-${index + 1}`}>
                        {drillQuery.data.columns.map((c) => (c.type === "money" || c.type === "quantity" || c.type === "number"
                          ? <TableNumberCell key={c.key} dir="ltr">{drillText(row[c.key], c.type)}</TableNumberCell>
                          : <TableCell key={c.key} dir="auto">{drillText(row[c.key], c.type)}</TableCell>))}
                        <TableCell>
                          {route ? (
                            <Button variant="link" size="sm" onClick={() => { void navigate(route); }} data-testid={`drill-open-${index + 1}`}>{cellText(row.document_number) || t("common.open")}</Button>
                          ) : <span dir="ltr">{cellText(row.document_number)}</span>}
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              <p className="mt-2 text-xs text-fg-muted">{t("reports.rowsCount", { count: Number(drillQuery.data.rowCount) })}{drillQuery.data.truncated ? ` · ${t("reports.truncated")}` : ""}</p>
            </div>
          ) : drillQuery.error ? <FormError message={toFormProblem(drillQuery.error, t("reports.runFailed")).message} /> : <p className="text-sm text-fg-muted">{t("common.loading")}</p>}
        </DialogContent>
      </Dialog>
    </div>
  );
}
