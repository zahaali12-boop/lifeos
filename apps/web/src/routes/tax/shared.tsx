import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";

export type TaxTemplate = components["schemas"]["TaxTemplateSummary"];
export type TaxRegime = components["schemas"]["TaxRegimeSummary"];
export type TaxRegimeDetail = components["schemas"]["TaxRegimeDetail"];
export type TaxCode = components["schemas"]["TaxCodeSummary"];
export type TaxRate = components["schemas"]["TaxRateDto"];
export type TaxRule = components["schemas"]["TaxRuleSummary"];
export type TaxGroup = components["schemas"]["TaxGroupSummary"];
export type TaxRegistration = components["schemas"]["CompanyTaxRegistrationSummary"];
export type TaxExemption = components["schemas"]["TaxExemptionSummary"];
export type TaxReturnBox = components["schemas"]["TaxReturnBox"];
export type TaxCodeReconciliation = components["schemas"]["TaxCodeReconciliation"];
export type TaxReturnPreview = components["schemas"]["TaxReturnPreview"];
export type TaxReturnDrillDownLine = components["schemas"]["TaxReturnDrillDownLine"];
export type TaxReturnPeriod = components["schemas"]["TaxReturnPeriodSummary"];

export const treatments = ["standard", "zero_rated", "exempt", "out_of_scope"] as const;
export const directions = ["sales", "purchase"] as const;
export const roundingLevels = ["line", "document"] as const;
export const taxPoints = ["invoice", "payment", "delivery"] as const;
export const returnFrequencies = ["monthly", "quarterly", "annual"] as const;

export function useTaxTemplates() {
  return useQuery({ queryKey: ["tax-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/tax/templates")) });
}

export function useTaxRegimes() {
  return useQuery({ queryKey: ["tax-regimes"], queryFn: async () => unwrap(await api.GET("/api/v1/tax/regimes")) });
}

export function useTaxRegime(regimeId: string) {
  return useQuery({
    queryKey: ["tax-regime", regimeId],
    enabled: Boolean(regimeId),
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/regimes/{regimeId}", { params: { path: { regimeId } } })),
  });
}

export function useTaxGroups(kind?: string) {
  return useQuery({
    queryKey: ["tax-groups", kind ?? ""],
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/groups", { params: { query: kind ? { kind } : {} } })),
  });
}

export function useTaxRegistrations(companyId?: string) {
  return useQuery({
    queryKey: ["tax-registrations", companyId ?? ""],
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/registrations", { params: { query: companyId ? { companyId } : {} } })),
  });
}

export function useTaxExemptions(partnerId?: string) {
  return useQuery({
    queryKey: ["tax-exemptions", partnerId ?? ""],
    queryFn: async () => unwrap(await api.GET("/api/v1/tax/exemptions", { params: { query: partnerId ? { partnerId } : {} } })),
  });
}
