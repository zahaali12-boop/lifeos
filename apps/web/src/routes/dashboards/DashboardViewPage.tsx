import { Button, Checkbox } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { ArrowDown, ArrowUp, PencilRuler, Plus, RefreshCw, Save, Trash, X } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useReports } from "../reports/shared";
import { layoutKey, layoutRef, useLayouts } from "../statements/shared";
import { DashboardGrid, periods, useDashboardRun, widgetSources, widgetViz, type Dashboard, type WidgetDefinition } from "./shared";

interface WidgetDraft {
  key: number;
  code: string;
  titleEn: string;
  titleAr: string;
  source: string;
  reportId: string;
  layout: string;
  viz: string;
  width: string;
  period: string;
  measure: string;
  rowCode: string;
  columnCode: string;
}

interface Draft {
  nameEn: string;
  nameAr: string;
  sharing: string;
  showOnHome: boolean;
  defaultPeriod: string;
  refreshSeconds: string;
  widgets: WidgetDraft[];
}

let keys = 0;
const key = (): number => ++keys;

const emptyWidget = (): WidgetDraft => ({ key: key(), code: "", titleEn: "", titleAr: "", source: "report", reportId: "", layout: "", viz: "kpi", width: "1", period: "dashboard", measure: "", rowCode: "", columnCode: "" });
const emptyDraft = (): Draft => ({ nameEn: "", nameAr: "", sharing: "private", showOnHome: false, defaultPeriod: "this_month", refreshSeconds: "", widgets: [emptyWidget()] });

function draftOf(d: Dashboard): Draft {
  return {
    nameEn: d.name.en ?? "",
    nameAr: d.name.ar ?? "",
    sharing: d.sharing,
    showOnHome: d.showOnHome,
    defaultPeriod: d.defaultPeriod,
    refreshSeconds: d.refreshSeconds === null ? "" : String(d.refreshSeconds),
    widgets: d.widgets.map((w) => ({
      key: key(),
      code: w.code,
      titleEn: w.title.en ?? "",
      titleAr: w.title.ar ?? "",
      source: w.source,
      reportId: w.reportId ?? "",
      layout: w.layoutId ?? (w.layoutCode ? `shipped:${w.layoutCode}` : ""),
      viz: w.viz,
      width: String(w.width),
      period: w.period,
      measure: w.options.measure ?? "",
      rowCode: w.options.rowCode ?? "",
      columnCode: w.options.columnCode ?? "",
    })),
  };
}

/** A dashboard (founder programme item 9d): its widgets run for the company and the period, and, for its owner, the designer that adds, orders and removes widgets. */
export function DashboardViewPage() {
  const search = useSearch({ strict: false });
  const dashboardId = search.dashboard ?? "";
  return <Dashboards key={dashboardId} dashboardId={dashboardId} />;
}

