import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Checkbox, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanySelect, StatusBadge, useCompanies, useCompanySelection } from "./shared";
const modules = ["GL", "AR", "AP", "INV", "FA", "BANK", "TAX"];
/** Period control per company: the states of every period and module for a fiscal year, close/reopen with a reason, and allow-posting windows per role. */
export function PeriodsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const companies = useCompanies();
    const [companyId, setCompanyId] = useCompanySelection(companies.data);
    const company = companies.data?.find((c) => c.id === companyId);
    const [yearId, setYearId] = useState("");
    const [selectedModules, setSelectedModules] = useState(["GL"]);
    const [reason, setReason] = useState("");
    const [problem, setProblem] = useState(null);
    const [windows, setWindows] = useState(null);
    const calendars = useQuery({ queryKey: ["fiscal-calendars"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/fiscal-calendars")) });
    const calendar = calendars.data?.find((c) => c.id === company?.fiscalCalendarId);
    const years = calendar?.years ?? [];
    const year = years.find((y) => y.id === yearId) ?? years.find((y) => y.status === "open") ?? years[0];
    const periods = useMemo(() => year?.periods.filter((p) => !p.isAdjustment) ?? [], [year]);
    const states = useQueries({
        queries: periods.map((period) => ({
            queryKey: ["period-states", period.id, companyId],
            enabled: Boolean(companyId),
            queryFn: async () => unwrap(await api.GET("/api/v1/organization/periods/{periodId}/states", { params: { path: { periodId: period.id }, query: { companyId } } })),
        })),
    });
    const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
    const storedWindows = useQuery({
        queryKey: ["posting-windows", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/posting-windows", { params: { path: { companyId } } })),
    });
    const windowRows = windows ?? (storedWindows.data ?? []).map((w) => ({ roleId: w.roleId ?? "", allowFrom: w.allowFrom ?? "", allowTo: w.allowTo ?? "", reason: w.reason ?? "" }));
    const change = useMutation({
        mutationFn: async (input) => input.reopen
            ? unwrap(await api.POST("/api/v1/organization/periods/{periodId}/reopen", { params: { path: { periodId: input.periodId } }, body: { companyId, modules: selectedModules, reason, state: input.state } }))
            : unwrap(await api.PUT("/api/v1/organization/periods/{periodId}/states", { params: { path: { periodId: input.periodId } }, body: { companyId, modules: selectedModules, state: input.state, reason: reason || null } })),
        onSuccess: async (_, input) => {
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["period-states", input.periodId, companyId] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const saveWindows = useMutation({
        mutationFn: async () => unwrap(await api.PUT("/api/v1/organization/companies/{companyId}/posting-windows", { params: { path: { companyId } }, body: windowRows.map((w) => ({ roleId: w.roleId || null, allowFrom: w.allowFrom || null, allowTo: w.allowTo || null, reason: w.reason || null })) })),
        onSuccess: async () => {
            setProblem(null);
            setWindows(null);
            await queryClient.invalidateQueries({ queryKey: ["posting-windows", companyId] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const stateOf = (index, module) => states[index]?.data?.find((s) => s.module === module);
    const toggleModule = (module, checked) => {
        setSelectedModules((current) => (checked ? [...new Set([...current, module])] : current.filter((m) => m !== module)));
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("accounting.periods"), description: t("accounting.periodsDescription") }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanySelect, { companies: companies.data ?? [], value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("accounting.fiscalYear"), children: _jsx(SelectField, { value: year?.id ?? "", onChange: (e) => { setYearId(e.target.value); }, children: years.map((y) => (_jsxs("option", { value: y.id, children: [y.code, " \u00B7 ", t(`accounting.yearStatus.${y.status}`)] }, y.id))) }) }), _jsx(Field, { label: t("common.reason"), description: t("accounting.reasonHint"), children: _jsx(TextField, { value: reason, onChange: (e) => { setReason(e.target.value); }, "data-testid": "period-reason" }) })] }), _jsxs("fieldset", { className: "mb-3 flex flex-wrap items-center gap-4 text-sm", children: [_jsx("legend", { className: "me-2 font-medium", children: t("accounting.modules") }), modules.map((module) => (_jsxs("label", { className: "flex items-center gap-2", children: [_jsx(Checkbox, { checked: selectedModules.includes(module), onCheckedChange: (checked) => { toggleModule(module, checked === true); } }), _jsx("span", { dir: "ltr", children: module })] }, module)))] }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.period") }), _jsx(TableHead, { children: t("accounting.dates") }), modules.map((module) => (_jsx(TableHead, { children: _jsx("span", { dir: "ltr", children: module }) }, module))), _jsx(TableHead, { children: t("accounting.actions") })] }) }), _jsx(TableBody, { children: periods.map((period, index) => {
                            const gl = stateOf(index, "GL");
                            const hardClosed = selectedModules.some((module) => stateOf(index, module)?.state === "hard_closed");
                            return (_jsxs(TableRow, { "data-testid": "period-row", children: [_jsx(TableCell, { children: t("accounting.periodNumber", { number: String(period.number) }) }), _jsxs(TableCell, { dir: "ltr", children: [formatDate(period.startsOn), " \u2013 ", formatDate(period.endsOn)] }), modules.map((module) => {
                                        const state = stateOf(index, module);
                                        return (_jsx(TableCell, { title: state?.reason ?? undefined, children: state ? _jsx(StatusBadge, { status: state.state, label: t(`accounting.periodStates.${state.state}`) }) : "…" }, module));
                                    }), _jsx(TableCell, { className: "space-x-1 whitespace-nowrap", children: hardClosed ? (_jsx(Button, { size: "sm", variant: "secondary", onClick: () => { change.mutate({ periodId: period.id, state: "open", reopen: true }); }, loading: change.isPending, "data-testid": "reopen-period", children: t("accounting.reopen") })) : (_jsxs(_Fragment, { children: [gl?.state !== "open" ? (_jsx(Button, { size: "sm", variant: "secondary", onClick: () => { change.mutate({ periodId: period.id, state: "open", reopen: false }); }, loading: change.isPending, children: t("accounting.open") })) : null, _jsx(Button, { size: "sm", variant: "secondary", onClick: () => { change.mutate({ periodId: period.id, state: "soft_closed", reopen: false }); }, loading: change.isPending, children: t("accounting.softClose") }), _jsx(Button, { size: "sm", variant: "secondary", onClick: () => { change.mutate({ periodId: period.id, state: "hard_closed", reopen: false }); }, loading: change.isPending, "data-testid": "hard-close-period", children: t("accounting.hardClose") })] })) })] }, period.id));
                        }) })] }), _jsxs("section", { className: "mt-8", "aria-labelledby": "posting-windows-heading", children: [_jsxs("div", { className: "mb-2 flex flex-wrap items-center justify-between gap-2", children: [_jsxs("div", { children: [_jsx("h2", { id: "posting-windows-heading", className: "text-base font-semibold", children: t("accounting.postingWindows") }), _jsx("p", { className: "text-sm text-fg-muted", children: t("accounting.postingWindowsDescription") })] }), _jsxs("div", { className: "flex gap-2", children: [_jsxs(Button, { variant: "secondary", onClick: () => { setWindows([...windowRows, { roleId: "", allowFrom: "", allowTo: "", reason: "" }]); }, "data-testid": "add-window", children: [_jsx(Plus, { "aria-hidden": "true" }), t("accounting.addWindow")] }), _jsx(Button, { onClick: () => { saveWindows.mutate(); }, loading: saveWindows.isPending, disabled: windows === null, "data-testid": "save-windows", children: t("common.save") })] })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("accounting.role") }), _jsx(TableHead, { children: t("accounting.allowFrom") }), _jsx(TableHead, { children: t("accounting.allowTo") }), _jsx(TableHead, { children: t("common.reason") }), _jsx(TableHead, {})] }) }), _jsxs(TableBody, { children: [windowRows.length === 0 ? (_jsx(TableRow, { children: _jsx(TableCell, { colSpan: 5, className: "text-fg-muted", children: t("accounting.noWindows") }) })) : null, windowRows.map((row, index) => {
                                        const update = (patch) => { setWindows(windowRows.map((w, i) => (i === index ? { ...w, ...patch } : w))); };
                                        return (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("accounting.role"), value: row.roleId, onChange: (e) => { update({ roleId: e.target.value }); }, "data-testid": `window-role-${index}`, children: [_jsx("option", { value: "", children: t("accounting.everyone") }), (roles.data ?? []).map((role) => (_jsx("option", { value: role.id, children: localized(role.name) || role.code }, role.id)))] }) }), _jsx(TableCell, { children: _jsx(TextField, { type: "date", "aria-label": t("accounting.allowFrom"), value: row.allowFrom, onChange: (e) => { update({ allowFrom: e.target.value }); }, dir: "ltr", "data-testid": `window-from-${index}` }) }), _jsx(TableCell, { children: _jsx(TextField, { type: "date", "aria-label": t("accounting.allowTo"), value: row.allowTo, onChange: (e) => { update({ allowTo: e.target.value }); }, dir: "ltr" }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("common.reason"), value: row.reason, onChange: (e) => { update({ reason: e.target.value }); } }) }), _jsx(TableCell, { children: _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("common.delete"), onClick: () => { setWindows(windowRows.filter((_, i) => i !== index)); }, children: _jsx(Trash2, { "aria-hidden": "true" }) }) })] }, index));
                                    })] })] })] })] }));
}
