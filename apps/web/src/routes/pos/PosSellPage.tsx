import { Badge, Button, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useSearch } from "@tanstack/react-router";
import { Minus, Plus, Trash2, Undo2 } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { add, compare, isDecimal, subtract } from "../../lib/decimal";
import { formatNumber, localized } from "../../lib/format";
import { SelectField, TextField } from "../common";
import { useOnline } from "../mobile/context";
import { Receipt } from "./Receipt";
import { addOrder, changeFor, computeTotals, lineAmount, lineOf, useTill, type CatalogueItem, type LocalOrder, type PaymentMethod, type TillLine, type TillPayment } from "./store";
import { syncOrders } from "./sync";

/** Case-insensitive match on the code, a barcode or the name in any language. */
function matches(item: CatalogueItem, needle: string): boolean {
  const q = needle.trim().toLowerCase();
  return q.length === 0 || item.code.toLowerCase().includes(q) || item.barcodes.some((b) => b.toLowerCase() === q) || Object.values(item.name).some((n) => n.toLowerCase().includes(q));
}

/**
 * The till (A-180): the cached catalogue on one side, the cart and the tenders on the other. Everything is computed on
 * the device the way the server books it; the order is queued and sent at once when online, kept for the next sync
 * when not. A refund starts from an order of this session and gives back lines at their sold price.
 */
