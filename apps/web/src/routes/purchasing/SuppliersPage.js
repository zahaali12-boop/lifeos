import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { HoldBadge, num, useDeliveryTerms, usePaymentTerms, useSupplierGroups, useSupplierPostingGroups, useWhtCodes } from "./shared";
const emptyPartner = () => ({ code: "", legalName: "", legalNameAr: "", tradeName: "", tradeNameAr: "", kind: "organization", email: "", phone: "", website: "", notes: "" });
const emptyAccount = (currency) => ({ supplierGroupId: "", paymentTermsId: "", deliveryTermsId: "", postingGroupId: "", whtCodeId: "", currency, leadTimeDays: "0", priceTolerancePct: "0", qtyTolerancePct: "0", requiresPo: false, isActive: true });
function accountBody(f) {
    return {
        supplierGroupId: f.supplierGroupId || null,
        paymentTermsId: f.paymentTermsId || null,
        deliveryTermsId: f.deliveryTermsId || null,
        postingGroupId: f.postingGroupId || null,
        whtCodeId: f.whtCodeId || null,
        currency: f.currency || null,
        leadTimeDays: num(f.leadTimeDays),
        priceTolerancePct: num(f.priceTolerancePct),
        qtyTolerancePct: num(f.qtyTolerancePct),
        requiresPo: f.requiresPo,
        isActive: f.isActive,
    };
}
function accountForm(a) {
    return {
        supplierGroupId: a.supplierGroupId ?? "",
        paymentTermsId: a.paymentTermsId ?? "",
        deliveryTermsId: a.deliveryTermsId ?? "",
        postingGroupId: a.postingGroupId ?? "",
        whtCodeId: a.whtCodeId ?? "",
        currency: a.currency,
        leadTimeDays: String(a.leadTimeDays),
        priceTolerancePct: String(a.priceTolerancePct),
        qtyTolerancePct: String(a.qtyTolerancePct),
        requiresPo: a.requiresPo,
        isActive: a.isActive,
    };
}
/** Suppliers of the company (roadmap 4.1): the partner record and its supplier account with terms, tolerances and holds; contacts, addresses, encrypted bank accounts, tax registrations. */
export function SuppliersPage() {
    const { t, i18n } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const openId = search.open;
    const { companies, companyId, setCompanyId, company } = useCompanyContext();
    const [q, setQ] = useState("");
    const [holdFilter, setHoldFilter] = useState("");
    const [creating, setCreating] = useState(null);
    const [problem, setProblem] = useState(null);
    const functionalCurrency = company?.functionalCurrency ?? "";
    const suppliers = useQuery({
        queryKey: ["suppliers", companyId, q, holdFilter],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId, ...(q ? { q } : {}), ...(holdFilter ? { holdStatus: holdFilter } : {}) } } })),
    });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["suppliers"] });
        await queryClient.invalidateQueries({ queryKey: ["partner"] });
    };
    const open = (id) => { setProblem(null); void navigate({ to: "/purchasing/suppliers", search: id ? { open: id } : {} }); };
    const create = useMutation({
        mutationFn: async (input) => {
            const p = input.partner;
            const partner = unwrap(await api.POST("/api/v1/partners", { body: { code: p.code, legalName: { en: p.legalName, ar: p.legalNameAr || p.legalName }, tradeName: p.tradeName ? { en: p.tradeName, ar: p.tradeNameAr || p.tradeName } : null, kind: p.kind, isSupplier: true, isCustomer: false, isEmployee: false, defaultLanguage: "en", isActive: true, email: p.email || null, phone: p.phone || null, website: p.website || null, notes: p.notes || null } }));
            unwrap(await api.PUT("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}", { params: { path: { partnerId: partner.id, companyId } }, body: accountBody(input.account) }));
            return partner;
        },
        onSuccess: async (partner) => {
            setCreating(null);
            setProblem(null);
            await refresh();
            open(partner.id);
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "code", accessorKey: "partnerCode", header: t("partners.code"), size: 120, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.partnerCode }) },
        { id: "name", accessorFn: (row) => localized(row.partnerName), header: t("partners.legalName"), size: 240, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.partnerName) }) },
        { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
        { id: "terms", accessorFn: (row) => row.effective.paymentTermsCode ?? "", header: t("partners.paymentTerms"), size: 120 },
        { id: "delivery", accessorFn: (row) => row.effective.deliveryTermsCode ?? "", header: t("partners.deliveryTerms"), size: 110 },
        { id: "lead", accessorKey: "leadTimeDays", header: t("partners.leadTimeDays"), size: 110, cell: ({ row }) => String(row.original.leadTimeDays) },
        { id: "hold", accessorKey: "holdStatus", header: t("partners.holdStatus"), size: 150, cell: ({ row }) => _jsx(HoldBadge, { status: row.original.holdStatus }) },
        { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.isActive ? "success" : "neutral", children: row.original.isActive ? t("common.active") : t("common.inactive") }) },
    ], [t]);
    const setPartner = (patch) => { setCreating((prev) => (prev ? { ...prev, partner: { ...prev.partner, ...patch } } : prev)); };
    const submitCreate = (event) => {
        event.preventDefault();
        if (creating) {
            create.mutate(creating);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.suppliers"), description: t("partners.suppliersDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setCreating({ partner: emptyPartner(), account: emptyAccount(functionalCurrency) }); }, disabled: !companyId, "data-testid": "new-supplier", children: [_jsx(Plus, { "aria-hidden": "true" }), t("partners.newSupplier")] }) }), _jsxs("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: [_jsx(CompanyFilter, { companies: companies, value: companyId, onChange: setCompanyId }), _jsx(Field, { label: t("common.search"), children: _jsx(TextField, { value: q, onChange: (e) => { setQ(e.target.value); }, placeholder: t("partners.search"), "data-testid": "supplier-search" }) }), _jsx(Field, { label: t("partners.holdStatus"), children: _jsx(SelectField, { value: holdFilter, onChange: (e) => { setHoldFilter(e.target.value); }, children: ["", "held", "none"].map((h) => (_jsx("option", { value: h, children: t(`partners.holdFilter.${h}`) }, h))) }) })] }), _jsx(DataGrid, { label: "nav.suppliers", columns: columns, data: suppliers.data ?? [], rowKey: (row) => row.id, loading: suppliers.isPending && Boolean(companyId), emptyTitle: t("partners.emptySuppliers"), emptyDescription: t("partners.emptySuppliersDescription"), onOpen: (row) => { open(row.partnerId); } }), openId ? _jsx(SupplierDialog, { partnerId: openId, companyId: companyId, onClose: () => { open(null); }, onChanged: refresh }) : null, _jsx(Dialog, { open: Boolean(creating), onOpenChange: (isOpen) => { if (!isOpen) {
                    setCreating(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-3xl", children: creating ? (_jsxs("form", { onSubmit: submitCreate, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("partners.newSupplier") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("partners.code"), required: true, children: _jsx(TextField, { value: creating.partner.code, onChange: (e) => { setPartner({ code: e.target.value }); }, dir: "ltr", required: true, "data-testid": "partner-code" }) }), _jsx(Field, { label: t("partners.kind"), children: _jsx(SelectField, { value: creating.partner.kind, onChange: (e) => { setPartner({ kind: e.target.value }); }, children: ["organization", "person"].map((k) => (_jsx("option", { value: k, children: t(`partners.kinds.${k}`) }, k))) }) }), _jsx(Field, { label: t("partners.legalName"), required: true, children: _jsx(TextField, { value: creating.partner.legalName, onChange: (e) => { setPartner({ legalName: e.target.value }); }, required: true, "data-testid": "partner-legal-name-en" }) }), _jsx(Field, { label: t("partners.legalNameAr"), children: _jsx(TextField, { value: creating.partner.legalNameAr, onChange: (e) => { setPartner({ legalNameAr: e.target.value }); }, dir: "rtl", lang: "ar", "data-testid": "partner-legal-name-ar" }) }), _jsx(Field, { label: t("partners.tradeName"), children: _jsx(TextField, { value: creating.partner.tradeName, onChange: (e) => { setPartner({ tradeName: e.target.value }); } }) }), _jsx(Field, { label: t("partners.tradeNameAr"), children: _jsx(TextField, { value: creating.partner.tradeNameAr, onChange: (e) => { setPartner({ tradeNameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), _jsx(Field, { label: t("partners.email"), children: _jsx(TextField, { type: "email", value: creating.partner.email, onChange: (e) => { setPartner({ email: e.target.value }); }, dir: "ltr", "data-testid": "partner-email" }) }), _jsx(Field, { label: t("partners.phone"), children: _jsx(TextField, { value: creating.partner.phone, onChange: (e) => { setPartner({ phone: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.website"), children: _jsx(TextField, { value: creating.partner.website, onChange: (e) => { setPartner({ website: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.notes"), children: _jsx(TextField, { value: creating.partner.notes, onChange: (e) => { setPartner({ notes: e.target.value }); }, lang: i18n.language }) })] }), _jsx(AccountFields, { form: creating.account, onChange: (patch) => { setCreating((prev) => (prev ? { ...prev, account: { ...prev.account, ...patch } } : prev)); } }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setCreating(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, "data-testid": "save-supplier", children: t("common.save") })] })] })) : null }) })] }));
}
function AccountFields({ form, onChange }) {
    const { t } = useTranslation();
    const groups = useSupplierGroups();
    const paymentTerms = usePaymentTerms();
    const deliveryTerms = useDeliveryTerms();
    const postingGroups = useSupplierPostingGroups();
    const whtCodes = useWhtCodes();
    return (_jsxs("div", { className: "grid gap-4 sm:grid-cols-3", "data-testid": "account-fields", children: [_jsx(Field, { label: t("partners.group"), children: _jsxs(SelectField, { value: form.supplierGroupId, onChange: (e) => { onChange({ supplierGroupId: e.target.value }); }, "data-testid": "account-group", children: [_jsx("option", { value: "", children: t("partners.noGroup") }), (groups.data ?? []).filter((g) => g.isActive).map((g) => (_jsxs("option", { value: g.id, children: [g.code, " \u00B7 ", localized(g.name)] }, g.id)))] }) }), _jsx(Field, { label: t("partners.paymentTerms"), children: _jsxs(SelectField, { value: form.paymentTermsId, onChange: (e) => { onChange({ paymentTermsId: e.target.value }); }, "data-testid": "account-payment-terms", children: [_jsxs("option", { value: "", children: ["\u2014 ", t("partners.fromGroup")] }), (paymentTerms.data ?? []).filter((p) => p.isActive).map((p) => (_jsxs("option", { value: p.id, children: [p.code, " \u00B7 ", localized(p.name)] }, p.id)))] }) }), _jsx(Field, { label: t("partners.deliveryTerms"), children: _jsxs(SelectField, { value: form.deliveryTermsId, onChange: (e) => { onChange({ deliveryTermsId: e.target.value }); }, "data-testid": "account-delivery-terms", children: [_jsxs("option", { value: "", children: ["\u2014 ", t("partners.fromGroup")] }), (deliveryTerms.data ?? []).filter((d) => d.isActive).map((d) => (_jsxs("option", { value: d.id, children: [d.code, " \u00B7 ", localized(d.name)] }, d.id)))] }) }), _jsx(Field, { label: t("partners.postingGroup"), children: _jsxs(SelectField, { value: form.postingGroupId, onChange: (e) => { onChange({ postingGroupId: e.target.value }); }, children: [_jsxs("option", { value: "", children: ["\u2014 ", t("partners.fromGroup")] }), (postingGroups.data ?? []).map((g) => (_jsxs("option", { value: g.id, children: [g.code, " \u00B7 ", localized(g.name)] }, g.id)))] }) }), _jsx(Field, { label: t("partners.whtCode"), children: _jsxs(SelectField, { value: form.whtCodeId, onChange: (e) => { onChange({ whtCodeId: e.target.value }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (whtCodes.data ?? []).filter((c) => c.isActive).map((c) => (_jsxs("option", { value: c.id, children: [c.code, " \u00B7 ", String(c.ratePct), "%"] }, c.id)))] }) }), _jsx(Field, { label: t("partners.currency"), required: true, children: _jsx(TextField, { value: form.currency, onChange: (e) => { onChange({ currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, required: true, "data-testid": "account-currency" }) }), _jsx(Field, { label: t("partners.leadTimeDays"), children: _jsx(TextField, { type: "number", min: 0, value: form.leadTimeDays, onChange: (e) => { onChange({ leadTimeDays: e.target.value }); }, dir: "ltr", "data-testid": "account-lead-time" }) }), _jsx(Field, { label: t("partners.priceTolerancePct"), children: _jsx(TextField, { inputMode: "decimal", value: form.priceTolerancePct, onChange: (e) => { onChange({ priceTolerancePct: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.qtyTolerancePct"), children: _jsx(TextField, { inputMode: "decimal", value: form.qtyTolerancePct, onChange: (e) => { onChange({ qtyTolerancePct: e.target.value }); }, dir: "ltr" }) }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.requiresPo, onChange: (e) => { onChange({ requiresPo: e.target.checked }); } }), t("partners.requiresPo")] }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.isActive, onChange: (e) => { onChange({ isActive: e.target.checked }); } }), t("common.active")] })] }));
}
function SupplierDialog({ partnerId, companyId, onClose, onChanged }) {
    const { t, i18n } = useTranslation();
    const queryClient = useQueryClient();
    const [tab, setTab] = useState("account");
    const [problem, setProblem] = useState(null);
    const [account, setAccount] = useState(null);
    const [hold, setHold] = useState({ status: "purchase", reason: "" });
    const [contact, setContact] = useState({ name: "", nameAr: "", role: "", email: "", phone: "", mobile: "", isPrimary: false, receivesStatements: false });
    const [address, setAddress] = useState({ role: "legal", country: "", region: "", line1: "", line1Ar: "", city: "", cityAr: "", isDefault: true });
    const [bank, setBank] = useState({ bankName: "", branch: "", swiftBic: "", currency: "", accountHolder: "", accountNumber: "", iban: "", isDefault: true });
    const [registration, setRegistration] = useState({ country: "", registrationType: "vat", number: "", validFrom: "", validTo: "" });
    const [revealed, setRevealed] = useState({});
    const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
    const permissions = useMemo(() => new Set(me.data?.permissions ?? []), [me.data]);
    const canReveal = permissions.has("*") || permissions.has("partners.supplier.reveal_bank_account");
    const detail = useQuery({ queryKey: ["partner", partnerId], queryFn: async () => unwrap(await api.GET("/api/v1/partners/{partnerId}", { params: { path: { partnerId } } })) });
    const partner = detail.data?.partner;
    const current = detail.data?.supplierAccounts.find((a) => a.companyId === companyId);
    const form = account ?? (current ? accountForm(current) : emptyAccount(""));
    const holdStatus = current?.holdStatus;
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["partner", partnerId] });
        await onChanged();
    };
    const fail = (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
    const saveAccount = useMutation({
        mutationFn: async (f) => unwrap(await api.PUT("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}", { params: { path: { partnerId, companyId } }, body: accountBody(f) })),
        onSuccess: async () => { setProblem(null); setAccount(null); await refresh(); },
        onError: fail,
    });
    const holdAccount = useMutation({
        mutationFn: async (release) => release
            ? unwrap(await api.POST("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}/release", { params: { path: { partnerId, companyId } } }))
            : unwrap(await api.POST("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}/hold", { params: { path: { partnerId, companyId } }, body: { status: hold.status, reason: hold.reason } })),
        onSuccess: async () => { setProblem(null); setHold({ status: "purchase", reason: "" }); await refresh(); },
        onError: fail,
    });
    const addContact = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/contacts", { params: { path: { partnerId } }, body: { name: { en: contact.name, ar: contact.nameAr || contact.name }, role: contact.role || null, email: contact.email || null, phone: contact.phone || null, mobile: contact.mobile || null, isPrimary: contact.isPrimary, receivesStatements: contact.receivesStatements, isActive: true } })),
        onSuccess: async () => { setProblem(null); setContact({ name: "", nameAr: "", role: "", email: "", phone: "", mobile: "", isPrimary: false, receivesStatements: false }); await refresh(); },
        onError: fail,
    });
    const addAddress = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/addresses", { params: { path: { partnerId } }, body: { role: address.role, country: address.country, region: address.region || null, isDefault: address.isDefault, address: { line1: { en: address.line1, ar: address.line1Ar || address.line1 }, city: { en: address.city, ar: address.cityAr || address.city } } } })),
        onSuccess: async () => { setProblem(null); setAddress({ role: "legal", country: "", region: "", line1: "", line1Ar: "", city: "", cityAr: "", isDefault: true }); await refresh(); },
        onError: fail,
    });
    const addBank = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/bank-accounts", { params: { path: { partnerId } }, body: { bankName: bank.bankName, branch: bank.branch || null, swiftBic: bank.swiftBic || null, currency: bank.currency, accountHolder: bank.accountHolder || null, accountNumber: bank.accountNumber || null, iban: bank.iban || null, isDefault: bank.isDefault, isActive: true } })),
        onSuccess: async () => { setProblem(null); setBank({ bankName: "", branch: "", swiftBic: "", currency: "", accountHolder: "", accountNumber: "", iban: "", isDefault: true }); await refresh(); },
        onError: fail,
    });
    const reveal = useMutation({
        mutationFn: async (accountId) => unwrap(await api.POST("/api/v1/partners/{partnerId}/bank-accounts/{accountId}/reveal", { params: { path: { partnerId, accountId } } })),
        onSuccess: (result) => { setProblem(null); setRevealed((prev) => ({ ...prev, [result.id]: { accountNumber: result.accountNumber, iban: result.iban } })); },
        onError: fail,
    });
    const addRegistration = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/tax-registrations", { params: { path: { partnerId } }, body: { country: registration.country, registrationType: registration.registrationType, number: registration.number, validFrom: registration.validFrom || null, validTo: registration.validTo || null } })),
        onSuccess: async () => { setProblem(null); setRegistration({ country: "", registrationType: "vat", number: "", validFrom: "", validTo: "" }); await refresh(); },
        onError: fail,
    });
    const submit = (run) => (event) => { event.preventDefault(); run(); };
    return (_jsx(Dialog, { open: true, onOpenChange: (isOpen) => { if (!isOpen) {
            onClose();
        } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: partner ? `${partner.code} · ${localized(partner.legalName)}` : t("common.loading") }) }), detail.data && partner ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "supplier-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(Badge, { tone: partner.isActive ? "success" : "neutral", children: partner.isActive ? t("common.active") : t("common.inactive") }), current ? _jsx(HoldBadge, { status: current.holdStatus }) : null, partner.email ? _jsx("span", { className: "text-fg-muted", dir: "ltr", children: partner.email }) : null, partner.phone ? _jsx("span", { className: "text-fg-muted", dir: "ltr", children: partner.phone }) : null] }), _jsx(Tabs, { tabs: [
                                { id: "account", label: t("partners.account"), testId: "tab-account" },
                                { id: "contacts", label: t("partners.contacts"), testId: "tab-contacts" },
                                { id: "addresses", label: t("partners.addresses"), testId: "tab-addresses" },
                                { id: "bank", label: t("partners.bankAccounts"), testId: "tab-bank" },
                                { id: "tax", label: t("partners.taxRegistrations"), testId: "tab-tax" },
                            ], value: tab, onChange: setTab }), _jsx(FormError, { message: problem?.message ?? null }), tab === "account" ? (_jsxs("form", { onSubmit: submit(() => { saveAccount.mutate(form); }), className: "flex flex-col gap-4", children: [current ? (_jsxs("div", { className: "flex flex-wrap items-center gap-3 rounded-md border border-border p-3 text-sm", "data-testid": "effective-terms", children: [_jsxs("span", { className: "font-medium", children: [t("partners.effective"), ":"] }), _jsxs("span", { children: [t("partners.paymentTerms"), " ", current.effective.paymentTermsCode ?? "—"] }), _jsxs("span", { children: [t("partners.deliveryTerms"), " ", current.effective.deliveryTermsCode ?? "—"] }), _jsxs("span", { children: [t("partners.whtCode"), " ", current.effective.whtCode ?? "—"] }), current.holdStatus !== "none" ? _jsx("span", { className: "text-warning", children: t("partners.heldSince", { when: formatDateTime(current.heldAt), reason: current.holdReason ?? "" }) }) : null] })) : null, _jsx(AccountFields, { form: form, onChange: (patch) => { setAccount({ ...form, ...patch }); } }), _jsxs(DialogFooter, { children: [holdStatus === "none" ? (_jsxs("span", { className: "flex flex-wrap items-center gap-2", children: [_jsx(SelectField, { "aria-label": t("partners.holdStatus"), value: hold.status, onChange: (e) => { setHold({ ...hold, status: e.target.value }); }, "data-testid": "hold-status", children: ["purchase", "payment", "all"].map((s) => (_jsx("option", { value: s, children: t(`partners.holdStatuses.${s}`) }, s))) }), _jsx(TextField, { "aria-label": t("partners.holdReason"), value: hold.reason, onChange: (e) => { setHold({ ...hold, reason: e.target.value }); }, placeholder: t("partners.holdReason"), "data-testid": "hold-reason", lang: i18n.language }), _jsx(Button, { type: "button", variant: "danger", onClick: () => { holdAccount.mutate(false); }, loading: holdAccount.isPending, disabled: !hold.reason.trim(), "data-testid": "hold-supplier", children: t("partners.hold") })] })) : null, holdStatus !== undefined && holdStatus !== "none" ? (_jsx(Button, { type: "button", variant: "secondary", onClick: () => { holdAccount.mutate(true); }, loading: holdAccount.isPending, "data-testid": "release-supplier", children: t("partners.release") })) : null, _jsx(Button, { type: "submit", loading: saveAccount.isPending, "data-testid": "save-account", children: t("partners.saveAccount") })] })] })) : null, tab === "contacts" ? (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.name") }), _jsx(TableHead, { children: t("partners.role") }), _jsx(TableHead, { children: t("partners.email") }), _jsx(TableHead, { children: t("partners.phone") }), _jsx(TableHead, { children: t("partners.primary") })] }) }), _jsx(TableBody, { children: detail.data.contacts.map((c) => (_jsxs(TableRow, { "data-testid": "contact-row", children: [_jsx(TableCell, { dir: "auto", children: localized(c.name) }), _jsx(TableCell, { children: c.role ?? "" }), _jsx(TableCell, { dir: "ltr", children: c.email ?? "" }), _jsx(TableCell, { dir: "ltr", children: c.mobile ?? c.phone ?? "" }), _jsx(TableCell, { children: c.isPrimary ? "✓" : "" })] }, c.id))) })] }), _jsxs("form", { onSubmit: submit(() => { addContact.mutate(); }), className: "grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.name"), required: true, children: _jsx(TextField, { value: contact.name, onChange: (e) => { setContact({ ...contact, name: e.target.value }); }, required: true, "data-testid": "contact-name" }) }), _jsx(Field, { label: t("partners.nameAr"), children: _jsx(TextField, { value: contact.nameAr, onChange: (e) => { setContact({ ...contact, nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), _jsx(Field, { label: t("partners.role"), children: _jsx(TextField, { value: contact.role, onChange: (e) => { setContact({ ...contact, role: e.target.value }); } }) }), _jsx(Field, { label: t("partners.email"), children: _jsx(TextField, { type: "email", value: contact.email, onChange: (e) => { setContact({ ...contact, email: e.target.value }); }, dir: "ltr", "data-testid": "contact-email" }) }), _jsx(Field, { label: t("partners.phone"), children: _jsx(TextField, { value: contact.phone, onChange: (e) => { setContact({ ...contact, phone: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.mobile"), children: _jsx(TextField, { value: contact.mobile, onChange: (e) => { setContact({ ...contact, mobile: e.target.value }); }, dir: "ltr" }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: contact.isPrimary, onChange: (e) => { setContact({ ...contact, isPrimary: e.target.checked }); } }), t("partners.primary")] }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "submit", variant: "secondary", loading: addContact.isPending, "data-testid": "add-contact", children: t("partners.addContact") }) })] })] })) : null, tab === "addresses" ? (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.addressRole") }), _jsx(TableHead, { children: t("partners.line1") }), _jsx(TableHead, { children: t("partners.city") }), _jsx(TableHead, { children: t("partners.country") }), _jsx(TableHead, { children: t("partners.default") })] }) }), _jsx(TableBody, { children: detail.data.addresses.map((a) => {
                                                const parts = a.address;
                                                return (_jsxs(TableRow, { "data-testid": "address-row", children: [_jsx(TableCell, { children: t(`partners.addressRoles.${a.role}`, { defaultValue: a.role }) }), _jsx(TableCell, { dir: "auto", children: localized(parts.line1) }), _jsx(TableCell, { dir: "auto", children: localized(parts.city) }), _jsxs(TableCell, { dir: "ltr", children: [a.country, a.region ? ` · ${a.region}` : ""] }), _jsx(TableCell, { children: a.isDefault ? "✓" : "" })] }, a.id));
                                            }) })] }), _jsxs("form", { onSubmit: submit(() => { addAddress.mutate(); }), className: "grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.addressRole"), children: _jsx(SelectField, { value: address.role, onChange: (e) => { setAddress({ ...address, role: e.target.value }); }, "data-testid": "address-role", children: ["legal", "billing", "shipping", "other"].map((r) => (_jsx("option", { value: r, children: t(`partners.addressRoles.${r}`) }, r))) }) }), _jsx(Field, { label: t("partners.line1"), required: true, children: _jsx(TextField, { value: address.line1, onChange: (e) => { setAddress({ ...address, line1: e.target.value }); }, required: true, "data-testid": "address-line1" }) }), _jsx(Field, { label: t("partners.city"), children: _jsx(TextField, { value: address.city, onChange: (e) => { setAddress({ ...address, city: e.target.value }); } }) }), _jsx(Field, { label: t("partners.country"), required: true, children: _jsx(TextField, { value: address.country, onChange: (e) => { setAddress({ ...address, country: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 2, required: true, "data-testid": "address-country" }) }), _jsx(Field, { label: t("partners.region"), children: _jsx(TextField, { value: address.region, onChange: (e) => { setAddress({ ...address, region: e.target.value }); } }) }), _jsxs("label", { className: "flex items-center gap-2 self-end text-sm", children: [_jsx("input", { type: "checkbox", checked: address.isDefault, onChange: (e) => { setAddress({ ...address, isDefault: e.target.checked }); } }), t("partners.default")] }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "submit", variant: "secondary", loading: addAddress.isPending, "data-testid": "add-address", children: t("partners.addAddress") }) })] })] })) : null, tab === "bank" ? (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsx("p", { className: "text-sm text-fg-muted", children: t("partners.masked") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.bankName") }), _jsx(TableHead, { children: t("partners.currency") }), _jsx(TableHead, { children: t("partners.iban") }), _jsx(TableHead, { children: t("partners.accountNumber") }), _jsx(TableHead, { children: t("partners.default") }), _jsx(TableHead, {})] }) }), _jsx(TableBody, { children: detail.data.bankAccounts.map((b) => (_jsxs(TableRow, { "data-testid": "bank-row", children: [_jsxs(TableCell, { dir: "auto", children: [b.bankName, b.branch ? ` · ${b.branch}` : ""] }), _jsx(TableCell, { children: b.currency }), _jsx(TableCell, { dir: "ltr", className: "font-mono", children: revealed[b.id]?.iban ?? b.ibanMasked ?? "" }), _jsx(TableCell, { dir: "ltr", className: "font-mono", children: revealed[b.id]?.accountNumber ?? b.accountNumberMasked ?? "" }), _jsx(TableCell, { children: b.isDefault ? "✓" : "" }), _jsx(TableCell, { children: canReveal && !revealed[b.id] ? (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { reveal.mutate(b.id); }, loading: reveal.isPending, "data-testid": "reveal-bank", children: t("partners.reveal") })) : revealed[b.id] ? (_jsx("span", { className: "text-xs text-fg-muted", children: t("partners.revealed") })) : null })] }, b.id))) })] }), _jsxs("form", { onSubmit: submit(() => { addBank.mutate(); }), className: "grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.bankName"), required: true, children: _jsx(TextField, { value: bank.bankName, onChange: (e) => { setBank({ ...bank, bankName: e.target.value }); }, required: true, "data-testid": "bank-name" }) }), _jsx(Field, { label: t("partners.branch"), children: _jsx(TextField, { value: bank.branch, onChange: (e) => { setBank({ ...bank, branch: e.target.value }); } }) }), _jsx(Field, { label: t("partners.swiftBic"), children: _jsx(TextField, { value: bank.swiftBic, onChange: (e) => { setBank({ ...bank, swiftBic: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("partners.currency"), required: true, children: _jsx(TextField, { value: bank.currency, onChange: (e) => { setBank({ ...bank, currency: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 3, required: true, "data-testid": "bank-currency" }) }), _jsx(Field, { label: t("partners.accountHolder"), children: _jsx(TextField, { value: bank.accountHolder, onChange: (e) => { setBank({ ...bank, accountHolder: e.target.value }); } }) }), _jsx(Field, { label: t("partners.iban"), children: _jsx(TextField, { value: bank.iban, onChange: (e) => { setBank({ ...bank, iban: e.target.value }); }, dir: "ltr", "data-testid": "bank-iban" }) }), _jsx(Field, { label: t("partners.accountNumber"), children: _jsx(TextField, { value: bank.accountNumber, onChange: (e) => { setBank({ ...bank, accountNumber: e.target.value }); }, dir: "ltr", "data-testid": "bank-account-number" }) }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "submit", variant: "secondary", loading: addBank.isPending, "data-testid": "add-bank", children: t("partners.addBankAccount") }) })] })] })) : null, tab === "tax" ? (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("partners.country") }), _jsx(TableHead, { children: t("partners.registrationType") }), _jsx(TableHead, { children: t("partners.number") }), _jsx(TableHead, { children: t("partners.validFrom") }), _jsx(TableHead, { children: t("partners.validTo") })] }) }), _jsx(TableBody, { children: detail.data.taxRegistrations.map((r) => (_jsxs(TableRow, { "data-testid": "registration-row", children: [_jsx(TableCell, { dir: "ltr", children: r.country }), _jsx(TableCell, { children: t(`partners.registrationTypes.${r.registrationType}`, { defaultValue: r.registrationType }) }), _jsx(TableCell, { dir: "ltr", className: "font-mono", children: r.number }), _jsx(TableCell, { children: formatDate(r.validFrom) }), _jsx(TableCell, { children: formatDate(r.validTo) })] }, r.id))) })] }), _jsxs("form", { onSubmit: submit(() => { addRegistration.mutate(); }), className: "grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4", children: [_jsx(Field, { label: t("partners.country"), required: true, children: _jsx(TextField, { value: registration.country, onChange: (e) => { setRegistration({ ...registration, country: e.target.value.toUpperCase() }); }, dir: "ltr", maxLength: 2, required: true, "data-testid": "registration-country" }) }), _jsx(Field, { label: t("partners.registrationType"), children: _jsx(SelectField, { value: registration.registrationType, onChange: (e) => { setRegistration({ ...registration, registrationType: e.target.value }); }, children: ["vat", "tin", "crn", "other"].map((k) => (_jsx("option", { value: k, children: t(`partners.registrationTypes.${k}`) }, k))) }) }), _jsx(Field, { label: t("partners.number"), required: true, children: _jsx(TextField, { value: registration.number, onChange: (e) => { setRegistration({ ...registration, number: e.target.value }); }, dir: "ltr", required: true, "data-testid": "registration-number" }) }), _jsx(Field, { label: t("partners.validFrom"), children: _jsx(TextField, { type: "date", value: registration.validFrom, onChange: (e) => { setRegistration({ ...registration, validFrom: e.target.value }); }, dir: "ltr" }) }), _jsx("div", { className: "flex items-end", children: _jsx(Button, { type: "submit", variant: "secondary", loading: addRegistration.isPending, "data-testid": "add-registration", children: t("partners.addRegistration") }) })] })] })) : null] })) : null] }) }));
}
