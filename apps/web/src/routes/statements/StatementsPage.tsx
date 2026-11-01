import { Button, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Download, PencilRuler, Play, Plus, X } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { saveFile } from "../../lib/download";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { cellText, layoutKey, layoutRef, StatementTable, statementExport, useLayouts, type Statement, type StatementAccountLine, type StatementColumn, type StatementRow } from "./shared";

type Dimension = components["schemas"]["DimensionSummary"];

interface DimensionFilter {
  id: number;
  dimensionCode: string;
  valueId: string;
}

/** One dimension filter: a dimension and one of its values, the values fetched for whichever dimension is chosen. */
function DimensionFilterRow({ dimensions, taken, filter, onChange, onRemove }: { dimensions: Dimension[]; taken: Set<string>; filter: DimensionFilter; onChange: (next: DimensionFilter) => void; onRemove: () => void }) {
  const { t } = useTranslation();
  const dimensionId = dimensions.find((d) => d.code === filter.dimensionCode)?.id;
  const values = useQuery({
    queryKey: ["dimension-values", dimensionId],
    enabled: Boolean(dimensionId),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: dimensionId ?? "" } } })),
  });
  return (
    <div className="flex items-end gap-2" data-testid="statement-filter-row">
      <Field label={t("accounting.filterDimension")}>
        <SelectField value={filter.dimensionCode} onChange={(e) => { onChange({ ...filter, dimensionCode: e.target.value, valueId: "" }); }} data-testid="statement-filter-dimension">
          <option value="">{t("accounting.noFilter")}</option>
          {dimensions.filter((d) => d.code === filter.dimensionCode || !taken.has(d.code)).map((d) => <option key={d.id} value={d.code}>{localized(d.name)}</option>)}
        </SelectField>
      </Field>
      <Field label={t("accounting.filterValue")}>
        <SelectField value={filter.valueId} onChange={(e) => { onChange({ ...filter, valueId: e.target.value }); }} disabled={!filter.dimensionCode} data-testid="statement-filter-value">
          <option value="">{t("accounting.anyValue")}</option>
          {(values.data ?? []).map((v) => <option key={v.id} value={v.id}>{v.code} · {localized(v.name)}</option>)}
        </SelectField>
      </Field>
      <Button variant="secondary" size="sm" onClick={onRemove} aria-label={t("accounting.removeFilter")}><X aria-hidden="true" /></Button>
    </div>
  );
}

