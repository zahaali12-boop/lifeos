import { Badge } from "@quicker/ui";
import { useQueries, useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import type { Flow } from "../StatusBar";

export type Expense = components["schemas"]["ExpenseSummary"];
export type ExpenseCategory = components["schemas"]["ExpenseCategorySummary"];
export type Fund = components["schemas"]["FundSummary"];
export type Replenishment = components["schemas"]["ReplenishmentSummary"];

export const expenseKinds = ["supplier", "employee", "petty_cash"] as const;
export const expenseStatuses = ["draft", "pending_approval", "confirmed", "reversed", "rejected", "cancelled"] as const;

export function useExpenseCategories(activeOnly = false) {
  return useQuery({
    queryKey: ["expense-categories", activeOnly],
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/categories", { params: { query: activeOnly ? { isActive: true } : {} } })),
  });
}

export function useFunds(companyId: string) {
  return useQuery({
    queryKey: ["expense-funds", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/funds", { params: { query: { companyId } } })),
  });
}

/** Every tax code of every regime, for the category and line pickers: "SA-S · Standard rate (SA-VAT)". */
export function useTaxCodeOptions(): { id: string; label: string }[] {
  const regimes = useQuery({ queryKey: ["tax-regimes"], queryFn: async () => unwrap(await api.GET("/api/v1/tax/regimes")) });
  const details = useQueries({
    queries: (regimes.data ?? []).map((r) => ({
      queryKey: ["tax-regime", r.id],
      queryFn: async () => unwrap(await api.GET("/api/v1/tax/regimes/{regimeId}", { params: { path: { regimeId: r.id } } })),
    })),
  });
  return details.flatMap((d) => (d.data ? d.data.codes.map((c) => ({ id: c.id, label: `${c.code} · ${localized(c.name)} (${d.data.regime.code})` })) : []));
}

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", pending_approval: "warning", confirmed: "success", reversed: "danger", rejected: "danger", cancelled: "neutral",
  unpaid: "info", partial: "warning", paid: "success", "n/a": "neutral",
};

export function ExpenseStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`expenses.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function PaymentStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return status === "n/a" ? null : (
    <Badge tone={tones[status] ?? "neutral"} data-testid="payment-status">
      {t(`expenses.paymentStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** The path of an expense: draft, (approval), confirmed, paid; a petty cash voucher is paid the moment it is confirmed, so its path stops there. */
export function expenseFlow(e: Pick<Expense, "status" | "kind" | "paymentStatus" | "approvalRequestId">, t: TFunction): Flow {
  const label = (id: string): string => t(`expenses.statuses.${id}`, { defaultValue: id });
  const ids = e.kind === "petty_cash" ? ["draft", "confirmed"] : ["draft", "confirmed", "paid"];
  if (e.status === "pending_approval" || e.approvalRequestId) {
    ids.splice(1, 0, "pending_approval");
  }
  const steps = ids.map((id) => ({ id, label: label(id) }));
  if (e.status === "confirmed" && e.paymentStatus === "paid") {
    return { steps, state: { step: "paid", label: label("paid") } };
  }
  if (e.status === "confirmed" && e.paymentStatus === "partial") {
    return { steps, state: { step: "confirmed", label: t("expenses.paymentStatuses.partial"), tone: "warning" } };
  }
  return steps.some((s) => s.id === e.status)
    ? { steps, state: { step: e.status, label: label(e.status) } }
    : { steps, state: { step: null, label: label(e.status), tone: e.status === "rejected" || e.status === "reversed" ? "danger" : "neutral" } };
}

export function payeeOf(e: Pick<Expense, "kind" | "partnerCode" | "partnerName" | "fundCode">): string {
  if (e.kind === "petty_cash") {
    return e.fundCode ?? "";
  }
  return e.partnerCode ? `${e.partnerCode} · ${localized(e.partnerName ?? {})}` : "";
}
