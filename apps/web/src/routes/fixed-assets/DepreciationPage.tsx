import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { AssetStatus, useAssetBooks, type DepreciationRun } from "./shared";

/** Depreciation (roadmap 6.6): post a book through a month, see and reverse the runs, the forecast by month, and the reconciliation of the register to the ledger. */
export function DepreciationPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [bookId, setBookId] = useState("");
  const [through, setThrough] = useState(() => { const d = new Date(); return `${String(d.getUTCFullYear())}-${String(d.getUTCMonth() + 1).padStart(2, "0")}-01`; });
  const [year, setYear] = useState(() => String(new Date().getUTCFullYear()));
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [reversal, setReversal] = useState<{ id: string; reason: string } | null>(null);
  const books = useAssetBooks(companyId);
  const currency = companies.find((c) => c.id === companyId)?.functionalCurrency ?? "";
  const runs = useQuery({
    queryKey: ["depreciation-runs", companyId, bookId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/depreciation-runs", { params: { query: { companyId, ...(bookId ? { bookId } : {}) } } })),
  });
  const forecast = useQuery({
    queryKey: ["fa-forecast", companyId, bookId, year],
    enabled: Boolean(companyId) && /^\d{4}$/u.test(year),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/forecast", { params: { query: { companyId, ...(bookId ? { bookId } : {}), from: `${year}-01-01`, to: `${year}-12-31` } } })),
  });
  const reconciliation = useQuery({
    queryKey: ["fa-reconciliation", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/reconciliation", { params: { query: { companyId } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["depreciation-runs"], ["fa-forecast"], ["fa-reconciliation"], ["fixed-assets"], ["fixed-asset"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const post = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/fixed-assets/depreciation-runs", { body: { companyId, through, bookId: bookId || null, postingDate: null } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const reverse = useMutation({
    mutationFn: async (input: { id: string; reason: string }) => unwrap(await api.POST("/api/v1/fixed-assets/depreciation-runs/{runId}/reverse", { params: { path: { runId: input.id } }, body: { reason: input.reason, reversalDate: null } })),
    onSuccess: async () => { setProblem(null); setReversal(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<DepreciationRun, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "book", accessorKey: "bookCode", header: t("fixedAssets.book"), size: 90, cell: ({ row }) => <span dir="ltr">{row.original.bookCode}</span> },
      { id: "from", accessorKey: "periodStart", header: t("fixedAssets.from"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.periodStart)}</span> },
      { id: "through", accessorKey: "periodEnd", header: t("fixedAssets.through"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.periodEnd)}</span> },
      { id: "posting", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
      { id: "status", accessorFn: (r) => t(`fixedAssets.statuses.${r.status}`), header: t("common.status"), size: 100, cell: ({ row }) => <AssetStatus status={row.original.status} /> },
      { id: "assets", accessorKey: "assetCount", header: t("fixedAssets.assets"), size: 80, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.assetCount)}</span> },
      { id: "total", accessorKey: "total", header: t("expenses.total"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.total, currency)}</span> },
      {
        id: "actions", header: "", size: 160, enableSorting: false, cell: ({ row }) => (row.original.status === "posted" && reversal?.id !== row.original.id ? <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setReversal({ id: row.original.id, reason: "" }); }} data-testid={`reverse-run-${row.original.number}`}>{t("purchasing.reverse")}</Button> : null),
      },
    ],
    [t, currency, reversal],
  );

  const submit = (event: FormEvent): void => { event.preventDefault(); post.mutate(); };
  const rec = reconciliation.data;

  return (
    <>
      <PageHeader title={t("nav.depreciation")} description={t("fixedAssets.depreciationDescription")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("fixedAssets.book")}>
          <SelectField value={bookId} onChange={(ev) => { setBookId(ev.target.value); }} data-testid="book-filter">
            <option value="">{t("fixedAssets.ledgerBook")}</option>
            {(books.data ?? []).map((b) => (
              <option key={b.id} value={b.id}>{b.code}{b.postsToGl ? " ★" : ""}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <form onSubmit={submit} className="mb-4 flex flex-wrap items-end gap-3 rounded-md border border-border p-3" data-testid="run-form">
        <Field label={t("fixedAssets.through")} description={t("fixedAssets.throughHelp")} required>
          <TextField type="date" value={through} onChange={(ev) => { setThrough(ev.target.value); }} dir="ltr" required data-testid="run-through" />
        </Field>
        <Button type="submit" loading={post.isPending} disabled={!companyId || !through} data-testid="post-run">{t("fixedAssets.postRun")}</Button>
        <Button type="button" variant="secondary" onClick={() => { const d = new Date(); setThrough(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 0)).toISOString().slice(0, 10)); }} data-testid="run-last-month">{t("fixedAssets.lastMonth")}</Button>
        <span className="text-xs text-fg-muted">{t("fixedAssets.runHelp")}</span>
      </form>
      <FormError message={problem?.message ?? null} />
      {reversal ? (
        <div className="mb-3 flex flex-wrap items-end gap-3">
          <Field label={t("purchasing.reversalReason")} required>
            <TextField value={reversal.reason} onChange={(ev) => { setReversal({ ...reversal, reason: ev.target.value }); }} data-testid="reversal-reason" />
          </Field>
          <Button onClick={() => { reverse.mutate(reversal); }} loading={reverse.isPending} disabled={!reversal.reason.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button>
          <Button variant="secondary" onClick={() => { setReversal(null); }}>{t("common.cancel")}</Button>
        </div>
      ) : null}
      <DataGrid<DepreciationRun> label="nav.depreciation" columns={columns} data={runs.data ?? []} rowKey={(row) => row.id} loading={runs.isPending && Boolean(companyId)} emptyTitle={t("fixedAssets.emptyRuns")} emptyDescription={t("fixedAssets.emptyRunsDescription")} exportable={false} height={280} />

      <div className="mt-6 grid gap-6 lg:grid-cols-2">
        <section className="flex flex-col gap-2" data-testid="forecast">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <h2 className="text-base font-semibold">{t("fixedAssets.forecast")}</h2>
            <Field label={t("fixedAssets.year")}>
              <TextField inputMode="numeric" value={year} onChange={(ev) => { setYear(ev.target.value); }} dir="ltr" className="w-24" data-testid="forecast-year" />
            </Field>
          </div>
          {(forecast.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("fixedAssets.emptyForecast")}</p> : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("fixedAssets.month")}</TableHead>
                  <TableHead>{t("fixedAssets.assets")}</TableHead>
                  <TableHead>{t("fixedAssets.planned")}</TableHead>
                  <TableHead>{t("fixedAssets.posted")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(forecast.data ?? []).map((f) => (
                  <TableRow key={f.periodStart} data-testid="forecast-row">
                    <TableCell dir="ltr">{formatDate(f.periodStart)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(f.assets)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(f.planned, currency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(f.posted, currency)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>
        <section className="flex flex-col gap-2" data-testid="reconciliation">
          <div className="flex items-center gap-3">
            <h2 className="text-base font-semibold">{t("fixedAssets.reconciliation")}</h2>
            {rec ? <Badge tone={rec.ledgerAgrees ? "success" : "danger"} data-testid="reconciliation-status">{rec.ledgerAgrees ? t("fixedAssets.ledgerAgrees") : t("fixedAssets.ledgerDisagrees")}</Badge> : null}
          </div>
          {rec ? (
            <>
              <KeyValues entries={rec.rows.map((r) => [t(`accountRoles.${r.role}`, { defaultValue: r.role }), <span key={r.role} className="tabular" dir="ltr">{formatMoney(r.register, rec.currency)}</span>] as [string, React.ReactNode])} />
              {rec.problems.length > 0 ? (
                <ul className="list-disc ps-5 text-sm text-danger">
                  {rec.problems.map((p) => <li key={p}>{p}</li>)}
                </ul>
              ) : null}
              <p className="text-xs text-fg-muted">{t("fixedAssets.reconciliationHelp")}</p>
            </>
          ) : <p className="text-sm text-fg-muted">{t("fixedAssets.emptyReconciliation")}</p>}
        </section>
      </div>
    </>
  );
}
