import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { num, useSuppliers } from "./shared";

interface WeightsForm {
  onTimeWeight: string;
  quantityWeight: string;
  priceWeight: string;
  invoiceWeight: string;
  onTimeToleranceDays: string;
  lookbackMonths: string;
}

const pct = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : `${formatNumber(value, { maximumFractionDigits: 2 })} %`);

/** Supplier intelligence (roadmap 4.8): a scorecard with configurable weights, lead-time statistics and price history, all computed from the purchasing documents as they stand. */
export function SupplierIntelligencePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [tab, setTab] = useState("scorecard");
  const [asOf, setAsOf] = useState(today());
  const [partnerId, setPartnerId] = useState("");
  const [itemFilter, setItemFilter] = useState("");
  const [form, setForm] = useState<WeightsForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const suppliers = useSuppliers(companyId);
  const scorecard = useQuery({
    queryKey: ["scorecard", companyId, asOf],
    enabled: Boolean(companyId) && tab === "scorecard",
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/intelligence/scorecard", { params: { query: { companyId, asOf } } })),
  });
  const leadTimes = useQuery({
    queryKey: ["lead-times", companyId, partnerId],
    enabled: Boolean(companyId) && tab === "lead-times",
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/intelligence/lead-times", { params: { query: { companyId, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const prices = useQuery({
    queryKey: ["price-history", companyId, partnerId],
    enabled: Boolean(companyId) && tab === "prices",
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/intelligence/price-history", { params: { query: { companyId, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const settings = scorecard.data?.settings;
  useEffect(() => {
    if (settings) {
      setForm({ onTimeWeight: String(settings.onTimeWeight), quantityWeight: String(settings.quantityWeight), priceWeight: String(settings.priceWeight), invoiceWeight: String(settings.invoiceWeight), onTimeToleranceDays: String(settings.onTimeToleranceDays), lookbackMonths: String(settings.lookbackMonths) });
    }
  }, [settings]);
  const save = useMutation({
    mutationFn: async (f: WeightsForm) => unwrap(await api.PUT("/api/v1/purchasing/intelligence/scoring-settings", { body: { companyId, onTimeWeight: num(f.onTimeWeight), quantityWeight: num(f.quantityWeight), priceWeight: num(f.priceWeight), invoiceWeight: num(f.invoiceWeight), onTimeToleranceDays: num(f.onTimeToleranceDays), lookbackMonths: num(f.lookbackMonths, 12) } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["scorecard"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const filter = itemFilter.trim().toUpperCase();
  const summary = (prices.data?.summary ?? []).filter((r) => !filter || r.itemCode.toUpperCase().includes(filter));
  const points = (prices.data?.points ?? []).filter((p) => !filter || p.itemCode.toUpperCase().includes(filter));
  const fc = prices.data?.functionalCurrency ?? "";

  return (
    <>
      <PageHeader title={t("nav.supplierIntelligence")} description={t("intelligence.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        {tab === "scorecard" ? (
          <Field label={t("payables.asOf")}>
            <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="scorecard-as-of" />
          </Field>
        ) : (
          <Field label={t("partners.supplier")}>
            <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="intelligence-supplier">
              <option value="">{t("common.all")}</option>
              {(suppliers.data ?? []).map((sup) => (
                <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
              ))}
            </SelectField>
          </Field>
        )}
        {tab === "prices" ? (
          <Field label={t("purchasing.item")}>
            <TextField value={itemFilter} onChange={(e) => { setItemFilter(e.target.value); }} dir="ltr" data-testid="price-item-filter" />
          </Field>
        ) : null}
      </div>
      <Tabs tabs={[{ id: "scorecard", label: t("intelligence.scorecard"), testId: "tab-scorecard" }, { id: "lead-times", label: t("intelligence.leadTimes"), testId: "tab-lead-times" }, { id: "prices", label: t("intelligence.priceHistory"), testId: "tab-prices" }]} value={tab} onChange={setTab} />

      {tab === "scorecard" ? (
        <div className="mt-3 flex flex-col gap-4">
          {scorecard.data ? (
            <>
              <p className="text-xs text-fg-muted">{t("intelligence.window", { from: formatDate(scorecard.data.from), to: formatDate(scorecard.data.asOf) })}</p>
              {scorecard.data.rows.length === 0 ? <p className="text-sm text-fg-muted">{t("intelligence.nothingToScore")}</p> : (
                <Table data-testid="scorecard">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.supplier")}</TableHead>
                      <TableHead>{t("intelligence.onTime")}</TableHead>
                      <TableHead>{t("intelligence.quantity")}</TableHead>
                      <TableHead>{t("intelligence.price")}</TableHead>
                      <TableHead>{t("intelligence.invoices")}</TableHead>
                      <TableHead>{t("intelligence.score")}</TableHead>
                      <TableHead>{t("intelligence.grade")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {scorecard.data.rows.map((r) => (
                      <TableRow key={r.partnerId} data-testid="scorecard-row">
                        <TableCell dir="auto">{r.partnerCode} · {localized(r.partnerName)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{pct(r.onTimePct)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{pct(r.quantityPct)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{pct(r.pricePct)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{pct(r.invoicePct)}</TableCell>
                        <TableCell className="tabular font-semibold" dir="ltr">{r.score === null ? "—" : formatNumber(r.score, { maximumFractionDigits: 2 })}</TableCell>
                        <TableCell>{r.grade ? <Badge tone={r.grade === "A" ? "success" : r.grade === "B" ? "info" : r.grade === "C" ? "warning" : "danger"} data-testid="grade">{r.grade}</Badge> : "—"}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </>
          ) : <p className="text-sm text-fg-muted">{companyId ? t("common.loading") : t("payables.chooseCompany")}</p>}
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="scoring-settings">
              <h3 className="text-sm font-semibold">{t("intelligence.settings")}</h3>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label={t("intelligence.onTimeWeight")}><TextField inputMode="decimal" value={form.onTimeWeight} onChange={(e) => { setForm({ ...form, onTimeWeight: e.target.value }); }} dir="ltr" data-testid="weight-on-time" /></Field>
                <Field label={t("intelligence.quantityWeight")}><TextField inputMode="decimal" value={form.quantityWeight} onChange={(e) => { setForm({ ...form, quantityWeight: e.target.value }); }} dir="ltr" data-testid="weight-quantity" /></Field>
                <Field label={t("intelligence.priceWeight")}><TextField inputMode="decimal" value={form.priceWeight} onChange={(e) => { setForm({ ...form, priceWeight: e.target.value }); }} dir="ltr" data-testid="weight-price" /></Field>
                <Field label={t("intelligence.invoiceWeight")}><TextField inputMode="decimal" value={form.invoiceWeight} onChange={(e) => { setForm({ ...form, invoiceWeight: e.target.value }); }} dir="ltr" data-testid="weight-invoice" /></Field>
                <Field label={t("intelligence.toleranceDays")}><TextField inputMode="numeric" value={form.onTimeToleranceDays} onChange={(e) => { setForm({ ...form, onTimeToleranceDays: e.target.value }); }} dir="ltr" data-testid="tolerance-days" /></Field>
                <Field label={t("intelligence.lookbackMonths")}><TextField inputMode="numeric" value={form.lookbackMonths} onChange={(e) => { setForm({ ...form, lookbackMonths: e.target.value }); }} dir="ltr" data-testid="lookback-months" /></Field>
              </div>
              <div><Button type="submit" loading={save.isPending} data-testid="save-scoring">{t("common.save")}</Button></div>
            </form>
          ) : null}
        </div>
      ) : null}

      {tab === "lead-times" ? (
        <div className="mt-3">
          {(leadTimes.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("intelligence.nothingReceived")}</p> : (
            <Table data-testid="lead-times">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("partners.supplier")}</TableHead>
                  <TableHead>{t("intelligence.stated")}</TableHead>
                  <TableHead>{t("intelligence.receipts")}</TableHead>
                  <TableHead>{t("intelligence.average")}</TableHead>
                  <TableHead>{t("intelligence.median")}</TableHead>
                  <TableHead>{t("intelligence.range")}</TableHead>
                  <TableHead>{t("intelligence.onTime")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(leadTimes.data ?? []).map((r) => (
                  <TableRow key={r.partnerId} data-testid="lead-time-row">
                    <TableCell dir="auto">{r.partnerCode} · {localized(r.partnerName)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(r.statedLeadTimeDays)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(r.receipts)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatNumber(r.averageDays, { maximumFractionDigits: 2 })}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatNumber(r.medianDays, { maximumFractionDigits: 2 })}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(r.minDays)}–{String(r.maxDays)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{pct(r.onTimePct)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      ) : null}

      {tab === "prices" ? (
        <div className="mt-3 flex flex-col gap-4">
          {summary.length === 0 ? <p className="text-sm text-fg-muted">{t("intelligence.noPrices")}</p> : (
            <Table data-testid="price-summary">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("purchasing.item")}</TableHead>
                  <TableHead>{t("partners.supplier")}</TableHead>
                  <TableHead>{t("intelligence.points")}</TableHead>
                  <TableHead>{t("intelligence.last")}</TableHead>
                  <TableHead>{t("intelligence.average")}</TableHead>
                  <TableHead>{t("intelligence.range")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {summary.map((r) => (
                  <TableRow key={`${r.itemId}-${r.partnerId}`} data-testid="price-summary-row">
                    <TableCell dir="ltr">{r.itemCode}</TableCell>
                    <TableCell dir="ltr">{r.partnerCode}</TableCell>
                    <TableCell className="tabular" dir="ltr">{String(r.points)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.lastPriceFc, fc)} · {formatDate(r.lastDate)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.averagePriceFc, fc)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.minPriceFc, fc)} – {formatMoney(r.maxPriceFc, fc)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          {points.length > 0 ? (
            <Table data-testid="price-points">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("purchasing.postingDate")}</TableHead>
                  <TableHead>{t("intelligence.source")}</TableHead>
                  <TableHead>{t("purchasing.number")}</TableHead>
                  <TableHead>{t("purchasing.item")}</TableHead>
                  <TableHead>{t("partners.supplier")}</TableHead>
                  <TableHead>{t("intelligence.unitPrice")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {points.map((p) => (
                  <TableRow key={`${p.source}-${p.documentNumber}-${p.itemId}-${String(p.unitPrice)}`} data-testid="price-point-row">
                    <TableCell dir="ltr">{formatDate(p.date)}</TableCell>
                    <TableCell>{t(`intelligence.sources.${p.source}`)}</TableCell>
                    <TableCell dir="ltr">{p.documentNumber}</TableCell>
                    <TableCell dir="ltr">{p.itemCode}</TableCell>
                    <TableCell dir="ltr">{p.partnerCode}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(p.unitPrice, p.currency)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : null}
        </div>
      ) : null}
    </>
  );
}
