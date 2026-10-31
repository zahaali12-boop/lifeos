import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatNumber, localized } from "../../lib/format";

export type ReportSummary = components["schemas"]["ReportSummary"];
export type QueryDefinition = components["schemas"]["QueryDefinition"];
export type QueryResult = components["schemas"]["QueryResult"];
export type ResultField = components["schemas"]["ResultField"];
export type CatalogueModel = components["schemas"]["ReportCatalogueModel"];
export type CatalogueField = components["schemas"]["ReportCatalogueField"];
export type ChartOptions = components["schemas"]["ChartOptions"];
export type ShareRequest = components["schemas"]["ShareRequest"];
export type DrillResult = components["schemas"]["DrillResult"];

/** A result row as the API sends it: `<dim>` carries the label (bilingual or plain), `<dim>_key` the key, measures are numbers. */
export type ResultRow = Record<string, unknown>;

export const reportKinds = ["table", "pivot", "chart"] as const;
export const chartTypes = ["bar", "line", "area"] as const;
export const comparisons = ["prior_period", "prior_year"] as const;
export const aggregations = ["sum", "count", "count_distinct", "avg", "min", "max"] as const;
export const operators = ["eq", "ne", "in", "not_in", "gt", "gte", "lt", "lte", "between", "contains", "starts_with", "is_null", "not_null"] as const;
export const calculatedTypes = ["number", "money", "percent", "quantity"] as const;

/** The models the signed-in member may read, each with its fields in both languages (ADR-0034). */
export function useCatalogue() {
  return useQuery({ queryKey: ["report-models"], queryFn: async () => unwrap(await api.GET("/api/v1/reporting/models")) });
}

/** The saved reports the member owns or that are shared with them. */
export function useReports() {
  return useQuery({ queryKey: ["reports"], queryFn: async () => unwrap(await api.GET("/api/v1/reporting/reports")) });
}

/** A dimension cell's text: the label in the reader's language when it is bilingual, the value itself otherwise. */
export function cellText(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (typeof value === "number" || typeof value === "boolean" || typeof value === "bigint") {
    return String(value);
  }
  if (value && typeof value === "object") {
    return localized(value as Record<string, string>);
  }
  return "";
}

