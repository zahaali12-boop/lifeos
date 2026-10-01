import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Pencil, Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { useCompanyCustomers } from "../sales/pricing/shared";
import {
  directions,
  returnFrequencies,
  roundingLevels,
  taxPoints,
  treatments,
  useTaxExemptions,
  useTaxGroups,
  useTaxRegime,
  useTaxRegimes,
  useTaxRegistrations,
  useTaxTemplates,
  type TaxCode,
  type TaxGroup,
  type TaxRegime,
  type TaxRule,
} from "./shared";

const s = (value: number | string | null | undefined): string => (value === null || value === undefined ? "" : String(value));

/** Tax set-up (roadmap 5.3d): country templates, regimes with their codes, rates and matrix, item and partner tax groups, company registrations and partner exemptions. */
export function TaxSetupPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState("templates");
  const { companies, companyId, setCompanyId } = useCompanyContext();
  return (
    <>
      <PageHeader title={t("nav.taxSetup")} description={t("tax.setupDescription")} />
      <Tabs
        tabs={[
          { id: "templates", label: t("tax.templates"), testId: "tab-templates" },
          { id: "regimes", label: t("tax.regimes"), testId: "tab-regimes" },
          { id: "groups", label: t("tax.groups"), testId: "tab-groups" },
          { id: "registrations", label: t("tax.registrations"), testId: "tab-registrations" },
          { id: "exemptions", label: t("tax.exemptions"), testId: "tab-exemptions" },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === "templates" ? <TemplatesTab /> : null}
      {tab === "regimes" ? <RegimesTab /> : null}
      {tab === "groups" ? <GroupsTab /> : null}
      {tab === "registrations" ? (
        <>
          <div className="mt-3 grid gap-3 sm:grid-cols-4">
            <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
          </div>
          {companyId ? <RegistrationsTab companyId={companyId} /> : null}
        </>
      ) : null}
      {tab === "exemptions" ? (
        <>
          <div className="mt-3 grid gap-3 sm:grid-cols-4">
            <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
          </div>
          {companyId ? <ExemptionsTab companyId={companyId} /> : null}
        </>
      ) : null}
    </>
  );
}

