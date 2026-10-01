import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Play, Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "../common";
import { DocStatus, KeyValues, Tabs } from "../inventory/shared";
import { RoutineRuns } from "./RoutineRuns";
import { Amount, CompanySelect, today, useCompanies, useCompanySelection } from "./shared";

type Template = components["schemas"]["RecurringTemplateSummary"];
type Schedule = components["schemas"]["DeferralScheduleSummary"];

interface TemplateLine {
  accountCode: string;
  debit: string;
  credit: string;
}

interface TemplateForm {
  code: string;
  nameEn: string;
  nameAr: string;
  cron: string;
  currency: string;
  amountMode: string;
  baseAmount: string;
  requiresReview: boolean;
  autoReverse: boolean;
  isActive: boolean;
  lines: TemplateLine[];
}

const amountModes = ["fixed", "variable", "percentage"];
const deferralKinds = ["prepayment", "accrual", "deferred_revenue"];
const deferralMethods = ["straight_line", "daily"];
const emptyLine: TemplateLine = { accountCode: "", debit: "", credit: "" };

/** Recurring journals and deferral schedules (roadmap 2.3): the templates that generate journals on a schedule, the prepayments, accruals and deferred revenue released period by period, and the routine that runs them. */
export function RoutinesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const currency = company?.functionalCurrency ?? "";
  const [tab, setTab] = useState("recurring");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [editing, setEditing] = useState<{ id: string | null; form: TemplateForm } | null>(null);
  const [openTemplate, setOpenTemplate] = useState<string | null>(null);
  const [runDate, setRunDate] = useState(today());
  const [baseAmount, setBaseAmount] = useState("");
  const [deferral, setDeferral] = useState<components["schemas"]["SaveDeferralRequest"] | null>(null);
  const [openSchedule, setOpenSchedule] = useState<string | null>(null);

  const templates = useQuery({
    queryKey: ["recurring", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/recurring-templates", { params: { path: { companyId } } })),
  });
  const schedules = useQuery({
    queryKey: ["deferrals", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/deferrals", { params: { path: { companyId } } })),
  });
  const template = templates.data?.find((x) => x.id === openTemplate);
  const schedule = schedules.data?.find((x) => x.id === openSchedule);

  const saveTemplate = useMutation({
    mutationFn: async (input: { id: string | null; form: TemplateForm }) => {
      const f = input.form;
      const body = {
        code: f.code,
        name: { en: f.nameEn, ...(f.nameAr ? { ar: f.nameAr } : {}) },
        cron: f.cron,
        currency: f.currency,
        amountMode: f.amountMode,
        baseAmount: f.amountMode === "percentage" && f.baseAmount ? f.baseAmount : null,
        requiresReview: f.requiresReview,
        autoReverse: f.autoReverse,
        isActive: f.isActive,
        lines: f.lines.filter((l) => l.accountCode.trim()).map((l) => ({ accountCode: l.accountCode.trim(), debit: l.debit || "0", credit: l.credit || "0" })),
      };
      return input.id
        ? unwrap(await api.PUT("/api/v1/accounting/recurring-templates/{templateId}", { params: { path: { templateId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/recurring-templates", { params: { path: { companyId } }, body }));
    },
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["recurring"] });
      setOpenTemplate(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const generate = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/recurring-templates/{templateId}/generate", { params: { path: { templateId: openTemplate ?? "" } }, body: { runDate: runDate || null, baseAmount: baseAmount || null } })),
    onSuccess: async () => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["recurring"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const previewDeferral = useMutation({
    mutationFn: async (body: components["schemas"]["SaveDeferralRequest"]) => unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/deferrals/preview", { params: { path: { companyId } }, body })),
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const saveDeferral = useMutation({
    mutationFn: async (body: components["schemas"]["SaveDeferralRequest"]) => unwrap(await api.POST("/api/v1/accounting/companies/{companyId}/deferrals", { params: { path: { companyId } }, body })),
    onSuccess: async (saved) => {
      setDeferral(null);
      setProblem(null);
      previewDeferral.reset();
      await queryClient.invalidateQueries({ queryKey: ["deferrals"] });
      setOpenSchedule(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const scheduleAction = useMutation({
    mutationFn: async (action: "post-due" | "cancel") =>
      action === "post-due"
        ? unwrap(await api.POST("/api/v1/accounting/deferrals/{scheduleId}/post-due", { params: { path: { scheduleId: openSchedule ?? "" } } }))
        : unwrap(await api.POST("/api/v1/accounting/deferrals/{scheduleId}/cancel", { params: { path: { scheduleId: openSchedule ?? "" } } })),
    onSuccess: async () => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["deferrals"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const runRoutines = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/accounting/routines/run", { params: { query: { companyId } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["recurring"] });
      await queryClient.invalidateQueries({ queryKey: ["deferrals"] });
      await queryClient.invalidateQueries({ queryKey: ["routine-runs"] });
    },
  });

  const templateColumns = useMemo<ColumnDef<Template, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("routines.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("routines.name"), size: 220 },
      { id: "cron", accessorKey: "cron", header: t("routines.schedule"), size: 130, cell: ({ row }) => <span dir="ltr" className="font-mono text-xs">{row.original.cron}</span> },
      { id: "next", accessorKey: "nextRunOn", header: t("routines.nextRun"), size: 120, cell: ({ row }) => formatDate(row.original.nextRunOn) },
      { id: "last", accessorKey: "lastGeneratedOn", header: t("routines.lastGenerated"), size: 130, cell: ({ row }) => formatDate(row.original.lastGeneratedOn) },
      { id: "mode", accessorKey: "amountMode", header: t("routines.amountMode"), size: 120, cell: ({ row }) => t(`routines.amountModes.${row.original.amountMode}`) },
      { id: "status", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <DocStatus status={row.original.isActive ? "active" : "inactive"} /> },
    ],
    [t],
  );
  const scheduleColumns = useMemo<ColumnDef<Schedule, unknown>[]>(
    () => [
      { id: "kind", accessorKey: "kind", header: t("routines.kind"), size: 150, cell: ({ row }) => t(`routines.kinds.${row.original.kind}`) },
      { id: "description", accessorFn: (row) => localized(row.description), header: t("routines.description"), size: 200 },
      { id: "accounts", accessorFn: (row) => `${row.balanceAccountCode} → ${row.targetAccountCode}`, header: t("routines.accounts"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.balanceAccountCode} → {row.original.targetAccountCode}</span> },
      { id: "starts", accessorKey: "startsOn", header: t("routines.startsOn"), size: 110, cell: ({ row }) => formatDate(row.original.startsOn) },
      { id: "periods", accessorKey: "periods", header: t("routines.periods"), size: 80 },
      { id: "total", accessorKey: "totalAmount", header: t("routines.total"), size: 120, cell: ({ row }) => <Amount value={row.original.totalAmount} /> },
      { id: "remaining", accessorKey: "remainingAmount", header: t("routines.remaining"), size: 120, cell: ({ row }) => <Amount value={row.original.remainingAmount} /> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const setForm = (patch: Partial<TemplateForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const updateLine = (index: number, patch: Partial<TemplateLine>): void => {
    if (editing) {
      setForm({ lines: editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
    }
  };
  const newTemplate = (): TemplateForm => ({ code: "", nameEn: "", nameAr: "", cron: "0 0 1 * *", currency, amountMode: "fixed", baseAmount: "", requiresReview: true, autoReverse: false, isActive: true, lines: [{ ...emptyLine }, { ...emptyLine }] });
  const editTemplate = (x: Template): TemplateForm => ({ code: x.code, nameEn: x.name.en ?? "", nameAr: x.name.ar ?? "", cron: x.cron, currency: x.currency, amountMode: x.amountMode, baseAmount: x.baseAmount === null ? "" : String(x.baseAmount), requiresReview: x.requiresReview, autoReverse: x.autoReverse, isActive: x.isActive, lines: x.lines.map((l) => ({ accountCode: l.accountCode, debit: String(l.debit), credit: String(l.credit) })) });
  const submitTemplate = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      saveTemplate.mutate(editing);
    }
  };
  const submitDeferral = (event: FormEvent): void => {
    event.preventDefault();
    if (deferral) {
      saveDeferral.mutate(deferral);
    }
  };
  const routineCounts = runRoutines.data ? { reversals: runRoutines.data.autoReversals.length, recurring: runRoutines.data.recurringJournals.length, deferrals: runRoutines.data.deferralPostings.length } : null;

  return (
    <>
      <PageHeader
        title={t("nav.routines")}
        description={t("routines.pageDescription")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { runRoutines.mutate(); }} loading={runRoutines.isPending} disabled={!companyId} data-testid="run-routines">
              <Play aria-hidden="true" />
              {t("routines.runNow")}
            </Button>
            {tab === "runs" ? null : tab === "recurring" ? (
              <Button onClick={() => { setProblem(null); setEditing({ id: null, form: newTemplate() }); }} disabled={!companyId} data-testid="new-template">
                <Plus aria-hidden="true" />
                {t("routines.newTemplate")}
              </Button>
            ) : (
              <Button onClick={() => { setProblem(null); previewDeferral.reset(); setDeferral({ kind: "prepayment", balanceAccountCode: "", targetAccountCode: "", startsOn: today(), periods: 12, totalAmount: "", currency, method: "straight_line", description: null }); }} disabled={!companyId} data-testid="new-deferral">
                <Plus aria-hidden="true" />
                {t("routines.newDeferral")}
              </Button>
            )}
          </>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
      </div>
      {routineCounts ? (
        <p className="mb-3 text-sm text-fg-muted" role="status" data-testid="routines-result">
          {t("routines.ran", routineCounts)}
        </p>
      ) : null}
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { id: "recurring", label: t("routines.recurring"), testId: "tab-recurring" },
          { id: "deferrals", label: t("routines.deferrals"), testId: "tab-deferrals" },
          { id: "runs", label: t("routines.runs"), testId: "tab-runs" },
        ]}
      />
      {tab === "runs" ? (
        <RoutineRuns companyId={companyId} />
      ) : tab === "recurring" ? (
        <DataGrid<Template> label="routines.recurring" columns={templateColumns} data={templates.data ?? []} rowKey={(row) => row.id} loading={templates.isPending && Boolean(companyId)} onOpen={(row) => { setProblem(null); generate.reset(); setOpenTemplate(row.id); }} emptyTitle={t("routines.noTemplates")} emptyDescription={t("routines.noTemplatesHint")} />
      ) : (
        <DataGrid<Schedule> label="routines.deferrals" columns={scheduleColumns} data={schedules.data ?? []} rowKey={(row) => row.id} loading={schedules.isPending && Boolean(companyId)} onOpen={(row) => { setProblem(null); setOpenSchedule(row.id); }} emptyTitle={t("routines.noDeferrals")} emptyDescription={t("routines.noDeferralsHint")} />
      )}

      <Dialog open={Boolean(template)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenTemplate(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">{template ? `${template.code} · ${localized(template.name)}` : ""}</DialogTitle>
          </DialogHeader>
          {template ? (
            <div className="flex flex-col gap-4" data-testid="template-detail">
              <KeyValues
                entries={[
                  [t("routines.schedule"), <span key="c" dir="ltr" className="font-mono text-xs">{template.cron}</span>],
                  [t("routines.nextRun"), formatDate(template.nextRunOn)],
                  [t("routines.amountMode"), t(`routines.amountModes.${template.amountMode}`)],
                  [t("routines.requiresReview"), template.requiresReview ? t("common.yes") : t("common.no")],
                  [t("routines.autoReverse"), template.autoReverse ? t("common.yes") : t("common.no")],
                ]}
              />
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("routines.account")}</TableHead>
                    <TableHead className="text-end">{t("routines.debit")}</TableHead>
                    <TableHead className="text-end">{t("routines.credit")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {template.lines.map((l, i) => (
                    <TableRow key={`${l.accountCode}-${String(i)}`}>
                      <TableCell dir="ltr">{l.accountCode}</TableCell>
                      <TableNumberCell><Amount value={l.debit} /></TableNumberCell>
                      <TableNumberCell><Amount value={l.credit} /></TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("routines.runDate")}>
                  <TextField type="date" value={runDate} onChange={(e) => { setRunDate(e.target.value); }} dir="ltr" />
                </Field>
                {template.amountMode === "fixed" ? null : (
                  <Field label={template.amountMode === "percentage" ? t("routines.baseAmount") : t("routines.amount")}>
                    <TextField inputMode="decimal" value={baseAmount} onChange={(e) => { setBaseAmount(e.target.value); }} dir="ltr" />
                  </Field>
                )}
              </div>
              {generate.data ? (
                <p className="text-sm" role="status" data-testid="generated">
                  {t("routines.generated", { number: generate.data.number })}
                </p>
              ) : null}
              <FormError message={problem?.message ?? null} />
              <DialogFooter>
                <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: template.id, form: editTemplate(template) }); setOpenTemplate(null); }}>
                  {t("common.edit")}
                </Button>
                <Button onClick={() => { generate.mutate(); }} loading={generate.isPending} data-testid="generate-journal">
                  {t("routines.generate")}
                </Button>
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {editing ? (
            <form onSubmit={submitTemplate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.id ? t("routines.editTemplate") : t("routines.newTemplate")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("routines.code")} required error={problem?.fields.code}>
                  <TextField value={editing.form.code} onChange={(e) => { setForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="template-code" />
                </Field>
                <Field label={t("routines.nameEn")} required error={problem?.fields.name}>
                  <TextField value={editing.form.nameEn} onChange={(e) => { setForm({ nameEn: e.target.value }); }} required data-testid="template-name-en" />
                </Field>
                <Field label={t("routines.nameAr")}>
                  <TextField value={editing.form.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <Field label={t("routines.schedule")} required error={problem?.fields.cron} description={t("routines.cronHint")}>
                  <TextField value={editing.form.cron} onChange={(e) => { setForm({ cron: e.target.value }); }} required dir="ltr" data-testid="template-cron" />
                </Field>
                <Field label={t("routines.amountMode")}>
                  <SelectField value={editing.form.amountMode} onChange={(e) => { setForm({ amountMode: e.target.value }); }}>
                    {amountModes.map((m) => (
                      <option key={m} value={m}>
                        {t(`routines.amountModes.${m}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                {editing.form.amountMode === "percentage" ? (
                  <Field label={t("routines.baseAmount")}>
                    <TextField inputMode="decimal" value={editing.form.baseAmount} onChange={(e) => { setForm({ baseAmount: e.target.value }); }} dir="ltr" />
                  </Field>
                ) : null}
              </div>
              <p className="text-sm text-fg-muted">{t(`routines.amountModeHints.${editing.form.amountMode}`)}</p>
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={editing.form.requiresReview} onChange={(e) => { setForm({ requiresReview: e.target.checked }); }} />
                  {t("routines.requiresReview")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={editing.form.autoReverse} onChange={(e) => { setForm({ autoReverse: e.target.checked }); }} />
                  {t("routines.autoReverse")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={editing.form.isActive} onChange={(e) => { setForm({ isActive: e.target.checked }); }} />
                  {t("routines.active")}
                </label>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("routines.account")}</TableHead>
                    <TableHead>{t("routines.debit")}</TableHead>
                    <TableHead>{t("routines.credit")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.form.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <TextField value={line.accountCode} onChange={(e) => { updateLine(index, { accountCode: e.target.value }); }} dir="ltr" aria-label={t("routines.account")} data-testid={`template-account-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <TextField inputMode="decimal" value={line.debit} onChange={(e) => { updateLine(index, { debit: e.target.value }); }} dir="ltr" aria-label={t("routines.debit")} data-testid={`template-debit-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <TextField inputMode="decimal" value={line.credit} onChange={(e) => { updateLine(index, { credit: e.target.value }); }} dir="ltr" aria-label={t("routines.credit")} data-testid={`template-credit-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setForm({ lines: editing.form.lines.filter((_, i) => i !== index) }); }}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div>
                <Button type="button" variant="secondary" onClick={() => { setForm({ lines: [...editing.form.lines, { ...emptyLine }] }); }}>
                  <Plus aria-hidden="true" />
                  {t("accounting.addLine")}
                </Button>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveTemplate.isPending} data-testid="save-template">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(schedule)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenSchedule(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">{schedule ? `${t(`routines.kinds.${schedule.kind}`)} · ${schedule.balanceAccountCode} → ${schedule.targetAccountCode}` : ""}</DialogTitle>
          </DialogHeader>
          {schedule ? (
            <div className="flex flex-col gap-4" data-testid="deferral-detail">
              <KeyValues
                entries={[
                  [t("routines.total"), <Amount key="t" value={schedule.totalAmount} />],
                  [t("routines.posted"), <span key="p" data-testid="deferral-posted"><Amount value={schedule.postedAmount} /></span>],
                  [t("routines.remaining"), <Amount key="r" value={schedule.remainingAmount} />],
                  [t("routines.method"), t(`routines.methods.${schedule.method}`)],
                ]}
              />
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("accounting.date")}</TableHead>
                    <TableHead className="text-end">{t("routines.amount")}</TableHead>
                    <TableHead>{t("common.status")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {schedule.lines.map((l) => (
                    <TableRow key={l.sequence} data-testid="deferral-line">
                      <TableCell>{String(l.sequence)}</TableCell>
                      <TableCell>{formatDate(l.postingDate)}</TableCell>
                      <TableNumberCell><Amount value={l.amount} /></TableNumberCell>
                      <TableCell><DocStatus status={l.status} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              {schedule.status === "active" ? (
                <DialogFooter>
                  <Button variant="secondary" onClick={() => { scheduleAction.mutate("cancel"); }} loading={scheduleAction.isPending}>
                    {t("routines.cancelSchedule")}
                  </Button>
                  <Button onClick={() => { scheduleAction.mutate("post-due"); }} loading={scheduleAction.isPending} data-testid="post-due">
                    {t("routines.postDue")}
                  </Button>
                </DialogFooter>
              ) : null}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(deferral)} onOpenChange={(isOpen) => { if (!isOpen) { setDeferral(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {deferral ? (
            <form onSubmit={submitDeferral} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("routines.newDeferral")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("routines.kind")}>
                  <SelectField value={deferral.kind} onChange={(e) => { setDeferral({ ...deferral, kind: e.target.value }); }} data-testid="deferral-kind">
                    {deferralKinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`routines.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("routines.balanceAccount")} required error={problem?.fields.balanceAccountCode}>
                  <TextField value={deferral.balanceAccountCode} onChange={(e) => { setDeferral({ ...deferral, balanceAccountCode: e.target.value }); }} required dir="ltr" data-testid="deferral-balance" />
                </Field>
                <Field label={t("routines.targetAccount")} required error={problem?.fields.targetAccountCode}>
                  <TextField value={deferral.targetAccountCode} onChange={(e) => { setDeferral({ ...deferral, targetAccountCode: e.target.value }); }} required dir="ltr" data-testid="deferral-target" />
                </Field>
                <Field label={t("routines.startsOn")} required>
                  <TextField type="date" value={deferral.startsOn} onChange={(e) => { setDeferral({ ...deferral, startsOn: e.target.value }); }} required dir="ltr" />
                </Field>
                <Field label={t("routines.periods")} required error={problem?.fields.periods}>
                  <TextField inputMode="numeric" value={String(deferral.periods)} onChange={(e) => { setDeferral({ ...deferral, periods: Number(e.target.value) || 0 }); }} required dir="ltr" data-testid="deferral-periods" />
                </Field>
                <Field label={t("routines.total")} required error={problem?.fields.totalAmount}>
                  <TextField inputMode="decimal" value={String(deferral.totalAmount)} onChange={(e) => { setDeferral({ ...deferral, totalAmount: e.target.value }); }} required dir="ltr" data-testid="deferral-total" />
                </Field>
                <Field label={t("routines.method")}>
                  <SelectField value={deferral.method} onChange={(e) => { setDeferral({ ...deferral, method: e.target.value }); }}>
                    {deferralMethods.map((m) => (
                      <option key={m} value={m}>
                        {t(`routines.methods.${m}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("routines.description")}>
                  <TextField value={deferral.description?.en ?? ""} onChange={(e) => { setDeferral({ ...deferral, description: e.target.value ? { en: e.target.value } : null }); }} data-testid="deferral-description" />
                </Field>
              </div>
              <p className="text-sm text-fg-muted">{t(`routines.kindHints.${deferral.kind}`)}</p>
              {previewDeferral.data ? (
                <Table data-testid="deferral-preview">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("accounting.date")}</TableHead>
                      <TableHead className="text-end">{t("routines.amount")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {previewDeferral.data.lines.map((l) => (
                      <TableRow key={l.sequence}>
                        <TableCell>{String(l.sequence)}</TableCell>
                        <TableCell>{formatDate(l.postingDate)}</TableCell>
                        <TableNumberCell><Amount value={l.amount} /></TableNumberCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setProblem(null); previewDeferral.mutate(deferral); }} loading={previewDeferral.isPending} data-testid="preview-deferral">
                  {t("routines.preview")}
                </Button>
                <Button type="submit" loading={saveDeferral.isPending} data-testid="save-deferral">
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
