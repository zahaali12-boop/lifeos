import { jsxs as _jsxs, jsx as _jsx } from "react/jsx-runtime";
import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatNumber, localized } from "../../lib/format";
import { Field, SelectField } from "../common";
const COMPANY_KEY = "quicker.company";
export function useCompanies() {
    return useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
}
/** The company the accounting screens work on: remembered per browser, the first company by default. */
export function useCompanySelection(companies) {
    const [companyId, setCompanyId] = useState(() => {
        try {
            return localStorage.getItem(COMPANY_KEY) ?? "";
        }
        catch {
            return "";
        }
    });
    useEffect(() => {
        const first = companies?.[0];
        if (first && !companies.some((c) => c.id === companyId)) {
            setCompanyId(first.id);
        }
    }, [companies, companyId]);
    const select = (id) => {
        setCompanyId(id);
        try {
            localStorage.setItem(COMPANY_KEY, id);
        }
        catch {
            // ignore
        }
    };
    return [companyId, select];
}
export function CompanySelect({ companies, value, onChange }) {
    const { t } = useTranslation();
    return (_jsx(Field, { label: t("accounting.company"), children: _jsx(SelectField, { value: value, onChange: (e) => { onChange(e.target.value); }, "data-testid": "company-select", children: companies.map((c) => (_jsxs("option", { value: c.id, children: [c.code, " \u00B7 ", localized(c.legalName)] }, c.id))) }) }));
}
/** Amounts arrive as decimal strings or numbers; shown with the currency's minor units and tabular figures. */
export function amount(value, minorUnits = 2) {
    return value === null || value === undefined ? "" : formatNumber(value, { minimumFractionDigits: minorUnits, maximumFractionDigits: minorUnits });
}
export function Amount({ value, minorUnits = 2 }) {
    return (_jsx("span", { className: "tabular", dir: "ltr", children: amount(value, minorUnits) }));
}
export function today() {
    return new Date().toISOString().slice(0, 10);
}
const tones = { posted: "success", approved: "accent", pending_approval: "accent", rejected: "danger", cancelled: "neutral", draft: "neutral", open: "success", soft_closed: "accent", hard_closed: "danger", never_opened: "neutral" };
export function StatusBadge({ status, label }) {
    return _jsx(Badge, { tone: tones[status] ?? "neutral", children: label });
}
/** Saves a download from the API (the bearer token travels with the client, so a plain link would not do). */
export function saveFile(blob, headers, fallbackName) {
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
export function withFilters(base, filters) {
    return Object.assign({}, base, filters);
}
export function dimensionFilters(search) {
    return Object.fromEntries(Object.entries(search).filter((pair) => pair[0].startsWith("d.") && typeof pair[1] === "string"));
}