export function PosSellPage() {
  const { t } = useTranslation();
  const till = useTill();
  const online = useOnline();
  const search: { refund?: string } = useSearch({ strict: false });
  const [query, setQuery] = useState("");
  const [lines, setLines] = useState<TillLine[]>([]);
  const [partnerId, setPartnerId] = useState<string>("");
  const [method, setMethod] = useState<PaymentMethod>("cash");
  const [tendered, setTendered] = useState("");
  const [reference, setReference] = useState("");
  const [refundOf, setRefundOf] = useState<LocalOrder | null>(null);
  const [done, setDone] = useState<LocalOrder | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const searchBox = useRef<HTMLInputElement>(null);
  const companies = useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")), enabled: online, staleTime: 600_000 });
  const companyName = localized((companies.data ?? []).find((c) => c.id === till.companyId)?.legalName) || "";

  const catalogue = till.catalogue;
  const register = till.register;
  const session = till.session;
  const minor = Number(catalogue?.minorUnits ?? 2);
  const money = (value: string | number): string => formatNumber(value, { minimumFractionDigits: minor, maximumFractionDigits: minor });

  // A refund named in the URL (from the orders screen) loads the sale's lines, once per arrival.
  const appliedRefund = useRef<string | null>(null);
  useEffect(() => {
    if (search.refund && appliedRefund.current !== search.refund && !refundOf) {
      appliedRefund.current = search.refund;
      const sale = till.orders.find((o) => o.deviceOrderId === search.refund && o.kind === "sale" && o.status === "synced");
      if (sale) {
        setRefundOf(sale);
        setLines(sale.lines.map((l) => ({ ...l })));
        setPartnerId(sale.partnerId ?? "");
        setMethod("cash");
        setDone(null);
      }
    }
  }, [search.refund, refundOf, till.orders]);

  const visible = useMemo(() => (catalogue ? catalogue.items.filter((i) => matches(i, query)).slice(0, 60) : []), [catalogue, query]);
  const allCash = method === "cash";
  const totals = useMemo(() => (catalogue ? computeTotals(lines, catalogue, allCash) : null), [lines, catalogue, allCash]);
  const payments: TillPayment[] = useMemo(() => {
    if (!totals) {
      return [];
    }
    if (method === "cash") {
      const amount = tendered.trim().length > 0 && isDecimal(tendered.trim()) ? tendered.trim() : totals.due;
      return [{ method: "cash", amount, reference: null }];
    }
    return [{ method, amount: totals.due, reference: reference.trim() || null }];
  }, [method, tendered, reference, totals]);
  const change = totals ? (method === "cash" && !refundOf ? changeFor(payments, totals.due) : "0") : "0";
  const shortBy = totals && method === "cash" && !refundOf ? subtract(totals.due, payments[0]?.amount ?? "0") : "0";

  if (!register || !catalogue || session?.status !== "open") {
    return (
      <div className="mx-auto max-w-xl rounded-lg border border-border bg-surface p-6 text-center">
        <p className="text-sm text-fg-muted">{t("pos.till.noSession")}</p>
        <Button asChild className="mt-3"><Link to="/pos">{t("pos.till.tabs.register")}</Link></Button>
      </div>
    );
  }

  const addItem = (item: CatalogueItem): void => {
    setDone(null);
    setMessage(null);
    if (item.unitPrice === null) {
      if (!catalogue.allowPriceOverride) {
        setMessage(t("pos.till.noPrice", { code: item.code }));
        return;
      }
    }
    setLines((current) => {
      const index = current.findIndex((l) => l.itemId === item.itemId && l.variantId === (item.variantId ?? null));
      if (index >= 0) {
        return current.map((l, i) => (i === index ? { ...l, quantity: add(l.quantity, "1") } : l));
      }
      return [...current, lineOf(item)];
    });
  };
  const onSearch = (): void => {
    const code = query.trim();
    if (code.length === 0) {
      return;
    }
    const exact = catalogue.items.find((i) => i.barcodes.some((b) => b.toLowerCase() === code.toLowerCase()) || i.code.toLowerCase() === code.toLowerCase()) ?? visible[0];
    if (exact) {
      addItem(exact);
      setQuery("");
    } else {
      setMessage(t("pos.till.notFound", { code }));
    }
  };
  const setQuantity = (index: number, quantity: string): void => {
    setLines((current) => current.map((l, i) => (i === index ? { ...l, quantity } : l)));
  };
  const bump = (index: number, delta: string): void => {
    setLines((current) => current.flatMap((l, i) => {
      if (i !== index) {
        return [l];
      }
      const next = add(l.quantity, delta);
      if (refundOf) {
        const sold = refundOf.lines.find((s) => s.itemId === l.itemId && s.variantId === l.variantId)?.quantity ?? "0";
        if (compare(next, sold) > 0) {
          return [l];
        }
      }
      return compare(next, "0") <= 0 ? [] : [{ ...l, quantity: next }];
    }));
  };
  const setPrice = (index: number, unitPrice: string): void => {
    setLines((current) => current.map((l, i) => (i === index ? { ...l, unitPrice } : l)));
  };
  const reset = (): void => {
    setLines([]);
    setTendered("");
    setReference("");
    setMethod("cash");
    setPartnerId(register.defaultPartnerId ?? "");
    setRefundOf(null);
    setMessage(null);
    searchBox.current?.focus();
  };
  const valid = totals !== null && lines.length > 0 && lines.every((l) => isDecimal(l.quantity) && compare(l.quantity, "0") > 0 && isDecimal(l.unitPrice) && compare(l.unitPrice, "0") >= 0)
    && (method !== "credit" || (partnerId.length > 0 && catalogue.allowCredit))
    && (refundOf !== null || method !== "cash" || compare(shortBy, "0") <= 0);
  const charge = async (): Promise<void> => {
    if (!totals || !valid) {
      return;
    }
    const customer = till.customers.find((c) => c.id === partnerId);
    const order: LocalOrder = {
      deviceOrderId: crypto.randomUUID(),
      kind: refundOf ? "refund" : "sale",
      refundsDeviceOrderId: refundOf?.deviceOrderId ?? null,
      partnerId: partnerId || null,
      partnerName: customer ? localized(customer.name) : null,
      orderedAt: new Date().toISOString(),
      lines,
      payments: refundOf ? [{ method, amount: totals.due, reference: reference.trim() || null }] : payments,
      changeGiven: change,
      totals,
      status: "queued",
      error: null,
      server: null,
    };
    addOrder(order);
    setDone(order);
    setLines([]);
    setTendered("");
    setReference("");
    setRefundOf(null);
    if (navigator.onLine) {
      await syncOrders();
    }
  };
  const shown = done ? till.orders.find((o) => o.deviceOrderId === done.deviceOrderId) ?? done : null;
  const sales = till.orders.filter((o) => o.kind === "sale" && o.status === "synced");

  return (
    <div className="grid gap-4 lg:grid-cols-[1fr_420px]">
      <section aria-label={t("pos.till.catalogue")} className="flex flex-col gap-3">
        <div className="flex flex-wrap items-end gap-2">
          <Field label={t("pos.till.search")} className="min-w-64 flex-1">
            <TextField ref={searchBox} value={query} autoFocus dir="auto" placeholder={t("pos.till.searchHint")} data-testid="till-search" onChange={(event) => { setQuery(event.target.value); setMessage(null); }} onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); onSearch(); } }} />
          </Field>
          {!refundOf && sales.length > 0 ? (
            <Field label={t("pos.till.refundOf")}>
              <SelectField value="" data-testid="till-refund-pick" onChange={(event) => {
                const sale = sales.find((o) => o.deviceOrderId === event.target.value);
                if (sale) {
                  setRefundOf(sale);
                  setLines(sale.lines.map((l) => ({ ...l })));
                  setPartnerId(sale.partnerId ?? "");
                  setMethod("cash");
                  setDone(null);
                }
              }}>
                <option value="">{t("pos.till.pickSale")}</option>
                {sales.map((o) => <option key={o.deviceOrderId} value={o.deviceOrderId}>{o.server?.number ?? o.deviceOrderId.slice(0, 8)} · {money(o.totals.due)}</option>)}
              </SelectField>
            </Field>
          ) : null}
        </div>
        {message ? <p role="status" className="text-sm text-warning" data-testid="till-message">{message}</p> : null}
        {refundOf ? (
          <p className="rounded-md border border-warning/40 bg-warning-soft px-3 py-2 text-sm" data-testid="till-refund-note">
            <Undo2 className="me-1 inline size-4" aria-hidden="true" />
            {t("pos.till.refunding", { number: refundOf.server?.number ?? refundOf.deviceOrderId.slice(0, 8) })}
            <Button variant="ghost" size="sm" className="ms-2" onClick={reset}>{t("common.cancel")}</Button>
          </p>
        ) : (
          <ul className="grid grid-cols-2 gap-2 sm:grid-cols-3 xl:grid-cols-4" data-testid="till-items">
            {visible.map((item) => (
              <li key={`${item.itemId}-${item.variantId ?? ""}`}>
                <button type="button" className="flex h-24 w-full flex-col justify-between rounded-lg border border-border bg-surface p-3 text-start hover:border-accent focus-visible:border-accent" data-testid={`till-item-${item.code}`} onClick={() => { addItem(item); }}>
                  <span className="line-clamp-2 text-sm font-medium" dir="auto">{localized(item.name)}</span>
                  <span className="flex w-full items-end justify-between text-xs text-fg-muted">
                    <span dir="ltr">{item.code}</span>
                    <span className="text-sm font-semibold text-fg tabular-nums" dir="ltr">{item.unitPrice === null ? "—" : money(item.unitPrice)}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <aside aria-label={t("pos.till.cart")} className="flex flex-col gap-3">
        {shown ? (
          <>
            <Receipt order={shown} register={register} catalogue={catalogue} companyName={companyName} onPrint={() => { window.print(); }} />
            <Button size="lg" className="h-12" onClick={reset} data-testid="till-new-sale">{t("pos.till.newSale")}</Button>
          </>
        ) : (
          <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4">
            <h2 className="font-semibold">{refundOf ? t("pos.till.refundLines") : t("pos.till.cart")}</h2>
            {lines.length === 0 ? <p className="text-sm text-fg-muted" data-testid="till-cart-empty">{t("pos.till.cartEmpty")}</p> : null}
            <ul className="flex flex-col divide-y divide-border" data-testid="till-cart">
              {lines.map((line, index) => (
                <li key={`${line.itemId}-${line.variantId ?? ""}`} className="flex flex-col gap-1 py-2" data-testid="till-cart-line">
                  <div className="flex items-start justify-between gap-2">
                    <span className="text-sm font-medium" dir="auto">{localized(line.name)}</span>
                    <span className="text-sm font-semibold tabular-nums" dir="ltr">{money(lineAmount(line, minor))}</span>
                  </div>
                  <div className="flex items-center gap-2">
                    <Button variant="secondary" size="icon" aria-label={t("pos.till.less")} onClick={() => { bump(index, "-1"); }}><Minus aria-hidden="true" /></Button>
                    <TextField inputMode="decimal" dir="ltr" className="w-20 text-center" aria-label={t("pos.till.quantity")} value={line.quantity} data-testid="till-line-quantity" onChange={(event) => { setQuantity(index, event.target.value); }} />
                    <Button variant="secondary" size="icon" aria-label={t("pos.till.more")} disabled={Boolean(refundOf)} onClick={() => { bump(index, "1"); }}><Plus aria-hidden="true" /></Button>
                    <span className="text-xs text-fg-muted" dir="ltr">× {catalogue.allowPriceOverride && !refundOf ? null : money(line.unitPrice)}</span>
                    {catalogue.allowPriceOverride && !refundOf ? <TextField inputMode="decimal" dir="ltr" className="w-24" aria-label={t("pos.till.unitPrice")} value={line.unitPrice} data-testid="till-line-price" onChange={(event) => { setPrice(index, event.target.value); }} /> : null}
                    <Button variant="ghost" size="icon" className="ms-auto" aria-label={t("pos.till.remove")} onClick={() => { setLines((current) => current.filter((_, i) => i !== index)); }}><Trash2 aria-hidden="true" /></Button>
                  </div>
                </li>
              ))}
            </ul>
            {till.customers.length > 0 ? (
              <Field label={t("pos.till.customer")}>
                <SelectField value={partnerId} data-testid="till-customer" disabled={Boolean(refundOf)} onChange={(event) => { setPartnerId(event.target.value); if (event.target.value === "" && method === "credit") { setMethod("cash"); } }}>
                  <option value="">{t("pos.till.walkIn")}</option>
                  {till.customers.map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>)}
                </SelectField>
              </Field>
            ) : null}
            {totals ? (
              <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-0.5 text-sm">
                <dt className="text-fg-muted">{t("pos.till.net")}</dt><dd className="text-end tabular-nums" dir="ltr">{money(totals.net)}</dd>
                <dt className="text-fg-muted">{t("pos.till.tax")}</dt><dd className="text-end tabular-nums" dir="ltr" data-testid="till-tax">{money(totals.tax)}</dd>
                {totals.rounding !== "0" ? <><dt className="text-fg-muted">{t("pos.till.rounding")}</dt><dd className="text-end tabular-nums" dir="ltr" data-testid="till-rounding">{money(totals.rounding)}</dd></> : null}
                <dt className="text-lg font-semibold">{refundOf ? t("pos.till.toRefund") : t("pos.till.totalDue")}</dt><dd className="text-end text-lg font-semibold tabular-nums" dir="ltr" data-testid="till-total">{money(totals.due)} {catalogue.currency}</dd>
              </dl>
            ) : null}
            <div className="flex flex-wrap gap-2" role="group" aria-label={t("pos.till.payment")}>
              {(["cash", "card", "credit"] as const).filter((m) => m !== "credit" || (catalogue.allowCredit && partnerId.length > 0)).filter((m) => m !== "card" || register.cardAccountId).map((m) => (
                <Button key={m} variant={method === m ? "primary" : "secondary"} aria-pressed={method === m} data-testid={`till-method-${m}`} onClick={() => { setMethod(m); }}>{t(`pos.paymentMethods.${m}`)}</Button>
              ))}
            </div>
            {method === "cash" && !refundOf ? (
              <Field label={t("pos.till.tendered")}>
                <TextField inputMode="decimal" dir="ltr" placeholder={totals ? money(totals.due) : ""} value={tendered} data-testid="till-tendered" onChange={(event) => { setTendered(event.target.value); }} />
              </Field>
            ) : null}
            {method === "card" ? (
              <Field label={t("pos.till.reference")}>
                <TextField dir="ltr" value={reference} data-testid="till-reference" onChange={(event) => { setReference(event.target.value); }} />
              </Field>
            ) : null}
            {method === "cash" && !refundOf && compare(change, "0") > 0 ? <p className="text-sm" data-testid="till-change">{t("pos.till.changeDue", { amount: money(change) })}</p> : null}
            {method === "cash" && !refundOf && compare(shortBy, "0") > 0 ? <p className="text-sm text-warning">{t("pos.till.shortBy", { amount: money(shortBy) })}</p> : null}
            {!online ? <Badge tone="warning">{t("pos.till.offlineQueued")}</Badge> : null}
            <Button size="lg" className="h-12" disabled={!valid} onClick={() => { void charge(); }} data-testid="till-charge">
              {refundOf ? t("pos.till.refund") : t("pos.till.charge", { amount: totals ? money(totals.due) : "" })}
            </Button>
          </div>
        )}
      </aside>
    </div>
  );
}
