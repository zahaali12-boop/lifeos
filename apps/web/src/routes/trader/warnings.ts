import type { TFunction } from "i18next";
import type { components } from "../../api/schema";
import { formatMoney, formatNumber } from "../../lib/format";

type Warning = components["schemas"]["TraderWarning"];

/** How grave each health warning is, for the badge beside it. */
export const warningTones: Record<string, "danger" | "warning" | "info"> = {
  loss: "danger",
  cash_runway_short: "danger",
  break_even_not_covered: "warning",
  margin_below_target: "warning",
  overdue_receivables: "warning",
  overdue_payables: "warning",
  shipments_attention: "warning",
  no_recent_sales: "warning",
  reorder_needed: "info",
  fixed_costs_unknown: "info",
};

/** The screen that acts on a warning: the trader's home links each one there. */
export const warningTargets: Record<string, { to: string; label: string }> = {
  loss: { to: "/trader/health", label: "nav.trader.health" },
  fixed_costs_unknown: { to: "/trader/health", label: "nav.trader.health" },
  cash_runway_short: { to: "/trader/money", label: "nav.trader.money" },
  break_even_not_covered: { to: "/trader/sales", label: "nav.trader.sales" },
  margin_below_target: { to: "/trader/pricing", label: "nav.trader.pricing" },
  overdue_receivables: { to: "/trader/money", label: "nav.trader.money" },
  overdue_payables: { to: "/trader/money", label: "nav.trader.money" },
  shipments_attention: { to: "/trader/shipments", label: "nav.trader.shipments" },
  no_recent_sales: { to: "/trader/sales", label: "nav.trader.sales" },
  reorder_needed: { to: "/trader/health", label: "nav.trader.health" },
};

/** A health warning in plain words (ADR-0035 §8): the server sends a code and a value, the client says it. */
export function traderWarningText(t: TFunction, w: Warning, coverDays: number, currency: string): string {
  const value = w.value === null ? null : Number(w.value);
  const money = (amount: number | null): string => (amount === null ? "—" : formatMoney(amount, currency));
  switch (w.code) {
    case "loss":
    case "overdue_receivables":
    case "overdue_payables":
      return t(`trader.health.warnings.${w.code}`, { amount: money(value) });
    case "cash_runway_short":
      return t("trader.health.warnings.cash_runway_short", { months: formatNumber(value ?? 0, { maximumFractionDigits: 1 }) });
    case "break_even_not_covered":
    case "margin_below_target":
      return t(`trader.health.warnings.${w.code}`, { pct: formatNumber(value ?? 0, { maximumFractionDigits: 1 }) });
    case "no_recent_sales":
      return value === null ? t("trader.health.warnings.no_sales_in_cover", { days: formatNumber(coverDays) }) : t("trader.health.warnings.no_recent_sales", { days: formatNumber(value) });
    case "shipments_attention":
    case "reorder_needed":
      return t(`trader.health.warnings.${w.code}`, { count: formatNumber(value ?? 0) });
    default:
      return t(`trader.health.warnings.${w.code}`, { defaultValue: w.code });
  }
}
