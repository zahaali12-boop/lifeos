import { Badge, Button, Checkbox, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, History, Play, Plus } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { saveFile } from "../../lib/download";
import { formatDateTime, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { periods, scheduleFormats, widgetSources } from "../dashboards/shared";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useReports } from "../reports/shared";
import { layoutKey, layoutRef, useLayouts } from "../statements/shared";

type Schedule = components["schemas"]["ReportScheduleSummary"];
type Run = components["schemas"]["ReportRunSummary"];

/** The cadences a schedule is usually set to, as cron; "custom" takes any five-field cron. */
const cadences = [
  { code: "daily", cron: "0 7 * * *" },
  { code: "weekdays", cron: "0 7 * * 1-5" },
  { code: "weekly", cron: "0 7 * * 1" },
  { code: "monthly", cron: "0 7 1 * *" },
  { code: "quarterly", cron: "0 7 1 1,4,7,10 *" },
] as const;

interface Draft {
  id: string | null;
  nameEn: string;
  nameAr: string;
  source: string;
  reportId: string;
  layout: string;
  period: string;
  cadence: string;
  cron: string;
  timeZone: string;
  format: string;
  members: Set<string>;
  emails: string;
  onlyIfRows: boolean;
  includeClosing: string;
  enabled: boolean;
}

const emptyDraft = (): Draft => ({ id: null, nameEn: "", nameAr: "", source: "report", reportId: "", layout: "", period: "last_month", cadence: "monthly", cron: "0 7 1 * *", timeZone: "", format: "xlsx", members: new Set(), emails: "", onlyIfRows: false, includeClosing: "default", enabled: true });

function draftOf(s: Schedule): Draft {
  const cadence = cadences.find((c) => c.cron === s.cron)?.code ?? "custom";
  return {
    id: s.id,
    nameEn: s.name.en ?? "",
    nameAr: s.name.ar ?? "",
    source: s.source,
    reportId: s.reportId ?? "",
    layout: s.layoutId ?? (s.layoutCode ? `shipped:${s.layoutCode}` : ""),
    period: s.period,
    cadence,
    cron: s.cron,
    timeZone: s.timeZone,
    format: s.format,
    members: new Set(s.recipients.filter((r) => r.membershipId).map((r) => r.membershipId ?? "")),
    emails: s.recipients.filter((r) => r.email).map((r) => r.email ?? "").join(", "),
    onlyIfRows: s.onlyIfRows,
    includeClosing: s.includeClosing === null ? "default" : s.includeClosing ? "include" : "exclude",
    enabled: s.enabled,
  };
}

function statusTone(status: string | null | undefined): "success" | "danger" | "warning" | "neutral" {
  switch (status) {
    case "sent": return "success";
    case "failed": return "danger";
    case "skipped": return "warning";
    default: return "neutral";
  }
}

