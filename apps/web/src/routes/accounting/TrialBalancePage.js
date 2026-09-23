import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Download } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Amount, CompanySelect, saveFile, today, useCompanies, useCompanySelection, withFilters } from "./shared";
/** The trial balance at any date: movement window, comparative, basis, one dimension filter or grouping; every row drills to its ledger. */
export function TrialBalancePage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const companies = useCompanies();
    const [companyId, setCompanyId] = useCompanySelection(companies.data);
    const company = companies.data?.find((c) => c.id === companyId);
    const [asOf, setAsOf] = useState(today());
    const [from, setFrom] = useState("");
    const [compareAsOf, setCompareAsOf] = useState("");
    const [basis, setBasis] = useState("fc");
    const [groupBy, setGroupBy] = useState("");
    const [filterDimension, setFilterDimension] = useState("");
    const [filterValue, setFilterValue] = useState("");
    const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
    const filterDimensionId = dimensions.data?.find((d) => d.code === filterDimension)?.id;
    const values = useQuery({
        queryKey: ["dimension-values", filterDimensionId],
        enabled: Boolean(filterDimensionId),
        queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: filterDimensionId ?? "" } } })),
    });
    const filters = filterDimension && filterValue ? { [`d.${filterDimension}`]: filterValue } : {};
    const query = withFilters({ asOf, ...(from ? { from } : {}), ...(compareAsOf ? { compareAsOf } : {}), basis, ...(groupBy ? { groupBy } : {}) }, filters);
    const report = useQuery({
        queryKey: ["trial-balance", companyId, query],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/reports/trial-balance", { params: { path: { companyId }, query } })),
    });
    const exportReport = useMutation({
        mutationFn: async (format) => {
            const result = await api.GET("/api/v1/accounting/companies/{companyId}/reports/trial-balance", { params: { path: { companyId }, query: { ...query, format } }, parseAs: "blob" });
            const blob = unwrap(result);
            saveFile(blob, result.response.headers, `trial-balance-${asOf}.${format}`);
        },
    });
    const openLedger = (row) => {
        const drill = { companyId, accountId: row.drill.accountId, to: row.drill.to, ...(row.drill.from ? { from: row.drill.from } : {}), basis };
        for (const [code, valueId] of Object.entries(row.drill.dimensions)) {
            drill[`d.${code}`] = valueId;
        }
        void navigate({ to: "/accounting/ledger", search: drill });
    };
    const data = report.data;
    const problem = report.error ? toFormProblem(report.error, t("accounting.loadFailed")) : null;
    const minorUnits = 2;
    const compare = Boolean(data?.compareAsOf);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("accounting.trialBalance"), description: t("accounting.trialBalanceDescription"), actions: _jsxs(_Fragment, { children: [_jsxs(Button, { variant: "secondary", onClick: () => { exportReport.mutate("csv"); }, loading: exportReport.isPending, disabled: !data, children: [_jsx(Download, { "aria-hidden": "true" }), "CSV"] }), _jsxs(Button, { variant: "secondary", onClick: () => { exportReport.mutate("xlsx"); }, loading: exportReport.isPending, disabled: !data, children: [_jsx(Download, { "aria-hidden": "true" }), "XLSX"] })] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3 lg:grid-cols-6", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("accounting.asOf"), required: true, children: _jsx(TextField, { type: "date", value: asOf, onChange: (e) => { setAsOf(e.target.value); }, dir: "ltr", "data-testid": "tb-as-of" }) }), _jsx(Field, { label: t("accounting.from"), description: t("accounting.fromHint"), children: _jsx(TextField, { type: "date", value: from, onChange: (e) => { setFrom(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.compareAsOf"), children: _jsx(TextField, { type: "date", value: compareAsOf, onChange: (e) => { setCompareAsOf(e.target.value); }, dir: "ltr" }) }), _jsx(Field, { label: t("accounting.basis"), children: _jsxs(SelectField, { value: basis, onChange: (e) => { setBasis(e.target.value); }, children: [_jsx("option", { value: "fc", children: t("accounting.basisFunctional", { currency: company?.functionalCurrency ?? "" }) }), company?.reportingCurrency ? _jsx("option", { value: "rc", children: t("accounting.basisReporting", { currency: company.reportingCurrency }) }) : null] }) }), _jsx(Field, { label: t("accounting.groupBy"), children: _jsxs(SelectField, { value: groupBy, onChange: (e) => { setGroupBy(e.target.value); }, children: [_jsx("option", { value: "", children: t("accounting.noGrouping") }), (dimensions.data ?? []).map((d) => (_jsx("option", { value: d.code, children: localized(d.name) }, d.id)))] }) }), _jsx(Field, { label: t("accounting.filterDimension"), children: _jsxs(SelectField, { value: filterDimension, onChange: (e) => { setFilterDimension(e.target.value); setFilterValue(""); }, children: [_jsx("option", { value: "", children: t("accounting.noFilter") }), (dimensions.data ?? []).map((d) => (_jsx("option", { value: d.code, children: localized(d.name) }, d.id)))] }) }), _jsx(Field, { label: t("accounting.filterValue"), children: _jsxs(SelectField, { value: filterValue, onChange: (e) => { setFilterValue(e.target.value); }, disabled: !filterDimension, children: [_jsx("option", { value: "", children: t("accounting.anyValue") }), (values.data ?? []).map((v) => (_jsxs("option", { value: v.id, children: [v.code, " \u00B7 ", localized(v.name)] }, v.id)))] }) })] }), _jsx(FormError, { message: problem?.message ?? null }), data ? (_jsxs(_Fragment, { children: [_jsxs("p", { className: "mb-2 flex items-center gap-2 text-sm text-fg-muted", role: "status", "data-testid": "tb-status", children: [_jsx(Badge, { tone: data.balanced ? "success" : "danger", children: data.balanced ? t("accounting.balanced") : t("accounting.unbalanced") }), _jsx("span", { children: t("accounting.tbSummary", { currency: data.currency, rows: data.rows.length }) })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.accountCode") }), _jsx(TableHead, { children: t("accounting.accountName") }), data.groupBy ? _jsx(TableHead, { children: data.groupBy }) : null, _jsx(TableHead, { className: "text-end", children: t("accounting.opening") }), _jsx(TableHead, { className: "text-end", children: t("accounting.debit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.credit") }), _jsx(TableHead, { className: "text-end", children: t("accounting.closing") }), compare ? _jsx(TableHead, { className: "text-end", children: t("accounting.compareClosing") }) : null] }) }), _jsxs(TableBody, { children: [data.rows.map((row) => (_jsxs(TableRow, { className: "cursor-pointer", onClick: () => { openLedger(row); }, "data-testid": "tb-row", children: [_jsx(TableCell, { dir: "ltr", children: _jsx("button", { type: "button", className: "font-medium underline-offset-2 hover:underline", onClick: () => { openLedger(row); }, children: row.accountCode }) }), _jsx(TableCell, { children: localized(row.accountName) }), data.groupBy ? _jsx(TableCell, { children: row.dimensionValueCode ? `${row.dimensionValueCode} · ${localized(row.dimensionValueName)}` : t("accounting.unassigned") }) : null, _jsx(TableNumberCell, { children: _jsx(Amount, { value: row.opening, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: row.debit, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: row.credit, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: row.closing, minorUnits: minorUnits }) }), compare ? _jsx(TableNumberCell, { children: _jsx(Amount, { value: row.compare?.closing ?? 0, minorUnits: minorUnits }) }) : null] }, `${row.accountId}-${row.dimensionValueId ?? ""}`))), _jsxs(TableRow, { className: "font-semibold", children: [_jsx(TableCell, { colSpan: data.groupBy ? 3 : 2, children: t("accounting.totals") }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: data.totals.opening, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: data.totals.debit, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: data.totals.credit, minorUnits: minorUnits }) }), _jsx(TableNumberCell, { children: _jsx(Amount, { value: data.totals.closing, minorUnits: minorUnits }) }), compare ? _jsx(TableNumberCell, { children: _jsx(Amount, { value: data.compareTotals?.closing ?? 0, minorUnits: minorUnits }) }) : null] })] })] })] })) : report.isPending && companyId ? (_jsx("p", { className: "text-sm text-fg-muted", children: t("common.loading") })) : null] }));
}
