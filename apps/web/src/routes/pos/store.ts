import { useSyncExternalStore } from "react";
import type { components } from "../../api/schema";
import { add, compare, multiply, roundTo, roundToIncrement, subtract, type RoundingMode } from "../../lib/decimal";

export type Register = components["schemas"]["PosRegisterSummary"];
export type Session = components["schemas"]["PosSessionSummary"];
export type Catalogue = components["schemas"]["PosCatalogue"];
export type CatalogueItem = components["schemas"]["PosCatalogueItem"];
export type ServerOrder = components["schemas"]["PosOrderSummary"];
export type PaymentMethod = "cash" | "card" | "credit";

/** A line of the cart or of an order kept on the device, in the catalogue's sales unit. */
export interface TillLine {
  itemId: string;
  variantId: string | null;
  code: string;
  name: Record<string, string>;
  uomId: string;
  uomCode: string;
  quantity: string;
  unitPrice: string;
  listUnitPrice: string | null;
  discountPct: string;
  taxCodeId: string | null;
  taxRatePct: string;
}

export interface TillPayment {
  method: PaymentMethod;
  amount: string;
  reference: string | null;
}

export interface Totals {
  net: string;
  tax: string;
  gross: string;
  rounding: string;
  due: string;
}

/**
 * An order made on this device (A-180): queued until the server posts it, then synced with the server's number and
 * e-invoice outcome, or failed with the server's reason for the cashier or the back office to act on. Orders of the
 * current session stay on the device for reprints and refunds.
 */
export interface LocalOrder {
  deviceOrderId: string;
  kind: "sale" | "refund";
  refundsDeviceOrderId: string | null;
  partnerId: string | null;
  partnerName: string | null;
  orderedAt: string;
  lines: TillLine[];
  payments: TillPayment[];
  changeGiven: string;
  totals: Totals;
  status: "queued" | "synced" | "failed";
  error: string | null;
  server: { id: string; number: string; einvoiceStatus: string | null; einvoiceQr: string | null } | null;
}

export interface TillCustomer {
  id: string;
  code: string;
  name: Record<string, string>;
}

export interface TillState {
  companyId: string | null;
  register: Register | null;
  session: Session | null;
  catalogue: Catalogue | null;
  customers: TillCustomer[];
  orders: LocalOrder[];
}

const STORAGE_KEY = "quicker.till";
const listeners = new Set<() => void>();
let state: TillState = load();

function empty(): TillState {
  return { companyId: null, register: null, session: null, catalogue: null, customers: [], orders: [] };
}

function load(): TillState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<TillState>;
      return { ...empty(), ...parsed, orders: Array.isArray(parsed.orders) ? parsed.orders : [], customers: Array.isArray(parsed.customers) ? parsed.customers : [] };
    }
  } catch {
    // storage unavailable or unreadable: start empty
  }
  return empty();
}

function commit(next: TillState): void {
  state = next;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // storage unavailable: the till lives in memory for this session
  }
  listeners.forEach((listener) => {
    listener();
  });
}

export function getTill(): TillState {
  return state;
}

export function useTill(): TillState {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getTill,
    getTill,
  );
}

export function setTill(patch: Partial<TillState>): void {
  commit({ ...state, ...patch });
}

/** Leaves the register: the catalogue and the session go; synced orders of the closed session go too, queued ones stay so nothing sold is lost. */
export function leaveRegister(): void {
  commit({ ...state, register: null, session: null, catalogue: null, orders: state.orders.filter((o) => o.status !== "synced") });
}

export function addOrder(order: LocalOrder): void {
  commit({ ...state, orders: [order, ...state.orders] });
}

export function updateOrder(deviceOrderId: string, patch: Partial<LocalOrder>): void {
  commit({ ...state, orders: state.orders.map((o) => (o.deviceOrderId === deviceOrderId ? { ...o, ...patch } : o)) });
}

export function removeOrder(deviceOrderId: string): void {
  commit({ ...state, orders: state.orders.filter((o) => o.deviceOrderId !== deviceOrderId) });
}

export function pendingOrders(): LocalOrder[] {
  return state.orders.filter((o) => o.status !== "synced");
}