export function numberOf(value: unknown): number | null {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** A measure as the screen prints it: money to two places, quantities to three, percentages to one, the rest to two at most. */
export function measureText(value: unknown, type: string | undefined): string {
  const n = numberOf(value);
  if (n === null) {
    return "—";
  }
  switch (type) {
    case "money":
      return formatNumber(n, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    case "quantity":
      return formatNumber(n, { maximumFractionDigits: 3 });
    case "percent":
      return `${formatNumber(n, { maximumFractionDigits: 1 })} %`;
    default:
      return formatNumber(n, { maximumFractionDigits: 2 });
  }
}

/** A drill cell as the screen prints it, by the column's type. */
export function drillText(value: unknown, type: string): string {
  if (value === null || value === undefined) {
    return "";
  }
  if (type === "money" || type === "quantity" || type === "number" || type === "percent") {
    return measureText(value, type);
  }
  if (type === "date") {
    return formatDate(cellText(value));
  }
  return cellText(value);
}

export interface PivotColumn {
  key: string;
  label: string;
  keys: Record<string, string>;
}

export interface PivotRow {
  key: string;
  cells: string[];
  keys: Record<string, string>;
  /** values[columnKey][measureKey] */
  values: Record<string, Record<string, number | null>>;
}

export interface Pivot {
  rowDims: string[];
  columnDims: string[];
  measures: ResultField[];
  columns: PivotColumn[];
  rows: PivotRow[];
  totals: Record<string, Record<string, number | null>>;
}

const separator = "\u0001";

function keyOf(value: unknown): string {
  return cellText(value);
}

/** Whether a measure's column total means something: sums and counts add up, averages and extremes do not, nor do calculated fields. */
function totalled(measure: ResultField): boolean {
  return !measure.isCalculated && (measure.aggregation === "sum" || measure.aggregation === "count");
}

/**
 * The client-side pivot: the flat GROUP BY rows the API returns, laid out with the row dimensions down the side and the
 * column dimensions across the top, one cell per measure, with a total per column where it adds up. The order of
 * rows and columns is the order the API sorted them in.
 */
export function pivot(result: QueryResult, rowDims: string[], columnDims: string[]): Pivot {
  const measures = result.fields.filter((f) => f.role === "measure");
  const rows = new Map<string, PivotRow>();
  const columns = new Map<string, PivotColumn>();
  for (const raw of result.rows as ResultRow[]) {
    const rowKeys = Object.fromEntries(rowDims.map((d) => [d, keyOf(raw[`${d}_key`])]));
    const columnKeys = Object.fromEntries(columnDims.map((d) => [d, keyOf(raw[`${d}_key`])]));
    const rowKey = rowDims.map((d) => rowKeys[d] ?? "").join(separator);
    const columnKey = columnDims.map((d) => columnKeys[d] ?? "").join(separator);
    if (!columns.has(columnKey)) {
      columns.set(columnKey, { key: columnKey, label: columnDims.map((d) => cellText(raw[d])).join(" · "), keys: columnKeys });
    }
    let row = rows.get(rowKey);
    if (!row) {
      row = { key: rowKey, cells: rowDims.map((d) => cellText(raw[d])), keys: rowKeys, values: {} };
      rows.set(rowKey, row);
    }
    const cell = row.values[columnKey] ?? {};
    row.values[columnKey] = cell;
    for (const m of measures) {
      const n = numberOf(raw[m.key]);
      cell[m.key] = n === null ? (cell[m.key] ?? null) : (cell[m.key] ?? 0) + n;
    }
  }
  const totals: Record<string, Record<string, number | null>> = {};
  for (const column of columns.values()) {
    const total: Record<string, number | null> = {};
    for (const m of measures) {
      let sum: number | null = null;
      if (totalled(m)) {
        for (const row of rows.values()) {
          const v = row.values[column.key]?.[m.key];
          if (v !== null && v !== undefined) {
            sum = (sum ?? 0) + v;
          }
        }
      }
      total[m.key] = sum;
    }
    totals[column.key] = total;
  }
  return { rowDims, columnDims, measures, columns: [...columns.values()], rows: [...rows.values()], totals };
}

export interface ChartSeries {
  name: string;
  values: (number | null)[];
}

export interface ChartData {
  categories: string[];
  series: ChartSeries[];
  /** The measure plotted, for the number format; undefined when nothing can be plotted. */
  measure: ResultField | undefined;
}

/** Keeps the first `max` entries by total and folds the rest into one bucket, so a chart never has more series or categories than a reader can tell apart. */
function fold(names: string[], totalOf: (name: string) => number, max: number, otherLabel: string): { kept: string[]; folded: Set<string> } {
  if (names.length <= max) {
    return { kept: names, folded: new Set() };
  }
  const ranked = [...names].sort((a, b) => Math.abs(totalOf(b)) - Math.abs(totalOf(a))).slice(0, max - 1);
  const keep = new Set(ranked);
  const folded = new Set(names.filter((n) => !keep.has(n)));
  return { kept: [...names.filter((n) => keep.has(n)), otherLabel], folded };
}

/**
 * The series of a chart from the flat rows: one category per value of the category dimension, one series per value
 * of the series dimension (one measure) or one series per chosen measure. Past forty categories or seven series the
 * tail is folded into "Other"; colours follow the entity, never its rank.
 */
export function chartData(result: QueryResult, options: ChartOptions, dims: string[], otherLabel: string): ChartData {
  const measures = result.fields.filter((f) => f.role === "measure");
  const category = options.categoryField && dims.includes(options.categoryField) ? options.categoryField : dims[0];
  const chosen = (options.measures ?? []).filter((key) => measures.some((m) => m.key === key));
  const plotted = chosen.length > 0 ? chosen : measures.slice(0, 1).map((m) => m.key);
  const first = measures.find((m) => m.key === plotted[0]);
  if (!category || !first) {
    return { categories: [], series: [], measure: undefined };
  }
  const seriesDim = options.seriesField && options.seriesField !== category && dims.includes(options.seriesField) ? options.seriesField : null;
  const categories: string[] = [];
  const seriesNames: string[] = [];
  const cells = new Map<string, number>();
  const categoryTotals = new Map<string, number>();
  const seriesTotals = new Map<string, number>();
  const add = (c: string, s: string, n: number | null): void => {
    if (!categories.includes(c)) {
      categories.push(c);
    }
    if (!seriesNames.includes(s)) {
      seriesNames.push(s);
    }
    if (n !== null) {
      const key = c + separator + s;
      cells.set(key, (cells.get(key) ?? 0) + n);
      categoryTotals.set(c, (categoryTotals.get(c) ?? 0) + n);
      seriesTotals.set(s, (seriesTotals.get(s) ?? 0) + n);
    }
  };
  for (const raw of result.rows as ResultRow[]) {
    const c = cellText(raw[category]) || "—";
    if (seriesDim) {
      add(c, cellText(raw[seriesDim]) || "—", numberOf(raw[first.key]));
    } else {
      for (const key of plotted) {
        const m = measures.find((x) => x.key === key);
        add(c, m ? localized(m.label) : key, numberOf(raw[key]));
      }
    }
  }
  const foldedCategories = fold(categories, (c) => categoryTotals.get(c) ?? 0, 40, otherLabel);
  const foldedSeries = fold(seriesNames, (s) => seriesTotals.get(s) ?? 0, 7, otherLabel);
  const bucket = (name: string, folded: Set<string>): string => (folded.has(name) ? otherLabel : name);
  const values = new Map<string, number>();
  for (const [key, n] of cells) {
    const [c, s] = key.split(separator) as [string, string];
    const k = bucket(c, foldedCategories.folded) + separator + bucket(s, foldedSeries.folded);
    values.set(k, (values.get(k) ?? 0) + n);
  }
  return {
    categories: foldedCategories.kept,
    series: foldedSeries.kept.map((s) => ({ name: s, values: foldedCategories.kept.map((c) => values.get(c + separator + s) ?? null) })),
    measure: first,
  };
}
