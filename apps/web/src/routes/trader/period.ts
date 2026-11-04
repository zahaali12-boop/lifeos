/**
 * The period a trader's list shows (ADR-0035 §18): a preset over the day it is, or a typed range. Days are kept as
 * YYYY-MM-DD strings and moved with UTC arithmetic, so the browser's time zone never shifts them.
 */
export const periodPresets = ["month", "today", "last7", "lastMonth", "quarter", "year", "all", "custom"] as const;

export type PeriodPreset = (typeof periodPresets)[number];

export interface Period {
  preset: PeriodPreset;
  /** The typed bounds, used by the custom preset only; either may be empty. */
  from: string;
  to: string;
}

export interface DateRange {
  from?: string;
  to?: string;
}

function ymd(iso: string): [number, number, number] {
  const [y, m, d] = iso.split("-").map(Number);
  return [y ?? 1970, m ?? 1, d ?? 1];
}

/** A day from year, month (1 to 12) and day, the month and day free to run over or under (day 0 is the last day of the month before). */
function iso(year: number, month: number, day: number): string {
  const date = new Date(Date.UTC(year, month - 1, day));
  return `${String(date.getUTCFullYear())}-${String(date.getUTCMonth() + 1).padStart(2, "0")}-${String(date.getUTCDate()).padStart(2, "0")}`;
}

/** The days a period covers on the day it is: nothing (everything) for "all", the typed bounds for "custom". */
export function periodRange(period: Period, today: string): DateRange {
  const [y, m, d] = ymd(today);
  switch (period.preset) {
    case "today":
      return { from: today, to: today };
    case "last7":
      return { from: iso(y, m, d - 6), to: today };
    case "month":
      return { from: iso(y, m, 1), to: today };
    case "lastMonth":
      return { from: iso(y, m - 1, 1), to: iso(y, m, 0) };
    case "quarter":
      return { from: iso(y, m - ((m - 1) % 3), 1), to: today };
    case "year":
      return { from: iso(y, 1, 1), to: today };
    case "custom":
      return { ...(period.from ? { from: period.from } : {}), ...(period.to ? { to: period.to } : {}) };
    default:
      return {};
  }
}

/** The range as query parameters, only those that are set. */
export function rangeParams(range: DateRange): { from?: string; to?: string } {
  return { ...(range.from ? { from: range.from } : {}), ...(range.to ? { to: range.to } : {}) };
}
