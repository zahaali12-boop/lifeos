import { Badge, Button, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Check, ChevronDown, ChevronUp } from "lucide-react";
import { useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatMoney, formatNumber, localized } from "../../lib/format";
import { usePref } from "../../lib/prefs";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { FormError, PageHeader, SelectField, TextField } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { ImportBox, type ImportUpload } from "./ImportBox";

type Status = components["schemas"]["SetupStatus"];
type PartnerImport = components["schemas"]["PartnerImportResult"];
type ItemImport = components["schemas"]["ItemImportResult"];
type OpeningImport = components["schemas"]["OpeningBalanceResult"];

export const setupSteps = ["company", "chart", "tax", "bank", "partners", "items", "opening", "ready"] as const;
export type SetupStep = (typeof setupSteps)[number];

const partnerColumns = ["code", "name_en", "name_ar", "kind", "customer", "supplier", "email", "phone", "currency", "payment_terms", "credit_limit", "customer_group", "supplier_group", "is_active"] as const;
const itemColumns = ["code", "name_en", "name_ar", "type", "category", "brand", "base_uom", "sales_uom", "purchase_uom", "uoms", "barcodes", "tracking", "list_price", "list_price_currency", "is_active"] as const;
const openingColumns = ["account", "debit", "credit", "partner", "bank_account", "due_date", "description"] as const;

/** Which steps the status says are done; tax counts when a regime exists or the member said the company is not registered. */
export function doneSteps(status: Status | undefined, bankAccounts: number, taxSkipped: boolean): Set<SetupStep> {
  const done = new Set<SetupStep>();
  if (!status) {
    return done;
  }
  if (Number(status.companies) > 0) {
    done.add("company");
  }
  if (status.hasChart) {
    done.add("chart");
  }
  if (Number(status.taxRegimes) > 0 || taxSkipped) {
    done.add("tax");
  }
  if (bankAccounts > 0) {
    done.add("bank");
  }
  if (Number(status.customers) + Number(status.suppliers) > 0) {
    done.add("partners");
  }
  if (Number(status.items) > 0) {
    done.add("items");
  }
  if (Number(status.openingJournals) > 0) {
    done.add("opening");
  }
  if (done.size === setupSteps.length - 1) {
    done.add("ready");
  }
  return done;
}

/** The imports post the file as it is: CSV text or an Excel workbook's bytes, the server reading either. */
function raw(upload: ImportUpload): { bodySerializer: () => string | ArrayBuffer; headers: Record<string, string> } {
  return { bodySerializer: () => upload.body, headers: { "Content-Type": upload.contentType } };
}

