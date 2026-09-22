import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Input } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDate, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { rememberRecent } from "../shell/CommandPalette";
import { FormError, PageHeader, SelectField, TextField } from "./common";

type Company = components["schemas"]["CompanySummary"];
type CustomField = components["schemas"]["CustomFieldView"];

interface CompanyForm {
  code: string;
  legalNameEn: string;
  legalNameAr: string;
  country: string;
  functionalCurrency: string;
  reportingCurrency: string;
  timeZone: string;
  isActive: boolean;
  customFields: Record<string, unknown>;
  /** Policies the form does not expose yet (company settings screen, M2); kept from the record so an edit never resets them. */
  policies: Policies;
}

interface Policies {
  defaultLanguage: string;
  costingMethod: string;
  costingScope: string;
  revenueRecognitionPoint: string;
  taxRoundingMode: string;
  roundingMode: string;
  negativeStockPolicy: string;
  bankRevaluationMode: string;
  fiscalCalendarId: string | null;
  businessCalendarId: string | null;
  tradeName: Record<string, string> | null;
  registrationNumbers: Record<string, string> | null;
  address: Record<string, string> | null;
}

const defaultPolicies: Policies = { defaultLanguage: "en", costingMethod: "average", costingScope: "company", revenueRecognitionPoint: "invoice", taxRoundingMode: "line", roundingMode: "half_away", negativeStockPolicy: "block", bankRevaluationMode: "permanent", fiscalCalendarId: null, businessCalendarId: null, tradeName: null, registrationNumbers: null, address: null };

const empty: CompanyForm = { code: "", legalNameEn: "", legalNameAr: "", country: "IQ", functionalCurrency: "IQD", reportingCurrency: "", timeZone: "Asia/Baghdad", isActive: true, customFields: {}, policies: defaultPolicies };

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function toForm(company: Company): CompanyForm {
  return {
    code: company.code,
    legalNameEn: company.legalName.en ?? "",
    legalNameAr: company.legalName.ar ?? "",
    country: company.country,
    functionalCurrency: company.functionalCurrency,
    reportingCurrency: company.reportingCurrency ?? "",
    timeZone: company.timeZone,
    isActive: company.isActive,
    customFields: asRecord(company.customFields),
    policies: {
      defaultLanguage: company.defaultLanguage,
      costingMethod: company.costingMethod,
      costingScope: company.costingScope,
      revenueRecognitionPoint: company.revenueRecognitionPoint,
      taxRoundingMode: company.taxRoundingMode,
      roundingMode: company.roundingMode,
      negativeStockPolicy: company.negativeStockPolicy,
      bankRevaluationMode: company.bankRevaluationMode,
      fiscalCalendarId: company.fiscalCalendarId,
      businessCalendarId: company.businessCalendarId,
      tradeName: company.tradeName,
      registrationNumbers: company.registrationNumbers,
      address: company.address,
    },
  };
}

/** Renders one custom-field control from its definition; values are validated by the API (custom_field.* problems map back here). */
function CustomFieldControl({ definition, value, onChange }: { definition: CustomField; value: unknown; onChange: (next: unknown) => void }) {
  const control = { name: `cf-${definition.key}` };
  switch (definition.type) {
    case "boolean":
      return (
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={Boolean(value)} onChange={(e) => { onChange(e.target.checked); }} {...control} />
          {localized(definition.label)}
        </label>
      );
    case "select":
      return (
        <SelectField value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} {...control}>
          <option value="">—</option>
          {definition.options.map((option) => (
            <option key={option.value} value={option.value}>
              {localized(option.label)}
            </option>
          ))}
        </SelectField>
      );
    case "number":
      return <TextField type="number" inputMode="decimal" value={typeof value === "number" || typeof value === "string" ? String(value) : ""} onChange={(e) => { onChange(e.target.value === "" ? undefined : Number(e.target.value)); }} {...control} dir="ltr" />;
    case "date":
      return <TextField type="date" value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} {...control} dir="ltr" />;
    default:
      return <TextField value={typeof value === "string" ? value : ""} onChange={(e) => { onChange(e.target.value || undefined); }} {...control} />;
  }
}

