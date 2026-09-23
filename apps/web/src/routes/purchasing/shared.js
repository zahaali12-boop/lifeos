import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatMoney, formatNumber } from "../../lib/format";
import { SelectField, TextField } from "../common";
export function usePaymentTerms() {
    return useQuery({ queryKey: ["payment-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/payment-terms")) });
}
export function useDeliveryTerms() {
    return useQuery({ queryKey: ["delivery-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/delivery-terms")) });
}
export function useSupplierGroups() {
    return useQuery({ queryKey: ["supplier-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/supplier-groups")) });
}
export function useWhtCodes() {
    return useQuery({ queryKey: ["wht-codes"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/wht-codes")) });
}
export function useSupplierPostingGroups() {
    return useQuery({ queryKey: ["posting-groups", "partner_supplier"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups", { params: { query: { kind: "partner_supplier" } } })) });
}
export function HoldBadge({ status }) {
    const { t } = useTranslation();
    if (status === "none") {
        return null;
    }
    return (_jsx(Badge, { tone: "warning", "data-testid": "hold-badge", children: t(`partners.holdStatuses.${status}`, { defaultValue: status }) }));
}
/** Number inputs travel as strings so nothing is ever a JavaScript float on the way to the API. */
export function num(value, fallback = 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
}
export function useSuppliers(companyId) {
    return useQuery({
        queryKey: ["suppliers", companyId, "", ""],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId } } })),
    });
}
export function useChargeTypes() {
    return useQuery({ queryKey: ["charge-types"], queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/charge-types")) });
}
export function useAgreements(companyId) {
    return useQuery({
        queryKey: ["agreements", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/agreements", { params: { query: { companyId } } })),
    });
}
const tones = {
    draft: "neutral", pending_approval: "warning", approved: "success", ordered: "success", sent: "info", active: "success", awarded: "success",
    partially_received: "info", received: "success", closed: "neutral", rejected: "danger", cancelled: "neutral", invited: "neutral", responded: "info", declined: "danger",
    open: "info", consumed: "neutral", released: "neutral", posted: "success", reversed: "danger", blocked: "danger", partially_settled: "info", settled: "success",
};
export function PurchaseStatus({ status }) {
    const { t } = useTranslation();
    return (_jsx(Badge, { tone: tones[status] ?? "neutral", "data-testid": "doc-status", children: t(`purchasing.statuses.${status}`, { defaultValue: status }) }));
}
export const emptyLine = () => ({ itemCode: "", description: "", quantity: "1", uom: "", price: "", supplierId: "", blanketLineId: "" });
export function LinesEditor({ lines, onChange, showPrice = true, priceLabel, suppliers, showDescription = false }) {
    const { t } = useTranslation();
    const patch = (index, change) => { onChange(lines.map((l, i) => (i === index ? { ...l, ...change } : l))); };
    return (_jsxs("div", { className: "flex flex-col gap-2", children: [_jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("purchasing.lines") }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { onChange([...lines, emptyLine()]); }, "data-testid": "add-line", children: t("purchasing.addLine") })] }), lines.length > 0 ? (_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("purchasing.itemCode") }), showDescription ? _jsx(TableHead, { children: t("purchasing.description") }) : null, _jsx(TableHead, { children: t("purchasing.quantity") }), _jsx(TableHead, { children: t("purchasing.uom") }), showPrice ? _jsx(TableHead, { children: priceLabel ?? t("purchasing.unitPrice") }) : null, suppliers ? _jsx(TableHead, { children: t("purchasing.suggestedSupplier") }) : null, _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: lines.map((line, index) => (_jsxs(TableRow, { children: [_jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.itemCode"), value: line.itemCode, onChange: (e) => { patch(index, { itemCode: e.target.value.toUpperCase() }); }, dir: "ltr", className: "w-28", "data-testid": `line-item-${String(index)}` }) }), showDescription ? _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.description"), value: line.description, onChange: (e) => { patch(index, { description: e.target.value }); }, className: "w-40" }) }) : null, _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.quantity"), inputMode: "decimal", value: line.quantity, onChange: (e) => { patch(index, { quantity: e.target.value }); }, dir: "ltr", className: "w-20", "data-testid": `line-qty-${String(index)}` }) }), _jsx(TableCell, { children: _jsx(TextField, { "aria-label": t("purchasing.uom"), value: line.uom, onChange: (e) => { patch(index, { uom: e.target.value.toUpperCase() }); }, dir: "ltr", className: "w-20", placeholder: t("purchasing.baseUom"), "data-testid": `line-uom-${String(index)}` }) }), showPrice ? _jsx(TableCell, { children: _jsx(TextField, { "aria-label": priceLabel ?? t("purchasing.unitPrice"), inputMode: "decimal", value: line.price, onChange: (e) => { patch(index, { price: e.target.value }); }, dir: "ltr", className: "w-24", "data-testid": `line-price-${String(index)}` }) }) : null, suppliers ? (_jsx(TableCell, { children: _jsxs(SelectField, { "aria-label": t("purchasing.suggestedSupplier"), value: line.supplierId, onChange: (e) => { patch(index, { supplierId: e.target.value }); }, "data-testid": `line-supplier-${String(index)}`, children: [_jsx("option", { value: "", children: t("purchasing.noSupplier") }), suppliers.map((s) => (_jsx("option", { value: s.partnerId, children: s.partnerCode }, s.partnerId)))] }) })) : null, _jsx(TableCell, { children: _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { onChange(lines.filter((_, i) => i !== index)); }, children: t("workflow.remove") }) })] }, index))) })] })) : (_jsx("p", { className: "text-xs text-fg-muted", children: t("purchasing.noLines") }))] }));
}
/** The optional decimal fields travel as numbers only when filled, so an empty price stays "not given". */
export function optionalNum(value) {
    return value.trim() ? num(value) : null;
}
export function orderLineBodies(lines) {
    return lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null, unitPrice: optionalNum(l.price), discountPct: 0, blanketLineId: l.blanketLineId || null }));
}
/** Read-only lines of a document, with the money in the document's currency. */
export function LinesTable({ lines, currency, testId = "doc-lines" }) {
    const { t } = useTranslation();
    return (_jsxs(Table, { "data-testid": testId, children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("purchasing.item") }), _jsx(TableHead, { children: t("purchasing.quantity") }), currency ? _jsx(TableHead, { children: t("purchasing.unitPrice") }) : null, currency ? _jsx(TableHead, { children: t("purchasing.net") }) : null, _jsx(TableHead, { children: t("common.status") })] }) }), _jsx(TableBody, { children: lines.map((l) => (_jsxs(TableRow, { "data-testid": "doc-line", children: [_jsx(TableCell, { children: String(l.lineNo) }), _jsxs(TableCell, { dir: "auto", children: [l.itemCode ?? "", l.description ? ` · ${l.description}` : ""] }), _jsxs(TableCell, { className: "tabular", dir: "ltr", children: [formatNumber(l.quantity, { maximumFractionDigits: 3 }), " ", l.uomCode] }), currency ? _jsx(TableCell, { className: "tabular", dir: "ltr", children: l.unitPrice === null || l.unitPrice === undefined ? "" : formatNumber(l.unitPrice, { maximumFractionDigits: 4 }) }) : null, currency ? _jsx(TableCell, { className: "tabular", dir: "ltr", children: l.netAmount === undefined ? "" : formatMoney(l.netAmount, currency) }) : null, _jsx(TableCell, { children: l.status ? _jsx(PurchaseStatus, { status: l.status }) : null })] }, l.id))) })] }));
}
