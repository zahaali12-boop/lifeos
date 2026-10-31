import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import type { Flow } from "../StatusBar";

export type PayComponent = components["schemas"]["PayComponentSummary"];
export type Employee = components["schemas"]["EmployeeSummary"];
export type Leave = components["schemas"]["LeaveSummary"];
export type PayrollRun = components["schemas"]["PayrollRunSummary"];
export type PayrollRunLine = components["schemas"]["PayrollRunLineSummary"];

export const componentKinds = ["earning", "deduction", "employer"] as const;
export const componentCalculations = ["fixed", "percent_of_basic", "percent_of_gross"] as const;
export const leaveKinds = ["annual", "sick", "unpaid"] as const;
export const runStatuses = ["calculated", "pending_approval", "approved", "posted", "paid", "reversed", "cancelled"] as const;

export function usePayComponents(companyId: string, activeOnly = false) {
  return useQuery({
    queryKey: ["pay-components", companyId, activeOnly],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/components", { params: { query: { companyId, ...(activeOnly ? { isActive: true } : {}) } } })),
  });
}

export function useEmployees(companyId: string, status?: string) {
  return useQuery({
    queryKey: ["employees", companyId, status ?? ""],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/employees", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
}

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", calculated: "info", pending_approval: "warning", approved: "info", posted: "success", paid: "success", reversed: "danger", cancelled: "neutral",
  active: "success", left: "neutral",
};

export function RunStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`hr.runStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export function EmployeeStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="employee-status">
      {t(`hr.employeeStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** The path of a run: calculated, (approval), approved, posted, paid; reversed and cancelled stand off it. */
export function runFlow(r: Pick<PayrollRun, "status" | "approvalRequestId">, t: TFunction): Flow {
  const label = (id: string): string => t(`hr.runStatuses.${id}`, { defaultValue: id });
  const ids = ["calculated", "approved", "posted", "paid"];
  if (r.status === "pending_approval" || r.approvalRequestId) {
    ids.splice(1, 0, "pending_approval");
  }
  const steps = ids.map((id) => ({ id, label: label(id) }));
  return steps.some((s) => s.id === r.status)
    ? { steps, state: { step: r.status, label: label(r.status) } }
    : { steps, state: { step: null, label: label(r.status), tone: r.status === "reversed" ? "danger" : "neutral" } };
}

/** "September 2026" in the interface language. */
export function periodLabel(year: number, month: number, language: string): string {
  return new Date(Date.UTC(year, month - 1, 1)).toLocaleDateString(language === "ar" ? "ar-u-nu-latn" : language, { month: "long", year: "numeric", timeZone: "UTC" });
}

/** A partner code from a person's name when none is typed: letters and digits, upper case, words joined by a dash. */
export function codeFromName(name: string): string {
  return name.toUpperCase().replace(/[^A-Z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32);
}
