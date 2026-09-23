import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { HoldBadge, num, useDeliveryTerms, usePaymentTerms, useSupplierGroups, useSupplierPostingGroups, useWhtCodes } from "./shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

type SupplierAccount = components["schemas"]["SupplierAccountSummary"];

interface PartnerForm {
  code: string;
  legalName: string;
  legalNameAr: string;
  tradeName: string;
  tradeNameAr: string;
  kind: string;
  email: string;
  phone: string;
  website: string;
  notes: string;
}

interface AccountForm {
  supplierGroupId: string;
  paymentTermsId: string;
  deliveryTermsId: string;
  postingGroupId: string;
  whtCodeId: string;
  currency: string;
  leadTimeDays: string;
  priceTolerancePct: string;
  qtyTolerancePct: string;
  requiresPo: boolean;
  isActive: boolean;
}

const emptyPartner = (): PartnerForm => ({ code: "", legalName: "", legalNameAr: "", tradeName: "", tradeNameAr: "", kind: "organization", email: "", phone: "", website: "", notes: "" });
const emptyAccount = (currency: string): AccountForm => ({ supplierGroupId: "", paymentTermsId: "", deliveryTermsId: "", postingGroupId: "", whtCodeId: "", currency, leadTimeDays: "0", priceTolerancePct: "0", qtyTolerancePct: "0", requiresPo: false, isActive: true });

