import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { CalendarClock, Mail, NotebookPen, Phone, SquareCheck, Users } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber } from "../../lib/format";

export type CustomerAccount = components["schemas"]["CustomerAccountSummary"];
export type CustomerGroup = components["schemas"]["CustomerGroupSummary"];
export type SalesRep = components["schemas"]["SalesRepSummary"];
export type CommissionPlan = components["schemas"]["CommissionPlanSummary"];
export type PipelineStage = components["schemas"]["PipelineStageSummary"];
export type Opportunity = components["schemas"]["OpportunitySummary"];
export type PipelineTotal = components["schemas"]["PipelineTotal"];
export type CrmActivity = components["schemas"]["CrmActivitySummary"];
export type Customer360 = components["schemas"]["Customer360"];

export function useCustomerGroups() {
  return useQuery({ queryKey: ["customer-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/customer-groups")) });
}

export function useSalesReps() {
  return useQuery({ queryKey: ["sales-reps"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/sales-reps")) });
}

export function useCommissionPlans() {
  return useQuery({ queryKey: ["commission-plans"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/commission-plans")) });
}

export function usePipelineStages() {
  return useQuery({ queryKey: ["pipeline-stages"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/pipeline-stages")) });
}

export function useCustomerPostingGroups() {
  return useQuery({ queryKey: ["posting-groups", "partner_customer"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups", { params: { query: { kind: "partner_customer" } } })) });
}

/** Minor units of the currencies ISO 4217 gives other than two; amounts show them only when they are there. */
const minorUnits: Record<string, number> = { IQD: 3, KWD: 3, BHD: 3, OMR: 3, JOD: 3, TND: 3, LYD: 3, JPY: 0, KRW: 0, CLP: 0, ISK: 0, VND: 0 };

/** An amount with its currency code, digits as the currency has them (a dinar's fils only when there are any). */
export function money(amount: number | string | null | undefined, currency: string): string {
  if (amount === null || amount === undefined || amount === "") {
    return "";
  }
  const digits = minorUnits[currency] ?? 2;
  return `${formatNumber(amount, { minimumFractionDigits: digits === 2 ? 2 : 0, maximumFractionDigits: digits })} ${currency}`;
}

export function Money({ amount, currency, testId }: { amount: number | string | null | undefined; currency: string; testId?: string }) {
  return (
    <span className="tabular" dir="ltr" data-testid={testId}>
      {money(amount, currency)}
    </span>
  );
}

/** Totals per currency on one line each: never summed across currencies. */
export function Totals({ totals, weighted = true }: { totals: PipelineTotal[]; weighted?: boolean }) {
  const { t } = useTranslation();
  if (totals.length === 0) {
    return <span className="text-fg-muted">—</span>;
  }
  return (
    <span className="flex flex-col gap-0.5">
      {totals.map((total) => (
        <span key={total.currency} className="tabular" dir="ltr">
          {money(weighted ? total.weighted : total.amount, total.currency)}
          <span className="text-fg-muted" dir="auto">
            {" "}
            · {t("sales.dealsCount", { count: Number(total.count) })}
          </span>
        </span>
      ))}
    </span>
  );
}

const creditTones: Record<string, "success" | "warning" | "danger"> = { ok: "success", on_hold: "warning", blocked: "danger" };

export function CreditBadge({ status, hideOk = false }: { status: string; hideOk?: boolean }) {
  const { t } = useTranslation();
  if (hideOk && status === "ok") {
    return null;
  }
  return (
    <Badge tone={creditTones[status] ?? "neutral"} data-testid="credit-badge">
      {t(`sales.creditStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

const outcomeTones: Record<string, "info" | "success" | "danger"> = { open: "info", won: "success", lost: "danger" };

export function OutcomeBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={outcomeTones[status] ?? "neutral"} data-testid="outcome-badge">
      {t(`sales.outcomes.${status}`, { defaultValue: status })}
    </Badge>
  );
}

const activityIcons = { call: Phone, meeting: Users, email: Mail, task: SquareCheck, note: NotebookPen } as const;

export function ActivityIcon({ kind }: { kind: string }) {
  const Icon = kind in activityIcons ? activityIcons[kind as keyof typeof activityIcons] : CalendarClock;
  return <Icon className="size-4 shrink-0 text-fg-muted" aria-hidden="true" />;
}

/** Whole days since an instant, for "in this stage for 12 days". */
export function daysSince(iso: string, now: Date = new Date()): number {
  return Math.max(0, Math.floor((now.getTime() - new Date(iso).getTime()) / 86_400_000));
}
