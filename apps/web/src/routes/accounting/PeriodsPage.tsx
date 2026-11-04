import { Button, Checkbox, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanySelect, StatusBadge, useCompanies, useCompanySelection } from "./shared";

type PeriodState = components["schemas"]["PeriodStateSummary"];

interface WindowForm {
  roleId: string;
  allowFrom: string;
  allowTo: string;
  reason: string;
}

const modules = ["GL", "AR", "AP", "INV", "FA", "BANK", "TAX"];

/** Period control per company: the states of every period and module for a fiscal year, close/reopen with a reason, and allow-posting windows per role. */
export function PeriodsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useCompanySelection(companies.data);
  const company = companies.data?.find((c) => c.id === companyId);
  const [yearId, setYearId] = useState("");
  const [selectedModules, setSelectedModules] = useState<string[]>(["GL"]);
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [windows, setWindows] = useState<WindowForm[] | null>(null);

  const calendars = useQuery({ queryKey: ["fiscal-calendars"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/fiscal-calendars")) });
  const calendar = calendars.data?.find((c) => c.id === company?.fiscalCalendarId);
  const years = calendar?.years ?? [];
  const year = years.find((y) => y.id === yearId) ?? years.find((y) => y.status === "open") ?? years[0];
  const periods = useMemo(() => year?.periods.filter((p) => !p.isAdjustment) ?? [], [year]);

  const states = useQueries({
    queries: periods.map((period) => ({
      queryKey: ["period-states", period.id, companyId],
      enabled: Boolean(companyId),
      queryFn: async () => unwrap(await api.GET("/api/v1/organization/periods/{periodId}/states", { params: { path: { periodId: period.id }, query: { companyId } } })),
    })),
  });
  const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
  const storedWindows = useQuery({
    queryKey: ["posting-windows", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies/{companyId}/posting-windows", { params: { path: { companyId } } })),
  });
  const windowRows: WindowForm[] = windows ?? (storedWindows.data ?? []).map((w) => ({ roleId: w.roleId ?? "", allowFrom: w.allowFrom ?? "", allowTo: w.allowTo ?? "", reason: w.reason ?? "" }));

  const change = useMutation({
    mutationFn: async (input: { periodId: string; state: string; reopen: boolean }) =>
      input.reopen
        ? unwrap(await api.POST("/api/v1/organization/periods/{periodId}/reopen", { params: { path: { periodId: input.periodId } }, body: { companyId, modules: selectedModules, reason, state: input.state } }))
        : unwrap(await api.PUT("/api/v1/organization/periods/{periodId}/states", { params: { path: { periodId: input.periodId } }, body: { companyId, modules: selectedModules, state: input.state, reason: reason || null } })),
    onSuccess: async (_, input) => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["period-states", input.periodId, companyId] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const saveWindows = useMutation({
    mutationFn: async () =>
      unwrap(await api.PUT("/api/v1/organization/companies/{companyId}/posting-windows", { params: { path: { companyId } }, body: windowRows.map((w) => ({ roleId: w.roleId || null, allowFrom: w.allowFrom || null, allowTo: w.allowTo || null, reason: w.reason || null })) })),
    onSuccess: async () => {
      setProblem(null);
      setWindows(null);
      await queryClient.invalidateQueries({ queryKey: ["posting-windows", companyId] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const stateOf = (index: number, module: string): PeriodState | undefined => states[index]?.data?.find((s) => s.module === module);
  const toggleModule = (module: string, checked: boolean): void => {
    setSelectedModules((current) => (checked ? [...new Set([...current, module])] : current.filter((m) => m !== module)));
  };

  return (
    <>
      <PageHeader title={t("accounting.periods")} description={t("accounting.periodsDescription")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanySelect companies={companies.data ?? []} value={companyId} onChange={setCompanyId} />
        <Field label={t("accounting.fiscalYear")}>
          <SelectField value={year?.id ?? ""} onChange={(e) => { setYearId(e.target.value); }}>
            {years.map((y) => (
              <option key={y.id} value={y.id}>
                {y.code} · {t(`accounting.yearStatus.${y.status}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("common.reason")} description={t("accounting.reasonHint")}>
          <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} data-testid="period-reason" />
        </Field>
      </div>
      <fieldset className="mb-3 flex flex-wrap items-center gap-4 text-sm">
        <legend className="me-2 font-medium">{t("accounting.modules")}</legend>
        {modules.map((module) => (
          <label key={module} className="flex items-center gap-2">
            <Checkbox checked={selectedModules.includes(module)} onCheckedChange={(checked) => { toggleModule(module, checked === true); }} />
            <span dir="ltr">{module}</span>
          </label>
        ))}
      </fieldset>
      <FormError message={problem?.message ?? null} />
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("accounting.period")}</TableHead>
            <TableHead>{t("accounting.dates")}</TableHead>
            {modules.map((module) => (
              <TableHead key={module}>
                <span dir="ltr">{module}</span>
              </TableHead>
            ))}
            <TableHead>{t("accounting.actions")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {periods.map((period, index) => {
            const gl = stateOf(index, "GL");
            const hardClosed = selectedModules.some((module) => stateOf(index, module)?.state === "hard_closed");
            return (
              <TableRow key={period.id} data-testid="period-row">
                <TableCell>{t("accounting.periodNumber", { number: String(period.number) })}</TableCell>
                <TableCell dir="ltr">
                  {formatDate(period.startsOn)} – {formatDate(period.endsOn)}
                </TableCell>
                {modules.map((module) => {
                  const state = stateOf(index, module);
                  return (
                    <TableCell key={module} title={state?.reason ?? undefined}>
                      {state ? <StatusBadge status={state.state} label={t(`accounting.periodStates.${state.state}`)} /> : "…"}
                    </TableCell>
                  );
                })}
                <TableCell className="space-x-1 whitespace-nowrap">
                  {hardClosed ? (
                    <Button size="sm" variant="secondary" onClick={() => { change.mutate({ periodId: period.id, state: "open", reopen: true }); }} loading={change.isPending} data-testid="reopen-period">
                      {t("accounting.reopen")}
                    </Button>
                  ) : (
                    <>
                      {gl?.state !== "open" ? (
                        <Button size="sm" variant="secondary" onClick={() => { change.mutate({ periodId: period.id, state: "open", reopen: false }); }} loading={change.isPending}>
                          {t("accounting.open")}
                        </Button>
                      ) : null}
                      <Button size="sm" variant="secondary" onClick={() => { change.mutate({ periodId: period.id, state: "soft_closed", reopen: false }); }} loading={change.isPending}>
                        {t("accounting.softClose")}
                      </Button>
                      <Button size="sm" variant="secondary" onClick={() => { change.mutate({ periodId: period.id, state: "hard_closed", reopen: false }); }} loading={change.isPending} data-testid="hard-close-period">
                        {t("accounting.hardClose")}
                      </Button>
                    </>
                  )}
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>

      <section className="mt-8" aria-labelledby="posting-windows-heading">
        <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
          <div>
            <h2 id="posting-windows-heading" className="text-base font-semibold">
              {t("accounting.postingWindows")}
            </h2>
            <p className="text-sm text-fg-muted">{t("accounting.postingWindowsDescription")}</p>
          </div>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={() => { setWindows([...windowRows, { roleId: "", allowFrom: "", allowTo: "", reason: "" }]); }} data-testid="add-window">
              <Plus aria-hidden="true" />
              {t("accounting.addWindow")}
            </Button>
            <Button onClick={() => { saveWindows.mutate(); }} loading={saveWindows.isPending} disabled={windows === null} data-testid="save-windows">
              {t("common.save")}
            </Button>
          </div>
        </div>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("accounting.role")}</TableHead>
              <TableHead>{t("accounting.allowFrom")}</TableHead>
              <TableHead>{t("accounting.allowTo")}</TableHead>
              <TableHead>{t("common.reason")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {windowRows.length === 0 ? (
              <TableRow>
                <TableCell colSpan={5} className="text-fg-muted">
                  {t("accounting.noWindows")}
                </TableCell>
              </TableRow>
            ) : null}
            {windowRows.map((row, index) => {
              const update = (patch: Partial<WindowForm>): void => { setWindows(windowRows.map((w, i) => (i === index ? { ...w, ...patch } : w))); };
              return (
                <TableRow key={index}>
                  <TableCell>
                    <SelectField aria-label={t("accounting.role")} value={row.roleId} onChange={(e) => { update({ roleId: e.target.value }); }} data-testid={`window-role-${index}`}>
                      <option value="">{t("accounting.everyone")}</option>
                      {(roles.data ?? []).map((role) => (
                        <option key={role.id} value={role.id}>
                          {localized(role.name) || role.code}
                        </option>
                      ))}
                    </SelectField>
                  </TableCell>
                  <TableCell>
                    <TextField type="date" aria-label={t("accounting.allowFrom")} value={row.allowFrom} onChange={(e) => { update({ allowFrom: e.target.value }); }} dir="ltr" data-testid={`window-from-${index}`} />
                  </TableCell>
                  <TableCell>
                    <TextField type="date" aria-label={t("accounting.allowTo")} value={row.allowTo} onChange={(e) => { update({ allowTo: e.target.value }); }} dir="ltr" />
                  </TableCell>
                  <TableCell>
                    <TextField aria-label={t("common.reason")} value={row.reason} onChange={(e) => { update({ reason: e.target.value }); }} />
                  </TableCell>
                  <TableCell>
                    <Button variant="ghost" size="icon" aria-label={t("common.delete")} onClick={() => { setWindows(windowRows.filter((_, i) => i !== index)); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </section>
    </>
  );
}
