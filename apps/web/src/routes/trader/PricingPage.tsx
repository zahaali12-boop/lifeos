import { Badge, Button, Checkbox, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";

type Board = components["schemas"]["TraderPricingBoard"];
type Row = components["schemas"]["TraderPricingRow"];

const alertTones: Record<string, "danger" | "warning" | "info" | "neutral"> = {
  loss_at_cost: "danger",
  loss_at_replacement: "danger",
  margin_below_target: "warning",
  no_price: "warning",
  no_cost: "neutral",
  no_market_rate: "warning",
  rate_up: "info",
  rate_down: "info",
};

const settingKeys = { margin: "trader.pricing.target_margin_pct", roundTo: "trader.pricing.round_to", list: "trader.pricing.price_list_id" } as const;

function num(value: string): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Selling prices (trader edition, ADR-0035 §5): every item with a landed cost or a sales price, what it cost landed at
 * the rate paid, what it would cost to buy again at today's rate, the current price and margin, the suggested price at
 * the target margin and the alerts; the ticked suggestions are written to the sales price list.
 */
export function PricingPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [settings, setSettings] = useState<{ margin: string; roundTo: string; listId: string } | null>(null);
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [ticked, setTicked] = useState<Record<string, boolean>>({});

  const board = useQuery({
    queryKey: ["trader-pricing", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/pricing/board", { params: { query: { companyId } } })),
  });
  const lists = useQuery({
    queryKey: ["price-lists", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/pricing/price-lists", { params: { query: { companyId } } })),
  });
  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["trader-pricing"] });
    await queryClient.invalidateQueries({ queryKey: ["price-lists"] });
  };

  const saveSettings = useMutation({
    mutationFn: async (input: { margin: string; roundTo: string; listId: string }) => {
      unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: settingKeys.margin } }, body: { value: num(input.margin), valueType: "number" } }));
      unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: settingKeys.roundTo } }, body: { value: num(input.roundTo), valueType: "number" } }));
      unwrap(await api.PUT("/api/v1/organization/settings/{key}", { params: { path: { key: settingKeys.list } }, body: { value: input.listId, valueType: "string" } }));
    },
    onSuccess: async () => { setProblem(null); setSettings(null); setNotice(t("trader.pricing.settingsSaved")); await refresh(); },
    onError: fail,
  });
  const createList = useMutation({
    mutationFn: async (currency: string) => unwrap(await api.POST("/api/v1/pricing/price-lists", { body: { companyId, code: "SALES", name: { en: "Sales prices", ar: "أسعار البيع" }, currency, pricesIncludeTax: false, priceSurcharge: 0, priority: 100, isDefault: true, isActive: true } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const apply = useMutation({
    mutationFn: async (prices: { itemId: string; price: number }[]) => unwrap(await api.POST("/api/v1/trader/pricing/apply", { body: { companyId, prices } })),
    onSuccess: async (result) => {
      setProblem(null);
      setEdits({});
      setTicked({});
      setNotice(t("trader.pricing.applied"));
      queryClient.setQueryData(["trader-pricing", companyId], result);
      await queryClient.invalidateQueries({ queryKey: ["price-list-items"] });
    },
    onError: fail,
  });

  const b: Board | undefined = board.data;
  const currency = b?.currency ?? "";
  const money = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : formatMoney(value, currency));
  const pct = (value: number | string | null | undefined): string => (value === null || value === undefined ? "—" : `${formatNumber(value, { maximumFractionDigits: 1 })}%`);
  const suggestedOf = (row: Row): string => edits[row.itemId] ?? (row.suggestedPrice === null ? "" : String(row.suggestedPrice));
  const isTicked = (row: Row): boolean => ticked[row.itemId] ?? (row.suggestedPrice !== null && row.suggestedPrice !== row.currentPrice);
  const selected = (b?.rows ?? []).filter((row) => isTicked(row) && suggestedOf(row).trim() !== "").map((row) => ({ itemId: row.itemId, price: num(suggestedOf(row)) }));
  const openSettings = (): void => {
    if (!b) {
      return;
    }
    setProblem(null);
    setSettings({ margin: String(b.targetMarginPct), roundTo: String(b.roundTo), listId: b.priceListId ?? "" });
  };
  const submitSettings = (event: FormEvent): void => { event.preventDefault(); if (settings) { saveSettings.mutate(settings); } };
  const alertLabel = (row: Row, code: string): string => t(`trader.pricing.alertNames.${code}`, { defaultValue: code, pct: formatNumber(Math.abs(Number(row.rateMovePct ?? 0)), { maximumFractionDigits: 1 }) });

  return (
    <>
      <PageHeader title={t("trader.pricing.title")} description={t("trader.pricing.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <FormError message={problem?.message ?? null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="pricing-notice">{notice}</p> : null}

      {b ? (
        <>
          <section className="mb-4 grid gap-4 lg:grid-cols-2">
            <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="pricing-settings">
              <div className="flex items-center justify-between">
                <h2 className="text-sm font-semibold">{t("trader.pricing.settings")}</h2>
                {!settings ? <Button type="button" variant="ghost" size="sm" onClick={openSettings} data-testid="edit-pricing-settings">{t("common.edit")}</Button> : null}
              </div>
              {settings ? (
                <form onSubmit={submitSettings} className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_1fr_auto]">
                  <Field label={t("trader.pricing.targetMargin")}>
                    <DecimalField value={settings.margin} onChange={(e) => { setSettings({ ...settings, margin: e.target.value }); }} dir="ltr" data-testid="target-margin" />
                  </Field>
                  <Field label={t("trader.pricing.roundTo")} description={t("trader.pricing.roundToHint")}>
                    <DecimalField value={settings.roundTo} onChange={(e) => { setSettings({ ...settings, roundTo: e.target.value }); }} dir="ltr" data-testid="round-to" />
                  </Field>
                  <Field label={t("trader.pricing.priceList")}>
                    <SelectField value={settings.listId} onChange={(e) => { setSettings({ ...settings, listId: e.target.value }); }} data-testid="pricing-list">
                      <option value="">{t("trader.pricing.defaultList")}</option>
                      {(lists.data ?? []).filter((l) => l.currency === currency).map((l) => <option key={l.id} value={l.id}>{l.code} · {localized(l.name)}</option>)}
                    </SelectField>
                  </Field>
                  <div className="flex gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setSettings(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={saveSettings.isPending} data-testid="save-pricing-settings">{t("trader.pricing.saveSettings")}</Button>
                  </div>
                </form>
              ) : (
                <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
                  <dt className="text-fg-muted">{t("trader.pricing.targetMargin")}</dt>
                  <dd className="tabular text-start" dir="ltr" data-testid="target-margin-value">{pct(b.targetMarginPct)}</dd>
                  <dt className="text-fg-muted">{t("trader.pricing.roundTo")}</dt>
                  <dd className="tabular text-start" dir="ltr">{Number(b.roundTo) > 0 ? formatNumber(b.roundTo) : "—"}</dd>
                  <dt className="text-fg-muted">{t("trader.pricing.priceList")}</dt>
                  <dd className="text-start" dir="auto">{b.priceListName ? localized(b.priceListName) : "—"}</dd>
                </dl>
              )}
              {b.hints.includes("no_price_list") ? (
                <div className="flex flex-wrap items-center gap-2 text-sm" data-testid="no-price-list">
                  <span>{t("trader.pricing.noList", { currency })}</span>
                  <Button type="button" variant="secondary" size="sm" onClick={() => { createList.mutate(currency); }} loading={createList.isPending} data-testid="create-price-list">{t("trader.pricing.createList")}</Button>
                </div>
              ) : null}
            </div>
            <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="market-rates">
              <h2 className="text-sm font-semibold">{t("trader.pricing.marketRates")}</h2>
              {b.marketRates.length === 0 ? <p className="text-sm text-fg-muted">—</p> : (
                <ul className="flex flex-col gap-1 text-sm">
                  {b.marketRates.map((r) => (
                    <li key={r.currency} dir="auto">
                      {r.rate === null ? t("trader.pricing.noMarketRate", { currency: r.currency }) : t("trader.pricing.marketRate", { currency: r.currency, rate: formatNumber(r.rate, { maximumFractionDigits: 6 }), date: formatDate(r.asOf) })}
                    </li>
                  ))}
                </ul>
              )}
              <Link to="/rates" className="text-sm text-accent underline">{t("trader.pricing.ratesLink")}</Link>
              <div className="mt-1 flex flex-wrap gap-2">
                {Number(b.losingItems) > 0 ? <Badge tone="danger" data-testid="losing-count">{t("trader.pricing.losing", { count: b.losingItems })}</Badge> : null}
                {Number(b.unpricedItems) > 0 ? <Badge tone="warning" data-testid="unpriced-count">{t("trader.pricing.unpriced", { count: b.unpricedItems })}</Badge> : null}
              </div>
            </div>
          </section>

          {b.rows.length === 0 ? (
            <div className="rounded-md border border-dashed border-border p-6 text-center text-sm text-fg-muted">
              <p className="font-medium text-fg">{t("trader.pricing.empty")}</p>
              <p>{t("trader.pricing.emptyDescription")}</p>
            </div>
          ) : (
            <>
              <Table data-testid="pricing-table">
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("trader.pricing.item")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.onHand")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.boughtAt")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.landed")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.costToday")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.currentPrice")}</TableHead>
                    <TableHead className="text-end">{t("trader.pricing.margin")}</TableHead>
                    <TableHead>{t("trader.pricing.suggested")}</TableHead>
                    <TableHead>{t("trader.pricing.alerts")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {b.rows.map((row, index) => (
                    <TableRow key={row.itemId} data-testid="price-row">
                      <TableCell dir="auto">{row.itemCode} · {localized(row.itemName)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{formatNumber(row.onHand, { maximumFractionDigits: 3 })}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{row.unitPriceFc !== null && row.purchaseCurrency ? (row.purchaseCurrency === currency ? formatMoney(row.unitPriceFc, row.purchaseCurrency) : t("trader.pricing.boughtAtValue", { price: formatMoney(row.unitPriceFc, row.purchaseCurrency), rate: formatNumber(row.ratePaid ?? 0, { maximumFractionDigits: 4 }) })) : "—"}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="landed-cost">{money(row.landedUnitCostRc ?? row.stockUnitCostRc)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="cost-today">{money(row.replacementUnitCostRc ?? row.costBasisRc)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="current-price">{money(row.currentPrice)}</TableCell>
                      <TableCell className={`tabular text-end ${row.currentMarginPct !== null && Number(row.currentMarginPct) < 0 ? "text-danger" : ""}`} dir="ltr" data-testid="current-margin">{pct(row.currentMarginPct)}</TableCell>
                      <TableCell>
                        <div className="flex items-center gap-2">
                          <Checkbox checked={isTicked(row)} onCheckedChange={(checked) => { setTicked({ ...ticked, [row.itemId]: checked === true }); }} aria-label={`${t("trader.pricing.apply")} ${row.itemCode}`} data-testid={`apply-${String(index)}`} />
                          <DecimalField aria-label={`${t("trader.pricing.suggested")} ${row.itemCode}`} value={suggestedOf(row)} onChange={(e) => { setEdits({ ...edits, [row.itemId]: e.target.value }); setTicked({ ...ticked, [row.itemId]: true }); }} dir="ltr" className="w-32" data-testid={`suggested-${String(index)}`} />
                          {row.suggestedMarginPct !== null ? <span className="text-xs text-fg-muted" dir="ltr">{pct(row.suggestedMarginPct)}</span> : null}
                        </div>
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {row.alerts.map((code) => <Badge key={code} tone={alertTones[code] ?? "neutral"} data-testid={`alert-${code}`}>{alertLabel(row, code)}</Badge>)}
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div className="mt-3 flex justify-end">
                <Button onClick={() => { apply.mutate(selected); }} disabled={selected.length === 0 || b.priceListId === null} loading={apply.isPending} data-testid="apply-prices">{t("trader.pricing.applyPrices")} ({formatNumber(selected.length)})</Button>
              </div>
            </>
          )}
        </>
      ) : null}
    </>
  );
}
