import { useQueries, useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { Field, SelectField, TextField } from "../common";

type Account = components["schemas"]["AccountSummary"];
type Dimension = components["schemas"]["DimensionSummary"];
type DimensionValue = components["schemas"]["DimensionValueSummary"];
type Rule = components["schemas"]["DimensionRuleSummary"];
type Partner = components["schemas"]["PartnerSummary"];

/** What a journal line carries besides its account and amount. */
export interface LineDetails {
  description: string;
  dimensions: Record<string, string>;
  subledgerType: string;
  subledgerRef: string;
}

export const emptyDetails = (): LineDetails => ({ description: "", dimensions: {}, subledgerType: "", subledgerRef: "" });

/** The dimensions a document line can carry (the branch is the document's, not a line's) and their values in one company. */
export function useLineDimensions(companyId: string) {
  const dimensionList = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const dimensions = useMemo(
    () => (dimensionList.data ?? []).filter((d) => d.isActive && d.code !== "BRANCH").sort((a, b) => Number(a.sortOrder) - Number(b.sortOrder) || a.code.localeCompare(b.code)),
    [dimensionList.data],
  );
  const values = useQueries({
    queries: dimensions.map((d) => ({
      queryKey: ["dimension-values", d.id],
      queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: d.id } } })),
      staleTime: 60_000,
    })),
  });
  const valuesByDimension = new Map<string, DimensionValue[]>(dimensions.map((d, i) => [d.code, (values[i]?.data ?? []).filter((v) => v.companyId === null || v.companyId === companyId)]));
  return { dimensions, valuesByDimension };
}

export type LineDimensionReference = ReturnType<typeof useLineDimensions>;

/** "Cost centre: CC-1 Head office · Project: …" for the values a line carries. */
export function describeDimensions(values: Record<string, string>, reference: LineDimensionReference): string {
  return Object.entries(values).map(([code, valueId]) => {
    const dimension = reference.dimensions.find((d) => d.code === code);
    const value = reference.valuesByDimension.get(code)?.find((v) => v.id === valueId);
    return `${dimension ? localized(dimension.name) : code}: ${value ? `${value.code} ${localized(value.name)}` : valueId.slice(0, 8)}`;
  }).join(" · ");
}

/** One select per dimension; the empty choice is labelled with what applies when none is picked. */
export function DimensionSelects({ testIdPrefix, values, reference, emptyLabel, onChange }: { testIdPrefix: string; values: Record<string, string>; reference: LineDimensionReference; emptyLabel: string; onChange: (values: Record<string, string>) => void }) {
  return (
    <>
      {reference.dimensions.map((dimension) => {
        const options = reference.valuesByDimension.get(dimension.code) ?? [];
        return (
          <Field key={dimension.id} label={localized(dimension.name)}>
            <SelectField
              value={values[dimension.code] ?? ""}
              onChange={(e) => {
                const rest = Object.fromEntries(Object.entries(values).filter(([code]) => code !== dimension.code));
                onChange(e.target.value ? { ...rest, [dimension.code]: e.target.value } : rest);
              }}
              data-testid={`${testIdPrefix}-${dimension.code}`}
            >
              <option value="">{emptyLabel}</option>
              {options.filter((v) => v.isActive || v.id === values[dimension.code]).map((v) => (
                <option key={v.id} value={v.id}>{`${v.code} · ${localized(v.name)}`}</option>
              ))}
            </SelectField>
          </Field>
        );
      })}
    </>
  );
}

/**
 * Everything the line details need for one company: its chart's accounts by code, the dimensions a line can carry
 * (the branch is the journal's, not a line's), their values, each used account's dimension rules, and the suppliers
 * and customers that items of the payables and receivables control accounts refer to.
 */
export function useJournalReference(companyId: string, chartId: string | null | undefined, accountIds: string[], accountCodes: string[]) {
  const chart = useQuery({
    queryKey: ["chart", chartId],
    enabled: Boolean(chartId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}", { params: { path: { chartId: chartId ?? "" }, query: { expand: "accounts" } } })),
  });
  const { dimensions, valuesByDimension } = useLineDimensions(companyId);
  const accounts = useMemo(() => new Map((chart.data?.accounts ?? []).map((a) => [a.code, a])), [chart.data]);
  const uniqueAccounts = [...new Set([...accountIds, ...accountCodes.map((code) => accounts.get(code.trim())?.id ?? "")].filter(Boolean))].sort();
  const rules = useQueries({
    queries: uniqueAccounts.map((id) => ({
      queryKey: ["dimension-rules", id],
      queryFn: async () => unwrap(await api.GET("/api/v1/accounting/accounts/{accountId}/dimension-rules", { params: { path: { accountId: id } } })),
      staleTime: 60_000,
    })),
  });
  const suppliers = useQuery({ queryKey: ["partners", "supplier", "all"], queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "supplier", limit: 500 } } })), staleTime: 60_000 });
  const customers = useQuery({ queryKey: ["partners", "customer", "all"], queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "customer", limit: 500 } } })), staleTime: 60_000 });

  const rulesByAccount = new Map<string, Rule[]>(uniqueAccounts.map((id, i) => [id, rules[i]?.data ?? []]));
  const partners = new Map<string, Partner[]>([["AP", suppliers.data?.items ?? []], ["AR", customers.data?.items ?? []]]);
  return { accounts, dimensions, valuesByDimension, rulesByAccount, partners };
}

