import { Badge, Button, Checkbox, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField } from "../common";
import { ItemCodeField } from "../inventory/ItemCodeField";
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

/** Keeps the cost the trader typed as the item's standard cost for the company, the item's other company settings untouched. */
async function saveItemCost(itemId: string, companyId: string, cost: number): Promise<void> {
  const current = unwrap(await api.GET("/api/v1/items/{itemId}/company-settings", { params: { path: { itemId } } })).find((s) => s.companyId === companyId);
  unwrap(await api.PUT("/api/v1/items/{itemId}/company-settings/{companyId}", {
    params: { path: { itemId, companyId } },
    body: {
      costingMethodOverride: current?.costingMethodOverride ?? null,
      standardCost: cost,
      itemPostingGroupOverride: current?.itemPostingGroupOverride ?? null,
      defaultWarehouseId: current?.defaultWarehouseId ?? null,
      allowNegativeStock: current?.allowNegativeStock ?? null,
    },
  }));
}

/**
 * Selling prices (trader edition, ADR-0035 §5, §15): every item with a landed cost, a cost typed on it or a sales
 * price, what it cost landed at the rate paid, what it would cost to buy again at today's rate, the current price and
 * margin, the suggested price at the target margin with the floor below which a unit loses money, and the alerts; the
 * ticked prices are written to the sales price list, any item is priced from "New pricing", a price is taken off again.
 */