function Step({ id, index, title, description, done, open, onToggle, children }: { id: SetupStep; index: number; title: string; description: string; done: boolean; open: boolean; onToggle: () => void; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <li className="rounded-lg border border-border bg-surface" data-testid={`setup-step-${id}`} data-done={done || undefined}>
      <div className="flex items-center gap-3 p-4">
        <span className={done ? "flex size-8 shrink-0 items-center justify-center rounded-full bg-success text-success-fg" : "flex size-8 shrink-0 items-center justify-center rounded-full bg-surface-sunken text-sm font-semibold text-fg-muted"} aria-hidden="true">
          {done ? <Check className="size-4" /> : index + 1}
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="text-base font-semibold">{title}</h2>
          <p className="text-sm text-fg-muted">{description}</p>
        </div>
        <Badge tone={done ? "success" : "neutral"} data-testid={`setup-status-${id}`}>{done ? t("setup.done") : t("setup.todo")}</Badge>
        <Button type="button" variant="ghost" size="sm" aria-expanded={open} aria-label={open ? t("setup.collapse", { step: title }) : t("setup.expand", { step: title })} onClick={onToggle} data-testid={`setup-toggle-${id}`}>
          {open ? <ChevronUp aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
        </Button>
      </div>
      {open ? <div className="border-t border-border p-4">{children}</div> : null}
    </li>
  );
}

/**
 * The setup wizard (founder programme item 1g, A-173): the eight steps from an empty workspace to the first posted
 * document, each shown done from what already exists, each with its form or import right there: company, chart of
 * accounts, taxes, cash and bank accounts, customers and suppliers, items, opening balances, and the readiness list.
 */
export function SetupPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const [taxSkipped, rememberTaxSkipped] = usePref("setup.taxSkipped");
  const [overrides, setOverrides] = useState<Partial<Record<SetupStep, boolean>>>({});
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const status = useQuery({
    queryKey: ["setup-status", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/onboarding/status", { params: { query: companyId ? { companyId } : {} } })),
  });
  const bankAccounts = useQuery({
    queryKey: ["bank-accounts", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })),
  });
  const chartTemplates = useQuery({ queryKey: ["chart-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/chart-templates")) });
  const taxTemplates = useQuery({ queryKey: ["tax-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/tax/templates")) });

  const refresh = async (): Promise<void> => {
    await Promise.all([["setup-status"], ["companies"], ["bank-accounts"], ["partners"], ["items"], ["journals"], ["tax-templates"], ["tax-regimes"], ["chart"], ["charts"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };

  const bankCount = bankAccounts.data?.length ?? 0;
  const done = useMemo(() => doneSteps(status.data, bankCount, taxSkipped === "1"), [bankCount, status.data, taxSkipped]);
  const firstOpen = setupSteps.find((step) => !done.has(step)) ?? "ready";

  // A step finished here stays open with its result in view (the next one opens beneath it); steps found done on
  // arrival are folded, so a returning member lands on what is left. Worked out in the render that sees the change,
  // so the step never folds for a moment and loses what it was showing.
  const seen = useRef<Set<SetupStep> | null>(null);
  const finished = useRef(new Set<SetupStep>());
  if (status.data && seen.current !== done) {
    if (seen.current !== null) {
      for (const step of setupSteps) {
        if (done.has(step) && !seen.current.has(step)) {
          finished.current.add(step);
        }
      }
    }
    seen.current = done;
  }
  const isOpen = (step: SetupStep): boolean => overrides[step] ?? (finished.current.has(step) || step === firstOpen);
  const toggle = (step: SetupStep): void => { setOverrides((prev) => ({ ...prev, [step]: !isOpen(step) })); };

  // Step 1: the company.
  const [companyForm, setCompanyForm] = useState({ code: "", nameEn: "", nameAr: "", country: "IQ", currency: "IQD", timeZone: "Asia/Baghdad", costingMethod: "average" });
  const createCompany = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/organization/companies", {
      // The policies a company is born with, as the companies screen defaults them; the costing method is the one choice made here.
      body: {
        code: companyForm.code, legalName: { en: companyForm.nameEn, ar: companyForm.nameAr }, country: companyForm.country, functionalCurrency: companyForm.currency, reportingCurrency: null, timeZone: companyForm.timeZone, isActive: true, customFields: {},
        defaultLanguage: "en", costingMethod: companyForm.costingMethod, costingScope: "company", revenueRecognitionPoint: "invoice", taxRoundingMode: "line", roundingMode: "half_away", negativeStockPolicy: "block", bankRevaluationMode: "permanent",
        fiscalCalendarId: null, businessCalendarId: null, tradeName: null, registrationNumbers: null, address: null,
      },
    })),
    onSuccess: async (created) => { setProblem(null); await refresh(); setCompanyId(created.id); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  // Step 2: the chart from a template.
  const [template, setTemplate] = useState("");
  const createChart = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/charts/from-template", { body: { templateCode: template || (chartTemplates.data?.[0]?.code ?? ""), code: `CH-${company?.code ?? "MAIN"}`, companyId, shared: false } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  // Step 3: taxes.
  const installTax = useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/v1/tax/templates/{templateCode}/install", { params: { path: { templateCode: code } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  // Step 4: a cash box or bank account.
  const [bankForm, setBankForm] = useState({ code: "CASH", nameEn: "Main cash box", nameAr: "الصندوق الرئيسي", kind: "cash", currency: "" });
  const createBank = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: bankForm.code, name: { en: bankForm.nameEn, ar: bankForm.nameAr }, kind: bankForm.kind, currency: bankForm.currency || (company?.functionalCurrency ?? ""), isActive: true } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  // Step 7: the day the opening balances stand at.
  const [asOf, setAsOf] = useState(today());

  const currency = company?.functionalCurrency ?? "";
  const money = (value: string | number): string => formatMoney(value, currency);
  const submit = (event: FormEvent, action: () => void): void => { event.preventDefault(); action(); };
  const doneCount = setupSteps.filter((step) => done.has(step)).length;

  return (
    <>
      <PageHeader title={t("setup.title")} description={t("setup.description")} />
      <p className="mb-4 text-sm text-fg-muted" data-testid="setup-progress">{t("setup.progress", { done: doneCount, total: setupSteps.length })}</p>
      <FormError message={problem?.message ?? null} />
      <ol className="flex flex-col gap-3" data-testid="setup-steps">
        <Step id="company" index={0} title={t("setup.steps.company.title")} description={t("setup.steps.company.description")} done={done.has("company")} open={isOpen("company")} onToggle={() => { toggle("company"); }}>
          {companies.length > 0 ? (
            <ul className="mb-3 text-sm" data-testid="setup-companies">
              {companies.map((c) => <li key={c.id}>{c.code} · {localized(c.legalName)} · {c.functionalCurrency}</li>)}
            </ul>
          ) : null}
          <form className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" onSubmit={(e) => { submit(e, () => { createCompany.mutate(); }); }}>
            <Field label={t("companies.code")} required>
              <TextField value={companyForm.code} onChange={(e) => { setCompanyForm({ ...companyForm, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="setup-company-code" />
            </Field>
            <Field label={t("companies.legalNameEn")} required>
              <TextField value={companyForm.nameEn} onChange={(e) => { setCompanyForm({ ...companyForm, nameEn: e.target.value }); }} required data-testid="setup-company-name-en" />
            </Field>
            <Field label={t("companies.legalNameAr")} required>
              <TextField value={companyForm.nameAr} onChange={(e) => { setCompanyForm({ ...companyForm, nameAr: e.target.value }); }} required dir="rtl" data-testid="setup-company-name-ar" />
            </Field>
            <Field label={t("companies.country")} required>
              <TextField value={companyForm.country} onChange={(e) => { setCompanyForm({ ...companyForm, country: e.target.value.toUpperCase() }); }} maxLength={2} required dir="ltr" data-testid="setup-company-country" />
            </Field>
            <Field label={t("companies.functionalCurrency")} required>
              <TextField value={companyForm.currency} onChange={(e) => { setCompanyForm({ ...companyForm, currency: e.target.value.toUpperCase() }); }} maxLength={3} required dir="ltr" data-testid="setup-company-currency" />
            </Field>
            <Field label={t("companies.timeZone")} required>
              <TextField value={companyForm.timeZone} onChange={(e) => { setCompanyForm({ ...companyForm, timeZone: e.target.value }); }} required dir="ltr" data-testid="setup-company-timezone" />
            </Field>
            <Field label={t("companies.policies.costingMethod")}>
              <SelectField value={companyForm.costingMethod} onChange={(e) => { setCompanyForm({ ...companyForm, costingMethod: e.target.value }); }} data-testid="setup-company-costing">
                {["average", "fifo", "standard"].map((m) => <option key={m} value={m}>{t(`companies.policies.values.costingMethod.${m}`)}</option>)}
              </SelectField>
            </Field>
            <div className="flex items-end">
              <Button type="submit" loading={createCompany.isPending} data-testid="setup-company-save">{t("setup.steps.company.create")}</Button>
            </div>
          </form>
        </Step>

        <Step id="chart" index={1} title={t("setup.steps.chart.title")} description={t("setup.steps.chart.description")} done={done.has("chart")} open={isOpen("chart")} onToggle={() => { toggle("chart"); }}>
          {done.has("chart") ? <p className="text-sm" data-testid="setup-chart-done">{t("setup.steps.chart.exists")}</p> : (
            <form className="flex flex-wrap items-end gap-3" onSubmit={(e) => { submit(e, () => { createChart.mutate(); }); }}>
              <Field label={t("setup.steps.chart.template")} required>
                <SelectField value={template || (chartTemplates.data?.[0]?.code ?? "")} onChange={(e) => { setTemplate(e.target.value); }} data-testid="setup-chart-template">
                  {(chartTemplates.data ?? []).map((tpl) => <option key={tpl.code} value={tpl.code}>{localized(tpl.name)} · {t("setup.steps.chart.accounts", { count: Number(tpl.accounts) })}</option>)}
                </SelectField>
              </Field>
              <Button type="submit" loading={createChart.isPending} disabled={!companyId} data-testid="setup-chart-create">{t("setup.steps.chart.create")}</Button>
            </form>
          )}
        </Step>

        <Step id="tax" index={2} title={t("setup.steps.tax.title")} description={t("setup.steps.tax.description")} done={done.has("tax")} open={isOpen("tax")} onToggle={() => { toggle("tax"); }}>
          <ul className="mb-3 flex flex-col gap-2">
            {(taxTemplates.data ?? []).map((tpl) => (
              <li key={tpl.code} className="flex flex-wrap items-center gap-3 text-sm">
                <span className="font-medium">{localized(tpl.name)}</span>
                <span className="text-fg-muted" dir="ltr">{tpl.country} · v{tpl.version}</span>
                {tpl.installed ? <Badge tone="success">{t("setup.steps.tax.installed")}</Badge> : (
                  <Button type="button" variant="secondary" size="sm" onClick={() => { installTax.mutate(tpl.code); }} loading={installTax.isPending && installTax.variables === tpl.code} data-testid={`setup-tax-install-${tpl.code}`}>{t("setup.steps.tax.install")}</Button>
                )}
              </li>
            ))}
          </ul>
          {Number(status.data?.taxRegimes ?? 0) === 0 ? (
            <Button type="button" variant={taxSkipped === "1" ? "secondary" : "ghost"} size="sm" onClick={() => { rememberTaxSkipped(taxSkipped === "1" ? "" : "1"); }} data-testid="setup-tax-skip">
              {taxSkipped === "1" ? t("setup.steps.tax.unskip") : t("setup.steps.tax.skip")}
            </Button>
          ) : null}
        </Step>

        <Step id="bank" index={3} title={t("setup.steps.bank.title")} description={t("setup.steps.bank.description")} done={done.has("bank")} open={isOpen("bank")} onToggle={() => { toggle("bank"); }}>
          {(bankAccounts.data ?? []).length > 0 ? (
            <ul className="mb-3 text-sm" data-testid="setup-bank-list">
              {(bankAccounts.data ?? []).map((a) => <li key={a.id}>{a.code} · {localized(a.name)} · {a.currency} · {t(`banking.kinds.${a.kind}`, { defaultValue: a.kind })}</li>)}
            </ul>
          ) : null}
          <form className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5" onSubmit={(e) => { submit(e, () => { createBank.mutate(); }); }}>
            <Field label={t("banking.code")} required>
              <TextField value={bankForm.code} onChange={(e) => { setBankForm({ ...bankForm, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="setup-bank-code" />
            </Field>
            <Field label={t("common.nameEn")} required>
              <TextField value={bankForm.nameEn} onChange={(e) => { setBankForm({ ...bankForm, nameEn: e.target.value }); }} required data-testid="setup-bank-name-en" />
            </Field>
            <Field label={t("common.nameAr")} required>
              <TextField value={bankForm.nameAr} onChange={(e) => { setBankForm({ ...bankForm, nameAr: e.target.value }); }} required dir="rtl" data-testid="setup-bank-name-ar" />
            </Field>
            <Field label={t("setup.steps.bank.kind")}>
              <SelectField value={bankForm.kind} onChange={(e) => { setBankForm({ ...bankForm, kind: e.target.value }); }} data-testid="setup-bank-kind">
                <option value="cash">{t("banking.kinds.cash")}</option>
                <option value="bank">{t("banking.kinds.bank")}</option>
              </SelectField>
            </Field>
            <div className="flex items-end">
              <Button type="submit" loading={createBank.isPending} disabled={!companyId} data-testid="setup-bank-save">{t("setup.steps.bank.create")}</Button>
            </div>
          </form>
        </Step>

        <Step id="partners" index={4} title={t("setup.steps.partners.title")} description={t("setup.steps.partners.description")} done={done.has("partners")} open={isOpen("partners")} onToggle={() => { toggle("partners"); }}>
          <p className="mb-2 text-sm" data-testid="setup-partners-count">{t("setup.steps.partners.count", { customers: Number(status.data?.customers ?? 0), suppliers: Number(status.data?.suppliers ?? 0) })}</p>
          <ImportBox<PartnerImport>
            id="setup-partners"
            columns={partnerColumns}
            example="CUS-001,Alpha Markets,أسواق ألفا,organization,yes,no,alpha@example.com,+9647700000000,IQD,,5000000,,,true"
            templateName="customers-suppliers"
            disabled={!companyId}
            run={async (upload, dryRun) => unwrap(await api.POST("/api/v1/partners/import", { params: { query: { companyId, dryRun } }, body: { rows: [] }, ...raw(upload) }))}
            summary={(result, dryRun) => t(dryRun ? "setup.steps.partners.checked" : "setup.steps.partners.imported", { created: Number(result.created), updated: Number(result.updated), accounts: Number(result.accounts) })}
            onImported={refresh}
          />
        </Step>

        <Step id="items" index={5} title={t("setup.steps.items.title")} description={t("setup.steps.items.description")} done={done.has("items")} open={isOpen("items")} onToggle={() => { toggle("items"); }}>
          <p className="mb-2 text-sm" data-testid="setup-items-count">{t("setup.steps.items.count", { count: Number(status.data?.items ?? 0) })}</p>
          <ImportBox<ItemImport>
            id="setup-items"
            columns={itemColumns}
            example="TEA-500,Tea 500g,شاي ٥٠٠غ,stock,,,PCS,,CTN,CTN:12/1,,none,2500,IQD,true"
            templateName="items"
            run={async (upload, dryRun) => unwrap(await api.POST("/api/v1/items/import", { params: { query: { dryRun } }, body: { items: [] }, ...raw(upload) }))}
            summary={(result, dryRun) => t(dryRun ? "setup.steps.items.checked" : "setup.steps.items.imported", { created: Number(result.created), updated: Number(result.updated) })}
            onImported={refresh}
          />
        </Step>

        <Step id="opening" index={6} title={t("setup.steps.opening.title")} description={t("setup.steps.opening.description")} done={done.has("opening")} open={isOpen("opening")} onToggle={() => { toggle("opening"); }}>
          <ImportBox<OpeningImport>
            id="setup-opening"
            columns={openingColumns}
            example={`1111,1000000,,,CASH,,Cash count\n1210,500000,,CUS-001,,${asOf},Open invoice\n3100,,1500000,,,,Capital`}
            templateName="opening-balances"
            disabled={!companyId}
            run={async (upload, dryRun) => unwrap(await api.POST("/api/v1/onboarding/companies/{companyId}/opening-balances", { params: { path: { companyId }, query: { asOf, dryRun } }, body: { lines: [] }, ...raw(upload) }))}
            summary={(result, dryRun) => (
              <>
                {dryRun ? t("setup.steps.opening.checked", { lines: Number(result.lines) }) : t("setup.steps.opening.posted", { number: result.number ?? "" })}
                {" "}
                <span dir="ltr">{t("setup.steps.opening.sides", { debit: money(result.debit), credit: money(result.credit) })}</span>
                {" "}
                {Number(result.difference) === 0 ? t("setup.steps.opening.balanced") : t("setup.steps.opening.difference", { amount: money(result.difference) })}
              </>
            )}
            onImported={refresh}
          >
            <Field label={t("setup.steps.opening.asOf")} required>
              <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" className="max-w-56" data-testid="setup-opening-asof" />
            </Field>
          </ImportBox>
        </Step>

        <Step id="ready" index={7} title={t("setup.steps.ready.title")} description={t("setup.steps.ready.description")} done={done.has("ready")} open={isOpen("ready")} onToggle={() => { toggle("ready"); }}>
          <ul className="mb-4 grid gap-1 text-sm sm:grid-cols-2" data-testid="setup-checklist">
            {setupSteps.filter((step) => step !== "ready").map((step) => (
              <li key={step} className="flex items-center gap-2">
                <span className={done.has(step) ? "text-success" : "text-fg-subtle"} aria-hidden="true">{done.has(step) ? "✓" : "○"}</span>
                <span>{t(`setup.steps.${step}.title`)}</span>
                <span className="sr-only">{done.has(step) ? t("setup.done") : t("setup.todo")}</span>
              </li>
            ))}
          </ul>
          <div className="flex flex-wrap gap-2">
            <Button asChild><Link to="/" data-testid="setup-go-home">{t("setup.steps.ready.home")}</Link></Button>
            <Button asChild variant="secondary"><Link to="/members">{t("setup.steps.ready.invite")}</Link></Button>
            <Button asChild variant="secondary"><Link to="/sales/invoices">{t("setup.steps.ready.firstInvoice")}</Link></Button>
          </div>
          <p className="mt-3 text-xs text-fg-muted">{t("setup.steps.ready.counts", { customers: formatNumber(Number(status.data?.customers ?? 0)), suppliers: formatNumber(Number(status.data?.suppliers ?? 0)), items: formatNumber(Number(status.data?.items ?? 0)) })}</p>
        </Step>
      </ol>
    </>
  );
}