/** A catalogue entry as a cart line of one unit at the catalogue's price. */
export function lineOf(item: CatalogueItem, quantity = "1"): TillLine {
  return {
    itemId: item.itemId,
    variantId: item.variantId ?? null,
    code: item.code,
    name: item.name,
    uomId: item.uomId,
    uomCode: item.uomCode,
    quantity,
    unitPrice: item.unitPrice === null ? "0" : String(item.unitPrice),
    listUnitPrice: item.unitPrice === null ? null : String(item.unitPrice),
    discountPct: "0",
    taxCodeId: item.taxCodeId ?? null,
    taxRatePct: String(item.taxRatePct),
  };
}

/** The line's amount after its discount, rounded to the currency, as the server computes it. */
export function lineAmount(line: TillLine, minorUnits: number, mode: RoundingMode = "half_away_from_zero"): string {
  const gross = multiply(line.quantity, line.unitPrice);
  const factor = subtract("1", multiply(line.discountPct, "0.01"));
  return roundTo(multiply(gross, factor), minorUnits, mode);
}

/**
 * The order's totals exactly as the server will book them (ADR-0032): each line's amount rounded, then its tax at the
 * catalogue's rate rounded per line (or per rate when the regime rounds at document level); prices that include tax
 * are split the way the tax engine splits them; cash rounding applies when the whole order is paid in cash.
 */
export function computeTotals(lines: TillLine[], catalogue: Catalogue, allCash: boolean): Totals {
  const minor = Number(catalogue.minorUnits);
  const mode: RoundingMode = catalogue.roundingMode === "half_even" ? "half_even" : "half_away_from_zero";
  const includeTax = catalogue.pricesIncludeTax;
  // The tax engine's arithmetic: the raw tax of a line is amount × rate / 100 (prices net of tax) or amount × rate / (100 + rate) (prices with tax in),
  // rounded per line, or summed per rate and rounded once when the regime rounds at document level.
  const rawTax = (amount: string, ratePct: string): string => (includeTax ? divide(multiply(amount, ratePct), add("100", ratePct)) : multiply(amount, multiply(ratePct, "0.01")));
  const perRate = new Map<string, { amount: string; raw: string }>();
  let net = "0";
  let tax = "0";
  let gross = "0";
  for (const line of lines) {
    const amount = lineAmount(line, minor, mode);
    const raw = rawTax(amount, line.taxRatePct);
    if (catalogue.taxRoundingLevel === "document") {
      const sofar = perRate.get(line.taxRatePct) ?? { amount: "0", raw: "0" };
      perRate.set(line.taxRatePct, { amount: add(sofar.amount, amount), raw: add(sofar.raw, raw) });
      continue;
    }
    const lineTax = roundTo(raw, minor, mode);
    tax = add(tax, lineTax);
    net = add(net, includeTax ? subtract(amount, lineTax) : amount);
    gross = add(gross, includeTax ? amount : add(amount, lineTax));
  }
  for (const { amount, raw } of perRate.values()) {
    const rateTax = roundTo(raw, minor, mode);
    tax = add(tax, rateTax);
    net = add(net, includeTax ? subtract(amount, rateTax) : amount);
    gross = add(gross, includeTax ? amount : add(amount, rateTax));
  }
  const increment = String(catalogue.cashRoundingIncrement);
  const due = allCash && compare(increment, "0") > 0 ? roundToIncrement(gross, increment, mode) : gross;
  return { net, tax, gross, rounding: subtract(due, gross), due };
}

/** a ÷ b to twelve decimals, enough for a split of tax out of a price that is then rounded to the currency. */
function divide(a: string, b: string): string {
  const scale = 12;
  const numerator = BigInt(digitsOf(a, scale));
  const denominator = BigInt(digitsOf(b, 0));
  if (denominator === 0n) {
    return "0";
  }
  const quotient = numerator / denominator;
  const text = quotient.toString().padStart(scale + 1, "0");
  return `${text.slice(0, text.length - scale)}.${text.slice(text.length - scale)}`.replace(/\.?0+$/, "");
}

/** The integer digits of a decimal text scaled up by the given number of decimals (truncating beyond them). */
function digitsOf(text: string, extraScale: number): string {
  const [whole = "0", fraction = ""] = text.split(".");
  return `${whole}${fraction.padEnd(extraScale, "0").slice(0, extraScale)}` || "0";
}

/** Cash tendered less what was due: the change to hand back; never negative. */
export function changeFor(payments: TillPayment[], due: string): string {
  const tendered = payments.reduce((sum, p) => add(sum, p.amount), "0");
  const change = subtract(tendered, due);
  return compare(change, "0") > 0 ? change : "0";
}
