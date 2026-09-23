import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowRightLeft, ClipboardCheck, PackageCheck, RefreshCw } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { SelectField } from "../common";
import { setScanContext, useScanContext } from "./context";
import { useQueue } from "./queue";
import { localized } from "./text";
/** Where the operator works and what to do next. */
export function MobileHomePage() {
    const { t } = useTranslation();
    const context = useScanContext();
    const queue = useQueue();
    const companyId = context.companyId;
    const companies = useQuery({ queryKey: ["mobile", "companies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")) });
    const warehouses = useQuery({
        queryKey: ["mobile", "warehouses", companyId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId: companyId ?? "" } } })),
        enabled: companyId !== null,
    });
    const ready = context.companyId !== null && context.warehouseId !== null;
    return (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs("div", { children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: t("mobile.home.title") }), _jsx("p", { className: "mt-1 text-sm text-fg-muted", children: t("mobile.home.description") })] }), _jsx(Field, { label: t("mobile.home.company"), children: _jsxs(SelectField, { className: "h-12 text-base", "data-testid": "scan-company", value: context.companyId ?? "", onChange: (event) => { setScanContext({ companyId: event.target.value || null, warehouseId: null }); }, children: [_jsx("option", { value: "", children: t("mobile.home.chooseCompany") }), (companies.data ?? []).map((company) => (_jsxs("option", { value: company.id, children: [company.code, " \u00B7 ", localized(company.legalName)] }, company.id)))] }) }), _jsx(Field, { label: t("mobile.home.warehouse"), children: _jsxs(SelectField, { className: "h-12 text-base", "data-testid": "scan-warehouse", value: context.warehouseId ?? "", disabled: companyId === null, onChange: (event) => { setScanContext({ warehouseId: event.target.value || null }); }, children: [_jsx("option", { value: "", children: t("mobile.home.chooseWarehouse") }), (warehouses.data ?? []).map((warehouse) => (_jsxs("option", { value: warehouse.id, children: [warehouse.code, " \u00B7 ", localized(warehouse.name)] }, warehouse.id)))] }) }), _jsxs("div", { className: "grid gap-2", children: [_jsx(Button, { asChild: true, size: "lg", className: "h-14 justify-start gap-3 text-base", disabled: !ready, children: _jsxs(Link, { to: "/m/count", "aria-disabled": !ready, children: [_jsx(ClipboardCheck, { "aria-hidden": "true" }), t("mobile.home.startCount")] }) }), _jsx(Button, { asChild: true, variant: "secondary", size: "lg", className: "h-14 justify-start gap-3 text-base", children: _jsxs(Link, { to: "/m/transfer", "aria-disabled": !ready, children: [_jsx(ArrowRightLeft, { "aria-hidden": "true" }), t("mobile.home.startTransfer")] }) }), _jsx(Button, { asChild: true, variant: "secondary", size: "lg", className: "h-14 justify-start gap-3 text-base", children: _jsxs(Link, { to: "/m/receive", "aria-disabled": !ready, "data-testid": "start-receive", children: [_jsx(PackageCheck, { "aria-hidden": "true" }), t("mobile.home.startReceive")] }) }), _jsx(Button, { asChild: true, variant: "secondary", size: "lg", className: "h-14 justify-start gap-3 text-base", children: _jsxs(Link, { to: "/m/queue", children: [_jsx(RefreshCw, { "aria-hidden": "true" }), t("mobile.home.openQueue")] }) })] }), _jsx("p", { className: "text-sm text-fg-muted", "data-testid": "pending-text", children: t("mobile.pending", { count: queue.length }) }), _jsx("p", { className: "text-xs text-fg-subtle", children: t("mobile.home.installHint") })] }));
}
