import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber, localized } from "../../lib/format";
import { Field, SelectField } from "../common";

export type Company = components["schemas"]["CompanySummary"];

const COMPANY_KEY = "quicker.company";

export function useCompanies() {
  return useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
}

/** The company the accounting screens work on: remembered per browser, the first company by default. */
export function useCompanySelection(companies: Company[] | undefined): [string, (id: string) => void] {
  const [companyId, setCompanyId] = useState<string>(() => {
    try {
      return localStorage.getItem(COMPANY_KEY) ?? "";
    } catch {
      return "";
    }
  });
  useEffect(() => {
    const first = companies?.[0];
    if (first && !companies.some((c) => c.id === companyId)) {
      setCompanyId(first.id);
    }
  }, [companies, companyId]);
  const select = (id: string): void => {
    setCompanyId(id);
    try {
      localStorage.setItem(COMPANY_KEY, id);
    } catch {
      // ignore
    }
  };
  return [companyId, select];
}

export function CompanySelect({ companies, value, onChange }: { companies: Company[]; value: string; onChange: (id: string) => void }) {
  const { t } = useTranslation();
  return (
    <Field label={t("accounting.company")}>
      <SelectField value={value} onChange={(e) => { onChange(e.target.value); }} data-testid="company-select">
        {companies.map((c) => (
          <option key={c.id} value={c.id}>
            {c.code} · {localized(c.legalName)}
          </option>
        ))}
      </SelectField>
    </Field>
  );
}

/** Amounts arrive as decimal strings or numbers; shown with the currency's minor units and tabular figures. */
export function amount(value: number | string | null | undefined, minorUnits = 2): string {
  return value === null || value === undefined ? "" : formatNumber(value, { minimumFractionDigits: minorUnits, maximumFractionDigits: minorUnits });
}

export function Amount({ value, minorUnits = 2 }: { value: number | string | null | undefined; minorUnits?: number }) {
  return (
    <span className="tabular" dir="ltr">
      {amount(value, minorUnits)}
    </span>
  );
}

export function today(): string {
  return new Date().toISOString().slice(0, 10);
}

const tones: Record<string, "success" | "accent" | "danger" | "neutral"> = { posted: "success", approved: "accent", pending_approval: "accent", rejected: "danger", cancelled: "neutral", draft: "neutral", open: "success", soft_closed: "accent", hard_closed: "danger", never_opened: "neutral" };

export function StatusBadge({ status, label }: { status: string; label: string }) {
  return <Badge tone={tones[status] ?? "neutral"}>{label}</Badge>;
}

/** Saves a download from the API (the bearer token travels with the client, so a plain link would not do). */
export function saveFile(blob: Blob, headers: Headers, fallbackName: string): void {
  const disposition = headers.get("content-disposition") ?? "";
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const name = match?.[1] ? decodeURIComponent(match[1]) : fallbackName;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** Query pairs beyond the typed ones (dimension filters `d.CODE=valueId`) merged without losing the typed shape. */
export function withFilters<T extends object>(base: T, filters: Record<string, string>): T {
  return Object.assign({}, base, filters);
}

export function dimensionFilters(search: Record<string, string | undefined>): Record<string, string> {
  return Object.fromEntries(Object.entries(search).filter((pair): pair is [string, string] => pair[0].startsWith("d.") && typeof pair[1] === "string"));
}