export type JournalReference = ReturnType<typeof useJournalReference>;

/** The dimension values, partner and description of one line, read-only (the journal's detail view). */
export function LineDetailsSummary({ dimensions, subledgerType, subledgerRef, description, reference }: { dimensions: Record<string, string>; subledgerType: string | null; subledgerRef: string | null; description: Record<string, string>; reference: JournalReference }) {
  const { t } = useTranslation();
  const parts: string[] = Object.keys(dimensions).length > 0 ? [describeDimensions(dimensions, reference)] : [];
  if (subledgerType && subledgerRef) {
    const partner = reference.partners.get(subledgerType)?.find((p) => p.id === subledgerRef);
    parts.push(`${t(`accounting.subledgerTypes.${subledgerType}`, { defaultValue: subledgerType })}: ${partner ? `${partner.code} ${localized(partner.legalName)}` : subledgerRef.slice(0, 8)}`);
  }
  const text = localized(description);
  if (parts.length === 0 && !text) {
    return null;
  }
  return (
    <p className="mt-0.5 text-xs text-fg-muted" data-testid="line-details">
      {[text, ...parts].filter(Boolean).join(" · ")}
    </p>
  );
}

/**
 * The details of one line being edited: a value for each dimension (required ones marked, blocked ones left out, the
 * account's default named), the supplier or customer when the account is a payables or receivables control account
 * (or the item reference for other control accounts), and the line's own description.
 */
export function LineDetailsEditor({ index, account, details, reference, onChange }: { index: number; account: Account | undefined; details: LineDetails; reference: JournalReference; onChange: (patch: Partial<LineDetails>) => void }) {
  const { t } = useTranslation();
  const rules = account ? (reference.rulesByAccount.get(account.id) ?? []) : [];
  const ruleFor = (dimension: Dimension) => rules.find((r) => r.dimensionCode === dimension.code);
  const subledgerType = account?.isControl ? (account.subledgerType ?? "") : "";
  const partnerList = reference.partners.get(subledgerType);

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" data-testid={`line-details-${index}`}>
      {reference.dimensions.filter((d) => ruleFor(d)?.rule !== "blocked").map((dimension) => {
        const rule = ruleFor(dimension);
        const options = reference.valuesByDimension.get(dimension.code) ?? [];
        const fallback = rule?.defaultValueId ? options.find((v) => v.id === rule.defaultValueId) : undefined;
        return (
          <Field key={dimension.id} label={localized(dimension.name)} required={rule?.rule === "required" && !fallback}>
            <SelectField
              value={details.dimensions[dimension.code] ?? ""}
              onChange={(e) => {
                const rest = Object.fromEntries(Object.entries(details.dimensions).filter(([code]) => code !== dimension.code));
                onChange({ dimensions: e.target.value ? { ...rest, [dimension.code]: e.target.value } : rest });
              }}
              data-testid={`line-dimension-${index}-${dimension.code}`}
            >
              <option value="">{fallback ? t("accounting.dimensionDefault", { value: fallback.code }) : "—"}</option>
              {options.filter((v) => v.isActive || v.id === details.dimensions[dimension.code]).map((v) => (
                <option key={v.id} value={v.id}>{`${v.code} · ${localized(v.name)}`}</option>
              ))}
            </SelectField>
          </Field>
        );
      })}
      {subledgerType ? (
        <Field label={t(`accounting.subledgerTypes.${subledgerType}`, { defaultValue: t("accounting.subledgerReference") })} required>
          {partnerList ? (
            <SelectField value={details.subledgerRef} onChange={(e) => { onChange({ subledgerType, subledgerRef: e.target.value }); }} data-testid={`line-subledger-${index}`}>
              <option value="">—</option>
              {partnerList.map((p) => (
                <option key={p.id} value={p.id}>{`${p.code} · ${localized(p.legalName)}`}</option>
              ))}
            </SelectField>
          ) : (
            <TextField value={details.subledgerRef} onChange={(e) => { onChange({ subledgerType, subledgerRef: e.target.value.trim() }); }} dir="ltr" placeholder={t("accounting.subledgerReferenceHint")} data-testid={`line-subledger-${index}`} />
          )}
        </Field>
      ) : null}
      <Field label={t("accounting.lineDescription")} className="sm:col-span-2">
        <TextField value={details.description} onChange={(e) => { onChange({ description: e.target.value }); }} data-testid={`line-description-${index}`} />
      </Field>
    </div>
  );
}
