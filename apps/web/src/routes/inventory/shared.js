import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatNumber, localized } from "../../lib/format";
import { CompanySelect, useCompanies, useCompanySelection } from "../accounting/shared";
import { Field, SelectField } from "../common";
/** The company every inventory screen works on, shared with the accounting screens (remembered per browser). */
export function useCompanyContext() {
    const companies = useCompanies();
    const [companyId, setCompanyId] = useCompanySelection(companies.data);
    const company = companies.data?.find((c) => c.id === companyId);
    return { companies: companies.data ?? [], companyId, setCompanyId, company };
}
export function useWarehouses(companyId) {
    return useQuery({
        queryKey: ["warehouses", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
    });
}
export function useReasonCodes(appliesTo) {
    return useQuery({
        queryKey: ["reason-codes", appliesTo ?? ""],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/reason-codes", { params: { query: appliesTo ? { appliesTo } : {} } })),
    });
}
export function CompanyFilter({ companies, value, onChange }) {
    return _jsx(CompanySelect, { companies: companies, value: value, onChange: onChange });
}
export function WarehouseSelect({ warehouses, value, onChange, label, allowAll = false, testId, required = false }) {
    const { t } = useTranslation();
    return (_jsx(Field, { label: label ?? t("inventory.warehouse"), required: required, children: _jsxs(SelectField, { value: value, onChange: (e) => { onChange(e.target.value); }, "data-testid": testId ?? "warehouse-select", required: required, children: [allowAll ? _jsx("option", { value: "", children: t("inventory.allWarehouses") }) : null, !allowAll && !value ? _jsx("option", { value: "", children: t("inventory.chooseWarehouse") }) : null, warehouses.map((w) => (_jsxs("option", { value: w.id, children: [w.code, " \u00B7 ", localized(w.name)] }, w.id)))] }) }));
}
/** Quantities are decimal strings on the wire; shown with up to three decimals and tabular figures. */
export function qty(value) {
    return value === null || value === undefined ? "" : formatNumber(value, { maximumFractionDigits: 3 });
}
export function Qty({ value, uom }) {
    return (_jsxs("span", { className: "tabular", dir: "ltr", children: [qty(value), uom ? ` ${uom}` : ""] }));
}
const tones = {
    posted: "success", received: "success", completed: "success", active: "success", in_stock: "success", accepted: "success",
    approved: "accent", pending_approval: "accent", shipped: "accent", partially_received: "accent", frozen: "accent", counting: "accent", review: "accent", running: "accent", queued: "accent", in_transit: "accent",
    rejected: "danger", recalled: "danger", failed: "danger", expired: "danger", scrapped: "danger",
    quarantine: "warning", in_repair: "warning", superseded: "warning", open: "warning",
    cancelled: "neutral", draft: "neutral", planned: "neutral", consumed: "neutral", dismissed: "neutral", sold: "info", returned: "info",
};
export function DocStatus({ status }) {
    const { t } = useTranslation();
    return (_jsx(Badge, { tone: tones[status] ?? "neutral", "data-testid": "doc-status", children: t(`inventory.statuses.${status}`, { defaultValue: status }) }));
}
/** A definition list for explanations and small records: key on the left, value on the right, no table semantics needed. */
export function KeyValues({ entries }) {
    return (_jsx("dl", { className: "grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm", children: entries.map(([key, value]) => (_jsxs("div", { className: "contents", children: [_jsx("dt", { className: "text-fg-muted", children: key }), _jsx("dd", { className: "tabular", dir: "auto", children: value })] }, key))) }));
}
/** Values from an explanation or custom-field object, rendered without assuming their shape. */
export function plain(value) {
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
export function Tabs({ tabs, value, onChange }) {
    return (_jsx("div", { role: "tablist", className: "mb-4 flex flex-wrap gap-1 border-b border-border", children: tabs.map((tab) => (_jsx("button", { type: "button", role: "tab", "aria-selected": value === tab.id, "data-testid": tab.testId, onClick: () => { onChange(tab.id); }, className: value === tab.id ? "-mb-px border-b-2 border-accent px-3 py-2 text-sm font-medium text-accent" : "px-3 py-2 text-sm text-fg-muted hover:text-fg", children: tab.label }, tab.id))) }));
}
/** Looks an item up by its code as the user leaves the field, so a line shows the name (and the API gets the id). */
export async function findItemByCode(code) {
    const trimmed = code.trim();
    if (!trimmed) {
        return null;
    }
    const result = await api.GET("/api/v1/items/by-code/{code}", { params: { path: { code: trimmed } } });
    return result.data ?? null;
}
export { CompanySelect, localized };
