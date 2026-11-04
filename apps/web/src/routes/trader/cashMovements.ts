/**
 * The arithmetic of the trader's cash and bank movements (ADR-0035 §24): a payment or receipt spread over the oldest
 * invoices first, and the rate a currency exchange was made at. Amounts are counted in thousandths (no currency the
 * program keeps has more decimals), so spreading never drifts the way adding fractions of a float does.
 */

/** What can happen to a box: money between two of the boxes, from a customer, to a supplier, for an expense, or the owner's own money in or out. */
export const movementKinds = ["transfer", "customer", "supplier", "expense", "ownerIn", "ownerOut"] as const;
export type MovementKind = (typeof movementKinds)[number];

/** The kinds that move money only through a box in the company's currency (an expense, the owner's money). */
export const companyCurrencyOnly: readonly MovementKind[] = ["expense", "ownerIn", "ownerOut"];

export interface OpenLike {
  id: string;
  remaining: number | string;
  dueDate: string;
  documentDate: string;
}

const SCALE = 1000;
const units = (value: number | string): number => Math.round(Number(value) * SCALE);
const amountOf = (value: number): number => value / SCALE;

/** The oldest due first (then the oldest invoice), each up to what it still asks for; what is left over stays on account. */
export function spreadOldestFirst(items: readonly OpenLike[], amount: number | string): { lines: { openItemId: string; amount: number }[]; onAccount: number } {
  let left = units(amount);
  const lines: { openItemId: string; amount: number }[] = [];
  const ordered = [...items].filter((i) => units(i.remaining) > 0).sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.documentDate.localeCompare(b.documentDate));
  for (const item of ordered) {
    if (left <= 0) {
      break;
    }
    const take = Math.min(left, units(item.remaining));
    lines.push({ openItemId: item.id, amount: amountOf(take) });
    left -= take;
  }
  return { lines, onAccount: left > 0 ? amountOf(left) : 0 };
}

/** What is still owed on the items, exactly. */
export function owedOn(items: readonly OpenLike[]): number {
  return amountOf(items.reduce((sum, i) => sum + Math.max(0, units(i.remaining)), 0));
}

/**
 * The rate of an exchange between a foreign currency and the company's: how many of the company's units one foreign
 * unit cost. Null when one side is missing or both are in one currency.
 */
export function exchangeRate(sent: number | string, sentCurrency: string, received: number | string, receivedCurrency: string, companyCurrency: string): { foreign: string; rate: number } | null {
  if (sentCurrency === receivedCurrency || Number(sent) <= 0 || Number(received) <= 0) {
    return null;
  }
  if (sentCurrency === companyCurrency) {
    return { foreign: receivedCurrency, rate: Number(sent) / Number(received) };
  }
  if (receivedCurrency === companyCurrency) {
    return { foreign: sentCurrency, rate: Number(received) / Number(sent) };
  }
  return null;
}
