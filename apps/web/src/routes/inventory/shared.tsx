import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber, localized } from "../../lib/format";
import { CompanySelect, useCompanies, useCompanySelection } from "../accounting/shared";
import { Field, SelectField } from "../common";

export type Warehouse = components["schemas"]["WarehouseSummary"];
export type Item = components["schemas"]["ItemSummary"];
export type ReasonCode = components["schemas"]["ReasonCodeSummary"];

/** The company every inventory screen works on, shared with the accounting screens (remembered per browser). */
export function useCompanyContext() {
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  return { companies: companies.data ?? [], companyId, setCompanyId, company };
}

export function useWarehouses(companyId: string) {
  return useQuery({
    queryKey: ["warehouses", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
  });
}

export function useReasonCodes(appliesTo?: string) {
  return useQuery({
    queryKey: ["reason-codes", appliesTo ?? ""],
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/reason-codes", { params: { query: appliesTo ? { appliesTo } : {} } })),
  });
}

export function CompanyFilter({ companies, value, onChange }: { companies: components["schemas"]["CompanySummary"][]; value: string; onChange: (id: string) => void }) {
  return <CompanySelect companies={companies} value={value} onChange={onChange} />;
}

export function WarehouseSelect({ warehouses, value, onChange, label, allowAll = false, testId, required = false }: { warehouses: Warehouse[]; value: string; onChange: (id: string) => void; label?: string; allowAll?: boolean; testId?: string; required?: boolean }) {
  const { t } = useTranslation();
  return (
    <Field label={label ?? t("inventory.warehouse")} required={required}>
      <SelectField value={value} onChange={(e) => { onChange(e.target.value); }} data-testid={testId ?? "warehouse-select"} required={required}>
        {allowAll ? <option value="">{t("inventory.allWarehouses")}</option> : null}
        {!allowAll && !value ? <option value="">{t("inventory.chooseWarehouse")}</option> : null}
        {warehouses.map((w) => (
          <option key={w.id} value={w.id}>
            {w.code} · {localized(w.name)}
          </option>
        ))}
      </SelectField>
    </Field>
  );
}

/** Quantities are decimal strings on the wire; shown with up to three decimals and tabular figures. */
export function qty(value: number | string | null | undefined): string {
  return value === null || value === undefined ? "" : formatNumber(value, { maximumFractionDigits: 3 });
}

export function Qty({ value, uom }: { value: number | string | null | undefined; uom?: string | null }) {
  return (
    <span className="tabular" dir="ltr">
      {qty(value)}
      {uom ? ` ${uom}` : ""}
    </span>
  );
}

const tones: Record<string, "success" | "accent" | "danger" | "neutral" | "warning" | "info"> = {
  posted: "success", received: "success", completed: "success", active: "success", in_stock: "success", accepted: "success",
  approved: "accent", pending_approval: "accent", shipped: "accent", partially_received: "accent", frozen: "accent", counting: "accent", review: "accent", running: "accent", queued: "accent", in_transit: "accent",
  rejected: "danger", recalled: "danger", failed: "danger", expired: "danger", scrapped: "danger",
  quarantine: "warning", in_repair: "warning", superseded: "warning", open: "warning",
  cancelled: "neutral", draft: "neutral", planned: "neutral", consumed: "neutral", dismissed: "neutral", sold: "info", returned: "info",
};

export function DocStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`inventory.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** A definition list for explanations and small records: key on the left, value on the right, no table semantics needed. */
export function KeyValues({ entries }: { entries: [string, ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
      {entries.map(([key, value]) => (
        <div key={key} className="contents">
          <dt className="text-fg-muted">{key}</dt>
          <dd className="tabular" dir="auto">
            {value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/** Values from an explanation or custom-field object, rendered without assuming their shape. */
export function plain(value: unknown): string {
  if (value === null || value === undefined) {
    return "—";
  }
  if (typeof value === "number" || typeof value === "string") {
    return typeof value === "number" ? formatNumber(value, { maximumFractionDigits: 3 }) : value;
  }
  if (typeof value === "boolean") {
    return value ? "✓" : "✗";
  }
  return JSON.stringify(value);
}

export function Tabs({ tabs, value, onChange }: { tabs: { id: string; label: string; testId?: string }[]; value: string; onChange: (id: string) => void }) {
  return (
    <div role="tablist" className="mb-4 flex flex-wrap gap-1 border-b border-border">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          aria-selected={value === tab.id}
          data-testid={tab.testId}
          onClick={() => { onChange(tab.id); }}
          className={value === tab.id ? "-mb-px border-b-2 border-accent px-3 py-2 text-sm font-medium text-accent" : "px-3 py-2 text-sm text-fg-muted hover:text-fg"}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}

/** Looks an item up by its code as the user leaves the field, so a line shows the name (and the API gets the id). */
export async function findItemByCode(code: string): Promise<Item | null> {
  const trimmed = code.trim();
  if (!trimmed) {
    return null;
  }
  const result = await api.GET("/api/v1/items/by-code/{code}", { params: { path: { code: trimmed } } });
  return result.data ?? null;
}

export { CompanySelect, localized };