/** Scheduled reports (founder programme item 9d): a saved report or a statement sent on a cadence as a file, run now on demand, with every run's outcome and file kept. */
export function SchedulesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const canManage = can("reporting.report.manage");
  const schedules = useQuery({
    queryKey: ["report-schedules", companyId],
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/schedules", { params: { query: companyId ? { companyId } : {} } })),
  });
  const reports = useReports();
  const layouts = useLayouts(companyId);
  const members = useQuery({ queryKey: ["members"], queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [runsOf, setRunsOf] = useState<Schedule | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const runs = useQuery({
    queryKey: ["report-runs", runsOf?.id],
    enabled: runsOf !== null,
    refetchInterval: 3_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/schedules/{scheduleId}/runs", { params: { path: { scheduleId: runsOf?.id ?? "" } } })),
  });

  // The run history polls while open; the list's last run follows it, and once more when the dialog closes.
  const runsData = runs.data;
  useEffect(() => {
    if (runsData) {
      void queryClient.invalidateQueries({ queryKey: ["report-schedules"] });
    }
  }, [runsData, queryClient]);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["report-schedules"] });
    await queryClient.invalidateQueries({ queryKey: ["report-runs"] });
  };
  const body = (d: Draft): components["schemas"]["SaveReportScheduleRequest"] => ({
    name: { en: d.nameEn, ar: d.nameAr },
    companyId,
    source: d.source,
    reportId: d.source === "report" && d.reportId ? d.reportId : null,
    ...(d.source === "statement" ? layoutRef(d.layout) : {}),
    period: d.period,
    cron: d.cadence === "custom" ? d.cron : cadences.find((c) => c.code === d.cadence)?.cron ?? d.cron,
    timeZone: d.timeZone || null,
    format: d.format,
    recipients: [
      ...[...d.members].map((membershipId) => ({ membershipId, email: null })),
      ...d.emails.split(/[,;\s]+/).map((e) => e.trim()).filter(Boolean).map((email) => ({ membershipId: null, email })),
    ],
    onlyIfRows: d.onlyIfRows,
    includeClosing: d.includeClosing === "default" ? null : d.includeClosing === "include",
    enabled: d.enabled,
  });
  const save = useMutation({
    mutationFn: async (d: Draft) => (d.id
      ? unwrap(await api.PUT("/api/v1/reporting/schedules/{scheduleId}", { params: { path: { scheduleId: d.id } }, body: body(d) }))
      : unwrap(await api.POST("/api/v1/reporting/schedules", { body: body(d) }))),
    onSuccess: async () => { setProblem(null); setDraft(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("schedules.saveFailed"))); },
  });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/reporting/schedules/{scheduleId}", { params: { path: { scheduleId: id } } })),
    onSuccess: async () => { setDraft(null); await refresh(); },
  });
  const runNow = useMutation({
    mutationFn: async (s: Schedule) => unwrap(await api.POST("/api/v1/reporting/schedules/{scheduleId}/run", { params: { path: { scheduleId: s.id } } })),
    onSuccess: async (_, s) => { setRunsOf(s); await refresh(); },
  });
  const download = async (run: Run): Promise<void> => {
    const response = await api.GET("/api/v1/reporting/schedules/runs/{runId}/file", { params: { path: { runId: run.id } }, parseAs: "blob" });
    saveFile(unwrap(response), response.response.headers, run.fileName ?? "report");
  };

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (draft) {
      save.mutate(draft);
    }
  };
  const sourceName = (s: Schedule): string => (s.sourceName ? localized(s.sourceName) : "—");
  const cadenceName = (cron: string): string => {
    const known = cadences.find((c) => c.cron === cron);
    return known ? t(`schedules.cadences.${known.code}`) : cron;
  };

  return (
    <div>
      <PageHeader
        title={t("nav.schedules")}
        description={t("schedules.pageDescription")}
        actions={canManage ? <Button onClick={() => { setProblem(null); setDraft(emptyDraft()); }} data-testid="new-schedule"><Plus aria-hidden="true" /> {t("schedules.newSchedule")}</Button> : null}
      />
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>

      <Table data-testid="schedules-table">
        <caption className="sr-only">{t("nav.schedules")}</caption>
        <TableHeader>
          <TableRow>
            <TableHead>{t("schedules.name")}</TableHead>
            <TableHead>{t("schedules.source")}</TableHead>
            <TableHead>{t("schedules.period")}</TableHead>
            <TableHead>{t("schedules.cadence")}</TableHead>
            <TableHead>{t("schedules.format")}</TableHead>
            <TableHead>{t("schedules.nextRun")}</TableHead>
            <TableHead>{t("schedules.lastRun")}</TableHead>
            <TableHead>{t("common.actions")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(schedules.data ?? []).length === 0 ? (
            <TableRow><TableCell colSpan={8} className="text-fg-muted">{schedules.isLoading ? t("common.loading") : t("schedules.empty")}</TableCell></TableRow>
          ) : (schedules.data ?? []).map((s, i) => (
            <TableRow key={s.id} data-testid={`schedule-row-${i + 1}`}>
              <TableCell dir="auto" className="font-medium">
                {localized(s.name)}
                {!s.enabled ? <Badge tone="neutral" className="ms-2">{t("schedules.disabled")}</Badge> : null}
              </TableCell>
              <TableCell dir="auto">{t(`dashboards.sources.${s.source}`)} · {sourceName(s)}</TableCell>
              <TableCell>{t(`dashboards.periods.${s.period}`)}</TableCell>
              <TableCell dir="ltr">{cadenceName(s.cron)}</TableCell>
              <TableCell className="uppercase">{s.format}</TableCell>
              <TableCell>{s.nextRunAt ? formatDateTime(s.nextRunAt) : "—"}</TableCell>
              <TableCell>
                {s.lastRunAt ? formatDateTime(s.lastRunAt) : "—"}
                {s.lastStatus ? <Badge tone={statusTone(s.lastStatus)} className="ms-2" data-testid={`schedule-${i + 1}-status`}>{t(`schedules.statuses.${s.lastStatus}`)}</Badge> : null}
              </TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {canManage ? <Button variant="secondary" size="sm" onClick={() => { runNow.mutate(s); }} disabled={runNow.isPending} data-testid={`schedule-${i + 1}-run`}><Play aria-hidden="true" /> {t("schedules.runNow")}</Button> : null}
                  <Button variant="secondary" size="sm" onClick={() => { setRunsOf(s); }} data-testid={`schedule-${i + 1}-runs`}><History aria-hidden="true" /> {t("schedules.runs")}</Button>
                  {canManage && s.isOwner ? <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setDraft(draftOf(s)); }} data-testid={`schedule-${i + 1}-edit`}>{t("common.edit")}</Button> : null}
                </div>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <Dialog open={draft !== null} onOpenChange={(open) => { if (!open) { setDraft(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{draft?.id ? t("schedules.editSchedule") : t("schedules.newSchedule")}</DialogTitle>
          </DialogHeader>
          {draft ? (
            <form onSubmit={submit} className="flex flex-col gap-3" data-testid="schedule-form">
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("schedules.nameEn")}><TextField value={draft.nameEn} onChange={(e) => { setDraft({ ...draft, nameEn: e.target.value }); }} required data-testid="schedule-name-en" /></Field>
                <Field label={t("schedules.nameAr")}><TextField value={draft.nameAr} onChange={(e) => { setDraft({ ...draft, nameAr: e.target.value }); }} dir="rtl" data-testid="schedule-name-ar" /></Field>
                <Field label={t("dashboards.source")}>
                  <SelectField value={draft.source} onChange={(e) => { setDraft({ ...draft, source: e.target.value }); }} data-testid="schedule-source">
                    {widgetSources.map((s) => <option key={s} value={s}>{t(`dashboards.sources.${s}`)}</option>)}
                  </SelectField>
                </Field>
                {draft.source === "report" ? (
                  <Field label={t("dashboards.report")}>
                    <SelectField value={draft.reportId} onChange={(e) => { setDraft({ ...draft, reportId: e.target.value }); }} required data-testid="schedule-report">
                      <option value="">—</option>
                      {(reports.data ?? []).map((r) => <option key={r.id} value={r.id}>{localized(r.name)}</option>)}
                    </SelectField>
                  </Field>
                ) : (
                  <Field label={t("dashboards.layout")}>
                    <SelectField value={draft.layout} onChange={(e) => { setDraft({ ...draft, layout: e.target.value }); }} required data-testid="schedule-layout">
                      <option value="">—</option>
                      {(layouts.data ?? []).map((l) => <option key={layoutKey(l)} value={layoutKey(l)}>{localized(l.name)}</option>)}
                    </SelectField>
                  </Field>
                )}
                <Field label={t("schedules.period")} description={t("schedules.periodHelp")}>
                  <SelectField value={draft.period} onChange={(e) => { setDraft({ ...draft, period: e.target.value }); }} data-testid="schedule-period">
                    {periods.map((p) => <option key={p} value={p}>{t(`dashboards.periods.${p}`)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("schedules.cadence")}>
                  <SelectField value={draft.cadence} onChange={(e) => { setDraft({ ...draft, cadence: e.target.value }); }} data-testid="schedule-cadence">
                    {cadences.map((c) => <option key={c.code} value={c.code}>{t(`schedules.cadences.${c.code}`)}</option>)}
                    <option value="custom">{t("schedules.cadences.custom")}</option>
                  </SelectField>
                </Field>
                {draft.cadence === "custom" ? (
                  <Field label={t("schedules.cron")} description={t("schedules.cronHelp")}>
                    <TextField value={draft.cron} onChange={(e) => { setDraft({ ...draft, cron: e.target.value }); }} dir="ltr" required data-testid="schedule-cron" />
                  </Field>
                ) : null}
                <Field label={t("schedules.timeZone")} description={t("schedules.timeZoneHelp")}>
                  <TextField value={draft.timeZone} onChange={(e) => { setDraft({ ...draft, timeZone: e.target.value }); }} dir="ltr" placeholder="Asia/Baghdad" data-testid="schedule-time-zone" />
                </Field>
                <Field label={t("schedules.format")}>
                  <SelectField value={draft.format} onChange={(e) => { setDraft({ ...draft, format: e.target.value }); }} data-testid="schedule-format">
                    {scheduleFormats.map((f) => <option key={f} value={f}>{f.toUpperCase()}</option>)}
                  </SelectField>
                </Field>
                {draft.source === "statement" ? (
                  <Field label={t("statements.closingEntries")}>
                    <SelectField value={draft.includeClosing} onChange={(e) => { setDraft({ ...draft, includeClosing: e.target.value }); }} data-testid="schedule-closing">
                      <option value="default">{t("statements.closingDefault")}</option>
                      <option value="include">{t("statements.closingInclude")}</option>
                      <option value="exclude">{t("statements.closingExclude")}</option>
                    </SelectField>
                  </Field>
                ) : null}
              </div>
              <fieldset className="rounded-md border border-border p-3">
                <legend className="px-1 text-sm font-medium">{t("schedules.recipients")}</legend>
                <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm" data-testid="schedule-members">
                  {(members.data ?? []).filter((m) => m.status === "active").map((m) => (
                    <label key={m.membershipId} className="flex items-center gap-2">
                      <Checkbox
                        checked={draft.members.has(m.membershipId)}
                        onCheckedChange={(checked) => {
                          const next = new Set(draft.members);
                          if (checked === true) {
                            next.add(m.membershipId);
                          } else {
                            next.delete(m.membershipId);
                          }
                          setDraft({ ...draft, members: next });
                        }}
                        data-testid={`schedule-member-${m.membershipId}`}
                      />
                      <span dir="auto">{m.displayName}</span> <span className="text-fg-muted" dir="ltr">{m.email}</span>
                    </label>
                  ))}
                </div>
                <Field label={t("schedules.emails")} description={t("schedules.emailsHelp")}>
                  <TextareaField value={draft.emails} onChange={(e) => { setDraft({ ...draft, emails: e.target.value }); }} dir="ltr" rows={2} data-testid="schedule-emails" />
                </Field>
              </fieldset>
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <Checkbox checked={draft.onlyIfRows} onCheckedChange={(checked) => { setDraft({ ...draft, onlyIfRows: checked === true }); }} data-testid="schedule-only-if-rows" />
                  {t("schedules.onlyIfRows")}
                </label>
                <label className="flex items-center gap-2">
                  <Checkbox checked={draft.enabled} onCheckedChange={(checked) => { setDraft({ ...draft, enabled: checked === true }); }} data-testid="schedule-enabled" />
                  {t("schedules.enabled")}
                </label>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={save.isPending} data-testid="save-schedule">{t("common.save")}</Button>
                <Button type="button" variant="secondary" onClick={() => { setDraft(null); }}>{t("common.cancel")}</Button>
                {draft.id ? <Button type="button" variant="danger" onClick={() => { if (draft.id && window.confirm(t("schedules.confirmDelete"))) { remove.mutate(draft.id); } }} data-testid="delete-schedule">{t("common.delete")}</Button> : null}
              </div>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={runsOf !== null} onOpenChange={(open) => { if (!open) { setRunsOf(null); void refresh(); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">{runsOf ? `${t("schedules.runs")} · ${localized(runsOf.name)}` : ""}</DialogTitle>
          </DialogHeader>
          <Table data-testid="runs-table">
            <caption className="sr-only">{t("schedules.runs")}</caption>
            <TableHeader>
              <TableRow>
                <TableHead>{t("schedules.startedAt")}</TableHead>
                <TableHead>{t("schedules.trigger")}</TableHead>
                <TableHead>{t("common.status")}</TableHead>
                <TableHead>{t("schedules.window")}</TableHead>
                <TableHead className="text-end">{t("schedules.rows")}</TableHead>
                <TableHead>{t("schedules.recipientsColumn")}</TableHead>
                <TableHead>{t("schedules.file")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(runs.data ?? []).length === 0 ? (
                <TableRow><TableCell colSpan={7} className="text-fg-muted">{t("schedules.noRuns")}</TableCell></TableRow>
              ) : (runs.data ?? []).map((r, i) => (
                <TableRow key={r.id} data-testid={`run-row-${i + 1}`}>
                  <TableCell>{formatDateTime(r.startedAt)}</TableCell>
                  <TableCell>{t(`schedules.triggers.${r.trigger}`)}</TableCell>
                  <TableCell>
                    <Badge tone={statusTone(r.status)} data-testid={`run-${i + 1}-status`}>{t(`schedules.statuses.${r.status}`)}</Badge>
                    {r.error ? <div className="mt-1 text-xs text-danger" dir="ltr">{r.error}</div> : null}
                  </TableCell>
                  <TableCell dir="ltr">{r.periodFrom && r.periodTo ? `${r.periodFrom} → ${r.periodTo}` : "—"}</TableCell>
                  <TableCell className="text-end tabular-nums" dir="ltr">{r.rowCount === null ? "—" : formatNumber(r.rowCount)}</TableCell>
                  <TableCell dir="ltr" className="text-xs">{r.recipients.join(", ") || "—"}</TableCell>
                  <TableCell>
                    {r.hasFile ? <Button variant="secondary" size="sm" onClick={() => { void download(r); }} data-testid={`run-${i + 1}-file`}><Download aria-hidden="true" /> {r.fileName}</Button> : "—"}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </DialogContent>
      </Dialog>
    </div>
  );
}
