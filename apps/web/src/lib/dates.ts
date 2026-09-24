import { getSession } from "../session/session";

/**
 * The calendar date (YYYY-MM-DD) it is now in a time zone: the signed-in person's (their profile), else the
 * browser's. The API dates documents in the company's time zone, so a form defaulting to "today" must not use the UTC
 * date, which is still yesterday in Baghdad from 21:00 UTC.
 */
export function todayIn(timeZone?: string, now: Date = new Date()): string {
  const zone = timeZone ?? getSession()?.user.timeZone;
  try {
    const parts = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit", numberingSystem: "latn" }).formatToParts(now);
    const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((p) => p.type === type)?.value ?? "";
    return `${part("year")}-${part("month")}-${part("day")}`;
  } catch {
    // An unknown zone falls back to the browser's own calendar date.
    const pad = (n: number): string => String(n).padStart(2, "0");
    return `${String(now.getFullYear())}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  }
}
