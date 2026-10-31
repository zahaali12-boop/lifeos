import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { ColumnDef, VisibilityState } from "@tanstack/react-table";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid, type GridPreset } from "../../grid/DataGrid";
import { sumDecimals } from "../../lib/decimal";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { today } from "../accounting/shared";
import { Field, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";

type Line = components["schemas"]["SalesLineRow"];

const periods = ["ytd", "month", "quarter", "year", "last12", "custom"] as const;
type Period = (typeof periods)[number];
const statusChoices = ["posted", "draft", "all"] as const;
type StatusChoice = (typeof statusChoices)[number];
const statusQuery: Record<StatusChoice, string> = { posted: "posted", draft: "draft", all: "posted,draft,cancelled,reversed" };

/** The first visible columns; the rest wait in the column chooser and the group-by menu. */
const shownByDefault = new Set(["invoiceNumber", "postingDate", "customer", "item", "quantity", "netAmount", "taxAmount", "grossAmount"]);

const iso = (date: Date): string => date.toISOString().slice(0, 10);

/** The dates a ready-made period covers, counted from a day; a custom period keeps whatever is typed. */
function periodRange(period: Period, day: string): { from: string; to: string } | null {
  const [y, m] = day.split("-").map(Number);
  const year = y ?? 2000;
  const month = m ?? 1;
  switch (period) {
    case "ytd":
      return { from: `${String(year)}-01-01`, to: day };
    case "month":
      return { from: iso(new Date(Date.UTC(year, month - 1, 1))), to: iso(new Date(Date.UTC(year, month, 0))) };
    case "quarter": {
      const first = Math.floor((month - 1) / 3) * 3;
      return { from: iso(new Date(Date.UTC(year, first, 1))), to: iso(new Date(Date.UTC(year, first + 3, 0))) };
    }
    case "year":
      return { from: `${String(year)}-01-01`, to: `${String(year)}-12-31` };
    case "last12":
      return { from: iso(new Date(Date.UTC(year, month - 12, 1))), to: day };
    default:
      return null;
  }
}

const coded = (code: string | null | undefined, name: Record<string, string> | null | undefined): string => (code ? (name ? `${code} · ${localized(name)}` : code) : "");

/**
 * Sales analysis (founder programme item 1f): every line of the company's invoices and credit notes in a period, with
 * thirty-odd fields to group, filter and sum by in the grid (customer, item, rep, month, category, branch, dimensions
 * and more) and ready-made groupings. Credit notes count against. A line opens its invoice.
 */
export function SalesAnalysisPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [period, setPeriod] = useState<Period>("ytd");
  const [range, setRange] = useState(() => periodRange("ytd", today()) ?? { from: today(), to: today() });
  const [status, setStatus] = useState<StatusChoice>("posted");

  const choosePeriod = (next: Period): void => {
    setPeriod(next);
    const dates = periodRange(next, today());
    if (dates) {
      setRange(dates);
    }
  };
  const setDate = (key: "from" | "to", value: string): void => {
    setPeriod("custom");
    setRange((prev) => ({ ...prev, [key]: value }));
  };

  const report = useQuery({
    queryKey: ["sales-lines", companyId, range.from, range.to, status],
    enabled: Boolean(companyId) && Boolean(range.from) && Boolean(range.to),
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/sales-lines", { params: { query: { companyId, from: range.from, to: range.to, status: statusQuery[status] } } })),
  });
  const data = report.data;
  const rows = useMemo(() => data?.rows ?? [], [data]);
  const currency = data?.functionalCurrency ?? "";

  const columns = useMemo<ColumnDef<Line, unknown>[]>(() => {
    const h = (key: string): string => t(`sales.analysis.columns.${key}`);
    const text = (id: string, accessor: (line: Line) => string, options: Partial<ColumnDef<Line, unknown>> = {}): ColumnDef<Line, unknown> => ({ id, header: h(id), accessorFn: accessor, ...options });
    const ltr = (id: string, accessor: (line: Line) => string): ColumnDef<Line, unknown> => text(id, accessor, { cell: ({ getValue }) => <span dir="ltr">{getValue<string>()}</span> });
    const date = (id: string, accessor: (line: Line) => string | null): ColumnDef<Line, unknown> => ({ id, header: h(id), accessorFn: (line) => accessor(line) ?? "", cell: ({ getValue }) => <span dir="ltr">{formatDate(getValue<string>() || null)}</span>, meta: { exportType: "date" } });
    const number = (id: string, accessor: (line: Line) => number, format: (value: string) => string, sum: boolean): ColumnDef<Line, unknown> => ({
      id,
      header: h(id),
      accessorFn: accessor,
      cell: ({ getValue }) => <span dir="ltr" className="tabular">{format(String(getValue<number>()))}</span>,
      meta: { exportType: "number", ...(sum ? { aggregate: "sum" as const, formatAggregate: format } : {}) },
    });
    const money = (value: string): string => formatMoney(value, currency);
    const quantity = (value: string): string => formatNumber(value, { maximumFractionDigits: 3 });
    const percent = (value: string): string => formatNumber(value, { maximumFractionDigits: 2 });
    return [
      ltr("invoiceNumber", (l) => l.invoiceNumber),
      text("kind", (l) => t(`sales.invoicing.kinds.${l.kind}`, { defaultValue: l.kind })),
      text("status", (l) => t(`sales.statuses.${l.status}`, { defaultValue: l.status })),
      date("documentDate", (l) => l.documentDate),
      date("postingDate", (l) => l.postingDate),
      date("dueDate", (l) => l.dueDate),
      ltr("year", (l) => l.year),
      ltr("quarter", (l) => l.quarter),
      ltr("month", (l) => l.month),
      text("customer", (l) => coded(l.customerCode, l.customerName)),
      text("customerGroup", (l) => l.customerGroup ?? ""),
      text("salesRep", (l) => coded(l.salesRep, l.salesRepName)),
      text("branch", (l) => l.branch ?? ""),
      text("paymentTerms", (l) => l.paymentTerms ?? ""),
      text("priceList", (l) => l.priceList ?? ""),
      ltr("currency", (l) => l.currency),
      number("exchangeRate", (l) => Number(l.exchangeRate), (v) => formatNumber(v, { maximumFractionDigits: 6 }), false),
      number("lineNo", (l) => Number(l.lineNo), (v) => formatNumber(v), false),
      text("lineKind", (l) => t(`sales.invoicing.lineKinds.${l.lineKind}`, { defaultValue: l.lineKind })),
      text("item", (l) => coded(l.itemCode, l.itemName)),
      text("itemType", (l) => (l.itemType ? t(`inventory.items.types.${l.itemType}`, { defaultValue: l.itemType }) : "")),
      text("category", (l) => l.category ?? ""),
      text("brand", (l) => l.brand ?? ""),
      text("uom", (l) => l.uom ?? ""),
      text("description", (l) => l.description ?? ""),
      text("taxCode", (l) => l.taxCode ?? ""),
      number("taxRatePct", (l) => Number(l.taxRatePct), percent, false),
      text("costCentre", (l) => l.costCentre ?? ""),
      text("department", (l) => l.department ?? ""),
      text("project", (l) => l.project ?? ""),
      number("quantity", (l) => Number(l.quantity), quantity, true),
      number("unitPrice", (l) => Number(l.unitPrice), money, false),
      number("discountPct", (l) => Number(l.discountPct), percent, false),
      number("netAmount", (l) => Number(l.netAmount), money, true),
      number("taxAmount", (l) => Number(l.taxAmount), money, true),
      number("grossAmount", (l) => Number(l.grossAmount), money, true),
      number("netAmountFc", (l) => Number(l.netAmountFc), (v) => formatNumber(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), true),
    ];
  }, [currency, t]);

  const initialColumns = useMemo<VisibilityState>(() => Object.fromEntries(columns.map((column) => [column.id ?? "", shownByDefault.has(column.id ?? "")])), [columns]);
  const presets = useMemo<GridPreset[]>(() => [
    { id: "customer", label: t("sales.analysis.presets.customer"), grouping: ["customer"] },
    { id: "item", label: t("sales.analysis.presets.item"), grouping: ["item"] },
    { id: "salesRep", label: t("sales.analysis.presets.salesRep"), grouping: ["salesRep"] },
    { id: "month", label: t("sales.analysis.presets.month"), grouping: ["month"] },
    { id: "customerItem", label: t("sales.analysis.presets.customerItem"), grouping: ["customer", "item"] },
    { id: "category", label: t("sales.analysis.presets.category"), grouping: ["category"] },
    { id: "customerGroup", label: t("sales.analysis.presets.customerGroup"), grouping: ["customerGroup"] },
    { id: "branch", label: t("sales.analysis.presets.branch"), grouping: ["branch"] },
  ], [t]);

  const viewExtras = useCallback(() => ({ period, from: range.from, to: range.to, status }), [period, range.from, range.to, status]);
  const applyExtras = useCallback((extras: Record<string, unknown> | undefined) => {
    if (!extras) {
      return;
    }
    const saved = typeof extras.period === "string" && (periods as readonly string[]).includes(extras.period) ? (extras.period as Period) : "custom";
    const dates = saved === "custom" ? (typeof extras.from === "string" && typeof extras.to === "string" ? { from: extras.from, to: extras.to } : null) : periodRange(saved, today());
    if (dates) {
      setPeriod(saved);
      setRange(dates);
    }
    if (typeof extras.status === "string" && (statusChoices as readonly string[]).includes(extras.status)) {
      setStatus(extras.status as StatusChoice);
    }
  }, []);

  const totals = useMemo(() => ({
    netAmount: sumDecimals(rows.map((r) => r.netAmount)),
    taxAmount: sumDecimals(rows.map((r) => r.taxAmount)),
    grossAmount: sumDecimals(rows.map((r) => r.grossAmount)),
    invoices: new Set(rows.map((r) => r.invoiceId)).size,
  }), [rows]);

  return (
    <>
      <PageHeader title={t("nav.salesAnalysis")} description={t("sales.analysis.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("sales.analysis.period")}>
          <SelectField value={period} onChange={(e) => { choosePeriod(e.target.value as Period); }} data-testid="analysis-period">
            {periods.map((p) => (
              <option key={p} value={p}>{t(`sales.analysis.periods.${p}`)}</option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.analysis.from")}>
          <TextField type="date" value={range.from} onChange={(e) => { setDate("from", e.target.value); }} dir="ltr" data-testid="analysis-from" />
        </Field>
        <Field label={t("sales.analysis.to")}>
          <TextField type="date" value={range.to} onChange={(e) => { setDate("to", e.target.value); }} dir="ltr" data-testid="analysis-to" />
        </Field>
        <Field label={t("sales.analysis.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value as StatusChoice); }} data-testid="analysis-status">
            {statusChoices.map((s) => (
              <option key={s} value={s}>{t(`sales.analysis.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      {data ? (
        <dl className="mb-4 grid gap-3 sm:grid-cols-5" data-testid="analysis-totals">
          {(["netAmount", "taxAmount", "grossAmount"] as const).map((key) => (
            <div key={key} className="rounded-lg border border-border bg-surface p-3">
              <dt className="text-xs uppercase tracking-wide text-fg-muted">{t(`sales.analysis.columns.${key}`)}</dt>
              <dd className="tabular text-lg font-semibold" dir="ltr" data-testid={`analysis-total-${key}`}>{formatMoney(totals[key], data.functionalCurrency)}</dd>
            </div>
          ))}
          <div className="rounded-lg border border-border bg-surface p-3">
            <dt className="text-xs uppercase tracking-wide text-fg-muted">{t("sales.analysis.invoices")}</dt>
            <dd className="tabular text-lg font-semibold" dir="ltr" data-testid="analysis-total-invoices">{formatNumber(totals.invoices)}</dd>
          </div>
          <div className="rounded-lg border border-border bg-surface p-3">
            <dt className="text-xs uppercase tracking-wide text-fg-muted">{t("sales.analysis.lines")}</dt>
            <dd className="tabular text-lg font-semibold" dir="ltr" data-testid="analysis-total-lines">{formatNumber(rows.length)}</dd>
          </div>
        </dl>
      ) : null}
      {data?.truncated ? <p className="mb-3 text-sm text-warning" role="alert" data-testid="analysis-truncated">{t("sales.analysis.truncated", { count: rows.length })}</p> : null}
      <DataGrid<Line>
        label="nav.salesAnalysis"
        entityType="sales_lines"
        documentType="sales_invoice"
        columns={columns}
        data={rows}
        rowKey={(row) => row.lineId}
        loading={report.isPending && Boolean(companyId)}
        emptyTitle={t("sales.analysis.empty")}
        emptyDescription={t("sales.analysis.emptyDescription")}
        initialColumns={initialColumns}
        presets={presets}
        viewExtras={viewExtras}
        onApplyView={applyExtras}
        onOpen={(row) => { void navigate({ to: "/sales/invoices", search: { open: row.invoiceId } }); }}
        height={640}
      />
    </>
  );
}
