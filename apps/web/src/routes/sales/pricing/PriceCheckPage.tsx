import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation } from "@tanstack/react-query";
import { Plus, SearchCheck, Trash2 } from "lucide-react";
import { Fragment, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../../api";
import { todayIn } from "../../../lib/dates";
import { formatDate, formatNumber, localized } from "../../../lib/format";
import { useCan } from "../../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../../common";
import { ItemCodeField } from "../../inventory/ItemCodeField";
import { CompanyFilter, findItemByCode, useCompanyContext, type Item } from "../../inventory/shared";
import { Money, money } from "../shared";
import { PriceExplanation, PriceStepView, price, useCompanyCustomers, usePriceLists, type PricingResult } from "./shared";

interface BasketLine {
  key: string;
  itemCode: string;
  item: Item | null;
  uomId: string;
  quantity: string;
  manualUnitPrice: string;
  manualDiscountPct: string;
}

let nextKey = 1;
const newLine = (): BasketLine => ({ key: String(nextKey++), itemCode: "", item: null, uomId: "", quantity: "1", manualUnitPrice: "", manualDiscountPct: "" });

/**
 * The price check (roadmap 5.2): price a basket for a customer on a date exactly as a quote or an order will, and see
 * why each line costs what it does: the source that won and the ones passed over, units, currency, discounts,
 * promotions, document discounts and the floor.
 */
export function PriceCheckPage() {
  const { t } = useTranslation();
  const can = useCan();
  const mayOverride = can("pricing.price.override");
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const customers = useCompanyCustomers(companyId);
  const lists = usePriceLists(companyId);
  const [partnerId, setPartnerId] = useState("");
  const [currency, setCurrency] = useState("");
  const [pricingDate, setPricingDate] = useState(todayIn());
  const [priceListId, setPriceListId] = useState("");
  const [channel, setChannel] = useState("");
  const [coupons, setCoupons] = useState("");
  const [documentDiscount, setDocumentDiscount] = useState("");
  const [lines, setLines] = useState<BasketLine[]>([newLine()]);
  const [open, setOpen] = useState<string | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const updateLine = (index: number, patch: Partial<BasketLine>): void => { setLines((prev) => prev.map((line, i) => (i === index ? { ...line, ...patch } : line))); };
  const lookUp = async (index: number, code: string): Promise<void> => {
    const item = await findItemByCode(code);
    updateLine(index, { item, uomId: item ? item.salesUomId ?? item.baseUomId : "" });
  };

  const calculate = useMutation({
    mutationFn: async (): Promise<PricingResult> => {
      const resolved = await Promise.all(lines.filter((l) => l.itemCode.trim()).map(async (l) => ({ line: l, item: l.item?.code === l.itemCode ? l.item : await findItemByCode(l.itemCode) })));
      const unknown = resolved.find((r) => !r.item);
      if (unknown) {
        throw new Error(t("pricing.itemUnknown", { code: unknown.line.itemCode }));
      }
      return unwrap(await api.POST("/api/v1/pricing/calculate", {
        body: {
          companyId,
          partnerId: partnerId || null,
          currency: currency.trim() || null,
          pricingDate: pricingDate || null,
          priceListId: priceListId || null,
          channel: channel.trim() || null,
          couponCodes: coupons.split(/[\s,]+/u).filter(Boolean),
          documentDiscountPct: documentDiscount ? Number(documentDiscount) : null,
          lines: resolved.map(({ line, item }) => ({
            key: line.key,
            itemId: item?.id ?? "",
            variantId: null,
            uomId: line.uomId || null,
            quantity: Number(line.quantity),
            manualUnitPrice: line.manualUnitPrice ? Number(line.manualUnitPrice) : null,
            manualDiscountPct: line.manualDiscountPct ? Number(line.manualDiscountPct) : null,
          })),
        },
      }));
    },
    onSuccess: (result) => { setProblem(null); setOpen(result.lines.find((l) => l.problem)?.key ?? result.lines[0]?.key ?? null); },
    onError: (error) => { setProblem(toFormProblem(error, t("pricing.calculateFailed"))); },
  });
  const result = calculate.data;
  const submit = (event: FormEvent): void => { event.preventDefault(); calculate.mutate(); };
  const itemCodes = new Map(lines.map((l) => [l.key, l.itemCode]));

  return (
    <>
      <PageHeader title={t("nav.priceCheck")} description={t("pricing.priceCheckDescription")} />
      <form onSubmit={submit} className="flex flex-col gap-4" data-testid="price-check-form">
        <div className="grid gap-3 sm:grid-cols-4">
          <CompanyFilter companies={companies} value={companyId} onChange={(id) => { setCompanyId(id); setPartnerId(""); setPriceListId(""); }} />
          <Field label={t("pricing.customer")}>
            <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="check-customer">
              <option value="">{t("pricing.noCustomer")}</option>
              {(customers.data ?? []).map((c) => (
                <option key={c.partnerId} value={c.partnerId}>
                  {c.partnerCode} · {localized(c.partnerName)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("pricing.pricingDate")}>
            <TextField type="date" value={pricingDate} onChange={(e) => { setPricingDate(e.target.value); }} dir="ltr" data-testid="check-date" />
          </Field>
          <Field label={t("pricing.currency")} description={t("pricing.currencyHint")}>
            <TextField value={currency} onChange={(e) => { setCurrency(e.target.value.toUpperCase()); }} maxLength={3} dir="ltr" data-testid="check-currency" />
          </Field>
          <Field label={t("pricing.priceList")}>
            <SelectField value={priceListId} onChange={(e) => { setPriceListId(e.target.value); }} data-testid="check-price-list">
              <option value="">{t("pricing.customersList")}</option>
              {(lists.data ?? []).map((l) => (
                <option key={l.id} value={l.id}>
                  {l.code} · {localized(l.name)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("pricing.channel")}>
            <TextField value={channel} onChange={(e) => { setChannel(e.target.value); }} dir="ltr" />
          </Field>
          <Field label={t("pricing.coupons")}>
            <TextField value={coupons} onChange={(e) => { setCoupons(e.target.value.toUpperCase()); }} dir="ltr" data-testid="check-coupons" />
          </Field>
          {mayOverride ? (
            <Field label={t("pricing.documentDiscountPct")}>
              <TextField type="number" min={0} max={100} step="any" value={documentDiscount} onChange={(e) => { setDocumentDiscount(e.target.value); }} dir="ltr" />
            </Field>
          ) : null}
        </div>

        <Table aria-label={t("pricing.basket")}>
          <TableHeader>
            <TableRow>
              <TableHead className="w-56">{t("pricing.item")}</TableHead>
              <TableHead className="w-32">{t("pricing.uom")}</TableHead>
              <TableHead className="w-28 text-end">{t("pricing.quantity")}</TableHead>
              {mayOverride ? <TableHead className="w-36 text-end">{t("pricing.typedPrice")}</TableHead> : null}
              {mayOverride ? <TableHead className="w-28 text-end">{t("pricing.typedDiscount")}</TableHead> : null}
              <TableHead className="w-12">
                <span className="sr-only">{t("common.actions")}</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {lines.map((line, index) => (
              <TableRow key={line.key} data-testid="basket-line">
                <TableCell>
                  <ItemCodeField aria-label={t("pricing.item")} value={line.itemCode} onChange={(code) => { updateLine(index, { itemCode: code, item: null }); }} onBlur={() => { void lookUp(index, line.itemCode); }} data-testid={`basket-item-${String(index)}`} />
                  {line.item ? <span className="text-xs text-fg-muted" dir="auto">{localized(line.item.name)}</span> : null}
                </TableCell>
                <TableCell>
                  <SelectField aria-label={t("pricing.uom")} value={line.uomId} onChange={(e) => { updateLine(index, { uomId: e.target.value }); }} data-testid={`basket-uom-${String(index)}`}>
                    {(line.item?.uoms ?? []).map((u) => (
                      <option key={u.uomId} value={u.uomId}>
                        {u.uomCode}
                      </option>
                    ))}
                  </SelectField>
                </TableCell>
                <TableCell>
                  <TextField aria-label={t("pricing.quantity")} type="number" min={0} step="any" value={line.quantity} onChange={(e) => { updateLine(index, { quantity: e.target.value }); }} className="text-end" dir="ltr" data-testid={`basket-qty-${String(index)}`} />
                </TableCell>
                {mayOverride ? (
                  <TableCell>
                    <TextField aria-label={t("pricing.typedPrice")} type="number" min={0} step="any" value={line.manualUnitPrice} onChange={(e) => { updateLine(index, { manualUnitPrice: e.target.value }); }} className="text-end" dir="ltr" />
                  </TableCell>
                ) : null}
                {mayOverride ? (
                  <TableCell>
                    <TextField aria-label={t("pricing.typedDiscount")} type="number" min={0} max={100} step="any" value={line.manualDiscountPct} onChange={(e) => { updateLine(index, { manualDiscountPct: e.target.value }); }} className="text-end" dir="ltr" data-testid={`basket-discount-${String(index)}`} />
                  </TableCell>
                ) : null}
                <TableCell>
                  <Button type="button" variant="ghost" size="sm" aria-label={t("pricing.removeLine")} onClick={() => { setLines((prev) => (prev.length > 1 ? prev.filter((_, i) => i !== index) : prev)); }}>
                    <Trash2 aria-hidden="true" />
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        <FormError message={problem?.message ?? null} />
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" onClick={() => { setLines((prev) => [...prev, newLine()]); }} data-testid="add-basket-line">
            <Plus aria-hidden="true" />
            {t("pricing.addLine")}
          </Button>
          <Button type="submit" loading={calculate.isPending} disabled={!companyId} data-testid="calculate">
            <SearchCheck aria-hidden="true" />
            {t("pricing.calculate")}
          </Button>
        </div>
      </form>

      {result ? (
        <section className="mt-6 flex flex-col gap-4" aria-label={t("pricing.result")} data-testid="price-result">
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <Badge tone="neutral">{result.currency}</Badge>
            <span className="text-fg-muted">{t("pricing.pricedOn", { date: formatDate(result.pricingDate) })}</span>
            {result.pricesIncludeTax === true ? <Badge tone="accent">{t("pricing.pricesIncludeTax")}</Badge> : null}
            {result.hasFloorBlocks ? <Badge tone="danger">{t("pricing.floorBlocks")}</Badge> : null}
            {result.hasFloorWarnings ? <Badge tone="accent">{t("pricing.floorWarnings")}</Badge> : null}
            {Number(result.unpricedLines) > 0 ? <Badge tone="danger">{t("pricing.unpriced", { count: result.unpricedLines })}</Badge> : null}
          </div>
          <Table aria-label={t("pricing.pricedLines")}>
            <TableHeader>
              <TableRow>
                <TableHead>{t("pricing.item")}</TableHead>
                <TableHead className="text-end">{t("pricing.quantity")}</TableHead>
                <TableHead>{t("pricing.priceSource")}</TableHead>
                <TableHead className="text-end">{t("pricing.unitPrice")}</TableHead>
                <TableHead className="text-end">{t("pricing.gross")}</TableHead>
                <TableHead className="text-end">{t("pricing.discounts")}</TableHead>
                <TableHead className="text-end">{t("pricing.net")}</TableHead>
                <TableHead>
                  <span className="sr-only">{t("pricing.whyThisPrice")}</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {result.lines.map((line) => (
                <Fragment key={line.key}>
                  <TableRow data-testid="priced-line" data-line-key={line.key}>
                    <TableCell>
                      <span className="font-medium" dir="ltr">
                        {line.itemCode || itemCodes.get(line.key)}
                      </span>
                      {line.isFreeGoods ? (
                        <Badge tone="success" className="ms-2">
                          {t("pricing.freeGoods", { promotion: line.promotionCode ?? "" })}
                        </Badge>
                      ) : null}
                      {line.floor?.breached ? (
                        <Badge tone={line.floor.onBreach === "block" ? "danger" : "accent"} className="ms-2">
                          {t(`pricing.floorBreached.${line.floor.onBreach}`)}
                        </Badge>
                      ) : null}
                    </TableCell>
                    <TableCell className="text-end tabular" dir="ltr">
                      {formatNumber(line.quantity)} {line.uomCode}
                    </TableCell>
                    <TableCell>{line.problem ? <Badge tone="danger">{t("pricing.notPriced")}</Badge> : t(`pricing.source.${line.priceSource ?? ""}`, { defaultValue: line.priceSource ?? "" })}</TableCell>
                    <TableCell className="text-end tabular" dir="ltr" data-testid="line-unit-price">
                      {line.problem ? "" : price(line.unitPrice, result.currency)}
                    </TableCell>
                    <TableCell className="text-end">{line.problem ? null : <Money amount={line.grossAmount} currency={result.currency} />}</TableCell>
                    <TableCell className="text-end">{line.problem ? null : <Money amount={Number(line.lineDiscountAmount) + Number(line.promotionDiscountAmount) + Number(line.documentDiscountAmount)} currency={result.currency} />}</TableCell>
                    <TableCell className="text-end font-semibold">{line.problem ? null : <Money amount={line.netAmount} currency={result.currency} testId="line-net" />}</TableCell>
                    <TableCell>
                      <Button type="button" variant="ghost" size="sm" aria-expanded={open === line.key} onClick={() => { setOpen(open === line.key ? null : line.key); }} data-testid="why-price">
                        {t("pricing.why")}
                      </Button>
                    </TableCell>
                  </TableRow>
                  {open === line.key ? (
                    <TableRow>
                      <TableCell colSpan={8} className="bg-surface-sunken">
                        <PriceExplanation line={line} currency={result.currency} />
                      </TableCell>
                    </TableRow>
                  ) : null}
                </Fragment>
              ))}
            </TableBody>
          </Table>

          {result.documentSteps.length > 0 ? (
            <section className="flex flex-col gap-2" aria-label={t("pricing.basketSteps")} data-testid="document-steps">
              <h2 className="text-sm font-semibold">{t("pricing.basketSteps")}</h2>
              <ol className="flex flex-col gap-2">
                {result.documentSteps.map((step, index) => (
                  <PriceStepView key={`${step.kind}-${String(index)}`} step={step} currency={result.currency} index={index} />
                ))}
              </ol>
            </section>
          ) : null}

          <dl className="ms-auto grid w-full max-w-sm grid-cols-2 gap-1 text-sm" data-testid="price-totals">
            <dt className="text-fg-muted">{t("pricing.gross")}</dt>
            <dd className="text-end tabular" dir="ltr">
              {money(result.grossAmount, result.currency)}
            </dd>
            <dt className="text-fg-muted">{t("pricing.discounts")}</dt>
            <dd className="text-end tabular" dir="ltr">
              −{money(result.discountAmount, result.currency)}
            </dd>
            <dt className="font-semibold">{t("pricing.net")}</dt>
            <dd className="text-end font-semibold tabular" dir="ltr" data-testid="total-net">
              {money(result.netAmount, result.currency)}
            </dd>
          </dl>
        </section>
      ) : null}
    </>
  );
}