function Dashboards({ dashboardId }: { dashboardId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const reports = useReports();
  const layouts = useLayouts(companyId);
  const isNew = dashboardId === "";
  const dashboard = useQuery({
    queryKey: ["dashboard", dashboardId],
    enabled: !isNew,
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/dashboards/{dashboardId}", { params: { path: { dashboardId } } })),
  });
  const [editing, setEditing] = useState(isNew);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [period, setPeriod] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const loaded = dashboard.data;
  useEffect(() => {
    if (loaded) {
      setDraft(draftOf(loaded));
      setPeriod((current) => current || loaded.defaultPeriod);
    }
  }, [loaded]);

  const run = useDashboardRun(dashboardId, companyId, period || (loaded?.defaultPeriod ?? "this_month"), from, to, loaded?.refreshSeconds);
  const runProblem = run.error ? toFormProblem(run.error, t("dashboards.runFailed")) : null;

  const setWidget = (k: number, patch: Partial<WidgetDraft>): void => { setDraft({ ...draft, widgets: draft.widgets.map((w) => (w.key === k ? { ...w, ...patch } : w)) }); };
  const move = (index: number, delta: number): void => {
    const next = [...draft.widgets];
    const target = index + delta;
    const item = next[index];
    if (!item || target < 0 || target >= next.length) {
      return;
    }
    next.splice(index, 1);
    next.splice(target, 0, item);
    setDraft({ ...draft, widgets: next });
  };

  const body = useMemo(() => (): { name: Record<string, string>; widgets: WidgetDefinition[]; sharing: string; showOnHome: boolean; defaultPeriod: string; refreshSeconds: number | null } => ({
    name: { en: draft.nameEn, ar: draft.nameAr },
    sharing: draft.sharing,
    showOnHome: draft.showOnHome,
    defaultPeriod: draft.defaultPeriod,
    refreshSeconds: draft.refreshSeconds ? Number(draft.refreshSeconds) : null,
    widgets: draft.widgets.map((w, i) => {
      const report = reports.data?.find((r) => r.id === w.reportId);
      return {
        code: w.code || `W${i + 1}`,
        source: w.source,
        viz: w.viz,
        title: w.titleEn || w.titleAr ? { en: w.titleEn, ar: w.titleAr } : null,
        reportId: w.source === "report" && w.reportId ? w.reportId : null,
        ...(w.source === "statement" ? layoutRef(w.layout) : {}),
        period: w.period,
        width: Number(w.width),
        options: {
          chart: w.source === "report" && report ? report.chart : null,
          measure: w.measure || null,
          rowCode: w.rowCode || null,
          columnCode: w.columnCode || null,
          includeClosing: null,
          showComparison: false,
        },
      };
    }),
  }), [draft, reports.data]);

  const save = useMutation({
    mutationFn: async () => (isNew
      ? unwrap(await api.POST("/api/v1/reporting/dashboards", { body: body() }))
      : unwrap(await api.PUT("/api/v1/reporting/dashboards/{dashboardId}", { params: { path: { dashboardId } }, body: body() }))),
    onSuccess: async (saved) => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["dashboards"] });
      await queryClient.invalidateQueries({ queryKey: ["dashboard", saved.id] });
      await queryClient.invalidateQueries({ queryKey: ["dashboard-run", saved.id] });
      setEditing(false);
      if (isNew) {
        void navigate({ to: "/dashboards/view", search: { dashboard: saved.id } });
      }
    },
    onError: (error) => { setProblem(toFormProblem(error, t("dashboards.saveFailed"))); },
  });
  const remove = useMutation({
    mutationFn: async () => unwrap(await api.DELETE("/api/v1/reporting/dashboards/{dashboardId}", { params: { path: { dashboardId } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["dashboards"] });
      void navigate({ to: "/dashboards", search: {} });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("dashboards.saveFailed"))); },
  });

  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const canEdit = isNew ? can("reporting.report.manage") : (loaded?.canEdit ?? false) && can("reporting.report.manage");
  const title = isNew ? t("dashboards.newDashboard") : loaded ? localized(loaded.name) : "";

  return (
    <div>
      <PageHeader
        title={<span data-testid="dashboard-title" dir="auto">{title}</span>}
        description={isNew ? t("dashboards.designerDescription") : undefined}
        actions={(
          <>
            <Button variant="secondary" onClick={() => { void navigate({ to: "/dashboards", search: {} }); }} data-testid="back-to-dashboards">{t("nav.dashboards")}</Button>
            {!isNew ? <Button variant="secondary" onClick={() => { void run.refetch(); }} disabled={run.isFetching} data-testid="run-dashboard"><RefreshCw aria-hidden="true" /> {t("common.refresh")}</Button> : null}
            {canEdit && !editing ? <Button onClick={() => { setEditing(true); }} data-testid="edit-dashboard"><PencilRuler aria-hidden="true" /> {t("dashboards.edit")}</Button> : null}
          </>
        )}
      />

      {!isNew ? (
        <div className="mb-4 flex flex-wrap items-end gap-3">
          <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
          <Field label={t("dashboards.period")}>
            <SelectField value={period || (loaded?.defaultPeriod ?? "this_month")} onChange={(e) => { setPeriod(e.target.value); }} data-testid="dashboard-period-select">
              {periods.map((p) => <option key={p} value={p}>{t(`dashboards.periods.${p}`)}</option>)}
              <option value="custom">{t("dashboards.periods.custom")}</option>
            </SelectField>
          </Field>
          {period === "custom" ? (
            <>
              <Field label={t("dashboards.from")}><TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} data-testid="dashboard-from" /></Field>
              <Field label={t("dashboards.to")}><TextField type="date" value={to} onChange={(e) => { setTo(e.target.value); }} data-testid="dashboard-to" /></Field>
            </>
          ) : null}
          {run.data ? <span className="pb-2 text-xs text-fg-subtle" dir="ltr" data-testid="dashboard-window">{run.data.from} → {run.data.to}</span> : null}
        </div>
      ) : null}

      {runProblem ? <FormError message={runProblem.message} /> : null}
      {!isNew && !editing ? (run.data ? <DashboardGrid run={run.data} /> : run.isLoading ? <p className="text-sm text-fg-muted">{t("common.loading")}</p> : null) : null}

      {editing ? (
        <form onSubmit={submit} className="mt-4 flex flex-col gap-4" data-testid="dashboard-designer">
          <FormError message={problem?.message ?? null} />
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <Field label={t("dashboards.nameEn")}><TextField value={draft.nameEn} onChange={(e) => { setDraft({ ...draft, nameEn: e.target.value }); }} required data-testid="dashboard-name-en" /></Field>
            <Field label={t("dashboards.nameAr")}><TextField value={draft.nameAr} onChange={(e) => { setDraft({ ...draft, nameAr: e.target.value }); }} dir="rtl" data-testid="dashboard-name-ar" /></Field>
            <Field label={t("dashboards.defaultPeriod")}>
              <SelectField value={draft.defaultPeriod} onChange={(e) => { setDraft({ ...draft, defaultPeriod: e.target.value }); }} data-testid="dashboard-default-period">
                {periods.map((p) => <option key={p} value={p}>{t(`dashboards.periods.${p}`)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("dashboards.sharing")}>
              <SelectField value={draft.sharing} onChange={(e) => { setDraft({ ...draft, sharing: e.target.value }); }} data-testid="dashboard-sharing">
                <option value="private">{t("dashboards.sharingPrivate")}</option>
                <option value="tenant">{t("dashboards.sharingTenant")}</option>
              </SelectField>
            </Field>
            <Field label={t("dashboards.refresh")} description={t("dashboards.refreshHelp")}>
              <TextField type="number" min={30} max={86400} value={draft.refreshSeconds} onChange={(e) => { setDraft({ ...draft, refreshSeconds: e.target.value }); }} data-testid="dashboard-refresh" />
            </Field>
            <label className="flex items-center gap-2 self-end pb-2 text-sm">
              <Checkbox checked={draft.showOnHome} onCheckedChange={(checked) => { setDraft({ ...draft, showOnHome: checked === true }); }} data-testid="dashboard-home" />
              {t("dashboards.showOnHome")}
            </label>
          </div>

          <div className="flex items-center justify-between">
            <h2 className="text-base font-semibold">{t("dashboards.widgets")}</h2>
            <Button type="button" variant="secondary" size="sm" onClick={() => { setDraft({ ...draft, widgets: [...draft.widgets, emptyWidget()] }); }} data-testid="add-widget"><Plus aria-hidden="true" /> {t("dashboards.addWidget")}</Button>
          </div>
          <ol className="flex flex-col gap-3">
            {draft.widgets.map((w, index) => {
              const n = index + 1;
              const report = reports.data?.find((r) => r.id === w.reportId);
              const layout = layouts.data?.find((l) => layoutKey(l) === w.layout);
              const measures = report?.definition.values?.map((v) => v.field) ?? [];
              const vizChoices = w.source === "statement" ? (["kpi", "table"] as const) : widgetViz;
              return (
                <li key={w.key} className="rounded-lg border border-border bg-surface p-3" data-testid={`widget-row-${n}`}>
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <span className="text-sm font-medium">{t("dashboards.widgetN", { n })}</span>
                    <div className="flex gap-1">
                      <Button type="button" variant="ghost" size="sm" onClick={() => { move(index, -1); }} aria-label={t("statements.moveUp")}><ArrowUp aria-hidden="true" /></Button>
                      <Button type="button" variant="ghost" size="sm" onClick={() => { move(index, 1); }} aria-label={t("statements.moveDown")}><ArrowDown aria-hidden="true" /></Button>
                      <Button type="button" variant="ghost" size="sm" onClick={() => { setDraft({ ...draft, widgets: draft.widgets.filter((x) => x.key !== w.key) }); }} aria-label={t("dashboards.removeWidget")} data-testid={`widget-${n}-remove`}><X aria-hidden="true" /></Button>
                    </div>
                  </div>
                  <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                    <Field label={t("dashboards.source")}>
                      <SelectField value={w.source} onChange={(e) => { setWidget(w.key, { source: e.target.value, viz: e.target.value === "statement" && !["kpi", "table"].includes(w.viz) ? "table" : w.viz }); }} data-testid={`widget-${n}-source`}>
                        {widgetSources.map((s) => <option key={s} value={s}>{t(`dashboards.sources.${s}`)}</option>)}
                      </SelectField>
                    </Field>
                    {w.source === "report" ? (
                      <Field label={t("dashboards.report")}>
                        <SelectField value={w.reportId} onChange={(e) => { setWidget(w.key, { reportId: e.target.value, measure: "" }); }} required data-testid={`widget-${n}-report`}>
                          <option value="">—</option>
                          {(reports.data ?? []).map((r) => <option key={r.id} value={r.id}>{localized(r.name)}</option>)}
                        </SelectField>
                      </Field>
                    ) : (
                      <Field label={t("dashboards.layout")}>
                        <SelectField value={w.layout} onChange={(e) => { setWidget(w.key, { layout: e.target.value, rowCode: "", columnCode: "" }); }} required data-testid={`widget-${n}-layout`}>
                          <option value="">—</option>
                          {(layouts.data ?? []).map((l) => <option key={layoutKey(l)} value={layoutKey(l)}>{localized(l.name)}</option>)}
                        </SelectField>
                      </Field>
                    )}
                    <Field label={t("dashboards.viz")}>
                      <SelectField value={w.viz} onChange={(e) => { setWidget(w.key, { viz: e.target.value }); }} data-testid={`widget-${n}-viz`}>
                        {vizChoices.map((v) => <option key={v} value={v}>{t(`dashboards.vizNames.${v}`)}</option>)}
                      </SelectField>
                    </Field>
                    <Field label={t("dashboards.width")}>
                      <SelectField value={w.width} onChange={(e) => { setWidget(w.key, { width: e.target.value }); }} data-testid={`widget-${n}-width`}>
                        {["1", "2", "3", "4"].map((x) => <option key={x} value={x}>{t("dashboards.columns", { n: x })}</option>)}
                      </SelectField>
                    </Field>
                    <Field label={t("dashboards.widgetPeriod")}>
                      <SelectField value={w.period} onChange={(e) => { setWidget(w.key, { period: e.target.value }); }} data-testid={`widget-${n}-period`}>
                        <option value="dashboard">{t("dashboards.periods.dashboard")}</option>
                        {periods.map((p) => <option key={p} value={p}>{t(`dashboards.periods.${p}`)}</option>)}
                      </SelectField>
                    </Field>
                    <Field label={t("dashboards.titleEn")}><TextField value={w.titleEn} onChange={(e) => { setWidget(w.key, { titleEn: e.target.value }); }} data-testid={`widget-${n}-title-en`} /></Field>
                    <Field label={t("dashboards.titleAr")}><TextField value={w.titleAr} onChange={(e) => { setWidget(w.key, { titleAr: e.target.value }); }} dir="rtl" data-testid={`widget-${n}-title-ar`} /></Field>
                    {w.viz === "kpi" && w.source === "report" ? (
                      <Field label={t("dashboards.measure")} description={t("dashboards.measureHelp")}>
                        <SelectField value={w.measure} onChange={(e) => { setWidget(w.key, { measure: e.target.value }); }} data-testid={`widget-${n}-measure`}>
                          <option value="">{t("dashboards.firstMeasure")}</option>
                          {measures.map((m) => <option key={m} value={m}>{m}</option>)}
                        </SelectField>
                      </Field>
                    ) : null}
                    {w.viz === "kpi" && w.source === "statement" ? (
                      <>
                        <Field label={t("dashboards.row")}>
                          <SelectField value={w.rowCode} onChange={(e) => { setWidget(w.key, { rowCode: e.target.value }); }} required data-testid={`widget-${n}-row`}>
                            <option value="">—</option>
                            {(layout?.definition.rows ?? []).filter((r) => r.rowType !== "blank" && r.rowType !== "header").map((r) => <option key={r.code} value={r.code}>{r.code} · {localized(r.label)}</option>)}
                          </SelectField>
                        </Field>
                        <Field label={t("dashboards.column")}>
                          <SelectField value={w.columnCode} onChange={(e) => { setWidget(w.key, { columnCode: e.target.value }); }} data-testid={`widget-${n}-column`}>
                            <option value="">{t("dashboards.firstColumn")}</option>
                            {(layout?.definition.columns ?? []).map((c) => <option key={c.code} value={c.code}>{c.code} · {localized(c.label)}</option>)}
                          </SelectField>
                        </Field>
                      </>
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ol>

          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={save.isPending} data-testid="save-dashboard"><Save aria-hidden="true" /> {t("common.save")}</Button>
            {!isNew ? <Button type="button" variant="secondary" onClick={() => { setEditing(false); if (loaded) { setDraft(draftOf(loaded)); } }} data-testid="cancel-edit">{t("common.cancel")}</Button> : null}
            {!isNew ? <Button type="button" variant="danger" onClick={() => { if (window.confirm(t("dashboards.confirmDelete"))) { remove.mutate(); } }} disabled={remove.isPending} data-testid="delete-dashboard"><Trash aria-hidden="true" /> {t("common.delete")}</Button> : null}
          </div>
        </form>
      ) : null}
    </div>
  );
}
