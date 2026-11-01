import { cn, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber, localized } from "../../lib/format";
import { ReportChart } from "../reports/ReportChart";
import { cellText, chartData, measureText, type ChartOptions, type QueryResult, type ResultRow } from "../reports/shared";
import { StatementTable, type Statement } from "../statements/shared";

export type Dashboard = components["schemas"]["DashboardSummary"];
export type DashboardWidget = components["schemas"]["DashboardWidgetSummary"];
export type WidgetDefinition = components["schemas"]["WidgetDefinition"];
export type DashboardRun = components["schemas"]["DashboardResult"];
export type WidgetResult = components["schemas"]["WidgetResult"];

/** The relative periods a dashboard, a widget or a schedule may name; a widget may also follow the dashboard. */
export const periods = ["today", "yesterday", "this_week", "last_week", "this_month", "last_month", "this_quarter", "last_quarter", "this_year", "last_year", "last_7_days", "last_30_days", "year_to_date"] as const;
export const widgetSources = ["report", "statement"] as const;
export const widgetViz = ["kpi", "bar", "line", "area", "table"] as const;
export const scheduleFormats = ["csv", "xlsx", "pdf"] as const;

export function useDashboards(companyId: string, homeOnly = false) {
  return useQuery({
    queryKey: ["dashboards", companyId, homeOnly],
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/dashboards", { params: { query: { ...(companyId ? { companyId } : {}), ...(homeOnly ? { home: true } : {}) } } })),
  });
}

export function useDashboardRun(dashboardId: string, companyId: string, period: string, from: string, to: string, refreshSeconds: number | string | null | undefined) {
  return useQuery({
    queryKey: ["dashboard-run", dashboardId, companyId, period, from, to],
    enabled: Boolean(dashboardId && companyId) && (period !== "custom" || Boolean(from && to)),
    retry: false,
    refetchInterval: refreshSeconds ? Number(refreshSeconds) * 1000 : false,
    queryFn: async () => unwrap(await api.POST("/api/v1/reporting/dashboards/{dashboardId}/run", {
      params: { path: { dashboardId } },
      body: { companyId, period, ...(period === "custom" ? { from, to } : {}) },
    })),
  });
}

/** A figure as a KPI shows it: money and numbers with their format, percentages with the sign. */
export function kpiText(value: number | string | null | undefined, type: string): string {
  if (value === null || value === undefined || value === "") {
    return "—";
  }
  const n = Number(value);
  if (type === "percent") {
    return `${formatNumber(n, { maximumFractionDigits: 1 })} %`;
  }
  return formatNumber(n, Math.abs(n) >= 1_000_000 ? { notation: "compact", maximumFractionDigits: 1 } : { minimumFractionDigits: type === "money" ? 2 : 0, maximumFractionDigits: 2 });
}

function Kpi({ widget }: { widget: WidgetResult }) {
  const { t } = useTranslation();
  const kpi = widget.kpi;
  if (!kpi) {
    return null;
  }
  const value = kpi.value === null ? null : Number(kpi.value);
  const prior = kpi.prior === null ? null : Number(kpi.prior);
  const change = value !== null && prior !== null && prior !== 0 ? (value - prior) / Math.abs(prior) : null;
  return (
    <div className="flex flex-col gap-1">
      <div className="text-3xl font-semibold tabular-nums" dir="ltr" data-testid={`kpi-${widget.code}-value`}>{kpiText(kpi.value, kpi.type)}</div>
      <div className="text-xs text-fg-muted">
        {kpi.type === "money" ? kpi.currency : localized(kpi.label)}
        {change !== null ? (
          <span className={cn("ms-2 font-medium", change >= 0 ? "text-success" : "text-danger")} data-testid={`kpi-${widget.code}-change`}>
            {change >= 0 ? "▲" : "▼"} {formatNumber(Math.round(Math.abs(change) * 100))}% {t("dashboards.vsPrior")}
          </span>
        ) : prior !== null ? <span className="ms-2">{t("dashboards.prior")}: {kpiText(prior, kpi.type)}</span> : null}
      </div>
    </div>
  );
}