export function CompaniesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const [query, setQuery] = useState(search.q ?? "");
  const [editing, setEditing] = useState<{ id: string | null; form: CompanyForm; version?: string | undefined } | null>(null);

  /** Opens a company for editing with its current version (the ETag of GET), so the save can send If-Match. */
  const openCompany = async (company: Company): Promise<void> => {
    setProblem(null);
    const result = await api.GET("/api/v1/organization/companies/{companyId}", { params: { path: { companyId: company.id } } });
    setEditing({ id: company.id, form: toForm(result.data ?? company), version: result.response.headers.get("etag") ?? undefined });
  };
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const filter = query.trim() ? `code like '${query.trim().replace(/'/g, "''")}'` : undefined;
  const companies = useQuery({
    queryKey: ["companies", filter ?? ""],
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies", { params: { query: filter ? { filter } : {} } })),
  });
  const currencies = useQuery({ queryKey: ["currencies"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/currencies")) });
  const fields = useQuery({ queryKey: ["custom-fields", "company"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields", { params: { query: { entityType: "company" } } })) });

  useEffect(() => {
    if (search.new) {
      setEditing({ id: null, form: empty });
    } else if (search.open && companies.data) {
      const company = companies.data.find((c) => c.id === search.open);
      if (company) {
        void openCompany(company);
      }
    }
  }, [search.new, search.open, companies.data]);

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: CompanyForm; version?: string | undefined }) => {
      const body = {
        ...input.form.policies,
        code: input.form.code,
        legalName: { en: input.form.legalNameEn, ...(input.form.legalNameAr ? { ar: input.form.legalNameAr } : {}) },
        country: input.form.country,
        functionalCurrency: input.form.functionalCurrency,
        reportingCurrency: input.form.reportingCurrency || null,
        timeZone: input.form.timeZone,
        isActive: input.form.isActive,
        customFields: input.form.customFields,
      };
      if (input.id) {
        // The version ETag from GET goes back as If-Match: a concurrent change answers 412 instead of being overwritten.
        return unwrap(await api.PUT("/api/v1/organization/companies/{companyId}", { params: { path: { companyId: input.id } }, body, headers: input.version ? { "If-Match": input.version } : {} }));
      }
      return unwrap(await api.POST("/api/v1/organization/companies", { body }));
    },
    onSuccess: async (company) => {
      setEditing(null);
      setProblem(null);
      rememberRecent({ to: `/companies?open=${company.id}`, label: `${company.code} · ${localized(company.legalName)}` });
      await queryClient.invalidateQueries({ queryKey: ["companies"] });
      void navigate({ to: "/companies", search: {} });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Company, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("companies.code"), size: 120 },
      { id: "legalName", accessorFn: (row) => localized(row.legalName), header: t("companies.legalName"), size: 260 },
      { id: "country", accessorKey: "country", header: t("companies.country"), size: 90 },
      { id: "functionalCurrency", accessorKey: "functionalCurrency", header: t("companies.functionalCurrency"), size: 110 },
      { id: "timeZone", accessorKey: "timeZone", header: t("companies.timeZone"), size: 160 },
      { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 110, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
      { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 140, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ],
    [t],
  );

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };

  const form = editing?.form;
  const isEdit = editing?.id != null;
  const setForm = (patch: Partial<CompanyForm>): void => {
    setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev));
  };

  return (
    <>
      <PageHeader
        title={t("nav.companies")}
        description={t("companies.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ id: null, form: empty }); }} data-testid="new-company">
            <Plus aria-hidden="true" />
            {t("companies.new")}
          </Button>
        }
      />
      <DataGrid<Company>
        label="nav.companies"
        columns={columns}
        data={companies.data ?? []}
        rowKey={(row) => row.id}
        entityType="company"
        loading={companies.isPending}
        onOpen={(company) => { void openCompany(company); }}
        emptyTitle={t("companies.emptyTitle")}
        emptyDescription={t("companies.emptyDescription")}
        emptyAction={<Button onClick={() => { setEditing({ id: null, form: empty }); }}>{t("companies.new")}</Button>}
        toolbar={<Input type="search" placeholder={t("companies.searchPlaceholder")} value={query} onChange={(e) => { setQuery(e.target.value); }} className="w-56" aria-label={t("common.search")} />}
      />

      <Dialog open={editing !== null} onOpenChange={(open) => { if (!open) { setEditing(null); void navigate({ to: "/companies", search: {} }); } }}>
        <DialogContent closeLabel={t("common.close")} className="sm:max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{isEdit ? t("companies.edit") : t("companies.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("companies.code")} required error={problem?.fields.code}>
                  <TextField value={form.code} onChange={(e) => { setForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" name="code" />
                </Field>
                <Field label={t("companies.country")} required error={problem?.fields.country} description={t("companies.countryHint")}>
                  <TextField value={form.country} onChange={(e) => { setForm({ country: e.target.value.toUpperCase() }); }} required maxLength={2} dir="ltr" name="country" />
                </Field>
                <Field label={t("companies.legalNameEn")} required error={problem?.fields.legalName}>
                  <TextField value={form.legalNameEn} onChange={(e) => { setForm({ legalNameEn: e.target.value }); }} required name="legalNameEn" />
                </Field>
                <Field label={t("companies.legalNameAr")}>
                  <TextField value={form.legalNameAr} onChange={(e) => { setForm({ legalNameAr: e.target.value }); }} name="legalNameAr" dir="rtl" />
                </Field>
                <Field label={t("companies.functionalCurrency")} required error={problem?.fields.functionalCurrency ?? problem?.fields.currency} description={isEdit ? t("companies.currencyLocked") : undefined}>
                  <SelectField value={form.functionalCurrency} onChange={(e) => { setForm({ functionalCurrency: e.target.value }); }} disabled={isEdit} name="functionalCurrency">
                    {(currencies.data ?? []).filter((c) => c.isActive).map((currency) => (
                      <option key={currency.code} value={currency.code}>
                        {currency.code} — {localized(currency.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("companies.reportingCurrency")} error={problem?.fields.reportingCurrency}>
                  <SelectField value={form.reportingCurrency} onChange={(e) => { setForm({ reportingCurrency: e.target.value }); }} name="reportingCurrency">
                    <option value="">—</option>
                    {(currencies.data ?? []).filter((c) => c.isActive).map((currency) => (
                      <option key={currency.code} value={currency.code}>
                        {currency.code}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("companies.timeZone")} required error={problem?.fields.timeZone}>
                  <TextField value={form.timeZone} onChange={(e) => { setForm({ timeZone: e.target.value }); }} required dir="ltr" name="timeZone" />
                </Field>
                <label className="flex items-center gap-2 self-end pb-2 text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ isActive: e.target.checked }); }} name="isActive" />
                  {t("common.active")}
                </label>
              </div>
              {(fields.data ?? []).filter((f) => f.active).length > 0 ? (
                <fieldset className="grid gap-4 rounded-md border border-border p-4 sm:grid-cols-2">
                  <legend className="px-1 text-sm font-medium">{t("customFields.title")}</legend>
                  {(fields.data ?? []).filter((f) => f.active).map((definition) => (
                    <Field key={definition.id} label={localized(definition.label)} required={definition.required} error={problem?.fields[`customFields.${definition.key}`]} description={localized(definition.description) || undefined}>
                      <CustomFieldControl definition={definition} value={form.customFields[definition.key]} onChange={(next) => { setForm({ customFields: { ...form.customFields, [definition.key]: next } }); }} />
                    </Field>
                  ))}
                </fieldset>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-company">
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
