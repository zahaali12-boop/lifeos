import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Download, Plus, X } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Amount, CompanySelect, saveFile, today, useCompanies, useCompanySelection, withFilters } from "./shared";

type TrialBalance = components["schemas"]["TrialBalance"];
type TrialBalanceRow = components["schemas"]["TrialBalanceRow"];
type Dimension = components["schemas"]["DimensionSummary"];

/** One dimension filter row: its own dimension and value pickers, the value list fetched for whichever dimension is chosen. */
function DimensionFilterRow({ dimensions, taken, dimensionCode, valueId, onChange, onRemove }: { dimensions: Dimension[]; taken: Set<string>; dimensionCode: string; valueId: string; onChange: (next: { dimensionCode: string; valueId: string }) => void; onRemove: () => void }) {
  const { t } = useTranslation();
  const dimensionId = dimensions.find((d) => d.code === dimensionCode)?.id;
  const values = useQuery({
    queryKey: ["dimension-values", dimensionId],
    enabled: Boolean(dimensionId),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: dimensionId ?? "" } } })),
  });
  return (
    <div className="flex items-end gap-2" data-testid="tb-filter-row">
      <Field label={t("accounting.filterDimension")}>
        <SelectField value={dimensionCode} onChange={(e) => { onChange({ dimensionCode: e.target.value, valueId: "" }); }} data-testid="tb-filter-dimension">
          <option value="">{t("accounting.noFilter")}</option>
          {dimensions.filter((d) => d.code === dimensionCode || !taken.has(d.code)).map((d) => (
            <option key={d.id} value={d.code}>
              {localized(d.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("accounting.filterValue")}>
        <SelectField value={valueId} onChange={(e) => { onChange({ dimensionCode, valueId: e.target.value }); }} disabled={!dimensionCode} data-testid="tb-filter-value">
          <option value="">{t("accounting.anyValue")}</option>
          {(values.data ?? []).map((v) => (
            <option key={v.id} value={v.id}>
              {v.code} · {localized(v.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Button variant="secondary" size="sm" onClick={onRemove} aria-label={t("accounting.removeFilter")} data-testid="tb-filter-remove">
        <X aria-hidden="true" />
      </Button>
    </div>
  );
}

/** The trial balance at any date: movement window, comparative, basis, one dimension filter or grouping; every row drills to its ledger. */
export function TrialBalancePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const [asOf, setAsOf] = useState(today());
  const [from, setFrom] = useState("");
  const [compareAsOf, setCompareAsOf] = useState("");
  const [basis, setBasis] = useState("fc");
  const [groupBy, setGroupBy] = useState("");
  const [filterRows, setFilterRows] = useState<{ id: string; dimensionCode: string; valueId: string }[]>([]);

  const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const usedDimensions = new Set(filterRows.map((r) => r.dimensionCode).filter(Boolean));
  const filters: Record<string, string> = Object.fromEntries(filterRows.filter((r) => r.dimensionCode && r.valueId).map((r) => [`d.${r.dimensionCode}`, r.valueId]));
  const query = withFilters({ asOf, ...(from ? { from } : {}), ...(compareAsOf ? { compareAsOf } : {}), basis, ...(groupBy ? { groupBy } : {}) }, filters);

  const report = useQuery({
    queryKey: ["trial-balance", companyId, query],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/reports/trial-balance", { params: { path: { companyId }, query } })),
  });
  const exportReport = useMutation({
    mutationFn: async (format: "csv" | "xlsx") => {
      const result = await api.GET("/api/v1/accounting/companies/{companyId}/reports/trial-balance", { params: { path: { companyId }, query: { ...query, format } }, parseAs: "blob" });
      const blob = unwrap(result);
      saveFile(blob, result.response.headers, `trial-balance-${asOf}.${format}`);
    },
  });

  const openLedger = (row: TrialBalanceRow): void => {
    const drill: Record<string, string> = { companyId, accountId: row.drill.accountId, to: row.drill.to, ...(row.drill.from ? { from: row.drill.from } : {}), basis };
    for (const [code, valueId] of Object.entries(row.drill.dimensions)) {
      drill[`d.${code}`] = valueId;
    }
    void navigate({ to: "/accounting/ledger", search: drill });
  };

  const data: TrialBalance | undefined = report.data;
  const problem = report.error ? toFormProblem(report.error, t("accounting.loadFailed")) : null;
  const minorUnits = 2;
  const compare = Boolean(data?.compareAsOf);

  return (
    <>
      <PageHeader
        title={t("accounting.trialBalance")}
        description={t("accounting.trialBalanceDescription")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { exportReport.mutate("csv"); }} loading={exportReport.isPending} disabled={!data}>
              <Download aria-hidden="true" />
              CSV
            </Button>
            <Button variant="secondary" onClick={() => { exportReport.mutate("xlsx"); }} loading={exportReport.isPending} disabled={!data}>
              <Download aria-hidden="true" />
              XLSX
            </Button>
          </>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3 lg:grid-cols-6">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
        <Field label={t("accounting.asOf")} required>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="tb-as-of" />
        </Field>
        <Field label={t("accounting.from")} description={t("accounting.fromHint")}>
          <TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("accounting.compareAsOf")}>
          <TextField type="date" value={compareAsOf} onChange={(e) => { setCompareAsOf(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("accounting.basis")}>
          <SelectField value={basis} onChange={(e) => { setBasis(e.target.value); }}>
            <option value="fc">{t("accounting.basisFunctional", { currency: company?.functionalCurrency ?? "" })}</option>
            {company?.reportingCurrency ? <option value="rc">{t("accounting.basisReporting", { currency: company.reportingCurrency })}</option> : null}
          </SelectField>
        </Field>
        <Field label={t("accounting.groupBy")}>
          <SelectField value={groupBy} onChange={(e) => { setGroupBy(e.target.value); }}>
            <option value="">{t("accounting.noGrouping")}</option>
            {(dimensions.data ?? []).map((d) => (
              <option key={d.id} value={d.code}>
                {localized(d.name)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      {filterRows.length > 0 ? (
        <div className="mb-4 flex flex-col gap-2" data-testid="tb-filters">
          {filterRows.map((row) => (
            <DimensionFilterRow
              key={row.id}
              dimensions={dimensions.data ?? []}
              taken={new Set([...usedDimensions].filter((code) => code !== row.dimensionCode))}
              dimensionCode={row.dimensionCode}
              valueId={row.valueId}
              onChange={(next) => { setFilterRows((rows) => rows.map((r) => (r.id === row.id ? { ...r, ...next } : r))); }}
              onRemove={() => { setFilterRows((rows) => rows.filter((r) => r.id !== row.id)); }}
            />
          ))}
        </div>
      ) : null}
      <Button
        variant="secondary"
        size="sm"
        className="mb-4"
        onClick={() => { setFilterRows((rows) => [...rows, { id: crypto.randomUUID(), dimensionCode: "", valueId: "" }]); }}
        disabled={usedDimensions.size >= (dimensions.data?.length ?? 0)}
        data-testid="tb-filter-add"
      >
        <Plus aria-hidden="true" />
        {t("accounting.addFilter")}
      </Button>
      <FormError message={problem?.message ?? null} />
      {data ? (
        <>
          <p className="mb-2 flex items-center gap-2 text-sm text-fg-muted" role="status" data-testid="tb-status">
            <Badge tone={data.balanced ? "success" : "danger"}>{data.balanced ? t("accounting.balanced") : t("accounting.unbalanced")}</Badge>
            <span>{t("accounting.tbSummary", { currency: data.currency, rows: data.rows.length })}</span>
          </p>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("accounting.accountCode")}</TableHead>
                <TableHead>{t("accounting.accountName")}</TableHead>
                {data.groupBy ? <TableHead>{data.groupBy}</TableHead> : null}
                <TableHead className="text-end">{t("accounting.opening")}</TableHead>
                <TableHead className="text-end">{t("accounting.debit")}</TableHead>
                <TableHead className="text-end">{t("accounting.credit")}</TableHead>
                <TableHead className="text-end">{t("accounting.closing")}</TableHead>
                {compare ? <TableHead className="text-end">{t("accounting.compareClosing")}</TableHead> : null}
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.rows.map((row) => (
                <TableRow key={`${row.accountId}-${row.dimensionValueId ?? ""}`} className="cursor-pointer" onClick={() => { openLedger(row); }} data-testid="tb-row">
                  <TableCell dir="ltr">
                    <button type="button" className="font-medium underline-offset-2 hover:underline" onClick={() => { openLedger(row); }}>
                      {row.accountCode}
                    </button>
                  </TableCell>
                  <TableCell>{localized(row.accountName)}</TableCell>
                  {data.groupBy ? <TableCell>{row.dimensionValueCode ? `${row.dimensionValueCode} · ${localized(row.dimensionValueName)}` : t("accounting.unassigned")}</TableCell> : null}
                  <TableNumberCell><Amount value={row.opening} minorUnits={minorUnits} /></TableNumberCell>
                  <TableNumberCell><Amount value={row.debit} minorUnits={minorUnits} /></TableNumberCell>
                  <TableNumberCell><Amount value={row.credit} minorUnits={minorUnits} /></TableNumberCell>
                  <TableNumberCell><Amount value={row.closing} minorUnits={minorUnits} /></TableNumberCell>
                  {compare ? <TableNumberCell><Amount value={row.compare?.closing ?? 0} minorUnits={minorUnits} /></TableNumberCell> : null}
                </TableRow>
              ))}
              <TableRow className="font-semibold">
                <TableCell colSpan={data.groupBy ? 3 : 2}>{t("accounting.totals")}</TableCell>
                <TableNumberCell><Amount value={data.totals.opening} minorUnits={minorUnits} /></TableNumberCell>
                <TableNumberCell><Amount value={data.totals.debit} minorUnits={minorUnits} /></TableNumberCell>
                <TableNumberCell><Amount value={data.totals.credit} minorUnits={minorUnits} /></TableNumberCell>
                <TableNumberCell><Amount value={data.totals.closing} minorUnits={minorUnits} /></TableNumberCell>
                {compare ? <TableNumberCell><Amount value={data.compareTotals?.closing ?? 0} minorUnits={minorUnits} /></TableNumberCell> : null}
              </TableRow>
            </TableBody>
          </Table>
        </>
      ) : report.isPending && companyId ? (
        <p className="text-sm text-fg-muted">{t("common.loading")}</p>
      ) : null}
    </>
  );
}
