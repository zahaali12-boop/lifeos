import { Badge, Button, EmptyState, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { ArrowLeft, CircleDollarSign, Plus, Target, Timer, Wallet } from "lucide-react";
import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { useCompanies } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CustomFieldValuesList } from "../CustomFieldsFieldset";
import { Tabs } from "../inventory/shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";
import { ActivityForm, ActivityList } from "./Activities";
import { CustomerAccountFields, customerAccountBody, customerAccountForm, emptyCustomerAccount, type CustomerAccountForm } from "./CustomerAccountFields";
import { NewOpportunityDialog, OpportunityDialog, useRefreshSales } from "./Opportunities";
import { CreditBadge, Money, OutcomeBadge, Totals, type Customer360, type CustomerAccount } from "./shared";

/**
 * Customer 360 (roadmap 5.1): who the customer is, what it is worth to each company, where its deals stand, what was
 * done and is due, and what every module holds with it, on one page. Each part shows what the member may read.
 */
export function Customer360Page() {
  const { t } = useTranslation();
  const { partnerId } = useParams({ strict: false });
  const id = partnerId ?? "";
  const can = useCan();
  const canManage = can("partners.customer.manage");
  const refreshSales = useRefreshSales();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState("overview");
  const [creatingDeal, setCreatingDeal] = useState(false);
  const [openDeal, setOpenDeal] = useState<string | null>(null);
  const view = useQuery({ queryKey: ["customer-360", id], enabled: Boolean(id), queryFn: async () => unwrap(await api.GET("/api/v1/partners/{partnerId}/customer-360", { params: { path: { partnerId: id } } })) });
  const refresh = async (): Promise<void> => {
    await refreshSales();
    await queryClient.invalidateQueries({ queryKey: ["partner", id] });
    await queryClient.invalidateQueries({ queryKey: ["customers"] });
  };

  if (view.isError) {
    return <EmptyState title={t("sales.customerNotFound")} description={t("sales.customerNotFoundDescription")} action={<Link to="/sales/customers" className="text-accent hover:underline">{t("nav.customers")}</Link>} />;
  }
  const data = view.data;
  if (!data) {
    return <p className="text-sm text-fg-muted">{t("common.loading")}</p>;
  }
  const partner = data.partner.partner;
  const overdue = data.openActivities.filter((a) => a.isOverdue).length;
  const defaultCompany = data.partner.customerAccounts[0]?.companyId ?? "";

  return (
    <div className="flex flex-col gap-4" data-testid="customer-360">
      <Link to="/sales/customers" className="flex items-center gap-1 text-sm text-accent hover:underline">
        <ArrowLeft className="size-4 rtl:rotate-180" aria-hidden="true" />
        {t("nav.customers")}
      </Link>
      <PageHeader
        title={
          <span dir="auto">
            <span dir="ltr">{partner.code}</span> · {localized(partner.legalName)}
          </span>
        }
        description={
          <span className="flex flex-wrap items-center gap-2">
            {partner.isCustomer ? <Badge tone="accent">{t("sales.roles.customer")}</Badge> : <Badge tone="info" data-testid="prospect-badge">{t("sales.roles.prospect")}</Badge>}
            {partner.isSupplier ? <Badge tone="neutral">{t("sales.roles.supplier")}</Badge> : null}
            {!partner.isActive ? <Badge tone="neutral">{t("common.inactive")}</Badge> : null}
            {data.partner.customerAccounts.map((a) => (a.creditStatus !== "ok" ? <CreditBadge key={a.id} status={a.creditStatus} /> : null))}
            {partner.email ? <a href={`mailto:${partner.email}`} className="text-accent hover:underline" dir="ltr">{partner.email}</a> : null}
            {partner.phone ? <span dir="ltr">{partner.phone}</span> : null}
            {partner.website ? <span dir="ltr">{partner.website}</span> : null}
          </span>
        }
        actions={
          canManage ? (
            <>
              <Button variant="secondary" onClick={() => { setTab("activities"); }} data-testid="log-activity">
                {t("sales.logActivity")}
              </Button>
              <Button onClick={() => { setCreatingDeal(true); }} data-testid="new-opportunity">
                <Plus aria-hidden="true" />
                {t("sales.newOpportunity")}
              </Button>
            </>
          ) : null
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" data-testid="kpis">
        <Kpi icon={<Target aria-hidden="true" />} label={t("sales.openPipelineWeighted")} testId="kpi-pipeline">
          <Totals totals={data.pipeline.openTotals} />
        </Kpi>
        <Kpi icon={<CircleDollarSign aria-hidden="true" />} label={t("sales.winRate")} testId="kpi-win-rate">
          <span className="text-lg font-semibold tabular">{data.pipeline.winRatePct === null ? "—" : `${String(data.pipeline.winRatePct)}%`}</span>
          <span className="text-xs text-fg-muted">{t("sales.wonLost", { won: data.pipeline.wonLastYear, lost: data.pipeline.lostLastYear })}</span>
        </Kpi>
        <Kpi icon={<Timer aria-hidden="true" />} label={t("sales.nextSteps")} testId="kpi-activities">
          <span className="text-lg font-semibold tabular">{data.openActivities.length}</span>
          {overdue > 0 ? <span className="text-xs font-medium text-danger">{t("sales.overdueCount", { count: overdue })}</span> : <span className="text-xs text-fg-muted">{t("sales.nothingLate")}</span>}
        </Kpi>
        <Kpi icon={<Wallet aria-hidden="true" />} label={t("sales.balances")} testId="kpi-balances">
          {data.panels.flatMap((p) => p.balances).length === 0 ? (
            <span className="text-sm text-fg-muted">{t("sales.nothingOpen")}</span>
          ) : (
            data.panels.flatMap((p) => p.balances).map((b) => (
              <span key={`${b.source}-${b.companyId}-${b.currency}`} className="text-sm">
                {t(`sales.balanceSides.${b.side}`)} <Money amount={b.open} currency={b.currency} />
              </span>
            ))
          )}
        </Kpi>
      </div>

      <Tabs
        tabs={[
          { id: "overview", label: t("sales.overview"), testId: "tab-overview" },
          { id: "accounts", label: t("sales.accounts"), testId: "tab-accounts" },
          { id: "contacts", label: t("sales.contactsAndAddresses"), testId: "tab-contacts" },
          { id: "opportunities", label: t("sales.opportunities"), testId: "tab-opportunities" },
          { id: "activities", label: t("sales.activities"), testId: "tab-activities" },
          { id: "transactions", label: t("sales.transactions"), testId: "tab-transactions" },
          { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" },
          { id: "history", label: t("history.tab"), testId: "tab-history" },
        ]}
        value={tab}
        onChange={setTab}
      />

      {tab === "overview" ? (
        <div className="grid gap-4 lg:grid-cols-2">
          <Section title={t("sales.nextSteps")}>
            <ActivityList activities={data.openActivities.slice(0, 5)} onChanged={refresh} testId="overview-activities" />
          </Section>
          <Section title={t("sales.openDeals")}>
            <DealTable deals={data.pipeline.open} onOpen={setOpenDeal} empty={t("sales.noOpenDeals")} />
          </Section>
          <div className="lg:col-span-2">
            <Section title={t("sales.accounts")}>
              <AccountSummaryTable accounts={data.partner.customerAccounts} />
            </Section>
          </div>
          <Section title={t("sales.recentActivity")}>
            <ActivityList activities={data.recentActivities.slice(0, 5)} onChanged={refresh} testId="overview-recent" />
          </Section>
          <CustomFieldValuesList entityType="partner" values={partner.customFields} />
        </div>
      ) : null}
      {tab === "accounts" ? <AccountsTab data={data} onChanged={refresh} /> : null}
      {tab === "contacts" ? <ContactsTab data={data} onChanged={refresh} canManage={canManage} /> : null}
      {tab === "opportunities" ? (
        <div className="flex flex-col gap-4">
          <Section title={t("sales.openDeals")}>
            <DealTable deals={data.pipeline.open} onOpen={setOpenDeal} empty={t("sales.noOpenDeals")} />
          </Section>
          <Section title={t("sales.closedLastYear")}>
            <DealTable deals={data.pipeline.recentlyClosed} onOpen={setOpenDeal} empty={t("sales.noClosedDeals")} />
          </Section>
        </div>
      ) : null}
      {tab === "activities" ? (
        <div className="flex flex-col gap-4">
          {canManage ? <ActivityForm partnerId={partner.id} onSaved={refresh} /> : null}
          <Section title={t("sales.nextSteps")}>
            <ActivityList activities={data.openActivities} onChanged={refresh} testId="open-activities" />
          </Section>
          <Section title={t("sales.recentActivity")}>
            <ActivityList activities={data.recentActivities} onChanged={refresh} testId="recent-activities" />
          </Section>
        </div>
      ) : null}
      {tab === "transactions" ? <TransactionsTab data={data} /> : null}
      {tab === "discussion" ? <RecordDiscussion entityType="partner" entityId={partner.id} /> : null}
      {tab === "history" ? <RecordHistory entityType="partner" entityId={partner.id} /> : null}

      {openDeal ? <OpportunityDialog opportunityId={openDeal} onClose={() => { setOpenDeal(null); }} /> : null}
      {creatingDeal ? (
        <NewOpportunityDialog
          open
          companyId={defaultCompany}
          partner={{ id: partner.id, code: partner.code, name: partner.legalName }}
          onClose={() => { setCreatingDeal(false); }}
          onCreated={(o) => { setCreatingDeal(false); void refresh(); setOpenDeal(o.id); }}
        />
      ) : null}
    </div>
  );
}

function Kpi({ icon, label, children, testId }: { icon: ReactNode; label: string; children: ReactNode; testId: string }) {
  return (
    <div className="flex flex-col gap-1 rounded-lg border border-border bg-surface p-3" data-testid={testId}>
      <span className="flex items-center gap-2 text-xs font-medium text-fg-muted [&_svg]:size-4">
        {icon}
        {label}
      </span>
      {children}
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-semibold">{title}</h2>
      {children}
    </section>
  );
}

function DealTable({ deals, onOpen, empty }: { deals: Customer360["pipeline"]["open"]; onOpen: (id: string) => void; empty: string }) {
  const { t } = useTranslation();
  if (deals.length === 0) {
    return <p className="text-sm text-fg-muted">{empty}</p>;
  }
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("sales.number")}</TableHead>
          <TableHead>{t("sales.title")}</TableHead>
          <TableHead>{t("sales.stage")}</TableHead>
          <TableHead>{t("sales.expectedAmount")}</TableHead>
          <TableHead>{t("sales.expectedClose")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {deals.map((o) => (
          <TableRow key={o.id} data-testid="deal-row">
            <TableCell dir="ltr">
              <button type="button" className="text-accent hover:underline" onClick={() => { onOpen(o.id); }}>
                {o.number}
              </button>
            </TableCell>
            <TableCell dir="auto">{o.title}</TableCell>
            <TableCell>
              {o.status === "open" ? <Badge tone="accent">{localized(o.stageName)}</Badge> : <OutcomeBadge status={o.status} />}
            </TableCell>
            <TableCell>
              <Money amount={o.expectedAmount} currency={o.currency} />
            </TableCell>
            <TableCell className={o.isOverdue ? "text-danger" : undefined}>{o.closedOn ? formatDate(o.closedOn) : o.expectedClose ? formatDate(o.expectedClose) : "—"}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function AccountSummaryTable({ accounts }: { accounts: CustomerAccount[] }) {
  const { t } = useTranslation();
  if (accounts.length === 0) {
    return <p className="text-sm text-fg-muted">{t("sales.noAccounts")}</p>;
  }
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("accounting.company")}</TableHead>
          <TableHead>{t("sales.customerGroup")}</TableHead>
          <TableHead>{t("sales.salesRep")}</TableHead>
          <TableHead>{t("partners.paymentTerms")}</TableHead>
          <TableHead>{t("sales.creditLimitShort")}</TableHead>
          <TableHead>{t("sales.creditStatus")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {accounts.map((a) => (
          <TableRow key={a.id} data-testid="account-row">
            <TableCell>{a.companyCode}</TableCell>
            <TableCell>{a.customerGroupCode ?? "—"}</TableCell>
            <TableCell>{a.salesRepCode ?? "—"}</TableCell>
            <TableCell>{a.effective.paymentTermsCode ?? "—"}</TableCell>
            <TableCell>{a.creditLimit === null ? t("sales.noLimit") : <Money amount={a.creditLimit} currency={a.functionalCurrency} />}</TableCell>
            <TableCell>
              <CreditBadge status={a.creditStatus} />
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** One account per company: edit it, hold, block or release its credit, or open one in a company it has none in. */
function AccountsTab({ data, onChanged }: { data: Customer360; onChanged: () => Promise<void> }) {
  const { t, i18n } = useTranslation();
  const can = useCan();
  const canManage = can("partners.customer.manage");
  const canCredit = can("partners.credit.manage");
  const companies = useCompanies();
  const accounts = data.partner.customerAccounts;
  const [companyId, setCompanyId] = useState(accounts[0]?.companyId ?? "");
  const [edits, setEdits] = useState<Record<string, CustomerAccountForm>>({});
  const [status, setStatus] = useState({ status: "on_hold", reason: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const partnerId = data.partner.partner.id;
  const chosen = companyId || (companies.data?.[0]?.id ?? "");
  const company = companies.data?.find((c) => c.id === chosen);
  const current = accounts.find((a) => a.companyId === chosen);
  const form = edits[chosen] ?? (current ? customerAccountForm(current) : emptyCustomerAccount(company?.functionalCurrency ?? ""));
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/v1/partners/{partnerId}/customer-accounts/{companyId}", { params: { path: { partnerId, companyId: chosen } }, body: customerAccountBody(form) })),
    onSuccess: async () => { setProblem(null); setEdits((prev) => Object.fromEntries(Object.entries(prev).filter(([key]) => key !== chosen))); await onChanged(); },
    onError: fail,
  });
  const setCredit = useMutation({
    mutationFn: async (next: { status: string; reason: string }) => unwrap(await api.POST("/api/v1/partners/{partnerId}/customer-accounts/{companyId}/credit-status", { params: { path: { partnerId, companyId: chosen } }, body: { status: next.status, reason: next.reason || null } })),
    onSuccess: async () => { setProblem(null); setStatus({ status: "on_hold", reason: "" }); await onChanged(); },
    onError: fail,
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("accounting.company")}>
          <SelectField value={chosen} onChange={(e) => { setCompanyId(e.target.value); setProblem(null); }} data-testid="account-company">
            {(companies.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.code} · {localized(c.legalName)}
                {accounts.some((a) => a.companyId === c.id) ? "" : ` (${t("sales.noAccountYet")})`}
              </option>
            ))}
          </SelectField>
        </Field>
        {current ? <CreditBadge status={current.creditStatus} /> : null}
        {current?.creditStatusReason ? <span className="text-sm text-warning" dir="auto">{t("sales.creditSince", { when: formatDateTime(current.creditStatusAt), reason: current.creditStatusReason })}</span> : null}
      </div>
      <FormError message={problem?.message ?? null} />
      <form onSubmit={submit} className="flex flex-col gap-4">
        {current ? (
          <p className="text-sm text-fg-muted" data-testid="effective-terms">
            {t("partners.effective")}: {t("partners.paymentTerms")} {current.effective.paymentTermsCode ?? "—"} · {t("partners.deliveryTerms")} {current.effective.deliveryTermsCode ?? "—"}
          </p>
        ) : (
          <p className="text-sm text-fg-muted">{t("sales.openAccountHint", { company: company?.code ?? "" })}</p>
        )}
        <CustomerAccountFields form={form} onChange={(patch) => { setEdits((prev) => ({ ...prev, [chosen]: { ...form, ...patch } })); }} companyId={chosen} functionalCurrency={company?.functionalCurrency ?? ""} canManage={canManage} canCredit={canCredit} />
        <div className="flex flex-wrap items-center justify-end gap-2">
          {(canManage || (current && canCredit)) ? (
            <Button type="submit" loading={save.isPending} data-testid="save-customer-account">
              {current ? t("partners.saveAccount") : t("sales.openAccount")}
            </Button>
          ) : null}
        </div>
      </form>
      {current && canCredit ? (
        <form
          className="flex flex-wrap items-end gap-2 rounded-md border border-border p-3"
          onSubmit={(event) => { event.preventDefault(); setCredit.mutate(status); }}
          data-testid="credit-control"
        >
          <h3 className="w-full text-sm font-semibold">{t("sales.creditControl")}</h3>
          {current.creditStatus === "ok" ? (
            <>
              <Field label={t("sales.creditStatus")}>
                <SelectField value={status.status} onChange={(e) => { setStatus({ ...status, status: e.target.value }); }} data-testid="credit-status">
                  {["on_hold", "blocked"].map((s) => (
                    <option key={s} value={s}>
                      {t(`sales.creditStatuses.${s}`)}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("common.reason")} required className="min-w-64 flex-1">
                <TextField value={status.reason} onChange={(e) => { setStatus({ ...status, reason: e.target.value }); }} required lang={i18n.language} data-testid="credit-reason" />
              </Field>
              <Button type="submit" variant="danger" loading={setCredit.isPending} disabled={!status.reason.trim()} data-testid="apply-credit-status">
                {t("sales.applyCreditStatus")}
              </Button>
            </>
          ) : (
            <Button type="button" variant="secondary" loading={setCredit.isPending} onClick={() => { setCredit.mutate({ status: "ok", reason: "" }); }} data-testid="release-credit">
              {t("sales.releaseCredit")}
            </Button>
          )}
        </form>
      ) : null}
    </div>
  );
}

function ContactsTab({ data, onChanged, canManage }: { data: Customer360; onChanged: () => Promise<void>; canManage: boolean }) {
  const { t } = useTranslation();
  const partnerId = data.partner.partner.id;
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const emptyContact = { name: "", nameAr: "", role: "", email: "", phone: "", isPrimary: false, receivesStatements: false };
  const emptyAddress = { role: "billing", country: "", line1: "", line1Ar: "", city: "", cityAr: "", isDefault: true };
  const [contact, setContact] = useState(emptyContact);
  const [address, setAddress] = useState(emptyAddress);
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const addContact = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/contacts", { params: { path: { partnerId } }, body: { name: { en: contact.name, ar: contact.nameAr || contact.name }, role: contact.role || null, email: contact.email || null, phone: contact.phone || null, mobile: null, isPrimary: contact.isPrimary, receivesStatements: contact.receivesStatements, notes: null, isActive: true } })),
    onSuccess: async () => { setProblem(null); setContact(emptyContact); await onChanged(); },
    onError: fail,
  });
  const addAddress = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/partners/{partnerId}/addresses", { params: { path: { partnerId } }, body: { role: address.role, country: address.country, region: null, isDefault: address.isDefault, address: { line1: { en: address.line1, ar: address.line1Ar || address.line1 }, city: { en: address.city, ar: address.cityAr || address.city } } } })),
    onSuccess: async () => { setProblem(null); setAddress(emptyAddress); await onChanged(); },
    onError: fail,
  });
  return (
    <div className="flex flex-col gap-6">
      <FormError message={problem?.message ?? null} />
      <Section title={t("partners.contacts")}>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("partners.name")}</TableHead>
              <TableHead>{t("partners.role")}</TableHead>
              <TableHead>{t("partners.email")}</TableHead>
              <TableHead>{t("partners.phone")}</TableHead>
              <TableHead>{t("partners.primary")}</TableHead>
              <TableHead>{t("partners.receivesStatements")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.partner.contacts.map((c) => (
              <TableRow key={c.id} data-testid="contact-row">
                <TableCell dir="auto">{localized(c.name)}</TableCell>
                <TableCell dir="auto">{c.role ?? ""}</TableCell>
                <TableCell dir="ltr">{c.email ? <a href={`mailto:${c.email}`} className="text-accent hover:underline">{c.email}</a> : ""}</TableCell>
                <TableCell dir="ltr">{c.mobile ?? c.phone ?? ""}</TableCell>
                <TableCell>{c.isPrimary ? "✓" : ""}</TableCell>
                <TableCell>{c.receivesStatements ? "✓" : ""}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        {canManage ? (
          <form onSubmit={(event) => { event.preventDefault(); addContact.mutate(); }} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
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
            <label className="flex items-center gap-2 self-end text-sm">
              <input type="checkbox" checked={contact.isPrimary} onChange={(e) => { setContact({ ...contact, isPrimary: e.target.checked }); }} />
              {t("partners.primary")}
            </label>
            <label className="flex items-center gap-2 self-end text-sm">
              <input type="checkbox" checked={contact.receivesStatements} onChange={(e) => { setContact({ ...contact, receivesStatements: e.target.checked }); }} />
              {t("partners.receivesStatements")}
            </label>
            <div className="flex items-end">
              <Button type="submit" variant="secondary" loading={addContact.isPending} data-testid="add-contact">
                {t("partners.addContact")}
              </Button>
            </div>
          </form>
        ) : null}
      </Section>
      <Section title={t("partners.addresses")}>
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
            {data.partner.addresses.map((a) => {
              const parts = a.address as { line1?: Record<string, string>; city?: Record<string, string> };
              return (
                <TableRow key={a.id} data-testid="address-row">
                  <TableCell>{t(`partners.addressRoles.${a.role}`, { defaultValue: a.role })}</TableCell>
                  <TableCell dir="auto">{localized(parts.line1)}</TableCell>
                  <TableCell dir="auto">{localized(parts.city)}</TableCell>
                  <TableCell dir="ltr">{a.country}</TableCell>
                  <TableCell>{a.isDefault ? "✓" : ""}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
        {canManage ? (
          <form onSubmit={(event) => { event.preventDefault(); addAddress.mutate(); }} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4">
            <Field label={t("partners.addressRole")}>
              <SelectField value={address.role} onChange={(e) => { setAddress({ ...address, role: e.target.value }); }} data-testid="address-role">
                {["billing", "shipping", "legal", "other"].map((r) => (
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
        ) : null}
      </Section>
    </div>
  );
}

/** What each module holds with the partner: balances per company and currency, and its latest documents. */
function TransactionsTab({ data }: { data: Customer360 }) {
  const { t } = useTranslation();
  const companies = useCompanies();
  const code = (id: string): string => companies.data?.find((c) => c.id === id)?.code ?? "";
  if (data.panels.length === 0 || data.panels.every((p) => p.balances.length === 0 && p.documents.length === 0)) {
    return <EmptyState title={t("sales.noTransactions")} description={t("sales.noTransactionsDescription")} data-testid="no-transactions" />;
  }
  return (
    <div className="flex flex-col gap-6">
      {data.panels.map((panel) => (
        <section key={panel.source} className="flex flex-col gap-3" data-testid={`panel-${panel.source}`}>
          <h2 className="text-sm font-semibold">{t(`sales.sources.${panel.source}`, { defaultValue: panel.source })}</h2>
          {panel.balances.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("accounting.company")}</TableHead>
                  <TableHead>{t("sales.side")}</TableHead>
                  <TableHead>{t("sales.open")}</TableHead>
                  <TableHead>{t("sales.overdue")}</TableHead>
                  <TableHead>{t("sales.openItems")}</TableHead>
                  <TableHead>{t("sales.oldestDue")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {panel.balances.map((b) => (
                  <TableRow key={`${b.companyId}-${b.currency}`} data-testid="balance-row">
                    <TableCell>{code(b.companyId)}</TableCell>
                    <TableCell>{t(`sales.balanceSides.${b.side}`)}</TableCell>
                    <TableCell><Money amount={b.open} currency={b.currency} /></TableCell>
                    <TableCell className={Number(b.overdue) !== 0 ? "text-danger" : undefined}><Money amount={b.overdue} currency={b.currency} /></TableCell>
                    <TableCell className="tabular">{b.openItems}</TableCell>
                    <TableCell>{b.oldestDueOn ? formatDate(b.oldestDueOn) : "—"}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : null}
          {panel.documents.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("sales.number")}</TableHead>
                  <TableHead>{t("sales.documentType")}</TableHead>
                  <TableHead>{t("accounting.company")}</TableHead>
                  <TableHead>{t("sales.date")}</TableHead>
                  <TableHead>{t("sales.amount")}</TableHead>
                  <TableHead>{t("sales.open")}</TableHead>
                  <TableHead>{t("common.status")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {panel.documents.map((d) => (
                  <TableRow key={d.documentId} data-testid="document-row">
                    <TableCell dir="ltr">{d.number}</TableCell>
                    <TableCell>{t(`sales.documentTypes.${d.documentType}`, { defaultValue: d.documentType })}</TableCell>
                    <TableCell>{code(d.companyId)}</TableCell>
                    <TableCell>{formatDate(d.date)}</TableCell>
                    <TableCell><Money amount={d.amount} currency={d.currency} /></TableCell>
                    <TableCell><Money amount={d.open} currency={d.currency} /></TableCell>
                    <TableCell>{t(`purchasing.statuses.${d.status}`, { defaultValue: d.status })}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : null}
        </section>
      ))}
    </div>
  );
}