export function PricingPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [settings, setSettings] = useState<{ margin: string; roundTo: string; listId: string } | null>(null);
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [ticked, setTicked] = useState<Record<string, boolean>>({});
  const [pricingOpen, setPricingOpen] = useState(false);
  const [costEdit, setCostEdit] = useState<{ itemId: string; value: string } | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [sellNext, setSellNext] = useState(false);
  const canCost = can("inventory.item.manage");

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
  const showBoard = async (result: Board): Promise<void> => {
    queryClient.setQueryData(["trader-pricing", companyId], result);
    await queryClient.invalidateQueries({ queryKey: ["price-list-items"] });
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
      setSellNext(true);
      await showBoard(result);
    },
    onError: fail,
  });
  const saveCost = useMutation({
    mutationFn: async (input: { itemId: string; cost: number }) => { await saveItemCost(input.itemId, companyId, input.cost); },
    onSuccess: async () => { setProblem(null); setCostEdit(null); setNotice(t("trader.pricing.costSaved")); await refresh(); },
    onError: fail,
  });
  const removePrice = useMutation({
    mutationFn: async (itemId: string) => unwrap(await api.DELETE("/api/v1/trader/pricing/prices/{itemId}", { params: { path: { itemId }, query: { companyId } } })),
    onSuccess: async (result) => {
      setProblem(null);
      setRemoving(null);
      setNotice(t("trader.pricing.removed"));
      await showBoard(result);
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
  // The margin follows what is typed: the price in the box against today's cost (or the cost basis), one decimal.
  const liveMargin = (row: Row): number | null => {
    const typed = suggestedOf(row).trim();
    const basis = row.replacementUnitCostRc ?? row.costBasisRc;
    if (typed === "" || basis === null || Number(basis) <= 0) {
      return row.suggestedMarginPct === null ? null : Number(row.suggestedMarginPct);
    }
    return Math.round(((num(typed) - Number(basis)) * 1000) / Number(basis)) / 10;
  };
  const canSaveRow = (row: Row): boolean => suggestedOf(row).trim() !== "" && Boolean(b?.priceListId) && num(suggestedOf(row)) !== Number(row.currentPrice ?? -1);
  // The floor under the price box: the cost basis rounded up to the increment; a price typed below it says what a unit would lose.
  const floorNote = (row: Row): ReactNode => {
    if (row.floorPrice === null) {
      return null;
    }
    const typed = suggestedOf(row).trim();
    const below = typed !== "" && num(typed) < Number(row.floorPrice);
    return (
      <span className={below ? "text-xs text-danger" : "text-xs text-fg-muted"} dir="auto" data-testid="floor-price">
        {below ? t("trader.pricing.belowFloor", { amount: money(Number(row.floorPrice) - num(typed)) }) : t("trader.pricing.floor", { price: money(row.floorPrice) })}
      </span>
    );
  };

  return (
    <>
      <PageHeader
        title={t("trader.pricing.title")}
        description={t("trader.pricing.description")}
        actions={
          <Button onClick={() => { setPricingOpen(true); }} disabled={!b?.priceListId} data-testid="new-pricing">
            <Plus aria-hidden="true" />
            {t("trader.pricing.newPricing")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <FormError message={problem?.message ?? null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="pricing-notice">{notice}</p> : null}
      {sellNext ? (
        <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-border p-2 text-sm" data-testid="next-step">
          <span className="font-medium">{t("trader.next.title")}</span>
          <Button asChild size="sm" variant="secondary"><Link to="/trader/sales" search={{ new: "1" }} data-testid="next-sell">{t("trader.pricing.sellNow")}</Link></Button>
        </div>
      ) : null}

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
                    <TableHead>{t("trader.pricing.newPrice")}</TableHead>
                    <TableHead>{t("trader.pricing.alerts")}</TableHead>
                    <TableHead><span className="sr-only">{t("common.actions")}</span></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {b.rows.map((row, index) => {
                    const margin = liveMargin(row);
                    return (
                    <TableRow key={row.itemId} data-testid="price-row">
                      <TableCell dir="auto">{row.itemCode} · {localized(row.itemName)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{formatNumber(row.onHand, { maximumFractionDigits: 3 })}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{row.unitPriceFc !== null && row.purchaseCurrency ? (row.purchaseCurrency === currency ? formatMoney(row.unitPriceFc, row.purchaseCurrency) : t("trader.pricing.boughtAtValue", { price: formatMoney(row.unitPriceFc, row.purchaseCurrency), rate: formatNumber(row.ratePaid ?? 0, { maximumFractionDigits: 4 }) })) : "—"}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="landed-cost">
                        {costEdit?.itemId === row.itemId ? (
                          <div className="flex items-center justify-end gap-1">
                            <DecimalField aria-label={t("trader.pricing.costPerUnit")} value={costEdit.value} onChange={(e) => { setCostEdit({ itemId: row.itemId, value: e.target.value }); }} className="w-28" autoFocus data-testid="cost-input" />
                            <Button type="button" size="sm" onClick={() => { saveCost.mutate({ itemId: row.itemId, cost: num(costEdit.value) }); }} disabled={num(costEdit.value) <= 0} loading={saveCost.isPending} data-testid="save-cost">{t("trader.pricing.saveCost")}</Button>
                            <Button type="button" size="sm" variant="ghost" onClick={() => { setCostEdit(null); }}>{t("common.cancel")}</Button>
                          </div>
                        ) : (
                          <>
                            {money(row.landedUnitCostRc ?? row.stockUnitCostRc ?? row.itemCostRc)}
                            {row.landedUnitCostRc === null && row.stockUnitCostRc === null && row.itemCostRc !== null ? <span className="ms-1 text-xs text-fg-muted" dir="auto">{t("trader.pricing.enteredCost")}</span> : null}
                          </>
                        )}
                      </TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="cost-today">{money(row.replacementUnitCostRc ?? row.costBasisRc)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="current-price">{money(row.currentPrice)}</TableCell>
                      <TableCell className={`tabular text-end ${row.currentMarginPct !== null && Number(row.currentMarginPct) < 0 ? "text-danger" : ""}`} dir="ltr" data-testid="current-margin">{pct(row.currentMarginPct)}</TableCell>
                      <TableCell>
                        <div className="flex flex-col gap-1">
                          <div className="flex items-center gap-2">
                            <Checkbox checked={isTicked(row)} onCheckedChange={(checked) => { setTicked({ ...ticked, [row.itemId]: checked === true }); }} aria-label={`${t("trader.pricing.apply")} ${row.itemCode}`} data-testid={`apply-${String(index)}`} />
                            <DecimalField aria-label={`${t("trader.pricing.newPrice")} ${row.itemCode}`} value={suggestedOf(row)} onChange={(e) => { setEdits({ ...edits, [row.itemId]: e.target.value }); setTicked({ ...ticked, [row.itemId]: true }); }} dir="ltr" className="w-32" data-testid={`suggested-${String(index)}`} />
                            {margin !== null ? <span className={margin < 0 ? "text-xs text-danger" : "text-xs text-fg-muted"} dir="ltr" data-testid="live-margin">{pct(margin)}</span> : null}
                            <Button type="button" size="sm" variant="secondary" disabled={!canSaveRow(row)} loading={apply.isPending} onClick={() => { apply.mutate([{ itemId: row.itemId, price: num(suggestedOf(row)) }]); }} data-testid="save-price">{t("trader.pricing.savePrice")}</Button>
                          </div>
                          {floorNote(row)}
                        </div>
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-wrap gap-1">
                          {row.alerts.map((code) => (code === "no_cost" && canCost ? (
                            <Button key={code} type="button" variant="secondary" size="sm" onClick={() => { setCostEdit({ itemId: row.itemId, value: "" }); }} data-testid={`add-cost-${String(index)}`}>{t("trader.pricing.addCost")}</Button>
                          ) : (
                            <Badge key={code} tone={alertTones[code] ?? "neutral"} data-testid={`alert-${code}`}>{alertLabel(row, code)}</Badge>
                          )))}
                        </div>
                      </TableCell>
                      <TableCell>
                        {row.currentPrice !== null ? (removing === row.itemId ? (
                          <div className="flex items-center gap-1 whitespace-nowrap text-xs" dir="auto">
                            <span>{t("trader.pricing.removeConfirm", { item: row.itemCode })}</span>
                            <Button type="button" size="sm" variant="danger" onClick={() => { removePrice.mutate(row.itemId); }} loading={removePrice.isPending} data-testid="remove-price-yes">{t("trader.pricing.removeYes")}</Button>
                            <Button type="button" size="sm" variant="ghost" onClick={() => { setRemoving(null); }}>{t("common.cancel")}</Button>
                          </div>
                        ) : (
                          <Button type="button" variant="ghost" size="icon" aria-label={`${t("trader.pricing.removePrice")} ${row.itemCode}`} onClick={() => { setRemoving(row.itemId); }} data-testid="remove-price">
                            <Trash2 aria-hidden="true" />
                          </Button>
                        )) : null}
                      </TableCell>
                    </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              <div className="mt-3 flex justify-end">
                <Button onClick={() => { apply.mutate(selected); }} disabled={selected.length === 0 || b.priceListId === null} loading={apply.isPending} data-testid="apply-prices">{t("trader.pricing.applyPrices")} ({formatNumber(selected.length)})</Button>
              </div>
            </>
          )}
          {pricingOpen ? (
            <NewPricingDialog
              companyId={companyId}
              currency={currency}
              canCost={canCost}
              onClose={() => { setPricingOpen(false); }}
              onSaved={async (result) => { setPricingOpen(false); setProblem(null); setNotice(t("trader.pricing.applied")); await showBoard(result); }}
            />
          ) : null}
        </>
      ) : null}
    </>
  );
}

/** Any item priced now: the item (made on the spot when new), its selling price and, when the program knows no cost yet, what a unit costs. */
function NewPricingDialog({ companyId, currency, canCost, onClose, onSaved }: { companyId: string; currency: string; canCost: boolean; onClose: () => void; onSaved: (board: Board) => Promise<void> }) {
  const { t } = useTranslation();
  const [code, setCode] = useState("");
  const [price, setPrice] = useState("");
  const [cost, setCost] = useState("");
  const [error, setError] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: async () => {
      const wanted = code.trim().toUpperCase();
      const found = unwrap(await api.GET("/api/v1/items", { params: { query: { q: wanted, limit: 12 } } })).items.find((item) => item.code.toUpperCase() === wanted);
      if (!found) {
        throw new Error(t("trader.pricing.itemUnknown", { code: wanted }));
      }
      if (canCost && num(cost) > 0) {
        await saveItemCost(found.id, companyId, num(cost));
      }
      return unwrap(await api.POST("/api/v1/trader/pricing/apply", { body: { companyId, prices: [{ itemId: found.id, price: num(price) }] } }));
    },
    onSuccess: async (result) => { await onSaved(result); },
    onError: (failure) => { setError(failure instanceof Error ? failure.message : toFormProblem(failure, t("common.saveFailed")).message); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    setError(null);
    if (code.trim() === "") {
      setError(t("trader.pricing.itemRequired"));
      return;
    }
    if (num(price) <= 0) {
      setError(t("trader.pricing.priceRequired"));
      return;
    }
    save.mutate();
  };
  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} data-testid="new-pricing-dialog">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle>{t("trader.pricing.newPricing")}</DialogTitle>
            <DialogDescription>{t("trader.pricing.newPricingDescription")}</DialogDescription>
          </DialogHeader>
          <Field label={t("trader.pricing.itemCode")} required>
            <ItemCodeField value={code} onChange={setCode} currency={currency} data-testid="pricing-item" />
          </Field>
          <Field label={t("trader.pricing.price")} required>
            <DecimalField value={price} onChange={(e) => { setPrice(e.target.value); }} data-testid="pricing-price" />
          </Field>
          {canCost ? (
            <Field label={t("trader.pricing.costPerUnit")} description={t("trader.pricing.costHint")}>
              <DecimalField value={cost} onChange={(e) => { setCost(e.target.value); }} data-testid="pricing-cost" />
            </Field>
          ) : null}
          <FormError message={error} />
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>{t("common.cancel")}</Button>
            <Button type="submit" loading={save.isPending} data-testid="save-pricing">{t("trader.pricing.savePricing")}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
