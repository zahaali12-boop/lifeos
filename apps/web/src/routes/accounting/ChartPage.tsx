import { Badge, Button, Checkbox, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableRow, cn } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { ExportChartButton, ImportChartDialog, StatutoryMappingDialog } from "./ChartTools";
import { CompanySelect, today, useCompanies, useCompanySelection } from "./shared";

type Account = components["schemas"]["AccountSummary"];
type DimensionRule = components["schemas"]["DimensionRuleSummary"];

interface AccountForm {
  code: string;
  nameEn: string;
  nameAr: string;
  type: string;
  parentCode: string;
  isHeader: boolean;
  isControl: boolean;
  subledgerType: string;
  allowManualPosting: boolean;
  defaultRole: string;
  currencyRestriction: string;
  isActive: boolean;
}

const types = ["asset", "liability", "equity", "revenue", "expense"];
const subledgers = ["", "AR", "AP", "INV", "FA", "BANK", "PDC", "GRNI", "IC", "WHT"];
const rules = ["required", "optional", "blocked"];

const emptyAccount: AccountForm = { code: "", nameEn: "", nameAr: "", type: "asset", parentCode: "", isHeader: false, isControl: false, subledgerType: "", allowManualPosting: true, defaultRole: "", currencyRestriction: "", isActive: true };

function toForm(account: Account): AccountForm {
  return {
    code: account.code,
    nameEn: account.name.en ?? "",
    nameAr: account.name.ar ?? "",
    type: account.type,
    parentCode: account.parentCode ?? "",
    isHeader: account.isHeader,
    isControl: account.isControl,
    subledgerType: account.subledgerType ?? "",
    allowManualPosting: account.allowManualPosting,
    defaultRole: account.defaultRole ?? "",
    currencyRestriction: account.currencyRestriction ?? "",
    isActive: account.isActive,
  };
}

function toRequest(form: AccountForm): components["schemas"]["SaveAccountRequest"] {
  return {
    code: form.code.trim(),
    name: { en: form.nameEn, ...(form.nameAr ? { ar: form.nameAr } : {}) },
    type: form.type,
    parentCode: form.parentCode || null,
    subtype: "",
    isHeader: form.isHeader,
    isControl: form.isControl,
    subledgerType: form.isControl && form.subledgerType ? form.subledgerType : null,
    currencyRestriction: form.currencyRestriction || null,
    allowManualPosting: form.allowManualPosting,
    revalueFx: false,
    defaultRole: form.defaultRole || null,
    isActive: form.isActive,
  };
}