function FormDialog({ title, open, onClose, onSubmit, busy, problem, children, testId }: { title: string; open: boolean; onClose: () => void; onSubmit: () => void; busy: boolean; problem: FormProblem | null; children: ReactNode; testId: string }) {
  const { t } = useTranslation();
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    onSubmit();
  };
  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{title}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          {children}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={busy} data-testid={testId}>
              {t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function Toolbar({ canManage, onNew, label, testId }: { canManage: boolean; onNew: () => void; label: string; testId: string }) {
  return canManage ? (
    <div className="my-3 flex justify-end">
      <Button onClick={onNew} data-testid={testId}>
        <Plus aria-hidden="true" />
        {label}
      </Button>
    </div>
  ) : (
    <div className="my-3" />
  );
}

function RowActions({ canManage, label, onEdit, onDelete }: { canManage: boolean; label: string; onEdit: () => void; onDelete?: () => void }) {
  const { t } = useTranslation();
  return canManage ? (
    <span className="inline-flex gap-1">
      <Button variant="ghost" size="sm" aria-label={t("tax.editNamed", { name: label })} onClick={onEdit}>
        <Pencil aria-hidden="true" />
      </Button>
      {onDelete ? (
        <Button variant="ghost" size="sm" aria-label={t("tax.deleteNamed", { name: label })} onClick={onDelete}>
          <Trash2 aria-hidden="true" />
        </Button>
      ) : null}
    </span>
  ) : null;
}

function useSaver<T>(invalidate: string[], save: (body: T) => Promise<unknown>, onDone: () => void) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const mutation = useMutation({
    mutationFn: save,
    onSuccess: async () => {
      setProblem(null);
      onDone();
      await Promise.all(invalidate.map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
    },
    onError: (error) => {
      setProblem(toFormProblem(error, t("common.saveFailed")));
    },
  });
  return { mutation, problem, setProblem };
}

// ------------------------------------------------------------------ templates

function TemplatesTab() {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("tax.setup.manage");
  const templates = useTaxTemplates();
  const queryClient = useQueryClient();
  const install = useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/v1/tax/templates/{templateCode}/install", { params: { path: { templateCode: code } } })),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["tax-templates"] }), queryClient.invalidateQueries({ queryKey: ["tax-regimes"] }), queryClient.invalidateQueries({ queryKey: ["tax-groups"] })]);
    },
  });
  return (
    <Table aria-label={t("tax.templates")}>
      <TableHeader>
        <TableRow>
          <TableHead>{t("tax.code")}</TableHead>
          <TableHead>{t("tax.country")}</TableHead>
          <TableHead>{t("tax.name")}</TableHead>
          <TableHead>{t("tax.version")}</TableHead>
          <TableHead className="text-end">{t("tax.codes")}</TableHead>
          <TableHead className="text-end">{t("tax.rules")}</TableHead>
          <TableHead>
            <span className="sr-only">{t("common.actions")}</span>
          </TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {(templates.data ?? []).map((template) => (
          <TableRow key={template.code} data-testid="template-row">
            <TableCell dir="ltr">
              <bdi>{template.code}</bdi>
            </TableCell>
            <TableCell dir="ltr">{template.country}</TableCell>
            <TableCell dir="auto">{localized(template.name)}</TableCell>
            <TableCell dir="ltr">{template.version}</TableCell>
            <TableCell className="text-end tabular">{formatNumber(template.codes)}</TableCell>
            <TableCell className="text-end tabular">{formatNumber(template.rules)}</TableCell>
            <TableCell className="text-end">
              {template.installed ? (
                <Badge tone="success" className="inline-flex items-center gap-1">
                  <CheckCircle2 aria-hidden="true" className="size-3.5" />
                  {t("tax.installed")}
                </Badge>
              ) : mayManage ? (
                <Button size="sm" onClick={() => { install.mutate(template.code); }} loading={install.isPending && install.variables === template.code} data-testid="install-template">
                  {t("tax.install")}
                </Button>
              ) : null}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

// ------------------------------------------------------------------ regimes

interface RegimeForm {
  nameEn: string;
  nameAr: string;
  roundingLevel: string;
  taxPoint: string;
  returnFrequency: string;
  einvoicingScheme: string;
  isActive: boolean;
}

interface CodeForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
  treatment: string;
  rates: { validFrom: string; ratePct: string }[];
  isRecoverable: boolean;
  isReverseCharge: boolean;
  appliesTo: string;
  exemptionReasonCode: string;
  salesBaseBox: string;
  salesTaxBox: string;
  purchaseBaseBox: string;
  purchaseTaxBox: string;
  outputAccountRole: string;
  inputAccountRole: string;
  isActive: boolean;
}

interface RuleForm {
  id: string | null;
  direction: string;
  taxCodeId: string;
  itemTaxGroupId: string;
  partnerTaxGroupId: string;
  shipFromCountry: string;
  shipToCountry: string;
  validFrom: string;
  validTo: string;
}

function RegimesTab() {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("tax.setup.manage");
  const regimes = useTaxRegimes();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const detail = useTaxRegime(selectedId ?? "");
  const itemGroups = useTaxGroups("item");
  const partnerGroups = useTaxGroups("partner");

  const [regimeForm, setRegimeForm] = useState<RegimeForm | null>(null);
  const regimeSaver = useSaver<RegimeForm>(["tax-regime", "tax-regimes"], async (f) => {
    if (!selectedId) {
      return;
    }
    return unwrap(await api.PUT("/api/v1/tax/regimes/{regimeId}", { params: { path: { regimeId: selectedId } }, body: { name: { en: f.nameEn, ar: f.nameAr }, roundingLevel: f.roundingLevel, taxPoint: f.taxPoint, returnFrequency: f.returnFrequency, einvoicingScheme: f.einvoicingScheme || null, isActive: f.isActive } }));
  }, () => { setRegimeForm(null); });

  const [codeForm, setCodeForm] = useState<CodeForm | null>(null);
  const codeSaver = useSaver<CodeForm>(["tax-regime"], async (f) => {
    if (!selectedId) {
      return;
    }
    const body = {
      code: f.code,
      name: { en: f.nameEn, ar: f.nameAr },
      kind: f.kind,
      treatment: f.treatment,
      rates: f.rates.filter((r) => r.validFrom).map((r) => ({ validFrom: r.validFrom, ratePct: Number(r.ratePct || "0") })),
      isRecoverable: f.isRecoverable,
      isReverseCharge: f.isReverseCharge,
      appliesTo: f.appliesTo,
      exemptionReasonCode: f.exemptionReasonCode || null,
      salesBaseBox: f.salesBaseBox || null,
      salesTaxBox: f.salesTaxBox || null,
      purchaseBaseBox: f.purchaseBaseBox || null,
      purchaseTaxBox: f.purchaseTaxBox || null,
      outputAccountRole: f.outputAccountRole,
      inputAccountRole: f.inputAccountRole,
      isActive: f.isActive,
    };
    return f.id
      ? unwrap(await api.PUT("/api/v1/tax/regimes/{regimeId}/codes/{codeId}", { params: { path: { regimeId: selectedId, codeId: f.id } }, body }))
      : unwrap(await api.POST("/api/v1/tax/regimes/{regimeId}/codes", { params: { path: { regimeId: selectedId } }, body }));
  }, () => { setCodeForm(null); });

  const [ruleForm, setRuleForm] = useState<RuleForm | null>(null);
  const ruleSaver = useSaver<RuleForm>(["tax-regime"], async (f) => {
    if (!selectedId) {
      return;
    }
    const body = { direction: f.direction, taxCodeId: f.taxCodeId, itemTaxGroupId: f.itemTaxGroupId || null, partnerTaxGroupId: f.partnerTaxGroupId || null, shipFromCountry: f.shipFromCountry || null, shipToCountry: f.shipToCountry || null, validFrom: f.validFrom || null, validTo: f.validTo || null };
    return f.id
      ? unwrap(await api.PUT("/api/v1/tax/regimes/{regimeId}/rules/{ruleId}", { params: { path: { regimeId: selectedId, ruleId: f.id } }, body }))
      : unwrap(await api.POST("/api/v1/tax/regimes/{regimeId}/rules", { params: { path: { regimeId: selectedId } }, body }));
  }, () => { setRuleForm(null); });
  const deleteRule = useSaver<string>(["tax-regime"], async (ruleId) => {
    if (!selectedId) {
      return;
    }
    unwrap(await api.DELETE("/api/v1/tax/regimes/{regimeId}/rules/{ruleId}", { params: { path: { regimeId: selectedId, ruleId } } }));
  }, () => undefined);

  const editRegime = (regime: TaxRegime): void => {
    regimeSaver.setProblem(null);
    setRegimeForm({ nameEn: regime.name.en ?? "", nameAr: regime.name.ar ?? "", roundingLevel: regime.roundingLevel, taxPoint: regime.taxPoint, returnFrequency: regime.returnFrequency, einvoicingScheme: regime.einvoicingScheme ?? "", isActive: regime.isActive });
  };
  const editCode = (code: TaxCode | null): void => {
    codeSaver.setProblem(null);
    setCodeForm(code
      ? { id: code.id, code: code.code, nameEn: code.name.en ?? "", nameAr: code.name.ar ?? "", kind: code.kind, treatment: code.treatment, rates: code.rates.map((r) => ({ validFrom: r.validFrom, ratePct: s(r.ratePct) })), isRecoverable: code.isRecoverable, isReverseCharge: code.isReverseCharge, appliesTo: code.appliesTo, exemptionReasonCode: code.exemptionReasonCode ?? "", salesBaseBox: code.salesBaseBox ?? "", salesTaxBox: code.salesTaxBox ?? "", purchaseBaseBox: code.purchaseBaseBox ?? "", purchaseTaxBox: code.purchaseTaxBox ?? "", outputAccountRole: code.outputAccountRole, inputAccountRole: code.inputAccountRole, isActive: code.isActive }
      : { id: null, code: "", nameEn: "", nameAr: "", kind: "vat", treatment: "standard", rates: [{ validFrom: "", ratePct: "" }], isRecoverable: true, isReverseCharge: false, appliesTo: "both", exemptionReasonCode: "", salesBaseBox: "", salesTaxBox: "", purchaseBaseBox: "", purchaseTaxBox: "", outputAccountRole: "OutputTax", inputAccountRole: "InputTax", isActive: true });
  };
  const editRule = (rule: TaxRule | null): void => {
    ruleSaver.setProblem(null);
    setRuleForm(rule
      ? { id: rule.id, direction: rule.direction, taxCodeId: rule.taxCodeId, itemTaxGroupId: rule.itemTaxGroupId ?? "", partnerTaxGroupId: rule.partnerTaxGroupId ?? "", shipFromCountry: rule.shipFromCountry ?? "", shipToCountry: rule.shipToCountry ?? "", validFrom: rule.validFrom ?? "", validTo: rule.validTo ?? "" }
      : { id: null, direction: "sales", taxCodeId: detail.data?.codes[0]?.id ?? "", itemTaxGroupId: "", partnerTaxGroupId: "", shipFromCountry: "", shipToCountry: "", validFrom: "", validTo: "" });
  };

  return (
    <div className="mt-3 grid gap-4 lg:grid-cols-[minmax(0,20rem)_1fr]">
      <Table aria-label={t("tax.regimes")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("tax.code")}</TableHead>
            <TableHead>{t("tax.country")}</TableHead>
            <TableHead className="text-end">{t("tax.codes")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(regimes.data ?? []).map((regime) => (
            <TableRow key={regime.id} data-testid="regime-row" aria-current={regime.id === selectedId} className={regime.id === selectedId ? "bg-bg-muted" : undefined}>
              <TableCell>
                <button type="button" className="font-medium underline-offset-2 hover:underline" onClick={() => { setSelectedId(regime.id); }} data-testid="select-regime">
                  <bdi dir="ltr">{regime.code}</bdi>
                </button>
                {!regime.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell dir="ltr">{regime.country}</TableCell>
              <TableCell className="text-end tabular">{formatNumber(regime.codes)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {selectedId && detail.data ? (
        <div className="flex flex-col gap-4">
          <div className="rounded-lg border border-border p-4">
            <div className="mb-3 flex items-center justify-between">
              <h2 className="font-semibold">{localized(detail.data.regime.name)}</h2>
              {mayManage ? (
                <Button variant="secondary" size="sm" onClick={() => { editRegime(detail.data.regime); }} data-testid="edit-regime">
                  <Pencil aria-hidden="true" />
                  {t("common.edit")}
                </Button>
              ) : null}
            </div>
            <dl className="grid gap-2 text-sm sm:grid-cols-3">
              <div>
                <dt className="text-fg-muted">{t("tax.roundingLevel")}</dt>
                <dd>{t(`tax.value.${detail.data.regime.roundingLevel}`)}</dd>
              </div>
              <div>
                <dt className="text-fg-muted">{t("tax.taxPoint")}</dt>
                <dd>{t(`tax.value.${detail.data.regime.taxPoint}`)}</dd>
              </div>
              <div>
                <dt className="text-fg-muted">{t("tax.returnFrequency")}</dt>
                <dd>{t(`tax.value.${detail.data.regime.returnFrequency}`)}</dd>
              </div>
              <div>
                <dt className="text-fg-muted">{t("tax.einvoicingScheme")}</dt>
                <dd dir="ltr">{detail.data.regime.einvoicingScheme ?? t("tax.none")}</dd>
              </div>
              <div>
                <dt className="text-fg-muted">{t("tax.registrations")}</dt>
                <dd className="tabular">{formatNumber(detail.data.regime.registrations)}</dd>
              </div>
            </dl>
          </div>

          <div>
            <div className="mb-2 flex items-center justify-between">
              <h3 className="font-semibold">{t("tax.codes")}</h3>
              <Toolbar canManage={mayManage} onNew={() => { editCode(null); }} label={t("tax.newCode")} testId="new-code" />
            </div>
            <Table aria-label={t("tax.codes")}>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("tax.code")}</TableHead>
                  <TableHead>{t("tax.name")}</TableHead>
                  <TableHead>{t("tax.treatment")}</TableHead>
                  <TableHead className="text-end">{t("tax.currentRate")}</TableHead>
                  <TableHead>{t("tax.boxes")}</TableHead>
                  <TableHead>
                    <span className="sr-only">{t("common.actions")}</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {detail.data.codes.map((code) => (
                  <TableRow key={code.id} data-testid="code-row">
                    <TableCell dir="ltr">
                      <bdi>{code.code}</bdi>
                      {code.isReverseCharge ? <Badge tone="neutral" className="ms-2">{t("tax.reverseCharge")}</Badge> : null}
                      {!code.isRecoverable ? <Badge tone="neutral" className="ms-2">{t("tax.notRecoverable")}</Badge> : null}
                    </TableCell>
                    <TableCell dir="auto">{localized(code.name)}</TableCell>
                    <TableCell>{t(`tax.value.${code.treatment}`)}</TableCell>
                    <TableCell className="text-end tabular">{code.currentRatePct !== null ? `${formatNumber(code.currentRatePct)}%` : "—"}</TableCell>
                    <TableCell dir="ltr" className="text-fg-muted">
                      {[code.salesBaseBox && `S${code.salesBaseBox}`, code.purchaseBaseBox && `P${code.purchaseBaseBox}`].filter(Boolean).join(" · ")}
                    </TableCell>
                    <TableCell className="text-end">
                      <RowActions canManage={mayManage} label={code.code} onEdit={() => { editCode(code); }} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          <div>
            <div className="mb-2 flex items-center justify-between">
              <h3 className="font-semibold">{t("tax.matrix")}</h3>
              <Toolbar canManage={mayManage} onNew={() => { editRule(null); }} label={t("tax.newRule")} testId="new-rule" />
            </div>
            <Table aria-label={t("tax.matrix")}>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("tax.direction")}</TableHead>
                  <TableHead>{t("tax.itemGroup")}</TableHead>
                  <TableHead>{t("tax.partnerGroup")}</TableHead>
                  <TableHead>{t("tax.shipping")}</TableHead>
                  <TableHead>{t("tax.validity")}</TableHead>
                  <TableHead>{t("tax.code")}</TableHead>
                  <TableHead>
                    <span className="sr-only">{t("common.actions")}</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {detail.data.rules.map((rule) => (
                  <TableRow key={rule.id} data-testid="rule-row">
                    <TableCell>{t(`tax.value.${rule.direction}`)}</TableCell>
                    <TableCell dir="ltr">{rule.itemTaxGroupCode ?? t("tax.any")}</TableCell>
                    <TableCell dir="ltr">{rule.partnerTaxGroupCode ?? t("tax.any")}</TableCell>
                    <TableCell dir="ltr">{[rule.shipFromCountry, rule.shipToCountry].filter(Boolean).join(" → ") || t("tax.any")}</TableCell>
                    <TableCell dir="ltr">{rule.validFrom ? formatDate(rule.validFrom) : t("tax.always")}</TableCell>
                    <TableCell dir="ltr">
                      <bdi>{rule.taxCode}</bdi>
                    </TableCell>
                    <TableCell className="text-end">
                      <RowActions canManage={mayManage} label={rule.taxCode} onEdit={() => { editRule(rule); }} onDelete={() => { deleteRule.mutation.mutate(rule.id); }} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </div>
      ) : (
        <p className="text-sm text-fg-muted">{t("tax.selectRegime")}</p>
      )}

      <FormDialog title={t("tax.editRegime")} open={Boolean(regimeForm)} onClose={() => { setRegimeForm(null); }} onSubmit={() => { if (regimeForm) { regimeSaver.mutation.mutate(regimeForm); } }} busy={regimeSaver.mutation.isPending} problem={regimeSaver.problem} testId="save-regime">
        {regimeForm ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("tax.nameEn")} required>
              <TextField value={regimeForm.nameEn} onChange={(e) => { setRegimeForm({ ...regimeForm, nameEn: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("tax.nameAr")} required>
              <TextField value={regimeForm.nameAr} onChange={(e) => { setRegimeForm({ ...regimeForm, nameAr: e.target.value }); }} required dir="rtl" />
            </Field>
            <Field label={t("tax.roundingLevel")}>
              <SelectField value={regimeForm.roundingLevel} onChange={(e) => { setRegimeForm({ ...regimeForm, roundingLevel: e.target.value }); }}>
                {roundingLevels.map((v) => (
                  <option key={v} value={v}>
                    {t(`tax.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.taxPoint")}>
              <SelectField value={regimeForm.taxPoint} onChange={(e) => { setRegimeForm({ ...regimeForm, taxPoint: e.target.value }); }}>
                {taxPoints.map((v) => (
                  <option key={v} value={v}>
                    {t(`tax.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.returnFrequency")}>
              <SelectField value={regimeForm.returnFrequency} onChange={(e) => { setRegimeForm({ ...regimeForm, returnFrequency: e.target.value }); }}>
                {returnFrequencies.map((v) => (
                  <option key={v} value={v}>
                    {t(`tax.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.einvoicingScheme")}>
              <TextField value={regimeForm.einvoicingScheme} onChange={(e) => { setRegimeForm({ ...regimeForm, einvoicingScheme: e.target.value }); }} dir="ltr" />
            </Field>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={regimeForm.isActive} onChange={(e) => { setRegimeForm({ ...regimeForm, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
          </div>
        ) : null}
      </FormDialog>

      <FormDialog title={codeForm?.id ? t("tax.editCode") : t("tax.newCode")} open={Boolean(codeForm)} onClose={() => { setCodeForm(null); }} onSubmit={() => { if (codeForm) { codeSaver.mutation.mutate(codeForm); } }} busy={codeSaver.mutation.isPending} problem={codeSaver.problem} testId="save-code">
        {codeForm ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("tax.code")} required>
              <TextField value={codeForm.code} onChange={(e) => { setCodeForm({ ...codeForm, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="code-code" />
            </Field>
            <Field label={t("tax.nameEn")} required>
              <TextField value={codeForm.nameEn} onChange={(e) => { setCodeForm({ ...codeForm, nameEn: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("tax.nameAr")} required>
              <TextField value={codeForm.nameAr} onChange={(e) => { setCodeForm({ ...codeForm, nameAr: e.target.value }); }} required dir="rtl" />
            </Field>
            <Field label={t("tax.treatment")}>
              <SelectField value={codeForm.treatment} onChange={(e) => { setCodeForm({ ...codeForm, treatment: e.target.value }); }} data-testid="code-treatment">
                {treatments.map((v) => (
                  <option key={v} value={v}>
                    {t(`tax.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.appliesTo")}>
              <SelectField value={codeForm.appliesTo} onChange={(e) => { setCodeForm({ ...codeForm, appliesTo: e.target.value }); }}>
                <option value="both">{t("tax.value.both")}</option>
                <option value="goods">{t("tax.value.goods")}</option>
                <option value="services">{t("tax.value.services")}</option>
              </SelectField>
            </Field>
            {codeForm.treatment === "exempt" ? (
              <Field label={t("tax.exemptionReasonCode")} required>
                <TextField value={codeForm.exemptionReasonCode} onChange={(e) => { setCodeForm({ ...codeForm, exemptionReasonCode: e.target.value }); }} required dir="ltr" />
              </Field>
            ) : null}
            <div className="sm:col-span-3">
              <span className="mb-1 block text-sm font-medium">{t("tax.rates")}</span>
              <div className="flex flex-col gap-2">
                {codeForm.rates.map((rate, index) => (
                  <div key={index} className="flex items-center gap-2">
                    <TextField type="date" value={rate.validFrom} onChange={(e) => { const rates = [...codeForm.rates]; rates[index] = { ...rate, validFrom: e.target.value }; setCodeForm({ ...codeForm, rates }); }} dir="ltr" data-testid="rate-from" />
                    <TextField type="number" min={0} max={100} step="any" value={rate.ratePct} onChange={(e) => { const rates = [...codeForm.rates]; rates[index] = { ...rate, ratePct: e.target.value }; setCodeForm({ ...codeForm, rates }); }} dir="ltr" className="w-24" data-testid="rate-pct" />
                    <span className="text-sm text-fg-muted">%</span>
                    <Button type="button" variant="ghost" size="sm" onClick={() => { setCodeForm({ ...codeForm, rates: codeForm.rates.filter((_, i) => i !== index) }); }} aria-label={t("common.delete")}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </div>
                ))}
                <Button type="button" variant="secondary" size="sm" className="self-start" onClick={() => { setCodeForm({ ...codeForm, rates: [...codeForm.rates, { validFrom: "", ratePct: "" }] }); }}>
                  <Plus aria-hidden="true" />
                  {t("tax.addRate")}
                </Button>
              </div>
            </div>
            <Field label={t("tax.salesBaseBox")}>
              <TextField value={codeForm.salesBaseBox} onChange={(e) => { setCodeForm({ ...codeForm, salesBaseBox: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("tax.salesTaxBox")}>
              <TextField value={codeForm.salesTaxBox} onChange={(e) => { setCodeForm({ ...codeForm, salesTaxBox: e.target.value }); }} dir="ltr" />
            </Field>
            <div />
            <Field label={t("tax.purchaseBaseBox")}>
              <TextField value={codeForm.purchaseBaseBox} onChange={(e) => { setCodeForm({ ...codeForm, purchaseBaseBox: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("tax.purchaseTaxBox")}>
              <TextField value={codeForm.purchaseTaxBox} onChange={(e) => { setCodeForm({ ...codeForm, purchaseTaxBox: e.target.value }); }} dir="ltr" />
            </Field>
            <div />
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={codeForm.isRecoverable} onChange={(e) => { setCodeForm({ ...codeForm, isRecoverable: e.target.checked }); }} />
              {t("tax.isRecoverable")}
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={codeForm.isReverseCharge} onChange={(e) => { setCodeForm({ ...codeForm, isReverseCharge: e.target.checked }); }} />
              {t("tax.reverseCharge")}
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={codeForm.isActive} onChange={(e) => { setCodeForm({ ...codeForm, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
          </div>
        ) : null}
      </FormDialog>

      <FormDialog title={ruleForm?.id ? t("tax.editRule") : t("tax.newRule")} open={Boolean(ruleForm)} onClose={() => { setRuleForm(null); }} onSubmit={() => { if (ruleForm) { ruleSaver.mutation.mutate(ruleForm); } }} busy={ruleSaver.mutation.isPending} problem={ruleSaver.problem} testId="save-rule">
        {ruleForm ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("tax.direction")}>
              <SelectField value={ruleForm.direction} onChange={(e) => { setRuleForm({ ...ruleForm, direction: e.target.value }); }} data-testid="rule-direction">
                {directions.map((v) => (
                  <option key={v} value={v}>
                    {t(`tax.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.code")} required>
              <SelectField value={ruleForm.taxCodeId} onChange={(e) => { setRuleForm({ ...ruleForm, taxCodeId: e.target.value }); }} required data-testid="rule-code">
                <option value="">—</option>
                {(detail.data?.codes ?? []).map((code) => (
                  <option key={code.id} value={code.id}>
                    {code.code}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.itemGroup")}>
              <SelectField value={ruleForm.itemTaxGroupId} onChange={(e) => { setRuleForm({ ...ruleForm, itemTaxGroupId: e.target.value }); }}>
                <option value="">{t("tax.any")}</option>
                {(itemGroups.data ?? []).map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.code} · {localized(g.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.partnerGroup")}>
              <SelectField value={ruleForm.partnerTaxGroupId} onChange={(e) => { setRuleForm({ ...ruleForm, partnerTaxGroupId: e.target.value }); }}>
                <option value="">{t("tax.any")}</option>
                {(partnerGroups.data ?? []).map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.code} · {localized(g.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.shipFromCountry")}>
              <TextField value={ruleForm.shipFromCountry} onChange={(e) => { setRuleForm({ ...ruleForm, shipFromCountry: e.target.value.toUpperCase() }); }} maxLength={2} dir="ltr" />
            </Field>
            <Field label={t("tax.shipToCountry")}>
              <TextField value={ruleForm.shipToCountry} onChange={(e) => { setRuleForm({ ...ruleForm, shipToCountry: e.target.value.toUpperCase() }); }} maxLength={2} dir="ltr" />
            </Field>
            <Field label={t("tax.validFrom")}>
              <TextField type="date" value={ruleForm.validFrom} onChange={(e) => { setRuleForm({ ...ruleForm, validFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("tax.validTo")}>
              <TextField type="date" value={ruleForm.validTo} onChange={(e) => { setRuleForm({ ...ruleForm, validTo: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
        ) : null}
      </FormDialog>
    </div>
  );
}

// ------------------------------------------------------------------ groups

interface GroupForm {
  id: string | null;
  kind: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
}

function GroupsTab() {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("tax.setup.manage");
  const [kind, setKind] = useState<"item" | "partner">("item");
  const groups = useTaxGroups(kind);
  const [form, setForm] = useState<GroupForm | null>(null);
  const saver = useSaver<GroupForm>(["tax-groups"], async (f) => {
    const body = { kind: f.kind, code: f.code, name: { en: f.nameEn, ar: f.nameAr }, isActive: f.isActive };
    return f.id ? unwrap(await api.PUT("/api/v1/tax/groups/{groupId}", { params: { path: { groupId: f.id } }, body })) : unwrap(await api.POST("/api/v1/tax/groups", { body }));
  }, () => { setForm(null); });
  const edit = (group: TaxGroup | null): void => {
    saver.setProblem(null);
    setForm(group
      ? { id: group.id, kind: group.kind, code: group.code, nameEn: group.name.en ?? "", nameAr: group.name.ar ?? "", isActive: group.isActive }
      : { id: null, kind, code: "", nameEn: "", nameAr: "", isActive: true });
  };
  return (
    <>
      <div className="mt-3 grid gap-3 sm:grid-cols-4">
        <Field label={t("tax.groupKind")}>
          <SelectField value={kind} onChange={(e) => { setKind(e.target.value === "partner" ? "partner" : "item"); }} data-testid="group-kind-filter">
            <option value="item">{t("tax.itemGroup")}</option>
            <option value="partner">{t("tax.partnerGroup")}</option>
          </SelectField>
        </Field>
      </div>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("tax.newGroup")} testId="new-group" />
      <Table aria-label={t("tax.groups")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("tax.code")}</TableHead>
            <TableHead>{t("tax.name")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(groups.data ?? []).map((group) => (
            <TableRow key={group.id} data-testid="group-row">
              <TableCell dir="ltr">
                <bdi>{group.code}</bdi>
                {!group.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell dir="auto">{localized(group.name)}</TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={group.code} onEdit={() => { edit(group); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <FormDialog title={form?.id ? t("tax.editGroup") : t("tax.newGroup")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { saver.mutation.mutate(form); } }} busy={saver.mutation.isPending} problem={saver.problem} testId="save-group">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("tax.code")} required>
              <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} required dir="ltr" disabled={Boolean(form.id)} data-testid="group-code" />
            </Field>
            <Field label={t("tax.nameEn")} required>
              <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("tax.nameAr")} required>
              <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} required dir="rtl" />
            </Field>
            <label className="flex items-center gap-2 self-end pb-2 text-sm">
              <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
          </div>
        ) : null}
      </FormDialog>
    </>
  );
}

// ------------------------------------------------------------------ registrations

interface RegistrationForm {
  id: string | null;
  regimeId: string;
  registrationNumber: string;
  registeredFrom: string;
  isPrimary: boolean;
}

function RegistrationsTab({ companyId }: { companyId: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("tax.setup.manage");
  const regimes = useTaxRegimes();
  const registrations = useTaxRegistrations(companyId);
  const [form, setForm] = useState<RegistrationForm | null>(null);
  const saver = useSaver<RegistrationForm>(["tax-registrations"], async (f) => {
    const body = { companyId, regimeId: f.regimeId, registrationNumber: f.registrationNumber || null, registeredFrom: f.registeredFrom || null, isPrimary: f.isPrimary };
    return f.id ? unwrap(await api.PUT("/api/v1/tax/registrations/{registrationId}", { params: { path: { registrationId: f.id } }, body })) : unwrap(await api.POST("/api/v1/tax/registrations", { body }));
  }, () => { setForm(null); });
  const edit = (registration: { id: string; regimeId: string; registrationNumber: string | null; registeredFrom: string | null; isPrimary: boolean } | null): void => {
    saver.setProblem(null);
    setForm(registration
      ? { id: registration.id, regimeId: registration.regimeId, registrationNumber: registration.registrationNumber ?? "", registeredFrom: registration.registeredFrom ?? "", isPrimary: registration.isPrimary }
      : { id: null, regimeId: (regimes.data ?? [])[0]?.id ?? "", registrationNumber: "", registeredFrom: "", isPrimary: true });
  };
  return (
    <>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("tax.newRegistration")} testId="new-registration" />
      <Table aria-label={t("tax.registrations")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("tax.regime")}</TableHead>
            <TableHead>{t("tax.registrationNumber")}</TableHead>
            <TableHead>{t("tax.registeredFrom")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(registrations.data ?? []).map((registration) => (
            <TableRow key={registration.id} data-testid="registration-row">
              <TableCell dir="ltr">
                <bdi>{registration.regimeCode}</bdi>
                {registration.isPrimary ? <Badge tone="success" className="ms-2">{t("tax.primary")}</Badge> : null}
              </TableCell>
              <TableCell dir="ltr">{registration.registrationNumber ?? "—"}</TableCell>
              <TableCell dir="ltr">{registration.registeredFrom ? formatDate(registration.registeredFrom) : "—"}</TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={registration.regimeCode} onEdit={() => { edit(registration); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <FormDialog title={form?.id ? t("tax.editRegistration") : t("tax.newRegistration")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { saver.mutation.mutate(form); } }} busy={saver.mutation.isPending} problem={saver.problem} testId="save-registration">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("tax.regime")} required>
              <SelectField value={form.regimeId} onChange={(e) => { setForm({ ...form, regimeId: e.target.value }); }} required data-testid="registration-regime">
                <option value="">—</option>
                {(regimes.data ?? []).map((regime) => (
                  <option key={regime.id} value={regime.id}>
                    {regime.code}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.registrationNumber")}>
              <TextField value={form.registrationNumber} onChange={(e) => { setForm({ ...form, registrationNumber: e.target.value }); }} dir="ltr" data-testid="registration-number" />
            </Field>
            <Field label={t("tax.registeredFrom")}>
              <TextField type="date" value={form.registeredFrom} onChange={(e) => { setForm({ ...form, registeredFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <label className="flex items-center gap-2 self-end pb-2 text-sm">
              <input type="checkbox" checked={form.isPrimary} onChange={(e) => { setForm({ ...form, isPrimary: e.target.checked }); }} />
              {t("tax.primary")}
            </label>
          </div>
        ) : null}
      </FormDialog>
    </>
  );
}

// ------------------------------------------------------------------ exemptions

interface ExemptionForm {
  id: string | null;
  partnerId: string;
  regimeId: string;
  taxCodeId: string;
  certificateNumber: string;
  validFrom: string;
  validTo: string;
  notes: string;
}

function ExemptionsTab({ companyId }: { companyId: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("tax.setup.manage");
  const customers = useCompanyCustomers(companyId);
  const exemptions = useTaxExemptions();
  const [form, setForm] = useState<ExemptionForm | null>(null);
  const formRegime = useTaxRegime(form?.regimeId ?? "");
  const regimes = useTaxRegimes();
  const exemptCodes = (formRegime.data?.codes ?? []).filter((code) => code.treatment !== "standard");
  const saver = useSaver<ExemptionForm>(["tax-exemptions"], async (f) => {
    const body = { partnerId: f.partnerId, regimeId: f.regimeId, taxCodeId: f.taxCodeId, certificateNumber: f.certificateNumber, validFrom: f.validFrom, validTo: f.validTo || null, notes: f.notes || null };
    return f.id ? unwrap(await api.PUT("/api/v1/tax/exemptions/{exemptionId}", { params: { path: { exemptionId: f.id } }, body })) : unwrap(await api.POST("/api/v1/tax/exemptions", { body }));
  }, () => { setForm(null); });
  const remove = useSaver<string>(["tax-exemptions"], async (id) => { unwrap(await api.DELETE("/api/v1/tax/exemptions/{exemptionId}", { params: { path: { exemptionId: id } } })); }, () => undefined);
  const edit = (exemption: { id: string; partnerId: string; regimeId: string; taxCodeId: string; certificateNumber: string; validFrom: string; validTo: string | null; notes: string | null } | null): void => {
    saver.setProblem(null);
    setForm(exemption
      ? { id: exemption.id, partnerId: exemption.partnerId, regimeId: exemption.regimeId, taxCodeId: exemption.taxCodeId, certificateNumber: exemption.certificateNumber, validFrom: exemption.validFrom, validTo: exemption.validTo ?? "", notes: exemption.notes ?? "" }
      : { id: null, partnerId: "", regimeId: (regimes.data ?? [])[0]?.id ?? "", taxCodeId: "", certificateNumber: "", validFrom: "", validTo: "", notes: "" });
  };
  return (
    <>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("tax.newExemption")} testId="new-exemption" />
      <Table aria-label={t("tax.exemptions")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("tax.partner")}</TableHead>
            <TableHead>{t("tax.regime")}</TableHead>
            <TableHead>{t("tax.code")}</TableHead>
            <TableHead>{t("tax.certificateNumber")}</TableHead>
            <TableHead>{t("tax.validity")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(exemptions.data ?? []).map((exemption) => (
            <TableRow key={exemption.id} data-testid="exemption-row">
              <TableCell dir="auto">
                {exemption.partnerCode} · {localized(exemption.partnerName)}
              </TableCell>
              <TableCell dir="ltr">{exemption.regimeCode}</TableCell>
              <TableCell dir="ltr">
                <bdi>{exemption.taxCode}</bdi>
              </TableCell>
              <TableCell dir="ltr">{exemption.certificateNumber}</TableCell>
              <TableCell dir="ltr">
                {formatDate(exemption.validFrom)} – {exemption.validTo ? formatDate(exemption.validTo) : t("tax.always")}
              </TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={exemption.certificateNumber} onEdit={() => { edit(exemption); }} onDelete={() => { remove.mutation.mutate(exemption.id); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <FormDialog title={form?.id ? t("tax.editExemption") : t("tax.newExemption")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { saver.mutation.mutate(form); } }} busy={saver.mutation.isPending} problem={saver.problem} testId="save-exemption">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("tax.partner")} required>
              <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required data-testid="exemption-partner">
                <option value="">—</option>
                {(customers.data ?? []).map((c) => (
                  <option key={c.partnerId} value={c.partnerId}>
                    {c.partnerCode} · {localized(c.partnerName)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.regime")} required>
              <SelectField value={form.regimeId} onChange={(e) => { setForm({ ...form, regimeId: e.target.value, taxCodeId: "" }); }} required>
                <option value="">—</option>
                {(regimes.data ?? []).map((regime) => (
                  <option key={regime.id} value={regime.id}>
                    {regime.code}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.code")} required>
              <SelectField value={form.taxCodeId} onChange={(e) => { setForm({ ...form, taxCodeId: e.target.value }); }} required data-testid="exemption-code">
                <option value="">—</option>
                {exemptCodes.map((code) => (
                  <option key={code.id} value={code.id}>
                    {code.code} · {t(`tax.value.${code.treatment}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("tax.certificateNumber")} required>
              <TextField value={form.certificateNumber} onChange={(e) => { setForm({ ...form, certificateNumber: e.target.value }); }} required dir="ltr" data-testid="exemption-certificate" />
            </Field>
            <Field label={t("tax.validFrom")} required>
              <TextField type="date" value={form.validFrom} onChange={(e) => { setForm({ ...form, validFrom: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("tax.validTo")}>
              <TextField type="date" value={form.validTo} onChange={(e) => { setForm({ ...form, validTo: e.target.value }); }} dir="ltr" />
            </Field>
            <div className="sm:col-span-2">
              <Field label={t("tax.notes")}>
                <TextField value={form.notes} onChange={(e) => { setForm({ ...form, notes: e.target.value }); }} dir="auto" />
              </Field>
            </div>
          </div>
        ) : null}
      </FormDialog>
    </>
  );
}
