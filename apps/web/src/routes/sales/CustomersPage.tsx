import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CustomFieldsFieldset, type CustomFieldValues } from "../CustomFieldsFieldset";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { CustomerAccountFields, customerAccountBody, emptyCustomerAccount, type CustomerAccountForm } from "./CustomerAccountFields";
import { CreditBadge, money, useSalesReps, type CustomerAccount } from "./shared";

interface PartnerForm {
  code: string;
  legalName: string;
  legalNameAr: string;
  kind: string;
  email: string;
  phone: string;
  website: string;
  customFields: CustomFieldValues;
}

const emptyPartner = (): PartnerForm => ({ code: "", legalName: "", legalNameAr: "", kind: "organization", email: "", phone: "", website: "", customFields: {} });

/** Customers of the company (roadmap 5.1): their accounts with terms, rep and credit; a row opens the customer's 360. */
export function CustomersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const [q, setQ] = useState("");
  const [credit, setCredit] = useState("");
  const [repId, setRepId] = useState("");
  const [mine, setMine] = useState(false);
  const [creating, setCreating] = useState<{ partner: PartnerForm; account: CustomerAccountForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const reps = useSalesReps();
  const canManage = can("partners.customer.manage");
  const canCredit = can("partners.credit.manage");

  const customers = useQuery({
    queryKey: ["customers", companyId, q, credit, repId, mine],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/customers", { params: { query: { companyId, ...(q ? { q } : {}), ...(credit ? { creditStatus: credit } : {}), ...(repId ? { salesRepId: repId } : {}), ...(mine ? { mine: true } : {}) } } })),
  });

  const create = useMutation({
    mutationFn: async (input: { partner: PartnerForm; account: CustomerAccountForm }) => {
      const p = input.partner;
      const partner = unwrap(await api.POST("/api/v1/partners", { body: { code: p.code, legalName: { en: p.legalName, ar: p.legalNameAr || p.legalName }, tradeName: null, kind: p.kind, isSupplier: false, isCustomer: true, isEmployee: false, defaultLanguage: "en", isActive: true, email: p.email || null, phone: p.phone || null, website: p.website || null, notes: null, customFields: p.customFields } }));
      unwrap(await api.PUT("/api/v1/partners/{partnerId}/customer-accounts/{companyId}", { params: { path: { partnerId: partner.id, companyId } }, body: customerAccountBody(input.account) }));
      return partner;
    },
    onSuccess: async (partner) => {
      setCreating(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["customers"] });
      void navigate({ to: "/sales/customers/$partnerId", params: { partnerId: partner.id } });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<CustomerAccount, unknown>[]>(
    () => [
      { id: "code", accessorKey: "partnerCode", header: t("partners.code"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.partnerCode}</span> },
      { id: "name", accessorFn: (row) => localized(row.partnerName), header: t("partners.legalName"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.partnerName)}</span> },
      { id: "group", accessorFn: (row) => row.customerGroupCode ?? "", header: t("sales.customerGroup"), size: 120 },
      { id: "rep", accessorFn: (row) => row.salesRepCode ?? "", header: t("sales.salesRep"), size: 110 },
      { id: "terms", accessorFn: (row) => row.effective.paymentTermsCode ?? "", header: t("partners.paymentTerms"), size: 110 },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
      { id: "limit", accessorFn: (row) => (row.creditLimit === null ? "" : String(row.creditLimit)), header: t("sales.creditLimitShort"), size: 170, meta: { exportType: "number" as const }, cell: ({ row }) => (row.original.creditLimit === null ? <span className="text-fg-muted">{t("sales.noLimit")}</span> : <span className="tabular" dir="ltr">{money(row.original.creditLimit, row.original.functionalCurrency)}</span>) },
      { id: "credit", accessorKey: "creditStatus", header: t("sales.creditStatus"), size: 160, cell: ({ row }) => <CreditBadge status={row.original.creditStatus} /> },
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
        title={t("nav.customers")}
        description={t("sales.customersDescription")}
        actions={
          canManage ? (
            <Button onClick={() => { setProblem(null); setCreating({ partner: emptyPartner(), account: emptyCustomerAccount(company?.functionalCurrency ?? "") }); }} disabled={!companyId} data-testid="new-customer">
              <Plus aria-hidden="true" />
              {t("sales.newCustomer")}
            </Button>
          ) : null
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-5">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.search")}>
          <TextField value={q} onChange={(e) => { setQ(e.target.value); }} placeholder={t("partners.search")} data-testid="customer-search" />
        </Field>
        <Field label={t("sales.creditStatus")}>
          <SelectField value={credit} onChange={(e) => { setCredit(e.target.value); }} data-testid="credit-filter">
            {["", "held", "ok", "on_hold", "blocked"].map((c) => (
              <option key={c} value={c}>
                {t(`sales.creditFilter.${c || "all"}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.salesRep")}>
          <SelectField value={repId} onChange={(e) => { setRepId(e.target.value); }}>
            <option value="">{t("sales.allReps")}</option>
            {(reps.data ?? []).map((r) => (
              <option key={r.id} value={r.id}>
                {r.code} · {localized(r.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm">
          <input type="checkbox" checked={mine} onChange={(e) => { setMine(e.target.checked); }} data-testid="mine-filter" />
          {t("sales.myCustomers")}
        </label>
      </div>
      <DataGrid<CustomerAccount>
        label="nav.customers"
        columns={columns}
        data={customers.data ?? []}
        rowKey={(row) => row.id}
        loading={customers.isPending && Boolean(companyId)}
        emptyTitle={t("sales.emptyCustomers")}
        emptyDescription={t("sales.emptyCustomersDescription")}
        onOpen={(row) => { void navigate({ to: "/sales/customers/$partnerId", params: { partnerId: row.partnerId } }); }}
      />

      <Dialog open={Boolean(creating)} onOpenChange={(isOpen) => { if (!isOpen) { setCreating(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {creating ? (
            <form onSubmit={submitCreate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("sales.newCustomer")}</DialogTitle>
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
                <Field label={t("partners.email")}>
                  <TextField type="email" value={creating.partner.email} onChange={(e) => { setPartner({ email: e.target.value }); }} dir="ltr" data-testid="partner-email" />
                </Field>
                <Field label={t("partners.phone")}>
                  <TextField value={creating.partner.phone} onChange={(e) => { setPartner({ phone: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <CustomerAccountFields
                form={creating.account}
                onChange={(patch) => { setCreating((prev) => (prev ? { ...prev, account: { ...prev.account, ...patch } } : prev)); }}
                companyId={companyId}
                functionalCurrency={company?.functionalCurrency ?? ""}
                canManage
                canCredit={canCredit}
              />
              <CustomFieldsFieldset entityType="partner" values={creating.partner.customFields} onChange={(customFields) => { setPartner({ customFields }); }} errors={problem?.fields} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCreating(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={create.isPending} data-testid="save-customer">
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