/** The chart of accounts of a company as a tree, the account editor, and the dimension rules of an account. */
export function ChartPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const chartId = company?.chartId ?? null;
  const [filter, setFilter] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ id: string | null; form: AccountForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [template, setTemplate] = useState("IFRS_SME");
  const [ruleDraft, setRuleDraft] = useState({ dimensionCode: "", rule: "required" });
  const [tool, setTool] = useState<"import" | "mapping" | null>(null);

  const templates = useQuery({ queryKey: ["chart-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/chart-templates")) });
  const chart = useQuery({
    queryKey: ["chart", chartId],
    enabled: Boolean(chartId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/charts/{chartId}", { params: { path: { chartId: chartId ?? "" }, query: { expand: "accounts" } } })),
  });
  const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const accountRules = useQuery({
    queryKey: ["dimension-rules", selectedId],
    enabled: Boolean(selectedId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/accounts/{accountId}/dimension-rules", { params: { path: { accountId: selectedId ?? "" } } })),
  });

  const createChart = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/charts/from-template", { body: { templateCode: template, code: `CH-${company?.code ?? "MAIN"}-${today().replaceAll("-", "")}`, companyId, shared: false } })),
    onSuccess: async () => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["companies"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const saveAccount = useMutation({
    mutationFn: async (input: { id: string | null; form: AccountForm }) =>
      input.id
        ? unwrap(await api.PUT("/api/v1/accounting/accounts/{accountId}", { params: { path: { accountId: input.id } }, body: toRequest(input.form) }))
        : unwrap(await api.POST("/api/v1/accounting/charts/{chartId}/accounts", { params: { path: { chartId: chartId ?? "" } }, body: toRequest(input.form) })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      setSelectedId(saved.id);
      await queryClient.invalidateQueries({ queryKey: ["chart", chartId] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const saveRules = useMutation({
    mutationFn: async (next: DimensionRule[]) =>
      unwrap(await api.PUT("/api/v1/accounting/accounts/{accountId}/dimension-rules", { params: { path: { accountId: selectedId ?? "" } }, body: next.map((r) => ({ dimensionCode: r.dimensionCode, rule: r.rule, defaultValueId: r.defaultValueId })) })),
    onSuccess: async () => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["dimension-rules", selectedId] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const accounts = useMemo(() => {
    const all = chart.data?.accounts ?? [];
    const needle = filter.trim().toLowerCase();
    return needle ? all.filter((a) => a.code.includes(needle) || localized(a.name).toLowerCase().includes(needle)) : all;
  }, [chart.data, filter]);
  const selected = chart.data?.accounts?.find((a) => a.id === selectedId) ?? null;

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      saveAccount.mutate(editing);
    }
  };
  const setForm = (patch: Partial<AccountForm>): void => {
    if (editing) {
      setEditing({ ...editing, form: { ...editing.form, ...patch } });
    }
  };

  return (
    <>
      <PageHeader
        title={t("accounting.chart")}
        description={t("accounting.chartDescription")}
        actions={
          chartId ? (
            <div className="flex flex-wrap items-center gap-2">
              <ExportChartButton chartId={chartId} fileName={`chart-${company?.code ?? "company"}.csv`} />
              <Button variant="secondary" onClick={() => { setTool("import"); }} data-testid="import-chart-open">
                {t("chartTools.import")}
              </Button>
              <Button variant="secondary" onClick={() => { setTool("mapping"); }} data-testid="statutory-mapping-open">
                {t("chartTools.mapping")}
              </Button>
              <Button onClick={() => { setProblem(null); setEditing({ id: null, form: { ...emptyAccount, parentCode: selected?.isHeader ? selected.code : "", type: selected?.type ?? "asset" } }); }} data-testid="new-account">
                <Plus aria-hidden="true" />
                {t("accounting.newAccount")}
              </Button>
            </div>
          ) : null
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={(id) => { setCompanyId(id); setSelectedId(null); }} />
        {chartId ? (
          <Field label={t("common.search")}>
            <Input type="search" value={filter} onChange={(e) => { setFilter(e.target.value); }} aria-label={t("common.search")} />
          </Field>
        ) : null}
      </div>
      <FormError message={problem && !editing ? problem.message : null} />
      {company && !chartId ? (
        <form
          className="flex flex-wrap items-end gap-3 rounded-lg border border-border bg-surface p-4"
          onSubmit={(event) => {
            event.preventDefault();
            createChart.mutate();
          }}
        >
          <p className="w-full text-sm text-fg-muted">{t("accounting.noChartYet", { company: company.code })}</p>
          <Field label={t("accounting.template")}>
            <SelectField value={template} onChange={(e) => { setTemplate(e.target.value); }} data-testid="chart-template">
              {(templates.data ?? []).map((tpl) => (
                <option key={tpl.code} value={tpl.code}>
                  {tpl.code} · {localized(tpl.name)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Button type="submit" loading={createChart.isPending} data-testid="create-chart">
            {t("accounting.createChart")}
          </Button>
        </form>
      ) : null}
      {chartId ? (
        <div className="grid gap-4 lg:grid-cols-[2fr_1fr]">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("accounting.accountCode")}</TableHead>
                <TableHead>{t("accounting.accountName")}</TableHead>
                <TableHead>{t("accounting.type")}</TableHead>
                <TableHead>{t("accounting.flags")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {accounts.map((account) => (
                <TableRow key={account.id} className={cn("cursor-pointer", selectedId === account.id && "bg-selection")} onClick={() => { setSelectedId(account.id); }} data-testid="account-row" aria-selected={selectedId === account.id}>
                  <TableCell dir="ltr" style={{ paddingInlineStart: `${12 + Number(account.level) * 16}px` }} className={cn(account.isHeader && "font-semibold")}>
                    <button type="button" className="underline-offset-2 hover:underline" onClick={() => { setSelectedId(account.id); }}>
                      {account.code}
                    </button>
                  </TableCell>
                  <TableCell className={cn(account.isHeader && "font-semibold")}>{localized(account.name)}</TableCell>
                  <TableCell>{t(`accounting.types.${account.type}`)}</TableCell>
                  <TableCell className="space-x-1">
                    {account.isHeader ? <Badge tone="neutral">{t("accounting.header")}</Badge> : null}
                    {account.isControl ? <Badge tone="accent">{account.subledgerType ?? t("accounting.control")}</Badge> : null}
                    {!account.allowManualPosting && !account.isHeader ? <Badge tone="neutral">{t("accounting.documentsOnly")}</Badge> : null}
                    {!account.isActive ? <Badge tone="danger">{t("common.inactive")}</Badge> : null}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <aside className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4" aria-label={t("accounting.accountDetails")}>
            {selected ? (
              <>
                <div>
                  <h2 className="text-base font-semibold" dir="auto">
                    <span dir="ltr">{selected.code}</span> {localized(selected.name)}
                  </h2>
                  <p className="text-sm text-fg-muted">
                    {t(`accounting.types.${selected.type}`)}
                    {selected.defaultRole ? ` · ${selected.defaultRole}` : ""}
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: selected.id, form: toForm(selected) }); }} data-testid="edit-account">
                    {t("accounting.edit")}
                  </Button>
                  {!selected.isHeader ? (
                    <Button variant="secondary" asChild>
                      <Link to="/accounting/ledger" search={{ companyId, accountId: selected.id, to: today() }}>
                        {t("accounting.ledger")}
                      </Link>
                    </Button>
                  ) : null}
                </div>
                {!selected.isHeader ? (
                  <section className="flex flex-col gap-2" data-testid="dimension-rules">
                    <h3 className="text-sm font-semibold">{t("accounting.dimensionRules")}</h3>
                    {(accountRules.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("accounting.noDimensionRules")}</p> : null}
                    <ul className="flex flex-col gap-1 text-sm">
                      {(accountRules.data ?? []).map((rule) => (
                        <li key={rule.dimensionId} className="flex items-center justify-between gap-2">
                          <span>
                            <span dir="ltr">{rule.dimensionCode}</span> · {t(`accounting.rules.${rule.rule}`)}
                          </span>
                          <Button variant="ghost" size="sm" onClick={() => { saveRules.mutate((accountRules.data ?? []).filter((r) => r.dimensionId !== rule.dimensionId)); }}>
                            {t("common.delete")}
                          </Button>
                        </li>
                      ))}
                    </ul>
                    <form
                      className="flex flex-wrap items-end gap-2"
                      onSubmit={(event) => {
                        event.preventDefault();
                        if (ruleDraft.dimensionCode) {
                          saveRules.mutate([...(accountRules.data ?? []).filter((r) => r.dimensionCode !== ruleDraft.dimensionCode), { dimensionId: "", dimensionCode: ruleDraft.dimensionCode, rule: ruleDraft.rule, defaultValueId: null }]);
                        }
                      }}
                    >
                      <Field label={t("accounting.dimension")}>
                        <SelectField value={ruleDraft.dimensionCode} onChange={(e) => { setRuleDraft({ ...ruleDraft, dimensionCode: e.target.value }); }} data-testid="rule-dimension">
                          <option value="">—</option>
                          {(dimensions.data ?? []).map((d) => (
                            <option key={d.id} value={d.code}>
                              {localized(d.name)}
                            </option>
                          ))}
                        </SelectField>
                      </Field>
                      <Field label={t("accounting.rule")}>
                        <SelectField value={ruleDraft.rule} onChange={(e) => { setRuleDraft({ ...ruleDraft, rule: e.target.value }); }}>
                          {rules.map((r) => (
                            <option key={r} value={r}>
                              {t(`accounting.rules.${r}`)}
                            </option>
                          ))}
                        </SelectField>
                      </Field>
                      <Button type="submit" variant="secondary" loading={saveRules.isPending} data-testid="add-rule">
                        {t("accounting.addRule")}
                      </Button>
                    </form>
                  </section>
                ) : null}
              </>
            ) : (
              <p className="text-sm text-fg-muted">{t("accounting.selectAccount")}</p>
            )}
          </aside>
        </div>
      ) : null}

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {editing ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.id ? t("accounting.editAccount") : t("accounting.newAccount")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("accounting.accountCode")} required error={problem?.fields.code}>
                  <TextField value={editing.form.code} onChange={(e) => { setForm({ code: e.target.value }); }} required dir="ltr" data-testid="account-code" />
                </Field>
                <Field label={t("accounting.parentCode")} error={problem?.fields.parentCode}>
                  <TextField value={editing.form.parentCode} onChange={(e) => { setForm({ parentCode: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("accounting.nameEn")} required error={problem?.fields.name}>
                  <TextField value={editing.form.nameEn} onChange={(e) => { setForm({ nameEn: e.target.value }); }} required data-testid="account-name-en" />
                </Field>
                <Field label={t("accounting.nameAr")}>
                  <TextField value={editing.form.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" />
                </Field>
                <Field label={t("accounting.type")} required>
                  <SelectField value={editing.form.type} onChange={(e) => { setForm({ type: e.target.value }); }}>
                    {types.map((type) => (
                      <option key={type} value={type}>
                        {t(`accounting.types.${type}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.subledger")} error={problem?.fields.subledgerType}>
                  <SelectField value={editing.form.subledgerType} onChange={(e) => { setForm({ subledgerType: e.target.value, isControl: Boolean(e.target.value) }); }}>
                    {subledgers.map((s) => (
                      <option key={s} value={s}>
                        {s || t("accounting.notControl")}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.defaultRole")} error={problem?.fields.defaultRole}>
                  <TextField value={editing.form.defaultRole} onChange={(e) => { setForm({ defaultRole: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("accounting.currencyRestriction")} error={problem?.fields.currencyRestriction}>
                  <TextField value={editing.form.currencyRestriction} onChange={(e) => { setForm({ currencyRestriction: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                </Field>
              </div>
              <div className="flex flex-wrap gap-6 text-sm">
                <label className="flex items-center gap-2">
                  <Checkbox checked={editing.form.isHeader} onCheckedChange={(checked) => { setForm({ isHeader: checked === true }); }} />
                  {t("accounting.header")}
                </label>
                <label className="flex items-center gap-2">
                  <Checkbox checked={editing.form.allowManualPosting} onCheckedChange={(checked) => { setForm({ allowManualPosting: checked === true }); }} />
                  {t("accounting.allowManualPosting")}
                </label>
                <label className="flex items-center gap-2">
                  <Checkbox checked={editing.form.isActive} onCheckedChange={(checked) => { setForm({ isActive: checked === true }); }} />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveAccount.isPending} data-testid="save-account">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
      {chartId ? <ImportChartDialog chartId={chartId} open={tool === "import"} onOpenChange={(isOpen) => { setTool(isOpen ? "import" : null); }} /> : null}
      {chartId ? <StatutoryMappingDialog chartId={chartId} open={tool === "mapping"} onOpenChange={(isOpen) => { setTool(isOpen ? "mapping" : null); }} /> : null}
    </>
  );
}