/** A query result as a flat table: every field a column, dimensions as text, measures as numbers. */
function ResultTable({ result, code }: { result: QueryResult; code: string }) {
  const { t } = useTranslation();
  const fields = result.fields;
  const rows = result.rows as ResultRow[];
  return (
    <div className="max-h-80 overflow-auto">
      <Table data-testid={`widget-${code}-table`}>
        <caption className="sr-only">{t("dashboards.tableOf", { rows: result.rowCount })}</caption>
        <TableHeader>
          <TableRow>
            {fields.map((f) => <TableHead key={f.key} className={f.role === "measure" ? "text-end" : ""}>{localized(f.label)}</TableHead>)}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row, i) => (
            <TableRow key={fields.map((f) => cellText(row[f.key])).join("|") + String(i)}>
              {fields.map((f) => (
                f.role === "measure"
                  ? <TableCell key={f.key} className="text-end tabular-nums" dir="ltr">{measureText(row[f.key], f.type)}</TableCell>
                  : <TableCell key={f.key} dir="auto">{cellText(row[f.key]) || t("reports.blank")}</TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

function Chart({ widget }: { widget: WidgetResult }) {
  const { t } = useTranslation();
  const describedBy = useId();
  const result = widget.query;
  const options: ChartOptions = useMemo(() => ({
    type: widget.viz === "table" ? "bar" : widget.viz,
    stacked: widget.chart?.stacked ?? false,
    horizontal: widget.chart?.horizontal ?? false,
    categoryField: widget.chart?.categoryField ?? null,
    seriesField: widget.chart?.seriesField ?? null,
    measures: widget.chart?.measures ?? null,
  }), [widget.chart, widget.viz]);
  const dims = useMemo(() => (result ? result.fields.filter((f) => f.role === "dimension").map((f) => f.key) : []), [result]);
  const data = useMemo(() => (result ? chartData(result, options, dims, { other: t("reports.other"), blank: t("reports.blank") }) : null), [result, options, dims, t]);
  if (!result || !data) {
    return null;
  }
  if (data.categories.length === 0) {
    return <p className="text-sm text-fg-muted">{t("dashboards.noData")}</p>;
  }
  const type = data.measure?.type;
  return (
    <>
      <ReportChart data={data} options={options} format={(v) => measureText(v, type)} describedBy={describedBy} label={localized(widget.title)} />
      <table id={describedBy} className="sr-only">
        <caption>{localized(widget.title)}</caption>
        <thead><tr><th>{t("dashboards.category")}</th>{data.series.map((s) => <th key={s.name}>{s.name}</th>)}</tr></thead>
        <tbody>{data.categories.map((c, i) => <tr key={c}><th>{c}</th>{data.series.map((s) => <td key={s.name}>{measureText(s.values[i], type)}</td>)}</tr>)}</tbody>
      </table>
    </>
  );
}

function StatementWidget({ statement, code }: { statement: Statement; code: string }) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const toggle = (row: string): void => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(row)) {
        next.delete(row);
      } else {
        next.add(row);
      }
      return next;
    });
  };
  return (
    <div className="max-h-96 overflow-auto">
      <StatementTable statement={statement} expanded={expanded} onToggle={toggle} testId={`widget-${code}-statement`} />
    </div>
  );
}

const widths: Record<number, string> = { 1: "lg:col-span-1", 2: "lg:col-span-2", 3: "lg:col-span-3", 4: "lg:col-span-4" };

/** One widget as a card: its title and window, then the KPI, the chart, the table or the statement; an error in place when its source failed. */
export function WidgetCard({ widget }: { widget: WidgetResult }) {
  const { t } = useTranslation();
  return (
    <section className={cn("flex min-w-0 flex-col gap-2 rounded-lg border border-border bg-surface p-4 shadow-sm", widths[Number(widget.width)] ?? "lg:col-span-2")} data-testid={`widget-${widget.code}`} aria-labelledby={`widget-${widget.code}-title`}>
      <div className="flex items-baseline justify-between gap-2">
        <h3 id={`widget-${widget.code}-title`} className="text-sm font-semibold" dir="auto">{localized(widget.title)}</h3>
        <span className="text-[11px] text-fg-subtle" dir="ltr">{widget.from} → {widget.to}</span>
      </div>
      {widget.errorCode
        ? <p className="text-sm text-danger" role="alert" data-testid={`widget-${widget.code}-error`}>{widget.errorMessage ?? widget.errorCode}</p>
        : widget.viz === "kpi"
          ? <Kpi widget={widget} />
          : widget.statement
            ? <StatementWidget statement={widget.statement} code={widget.code} />
            : widget.query
              ? widget.viz === "table" ? <ResultTable result={widget.query} code={widget.code} /> : <Chart widget={widget} />
              : <p className="text-sm text-fg-muted">{t("dashboards.noData")}</p>}
    </section>
  );
}

/** The widgets of a run laid out on four columns, each as wide as it was designed. */
export function DashboardGrid({ run }: { run: DashboardRun }) {
  const { t } = useTranslation();
  if (run.widgets.length === 0) {
    return <p className="text-sm text-fg-muted">{t("dashboards.empty")}</p>;
  }
  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="dashboard-grid">
      {run.widgets.map((w) => <WidgetCard key={w.widgetId} widget={w} />)}
    </div>
  );
}