function accountBody(f: AccountForm) {
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

function accountForm(a: SupplierAccount): AccountForm {
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
  const [creating, setCreating] = useState<{ partner: PartnerForm; account: AccountForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const functionalCurrency = company?.functionalCurrency ?? "";

  const suppliers = useQuery({
    queryKey: ["suppliers", companyId, q, holdFilter],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId, ...(q ? { q } : {}), ...(holdFilter ? { holdStatus: holdFilter } : {}) } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["suppliers"] });
    await queryClient.invalidateQueries({ queryKey: ["partner"] });
  };
  const open = (id: string | null): void => { setProblem(null); void navigate({ to: "/purchasing/suppliers", search: id ? { open: id } : {} }); };

  const create = useMutation({
    mutationFn: async (input: { partner: PartnerForm; account: AccountForm }) => {
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

  const columns = useMemo<ColumnDef<SupplierAccount, unknown>[]>(
    () => [
      { id: "code", accessorKey: "partnerCode", header: t("partners.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.partnerCode}</span> },
      { id: "name", accessorFn: (row) => localized(row.partnerName), header: t("partners.legalName"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.partnerName)}</span> },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
      { id: "terms", accessorFn: (row) => row.effective.paymentTermsCode ?? "", header: t("partners.paymentTerms"), size: 120 },
      { id: "delivery", accessorFn: (row) => row.effective.deliveryTermsCode ?? "", header: t("partners.deliveryTerms"), size: 110 },
      { id: "lead", accessorKey: "leadTimeDays", header: t("partners.leadTimeDays"), size: 110, cell: ({ row }) => String(row.original.leadTimeDays) },
      { id: "hold", accessorKey: "holdStatus", header: t("partners.holdStatus"), size: 150, cell: ({ row }) => <HoldBadge status={row.original.holdStatus} /> },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );

  const setPartner = (patch: Partial<PartnerForm>): void => { setCreating((prev) => (prev ? { ...prev, partner: { ...prev.partner, ...patch } } : prev)); };
  const submitCreate = (event: FormEvent): void => {
    event.preventDefault();
    if (creating) {
      create.mutate(creating);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.suppliers")}
        description={t("partners.suppliersDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setCreating({ partner: emptyPartner(), account: emptyAccount(functionalCurrency) }); }} disabled={!companyId} data-testid="new-supplier">
            <Plus aria-hidden="true" />
            {t("partners.newSupplier")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.search")}>
          <TextField value={q} onChange={(e) => { setQ(e.target.value); }} placeholder={t("partners.search")} data-testid="supplier-search" />
        </Field>
        <Field label={t("partners.holdStatus")}>
          <SelectField value={holdFilter} onChange={(e) => { setHoldFilter(e.target.value); }}>
            {["", "held", "none"].map((h) => (
              <option key={h} value={h}>
                {t(`partners.holdFilter.${h}`)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<SupplierAccount> label="nav.suppliers" columns={columns} data={suppliers.data ?? []} rowKey={(row) => row.id} loading={suppliers.isPending && Boolean(companyId)} emptyTitle={t("partners.emptySuppliers")} emptyDescription={t("partners.emptySuppliersDescription")} onOpen={(row) => { open(row.partnerId); }} />

      {openId ? <SupplierDialog partnerId={openId} companyId={companyId} onClose={() => { open(null); }} onChanged={refresh} /> : null}

      <Dialog open={Boolean(creating)} onOpenChange={(isOpen) => { if (!isOpen) { setCreating(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {creating ? (
            <form onSubmit={submitCreate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("partners.newSupplier")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("partners.code")} required>
                  <TextField value={creating.partner.code} onChange={(e) => { setPartner({ code: e.target.value }); }} dir="ltr" required data-testid="partner-code" />
                </Field>
                <Field label={t("partners.kind")}>
                  <SelectField value={creating.partner.kind} onChange={(e) => { setPartner({ kind: e.target.value }); }}>
                    {["organization", "person"].map((k) => (
                      <option key={k} value={k}>
                        {t(`partners.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("partners.legalName")} required>
                  <TextField value={creating.partner.legalName} onChange={(e) => { setPartner({ legalName: e.target.value }); }} required data-testid="partner-legal-name-en" />
                </Field>
                <Field label={t("partners.legalNameAr")}>
                  <TextField value={creating.partner.legalNameAr} onChange={(e) => { setPartner({ legalNameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="partner-legal-name-ar" />
                </Field>
                <Field label={t("partners.tradeName")}>
                  <TextField value={creating.partner.tradeName} onChange={(e) => { setPartner({ tradeName: e.target.value }); }} />
                </Field>
                <Field label={t("partners.tradeNameAr")}>
                  <TextField value={creating.partner.tradeNameAr} onChange={(e) => { setPartner({ tradeNameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <Field label={t("partners.email")}>
                  <TextField type="email" value={creating.partner.email} onChange={(e) => { setPartner({ email: e.target.value }); }} dir="ltr" data-testid="partner-email" />
                </Field>
                <Field label={t("partners.phone")}>
                  <TextField value={creating.partner.phone} onChange={(e) => { setPartner({ phone: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("partners.website")}>
                  <TextField value={creating.partner.website} onChange={(e) => { setPartner({ website: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("partners.notes")}>
                  <TextField value={creating.partner.notes} onChange={(e) => { setPartner({ notes: e.target.value }); }} lang={i18n.language} />
                </Field>
              </div>
              <AccountFields form={creating.account} onChange={(patch) => { setCreating((prev) => (prev ? { ...prev, account: { ...prev.account, ...patch } } : prev)); }} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCreating(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={create.isPending} data-testid="save-supplier">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}

function AccountFields({ form, onChange }: { form: AccountForm; onChange: (patch: Partial<AccountForm>) => void }) {
  const { t } = useTranslation();
  const groups = useSupplierGroups();
  const paymentTerms = usePaymentTerms();
  const deliveryTerms = useDeliveryTerms();
  const postingGroups = useSupplierPostingGroups();
  const whtCodes = useWhtCodes();
  return (
    <div className="grid gap-4 sm:grid-cols-3" data-testid="account-fields">
      <Field label={t("partners.group")}>
        <SelectField value={form.supplierGroupId} onChange={(e) => { onChange({ supplierGroupId: e.target.value }); }} data-testid="account-group">
          <option value="">{t("partners.noGroup")}</option>
          {(groups.data ?? []).filter((g) => g.isActive).map((g) => (
            <option key={g.id} value={g.id}>
              {g.code} · {localized(g.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("partners.paymentTerms")}>
        <SelectField value={form.paymentTermsId} onChange={(e) => { onChange({ paymentTermsId: e.target.value }); }} data-testid="account-payment-terms">
          <option value="">— {t("partners.fromGroup")}</option>
          {(paymentTerms.data ?? []).filter((p) => p.isActive).map((p) => (
            <option key={p.id} value={p.id}>
              {p.code} · {localized(p.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("partners.deliveryTerms")}>
        <SelectField value={form.deliveryTermsId} onChange={(e) => { onChange({ deliveryTermsId: e.target.value }); }} data-testid="account-delivery-terms">
          <option value="">— {t("partners.fromGroup")}</option>
          {(deliveryTerms.data ?? []).filter((d) => d.isActive).map((d) => (
            <option key={d.id} value={d.id}>
              {d.code} · {localized(d.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("partners.postingGroup")}>
        <SelectField value={form.postingGroupId} onChange={(e) => { onChange({ postingGroupId: e.target.value }); }}>
          <option value="">— {t("partners.fromGroup")}</option>
          {(postingGroups.data ?? []).map((g) => (
            <option key={g.id} value={g.id}>
              {g.code} · {localized(g.name)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("partners.whtCode")}>
        <SelectField value={form.whtCodeId} onChange={(e) => { onChange({ whtCodeId: e.target.value }); }}>
          <option value="">—</option>
          {(whtCodes.data ?? []).filter((c) => c.isActive).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {String(c.ratePct)}%
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("partners.currency")} required>
        <TextField value={form.currency} onChange={(e) => { onChange({ currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="account-currency" />
      </Field>
      <Field label={t("partners.leadTimeDays")}>
        <TextField type="number" min={0} value={form.leadTimeDays} onChange={(e) => { onChange({ leadTimeDays: e.target.value }); }} dir="ltr" data-testid="account-lead-time" />
      </Field>
      <Field label={t("partners.priceTolerancePct")}>
        <TextField inputMode="decimal" value={form.priceTolerancePct} onChange={(e) => { onChange({ priceTolerancePct: e.target.value }); }} dir="ltr" />
      </Field>
      <Field label={t("partners.qtyTolerancePct")}>
        <TextField inputMode="decimal" value={form.qtyTolerancePct} onChange={(e) => { onChange({ qtyTolerancePct: e.target.value }); }} dir="ltr" />
      </Field>
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={form.requiresPo} onChange={(e) => { onChange({ requiresPo: e.target.checked }); }} />
        {t("partners.requiresPo")}
      </label>
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={form.isActive} onChange={(e) => { onChange({ isActive: e.target.checked }); }} />
        {t("common.active")}
      </label>
    </div>
  );
}

function SupplierDialog({ partnerId, companyId, onClose, onChanged }: { partnerId: string; companyId: string; onClose: () => void; onChanged: () => Promise<void> }) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState("account");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [account, setAccount] = useState<AccountForm | null>(null);
  const [hold, setHold] = useState({ status: "purchase", reason: "" });
  const [contact, setContact] = useState({ name: "", nameAr: "", role: "", email: "", phone: "", mobile: "", isPrimary: false, receivesStatements: false });
  const [address, setAddress] = useState({ role: "legal", country: "", region: "", line1: "", line1Ar: "", city: "", cityAr: "", isDefault: true });
  const [bank, setBank] = useState({ bankName: "", branch: "", swiftBic: "", currency: "", accountHolder: "", accountNumber: "", iban: "", isDefault: true });
  const [registration, setRegistration] = useState({ country: "", registrationType: "vat", number: "", validFrom: "", validTo: "" });
  const [revealed, setRevealed] = useState<Record<string, { accountNumber: string | null; iban: string | null }>>({});

  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  const permissions = useMemo(() => new Set<string>(me.data?.permissions ?? []), [me.data]);
  const canReveal = permissions.has("*") || permissions.has("partners.supplier.reveal_bank_account");
  const detail = useQuery({ queryKey: ["partner", partnerId], queryFn: async () => unwrap(await api.GET("/api/v1/partners/{partnerId}", { params: { path: { partnerId } } })) });
  const partner = detail.data?.partner;
  const current = detail.data?.supplierAccounts.find((a) => a.companyId === companyId);
  const form = account ?? (current ? accountForm(current) : emptyAccount(""));
  const holdStatus = current?.holdStatus;
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["partner", partnerId] });
    await onChanged();
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const saveAccount = useMutation({
    mutationFn: async (f: AccountForm) => unwrap(await api.PUT("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}", { params: { path: { partnerId, companyId } }, body: accountBody(f) })),
    onSuccess: async () => { setProblem(null); setAccount(null); await refresh(); },
    onError: fail,
  });
  const holdAccount = useMutation({
    mutationFn: async (release: boolean) => release
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
    mutationFn: async (accountId: string) => unwrap(await api.POST("/api/v1/partners/{partnerId}/bank-accounts/{accountId}/reveal", { params: { path: { partnerId, accountId } } })),
    onSuccess: (result) => { setProblem(null); setRevealed((prev) => ({ ...prev, [result.id]: { accountNumber: result.accountNumber, iban: result.iban } })); },
    onError: fail,
  });
  const addRegistration = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/tax-registrations", { params: { path: { partnerId } }, body: { country: registration.country, registrationType: registration.registrationType, number: registration.number, validFrom: registration.validFrom || null, validTo: registration.validTo || null } })),
    onSuccess: async () => { setProblem(null); setRegistration({ country: "", registrationType: "vat", number: "", validFrom: "", validTo: "" }); await refresh(); },
    onError: fail,
  });

  const submit = (run: () => void) => (event: FormEvent): void => { event.preventDefault(); run(); };

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold" dir="auto">
            {partner ? `${partner.code} · ${localized(partner.legalName)}` : t("common.loading")}
          </DialogTitle>
        </DialogHeader>
        {detail.data && partner ? (
          <div className="flex flex-col gap-4" data-testid="supplier-detail">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <Badge tone={partner.isActive ? "success" : "neutral"}>{partner.isActive ? t("common.active") : t("common.inactive")}</Badge>
              {current ? <HoldBadge status={current.holdStatus} /> : null}
              {partner.email ? <span className="text-fg-muted" dir="ltr">{partner.email}</span> : null}
              {partner.phone ? <span className="text-fg-muted" dir="ltr">{partner.phone}</span> : null}
            </div>
            <Tabs
              tabs={[
                { id: "account", label: t("partners.account"), testId: "tab-account" },
                { id: "contacts", label: t("partners.contacts"), testId: "tab-contacts" },
                { id: "addresses", label: t("partners.addresses"), testId: "tab-addresses" },
                { id: "bank", label: t("partners.bankAccounts"), testId: "tab-bank" },
                { id: "tax", label: t("partners.taxRegistrations"), testId: "tab-tax" },
                { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" },
                { id: "history", label: t("history.tab"), testId: "tab-history" },
              ]}
              value={tab}
              onChange={setTab}
            />
            {tab === "discussion" ? <RecordDiscussion entityType="partner" entityId={partner.id} /> : null}
            {tab === "history" ? <RecordHistory entityType="partner" entityId={partner.id} /> : null}
            <FormError message={problem?.message ?? null} />
            {tab === "account" ? (
              <form onSubmit={submit(() => { saveAccount.mutate(form); })} className="flex flex-col gap-4">
                {current ? (
                  <div className="flex flex-wrap items-center gap-3 rounded-md border border-border p-3 text-sm" data-testid="effective-terms">
                    <span className="font-medium">{t("partners.effective")}:</span>
                    <span>{t("partners.paymentTerms")} {current.effective.paymentTermsCode ?? "—"}</span>
                    <span>{t("partners.deliveryTerms")} {current.effective.deliveryTermsCode ?? "—"}</span>
                    <span>{t("partners.whtCode")} {current.effective.whtCode ?? "—"}</span>
                    {current.holdStatus !== "none" ? <span className="text-warning">{t("partners.heldSince", { when: formatDateTime(current.heldAt), reason: current.holdReason ?? "" })}</span> : null}
                  </div>
                ) : null}
                <AccountFields form={form} onChange={(patch) => { setAccount({ ...form, ...patch }); }} />
                <DialogFooter>
                  {holdStatus === "none" ? (
                    <span className="flex flex-wrap items-center gap-2">
                      <SelectField aria-label={t("partners.holdStatus")} value={hold.status} onChange={(e) => { setHold({ ...hold, status: e.target.value }); }} data-testid="hold-status">
                        {["purchase", "payment", "all"].map((s) => (
                          <option key={s} value={s}>
                            {t(`partners.holdStatuses.${s}`)}
                          </option>
                        ))}
                      </SelectField>
                      <TextField aria-label={t("partners.holdReason")} value={hold.reason} onChange={(e) => { setHold({ ...hold, reason: e.target.value }); }} placeholder={t("partners.holdReason")} data-testid="hold-reason" lang={i18n.language} />
                      <Button type="button" variant="danger" onClick={() => { holdAccount.mutate(false); }} loading={holdAccount.isPending} disabled={!hold.reason.trim()} data-testid="hold-supplier">
                        {t("partners.hold")}
                      </Button>
                    </span>
                  ) : null}
                  {holdStatus !== undefined && holdStatus !== "none" ? (
                    <Button type="button" variant="secondary" onClick={() => { holdAccount.mutate(true); }} loading={holdAccount.isPending} data-testid="release-supplier">
                      {t("partners.release")}
                    </Button>
                  ) : null}
                  <Button type="submit" loading={saveAccount.isPending} data-testid="save-account">
                    {t("partners.saveAccount")}
                  </Button>
                </DialogFooter>
              </form>
            ) : null}
            {tab === "contacts" ? (
              <div className="flex flex-col gap-4">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.name")}</TableHead>
                      <TableHead>{t("partners.role")}</TableHead>
                      <TableHead>{t("partners.email")}</TableHead>
                      <TableHead>{t("partners.phone")}</TableHead>
                      <TableHead>{t("partners.primary")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {detail.data.contacts.map((c) => (
                      <TableRow key={c.id} data-testid="contact-row">
                        <TableCell dir="auto">{localized(c.name)}</TableCell>
                        <TableCell>{c.role ?? ""}</TableCell>
                        <TableCell dir="ltr">{c.email ?? ""}</TableCell>
                        <TableCell dir="ltr">{c.mobile ?? c.phone ?? ""}</TableCell>
                        <TableCell>{c.isPrimary ? "✓" : ""}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                <form onSubmit={submit(() => { addContact.mutate(); })} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
                  <Field label={t("partners.name")} required>
                    <TextField value={contact.name} onChange={(e) => { setContact({ ...contact, name: e.target.value }); }} required data-testid="contact-name" />
                  </Field>
                  <Field label={t("partners.nameAr")}>
                    <TextField value={contact.nameAr} onChange={(e) => { setContact({ ...contact, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                  </Field>
                  <Field label={t("partners.role")}>
                    <TextField value={contact.role} onChange={(e) => { setContact({ ...contact, role: e.target.value }); }} />
                  </Field>
                  <Field label={t("partners.email")}>
                    <TextField type="email" value={contact.email} onChange={(e) => { setContact({ ...contact, email: e.target.value }); }} dir="ltr" data-testid="contact-email" />
                  </Field>
                  <Field label={t("partners.phone")}>
                    <TextField value={contact.phone} onChange={(e) => { setContact({ ...contact, phone: e.target.value }); }} dir="ltr" />
                  </Field>
                  <Field label={t("partners.mobile")}>
                    <TextField value={contact.mobile} onChange={(e) => { setContact({ ...contact, mobile: e.target.value }); }} dir="ltr" />
                  </Field>
                  <label className="flex items-center gap-2 self-end text-sm">
                    <input type="checkbox" checked={contact.isPrimary} onChange={(e) => { setContact({ ...contact, isPrimary: e.target.checked }); }} />
                    {t("partners.primary")}
                  </label>
                  <div className="flex items-end">
                    <Button type="submit" variant="secondary" loading={addContact.isPending} data-testid="add-contact">
                      {t("partners.addContact")}
                    </Button>
                  </div>
                </form>
              </div>
            ) : null}
            {tab === "addresses" ? (
              <div className="flex flex-col gap-4">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.addressRole")}</TableHead>
                      <TableHead>{t("partners.line1")}</TableHead>
                      <TableHead>{t("partners.city")}</TableHead>
                      <TableHead>{t("partners.country")}</TableHead>
                      <TableHead>{t("partners.default")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {detail.data.addresses.map((a) => {
                      const parts = a.address as { line1?: Record<string, string>; city?: Record<string, string> };
                      return (
                        <TableRow key={a.id} data-testid="address-row">
                          <TableCell>{t(`partners.addressRoles.${a.role}`, { defaultValue: a.role })}</TableCell>
                          <TableCell dir="auto">{localized(parts.line1)}</TableCell>
                          <TableCell dir="auto">{localized(parts.city)}</TableCell>
                          <TableCell dir="ltr">{a.country}{a.region ? ` · ${a.region}` : ""}</TableCell>
                          <TableCell>{a.isDefault ? "✓" : ""}</TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
                <form onSubmit={submit(() => { addAddress.mutate(); })} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
                  <Field label={t("partners.addressRole")}>
                    <SelectField value={address.role} onChange={(e) => { setAddress({ ...address, role: e.target.value }); }} data-testid="address-role">
                      {["legal", "billing", "shipping", "other"].map((r) => (
                        <option key={r} value={r}>
                          {t(`partners.addressRoles.${r}`)}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("partners.line1")} required>
                    <TextField value={address.line1} onChange={(e) => { setAddress({ ...address, line1: e.target.value }); }} required data-testid="address-line1" />
                  </Field>
                  <Field label={t("partners.city")}>
                    <TextField value={address.city} onChange={(e) => { setAddress({ ...address, city: e.target.value }); }} />
                  </Field>
                  <Field label={t("partners.country")} required>
                    <TextField value={address.country} onChange={(e) => { setAddress({ ...address, country: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={2} required data-testid="address-country" />
                  </Field>
                  <Field label={t("partners.region")}>
                    <TextField value={address.region} onChange={(e) => { setAddress({ ...address, region: e.target.value }); }} />
                  </Field>
                  <label className="flex items-center gap-2 self-end text-sm">
                    <input type="checkbox" checked={address.isDefault} onChange={(e) => { setAddress({ ...address, isDefault: e.target.checked }); }} />
                    {t("partners.default")}
                  </label>
                  <div className="flex items-end">
                    <Button type="submit" variant="secondary" loading={addAddress.isPending} data-testid="add-address">
                      {t("partners.addAddress")}
                    </Button>
                  </div>
                </form>
              </div>
            ) : null}
            {tab === "bank" ? (
              <div className="flex flex-col gap-4">
                <p className="text-sm text-fg-muted">{t("partners.masked")}</p>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.bankName")}</TableHead>
                      <TableHead>{t("partners.currency")}</TableHead>
                      <TableHead>{t("partners.iban")}</TableHead>
                      <TableHead>{t("partners.accountNumber")}</TableHead>
                      <TableHead>{t("partners.default")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {detail.data.bankAccounts.map((b) => (
                      <TableRow key={b.id} data-testid="bank-row">
                        <TableCell dir="auto">{b.bankName}{b.branch ? ` · ${b.branch}` : ""}</TableCell>
                        <TableCell>{b.currency}</TableCell>
                        <TableCell dir="ltr" className="font-mono">{revealed[b.id]?.iban ?? b.ibanMasked ?? ""}</TableCell>
                        <TableCell dir="ltr" className="font-mono">{revealed[b.id]?.accountNumber ?? b.accountNumberMasked ?? ""}</TableCell>
                        <TableCell>{b.isDefault ? "✓" : ""}</TableCell>
                        <TableCell>
                          {canReveal && !revealed[b.id] ? (
                            <Button type="button" variant="ghost" size="sm" onClick={() => { reveal.mutate(b.id); }} loading={reveal.isPending} data-testid="reveal-bank">
                              {t("partners.reveal")}
                            </Button>
                          ) : revealed[b.id] ? (
                            <span className="text-xs text-fg-muted">{t("partners.revealed")}</span>
                          ) : null}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                <form onSubmit={submit(() => { addBank.mutate(); })} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
                  <Field label={t("partners.bankName")} required>
                    <TextField value={bank.bankName} onChange={(e) => { setBank({ ...bank, bankName: e.target.value }); }} required data-testid="bank-name" />
                  </Field>
                  <Field label={t("partners.branch")}>
                    <TextField value={bank.branch} onChange={(e) => { setBank({ ...bank, branch: e.target.value }); }} />
                  </Field>
                  <Field label={t("partners.swiftBic")}>
                    <TextField value={bank.swiftBic} onChange={(e) => { setBank({ ...bank, swiftBic: e.target.value }); }} dir="ltr" />
                  </Field>
                  <Field label={t("partners.currency")} required>
                    <TextField value={bank.currency} onChange={(e) => { setBank({ ...bank, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="bank-currency" />
                  </Field>
                  <Field label={t("partners.accountHolder")}>
                    <TextField value={bank.accountHolder} onChange={(e) => { setBank({ ...bank, accountHolder: e.target.value }); }} />
                  </Field>
                  <Field label={t("partners.iban")}>
                    <TextField value={bank.iban} onChange={(e) => { setBank({ ...bank, iban: e.target.value }); }} dir="ltr" data-testid="bank-iban" />
                  </Field>
                  <Field label={t("partners.accountNumber")}>
                    <TextField value={bank.accountNumber} onChange={(e) => { setBank({ ...bank, accountNumber: e.target.value }); }} dir="ltr" data-testid="bank-account-number" />
                  </Field>
                  <div className="flex items-end">
                    <Button type="submit" variant="secondary" loading={addBank.isPending} data-testid="add-bank">
                      {t("partners.addBankAccount")}
                    </Button>
                  </div>
                </form>
              </div>
            ) : null}
            {tab === "tax" ? (
              <div className="flex flex-col gap-4">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.country")}</TableHead>
                      <TableHead>{t("partners.registrationType")}</TableHead>
                      <TableHead>{t("partners.number")}</TableHead>
                      <TableHead>{t("partners.validFrom")}</TableHead>
                      <TableHead>{t("partners.validTo")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {detail.data.taxRegistrations.map((r) => (
                      <TableRow key={r.id} data-testid="registration-row">
                        <TableCell dir="ltr">{r.country}</TableCell>
                        <TableCell>{t(`partners.registrationTypes.${r.registrationType}`, { defaultValue: r.registrationType })}</TableCell>
                        <TableCell dir="ltr" className="font-mono">{r.number}</TableCell>
                        <TableCell>{formatDate(r.validFrom)}</TableCell>
                        <TableCell>{formatDate(r.validTo)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                <form onSubmit={submit(() => { addRegistration.mutate(); })} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
                  <Field label={t("partners.country")} required>
                    <TextField value={registration.country} onChange={(e) => { setRegistration({ ...registration, country: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={2} required data-testid="registration-country" />
                  </Field>
                  <Field label={t("partners.registrationType")}>
                    <SelectField value={registration.registrationType} onChange={(e) => { setRegistration({ ...registration, registrationType: e.target.value }); }}>
                      {["vat", "tin", "crn", "other"].map((k) => (
                        <option key={k} value={k}>
                          {t(`partners.registrationTypes.${k}`)}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("partners.number")} required>
                    <TextField value={registration.number} onChange={(e) => { setRegistration({ ...registration, number: e.target.value }); }} dir="ltr" required data-testid="registration-number" />
                  </Field>
                  <Field label={t("partners.validFrom")}>
                    <TextField type="date" value={registration.validFrom} onChange={(e) => { setRegistration({ ...registration, validFrom: e.target.value }); }} dir="ltr" />
                  </Field>
                  <div className="flex items-end">
                    <Button type="submit" variant="secondary" loading={addRegistration.isPending} data-testid="add-registration">
                      {t("partners.addRegistration")}
                    </Button>
                  </div>
                </form>
              </div>
            ) : null}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