/** Financial statements (founder programme item 9, slice 9c): the shipped and designed layouts run for a company and a period, drilled to the accounts and the ledger, exported. */
export function StatementsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const can = useCan();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const layouts = useLayouts(companyId);
  const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const [layout, setLayout] = useState<string>(search.layout ?? "");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [closing, setClosing] = useState<"default" | "include" | "exclude">("default");
  const [filters, setFilters] = useState<DimensionFilter[]>([]);
  const [executed, setExecuted] = useState<Record<string, unknown> | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [drill, setDrill] = useState<{ row: StatementRow; column: StatementColumn } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  // The first layout (the shipped profit and loss) is selected until the reader picks another; a layout named in the URL wins.
  const first = layouts.data?.[0];
  useEffect(() => {
    if (!layout && first) {
      setLayout(layoutKey(first));
    }
  }, [layout, first]);
  const selected = layouts.data?.find((l) => layoutKey(l) === layout);

  const request = (): Record<string, unknown> => ({
    companyId,
    ...layoutRef(layout),
    ...(from ? { from } : {}),
    ...(to ? { to } : {}),
    ...(closing === "default" ? {} : { includeClosing: closing === "include" }),
    filters: Object.fromEntries(filters.filter((f) => f.dimensionCode && f.valueId).map((f) => [f.dimensionCode, f.valueId])),
  });
  const run = (): void => { setProblem(null); setExpanded(new Set()); setExecuted(request()); };
  const result = useQuery({
    queryKey: ["statement", executed],
    enabled: executed !== null,
    retry: false,
    queryFn: async () => unwrap(await api.POST("/api/v1/reporting/statements/run", { body: (executed ?? { companyId }) as components["schemas"]["StatementRequest"] })),
  });
  const runProblem = result.error ? toFormProblem(result.error, t("statements.runFailed")) : null;

  const drillQuery = useQuery({
    queryKey: ["statement-drill", executed, drill?.row.code, drill?.column.code],
    enabled: Boolean(drill && executed),
    retry: false,
    queryFn: async () => {
      if (!drill || !executed) {
        throw new Error("nothing to drill");
      }
      return unwrap(await api.POST("/api/v1/reporting/statements/drill", { body: { request: executed as components["schemas"]["StatementRequest"], rowCode: drill.row.code, columnCode: drill.column.code } }));
    },
  });

  const openLedger = (account: StatementAccountLine, column: StatementColumn): void => {
    void navigate({ to: "/accounting/ledger", search: { companyId, accountId: account.accountId, ...(column.from ? { from: column.from } : {}), ...(column.to ? { to: column.to } : {}) } });
  };
  const exportTable = useMutation({
    mutationFn: async (format: "csv" | "xlsx") => {
      if (!result.data) {
        return;
      }
      const { columns, rows } = statementExport(result.data);
      const name = localized(result.data.name) || t("nav.statements");
      const response = await api.POST("/api/v1/exports/table", { body: { format, name, documentType: null, rightToLeft: document.documentElement.dir === "rtl", columns, rows }, parseAs: "blob" });
      saveFile(unwrap(response), response.response.headers, `${name}.${format}`);
    },
  });

  const toggle = (code: string): void => {
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(code)) {
        next.delete(code);
      } else {
        next.add(code);
      }
      return next;
    });
  };
  const statement: Statement | undefined = result.data;
  const taken = new Set(filters.map((f) => f.dimensionCode).filter(Boolean));

  return (
    <div>
      <PageHeader
        title={t("nav.statements")}
        description={t("statements.pageDescription")}
        actions={can("reporting.statement.manage") ? (
          <>
            <Button variant="secondary" onClick={() => { void navigate({ to: "/statements/designer", search: selected ? (selected.id ? { layout: selected.id } : { template: selected.code }) : {} }); }} disabled={!selected} data-testid="design-layout"><PencilRuler aria-hidden="true" /> {selected?.isSystem ? t("statements.customise") : t("statements.design")}</Button>
            <Button variant="secondary" onClick={() => { void navigate({ to: "/statements/designer", search: {} }); }} data-testid="new-layout"><Plus aria-hidden="true" /> {t("statements.newLayout")}</Button>
          </>
        ) : null}
      />
      <FormError message={problem?.message ?? runProblem?.message ?? null} />
      <div className="flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={(id) => { setCompanyId(id); setLayout(""); setExecuted(null); }} />
        <Field label={t("statements.layout")}>
          <SelectField value={layout} onChange={(e) => { setLayout(e.target.value); }} data-testid="statement-layout">
            {(layouts.data ?? []).map((l) => <option key={layoutKey(l)} value={layoutKey(l)}>{localized(l.name)}{l.isSystem ? "" : ` · ${l.code}`}</option>)}
          </SelectField>
        </Field>
        <Field label={t("reports.from")} description={t("statements.fromHelp")}>
          <TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} dir="ltr" data-testid="statement-from" />
        </Field>
        <Field label={t("reports.to")} description={t("statements.toHelp")}>
          <TextField type="date" value={to} onChange={(e) => { setTo(e.target.value); }} dir="ltr" data-testid="statement-to" />
        </Field>
        <Field label={t("statements.closingEntries")}>
          <SelectField value={closing} onChange={(e) => { setClosing(e.target.value as "default" | "include" | "exclude"); }} data-testid="statement-closing">
            <option value="default">{t("statements.closingDefault")}</option>
            <option value="include">{t("statements.closingInclude")}</option>
            <option value="exclude">{t("statements.closingExclude")}</option>
          </SelectField>
        </Field>
        <Button onClick={run} disabled={!companyId || !layout} data-testid="run-statement"><Play aria-hidden="true" /> {t("reports.run")}</Button>
      </div>
      <div className="mt-2 flex flex-wrap items-end gap-2">
        {filters.map((f) => <DimensionFilterRow key={f.id} dimensions={dimensions.data ?? []} taken={taken} filter={f} onChange={(next) => { setFilters(filters.map((x) => (x.id === f.id ? next : x))); }} onRemove={() => { setFilters(filters.filter((x) => x.id !== f.id)); }} />)}
        <Button variant="ghost" size="sm" onClick={() => { setFilters([...filters, { id: Date.now(), dimensionCode: "", valueId: "" }]); }} disabled={(dimensions.data ?? []).length === 0} data-testid="add-statement-filter"><Plus aria-hidden="true" /> {t("accounting.addFilter")}</Button>
      </div>
      {selected ? <p className="mt-2 text-sm text-fg-muted" dir="auto" data-testid="layout-description">{localized(selected.description)}</p> : null}

      {executed === null ? (
        <div className="mt-4 rounded-md border border-dashed border-border p-8 text-center text-sm text-fg-muted" data-testid="statement-empty">{t("statements.pressRun")}</div>
      ) : result.isPending ? (
        <div className="p-8 text-center text-sm text-fg-muted" aria-live="polite">{t("common.loading")}</div>
      ) : statement ? (
        <div className={result.isFetching ? "mt-4 opacity-60 transition-opacity" : "mt-4 transition-opacity"}>
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <div>
              <h2 className="text-lg font-semibold" dir="auto" data-testid="statement-title">{localized(statement.name)}</h2>
              <p className="text-xs text-fg-muted" data-testid="statement-subtitle">
                {company ? `${localized(company.legalName)} · ` : ""}<span dir="ltr">{statement.from} → {statement.to}</span> · {t("reports.currencyNote", { currency: statement.currency })}
                {statement.includeClosing ? ` · ${t("statements.closingIncluded")}` : ` · ${t("statements.closingExcluded")}`}
                {statement.projectedUpTo ? ` · ${t("reports.projectedUpTo", { when: formatDateTime(statement.projectedUpTo) })}` : ""}
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="secondary" size="sm" onClick={() => { setExpanded(new Set(statement.rows.filter((r) => (r.accounts ?? []).length > 0).map((r) => r.code))); }} data-testid="expand-all">{t("statements.expandAll")}</Button>
              <Button variant="secondary" size="sm" onClick={() => { setExpanded(new Set()); }}>{t("statements.collapseAll")}</Button>
              <Button variant="secondary" size="sm" onClick={() => { exportTable.mutate("csv"); }} loading={exportTable.isPending} data-testid="statement-export-csv"><Download aria-hidden="true" /> CSV</Button>
              <Button variant="secondary" size="sm" onClick={() => { exportTable.mutate("xlsx"); }} loading={exportTable.isPending} data-testid="statement-export-xlsx"><Download aria-hidden="true" /> Excel</Button>
            </div>
          </div>
          <StatementTable statement={statement} expanded={expanded} onToggle={toggle} onDrill={(row, column) => { setDrill({ row, column }); }} onOpenAccount={openLedger} />
        </div>
      ) : null}

      <Dialog open={drill !== null} onOpenChange={(isOpen) => { if (!isOpen) { setDrill(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{drill ? `${localized(drill.row.label)} · ${localized(drill.column.label)}` : ""}</DialogTitle>
          </DialogHeader>
          {drill?.column.from && drill.column.to ? <p className="text-sm text-fg-muted" dir="ltr">{drill.column.from} → {drill.column.to}</p> : null}
          {drillQuery.data ? (
            <Table data-testid="statement-drill">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("accounting.account")}</TableHead>
                  <TableHead className="text-end">{t("statements.amount")}</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {drillQuery.data.accounts.map((a) => (
                  <TableRow key={a.accountId} data-testid={`drill-account-${a.code}`}>
                    <TableCell><span dir="ltr" className="font-mono text-xs">{a.code}</span> <span dir="auto">{localized(a.name)}</span></TableCell>
                    <TableNumberCell dir="ltr">{cellText(a.values[0] ?? null, false)}</TableNumberCell>
                    <TableCell><Button variant="link" size="sm" onClick={() => { openLedger(a, drillQuery.data.column); }} data-testid={`open-ledger-${a.code}`}>{t("statements.openLedger")}</Button></TableCell>
                  </TableRow>
                ))}
                <TableRow className="font-semibold">
                  <TableCell>{t("reports.total")}</TableCell>
                  <TableNumberCell dir="ltr">{cellText(drillQuery.data.row.values[0] ?? null, false)}</TableNumberCell>
                  <TableCell />
                </TableRow>
              </TableBody>
            </Table>
          ) : drillQuery.error ? <FormError message={toFormProblem(drillQuery.error, t("statements.runFailed")).message} /> : <p className="text-sm text-fg-muted">{t("common.loading")}</p>}
        </DialogContent>
      </Dialog>
    </div>
  );
}
