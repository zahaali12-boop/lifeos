import { useSyncExternalStore } from "react";
import { currentLanguage } from "../i18n";

/** Western ("latn") or Eastern Arabic ("arab") digits: a per-user preference (ADR-0027), applied through Intl. */
export type DigitStyle = "latn" | "arab";

const STORAGE_KEY = "quicker.digits";
const listeners = new Set<() => void>();
let digits: DigitStyle = (() => {
  try {
    return localStorage.getItem(STORAGE_KEY) === "arab" ? "arab" : "latn";
  } catch {
    return "latn";
  }
})();

export function getDigitStyle(): DigitStyle {
  return digits;
}

export function setDigitStyle(style: DigitStyle): void {
  digits = style;
  document.documentElement.setAttribute("data-digits", style);
  try {
    localStorage.setItem(STORAGE_KEY, style);
  } catch {
    // ignore
  }
  listeners.forEach((listener) => {
    listener();
  });
}

export function useDigitStyle(): DigitStyle {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getDigitStyle,
    getDigitStyle,
  );
}

function locale(): string {
  const base = currentLanguage() === "ar" ? "ar-IQ" : "en-IQ";
  return digits === "arab" ? `${base}-u-nu-arab` : `${base}-u-nu-latn`;
}

export function formatNumber(value: number | string, options?: Intl.NumberFormatOptions): string {
  const numeric = typeof value === "string" ? Number(value) : value;
  return Number.isFinite(numeric) ? new Intl.NumberFormat(locale(), options).format(numeric) : String(value);
}

/** Amounts are decimal strings on the wire; the currency code is always shown, never inferred. */
export function formatMoney(amount: number | string, currency: string, minorUnits = 2): string {
  return formatNumber(amount, { style: "currency", currency, currencyDisplay: "code", minimumFractionDigits: minorUnits, maximumFractionDigits: minorUnits });
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) {
    return "";
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : new Intl.DateTimeFormat(locale(), { dateStyle: "medium" }).format(date);
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) {
    return "";
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : new Intl.DateTimeFormat(locale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

/** Resolves a bilingual map for the current language with the documented fallback (requested → English → any). */
export function localized(map: Record<string, string> | null | undefined): string {
  if (!map) {
    return "";
  }
  return map[currentLanguage()] ?? map.en ?? Object.values(map)[0] ?? "";
}
