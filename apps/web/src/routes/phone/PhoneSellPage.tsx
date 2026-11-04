import { Button } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, SelectField } from "../common";
import { useCompanyContext, useWarehouses } from "../inventory/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";
import { QuickCreateButton } from "../QuickCreate";
import { money, n, printVariants, useCustomers, useSellableItems } from "./shared";

interface Line {
  itemId: string;
  quantity: string;
  price: string;
}

interface Sold {
  invoiceId: string;
  number: string;
  amount: number;
  currency: string;
  partnerId: string;
}

const emptyLine = (): Line => ({ itemId: "", quantity: "1", price: "" });

/** A sale from the phone: the customer, the items in stock with the board's current price, the total; then its invoice or its payment. */
export function PhoneSellPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { companyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  const customers = useCustomers(companyId);
  const warehouses = useWarehouses(companyId);
  const sellable = useSellableItems(companyId);
  const [partnerId, setPartnerId] = useState("");
  const [warehouseId, setWarehouseId] = useState("");
  const [lines, setLines] = useState<Line[]>([emptyLine()]);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [sold, setSold] = useState<Sold | null>(null);
  const [printing, setPrinting] = useState<PrintSubject | null>(null);
  const rows = sellable.items;
  const warehouse = warehouseId !== "" ? warehouseId : (warehouses.data?.[0]?.id ?? "");
  const total = lines.reduce((sum, line) => sum + n(line.quantity) * n(line.price), 0);

  const update = (index: number, patch: Partial<Line>): void => { setLines((prev) => prev.map((line, i) => (i === index ? { ...line, ...patch } : line))); };
  const chooseItem = (index: number, itemId: string): void => {
    const row = rows.find((r) => r.itemId === itemId);
    update(index, { itemId, price: row?.price === null || row?.price === undefined ? "" : String(row.price) });
  };
  // The refusal a trader meets most often, in his own words from the facts the server gave.
  const wordProblem = (p: FormProblem | null): string | null => {
    if (p?.code === "quick_sale.not_enough_stock" && p.why) {
      return t("trader.sales.notEnoughStock", { item: typeof p.why.item === "string" ? p.why.item : "", available: formatNumber(n(p.why.available as number | string | null)), wanted: formatNumber(n(p.why.wanted as number | string | null)) });
    }
    return p?.message ?? null;
  };

  const sell = useMutation({
    mutationFn: async () =>
      unwrap(await api.POST("/api/v1/sales/quick-sales", {
        body: { companyId, partnerId, warehouseId: warehouse, lines: lines.filter((line) => line.itemId).map((line) => ({ itemId: line.itemId, quantity: n(line.quantity), unitPrice: line.price.trim() ? n(line.price) : null })) },
      })),
    onSuccess: async (result) => {
      setProblem(null);
      setSold({ invoiceId: result.invoiceId, number: result.invoiceNumber, amount: n(result.totalGross), currency: result.currency, partnerId });
      await Promise.all([["invoices"], ["ar-aging"], ["stock"], ["stock-balances"], ["trader-pricing"], ["trader-cash"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    sell.mutate();
  };
  const reset = (): void => {
    setSold(null);
    setLines([emptyLine()]);
    setProblem(null);
  };

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("phone.sell.title")}</h1>
      {sold ? (
        <div className="flex flex-col gap-3 rounded-lg border border-success/40 bg-success-soft p-4" data-testid="phone-sold">
          <p role="status" className="font-medium">{t("phone.sell.sold", { number: sold.number, amount: money(sold.amount, sold.currency) })}</p>
          <Button type="button" size="lg" className="h-12 text-base" onClick={() => { setPrinting({ documentType: "sales_invoice", documentId: sold.invoiceId, number: sold.number, partnerId: sold.partnerId, variants: printVariants }); }} data-testid="phone-invoice-pdf">{t("phone.sell.invoicePdf")}</Button>
          <Button type="button" size="lg" variant="secondary" className="h-12 text-base" onClick={() => { void navigate({ to: "/p/collect", search: { invoice: sold.invoiceId } }); }} data-testid="phone-collect-now">{t("phone.sell.collectNow")}</Button>
          <Button type="button" size="lg" variant="ghost" className="h-12 text-base" onClick={reset} data-testid="phone-new-sale">{t("phone.sell.newSale")}</Button>
        </div>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <Field label={t("phone.sell.customer")} required>
            <div className="flex items-center gap-2">
              <SelectField className="h-12 flex-1 text-base" value={partnerId} onChange={(event) => { setPartnerId(event.target.value); }} required data-testid="phone-sale-customer">
                <option value="">{t("phone.sell.chooseCustomer")}</option>
                {(customers.data ?? []).map((customer) => (
                  <option key={customer.partnerId} value={customer.partnerId}>
                    {customer.partnerCode} · {localized(customer.partnerName)}
                  </option>
                ))}
              </SelectField>
              {companyId ? <QuickCreateButton kind="customer" companyId={companyId} currency={currency} onCreated={(created) => { void queryClient.invalidateQueries({ queryKey: ["customers"] }); setPartnerId(created.id); }} /> : null}
            </div>
          </Field>
          {(warehouses.data?.length ?? 0) > 1 ? (
            <Field label={t("phone.sell.warehouse")}>
              <SelectField className="h-12 text-base" value={warehouse} onChange={(event) => { setWarehouseId(event.target.value); }} data-testid="phone-sale-warehouse">
                {(warehouses.data ?? []).map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.code} · {localized(w.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
          ) : null}
          {rows.length === 0 && !sellable.isPending ? <p className="text-sm text-fg-muted" data-testid="phone-no-items">{t("phone.sell.noItems")}</p> : null}
          <div className="flex flex-col gap-2">
            {lines.map((line, index) => (
              <div key={index} className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3" data-testid="phone-line">
                <Field label={t("phone.sell.item")} required>
                  <SelectField className="h-12 text-base" value={line.itemId} onChange={(event) => { chooseItem(index, event.target.value); }} required data-testid={`phone-line-item-${index}`}>
                    <option value="">{t("phone.sell.chooseItem")}</option>
                    {rows.map((row) => (
                      <option key={row.itemId} value={row.itemId}>
                        {row.itemCode} · {localized(row.itemName)}{n(row.onHand) > 0 ? ` (${formatNumber(n(row.onHand))})` : ""}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <div className="grid grid-cols-2 gap-2">
                  <Field label={t("phone.sell.quantity")} required>
                    <DecimalField className="h-12 text-base" value={line.quantity} onChange={(event) => { update(index, { quantity: event.target.value }); }} required data-testid={`phone-line-qty-${index}`} />
                  </Field>
                  <Field label={t("phone.sell.price")} required>
                    <DecimalField className="h-12 text-base" value={line.price} onChange={(event) => { update(index, { price: event.target.value }); }} required data-testid={`phone-line-price-${index}`} />
                  </Field>
                </div>
                {lines.length > 1 ? <Button type="button" variant="ghost" size="sm" className="self-start" onClick={() => { setLines((prev) => prev.filter((_, i) => i !== index)); }}>{t("phone.sell.removeLine")}</Button> : null}
              </div>
            ))}
            <Button type="button" variant="secondary" onClick={() => { setLines((prev) => [...prev, emptyLine()]); }} data-testid="phone-add-line">{t("phone.sell.addLine")}</Button>
          </div>
          <div className="flex items-center justify-between rounded-lg border border-border bg-surface p-3 text-lg font-semibold">
            <span>{t("phone.sell.total")}</span>
            <span className="tabular" dir="ltr" data-testid="phone-sale-total">{money(total, currency)}</span>
          </div>
          <FormError message={wordProblem(problem)} />
          <Button type="submit" size="lg" className="h-14 text-base" loading={sell.isPending} disabled={!partnerId || !warehouse || !lines.some((line) => line.itemId)} data-testid="phone-sell-submit">{t("phone.sell.submit")}</Button>
        </form>
      )}
      {printing ? <PrintDialog subject={printing} onClose={() => { setPrinting(null); }} /> : null}
    </div>
  );
}
