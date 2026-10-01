import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatDate, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { DocStatus, Tabs } from "./inventory/shared";

const weekdays = [0, 1, 2, 3, 4, 5, 6];

/** A weekday's name in the interface language (0 = Sunday), from the platform's own calendar data. */
function weekdayName(day: number, language: string): string {
  return new Intl.DateTimeFormat(language, { weekday: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2023, 0, 1 + day)));
}

function monthName(month: number, language: string): string {
  return new Intl.DateTimeFormat(language, { month: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2023, month - 1, 1)));
}

/**
 * Calendars (roadmap 1.7, ADR-0011): fiscal calendars with their years and periods (the period control screen closes
 * them per company and module), and business calendars with working days and holidays that due dates and lead times
 * count in.
 */
export function CalendarsPage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState("fiscal");
  const [fiscalId, setFiscalId] = useState("");
  const [businessId, setBusinessId] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [newFiscal, setNewFiscal] = useState<{ code: string; nameEn: string; nameAr: string; startMonth: string; periodsPerYear: string } | null>(null);
  const [newBusiness, setNewBusiness] = useState<{ code: string; nameEn: string; nameAr: string } | null>(null);
  const [days, setDays] = useState<number[] | null>(null);
  const [holiday, setHoliday] = useState<{ onDate: string; nameEn: string; nameAr: string } | null>(null);
  const [openYear, setOpenYear] = useState<string | null>(null);
  const language = i18n.language;
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const fiscal = useQuery({ queryKey: ["fiscal-calendars"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/fiscal-calendars")) });
  const business = useQuery({ queryKey: ["business-calendars"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/business-calendars")) });
  const selectedFiscal = fiscal.data?.find((c) => c.id === fiscalId) ?? fiscal.data?.[0];
  const selectedBusiness = business.data?.find((c) => c.id === businessId) ?? business.data?.[0];
  const years = [...(selectedFiscal?.years ?? [])].sort((a, b) => b.startsOn.localeCompare(a.startsOn));
  const nextStartYear = years.length > 0 ? Number(years[0]?.startsOn.slice(0, 4)) + 1 : new Date().getFullYear();

  const saveFiscal = useMutation({
    mutationFn: async () => {
      if (!newFiscal) {
        return null;
      }
      return unwrap(await api.POST("/api/v1/organization/fiscal-calendars", { body: { code: newFiscal.code, name: { en: newFiscal.nameEn, ...(newFiscal.nameAr ? { ar: newFiscal.nameAr } : {}) }, startMonth: Number(newFiscal.startMonth), periodsPerYear: Number(newFiscal.periodsPerYear) } }));
    },
    onSuccess: async (created) => { setNewFiscal(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["fiscal-calendars"] }); if (created) { setFiscalId(created.id); } },
    onError: fail,
  });
  const addYear = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/organization/fiscal-calendars/{calendarId}/years", { params: { path: { calendarId: selectedFiscal?.id ?? "" } }, body: { startYear: nextStartYear, status: openYear ?? "open" } })),
    onSuccess: async () => { setOpenYear(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["fiscal-calendars"] }); },
    onError: fail,
  });
  const saveBusiness = useMutation({
    mutationFn: async (input: { id: string | null; code: string; name: Record<string, string>; workingDays: number[] }) =>
      input.id
        ? unwrap(await api.PUT("/api/v1/organization/business-calendars/{calendarId}", { params: { path: { calendarId: input.id } }, body: { code: input.code, name: input.name, workingDays: input.workingDays } }))
        : unwrap(await api.POST("/api/v1/organization/business-calendars", { body: { code: input.code, name: input.name, workingDays: input.workingDays } })),
    onSuccess: async (saved) => { setNewBusiness(null); setDays(null); setProblem(null); setBusinessId(saved.id); await queryClient.invalidateQueries({ queryKey: ["business-calendars"] }); },
    onError: fail,
  });
  const addHoliday = useMutation({
    mutationFn: async () => {
      if (!holiday) {
        return;
      }
      unwrap(await api.POST("/api/v1/organization/business-calendars/{calendarId}/holidays", { params: { path: { calendarId: selectedBusiness?.id ?? "" } }, body: { onDate: holiday.onDate, name: { en: holiday.nameEn, ...(holiday.nameAr ? { ar: holiday.nameAr } : {}) } } }));
    },
    onSuccess: async () => { setHoliday(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["business-calendars"] }); },
    onError: fail,
  });
  const removeHoliday = useMutation({
    mutationFn: async (holidayId: string) => { await api.DELETE("/api/v1/organization/business-calendars/{calendarId}/holidays/{holidayId}", { params: { path: { calendarId: selectedBusiness?.id ?? "", holidayId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["business-calendars"] }); },
    onError: fail,
  });

  const workingDays = days ?? selectedBusiness?.workingDays.map(Number) ?? [];
  const submitFiscal = (event: FormEvent): void => { event.preventDefault(); saveFiscal.mutate(); };
  const submitBusiness = (event: FormEvent): void => {
    event.preventDefault();
    if (newBusiness) {
      saveBusiness.mutate({ id: null, code: newBusiness.code, name: { en: newBusiness.nameEn, ...(newBusiness.nameAr ? { ar: newBusiness.nameAr } : {}) }, workingDays: [0, 1, 2, 3, 4] });
    }
  };
  const submitHoliday = (event: FormEvent): void => { event.preventDefault(); addHoliday.mutate(); };

  return (
    <>
      <PageHeader
        title={t("nav.calendars")}
        description={t("calendars.description")}
        actions={
          tab === "fiscal" ? (
            <Button onClick={() => { setProblem(null); setNewFiscal({ code: "", nameEn: "", nameAr: "", startMonth: "1", periodsPerYear: "12" }); }} data-testid="new-fiscal-calendar">
              <Plus aria-hidden="true" />
              {t("calendars.newFiscal")}
            </Button>
          ) : (
            <Button onClick={() => { setProblem(null); setNewBusiness({ code: "", nameEn: "", nameAr: "" }); }} data-testid="new-business-calendar">
              <Plus aria-hidden="true" />
              {t("calendars.newBusiness")}
            </Button>
          )
        }
      />
      <Tabs
        value={tab}
        onChange={(next) => { setTab(next); setProblem(null); }}
        tabs={[
          { id: "fiscal", label: t("calendars.fiscal"), testId: "tab-fiscal" },
          { id: "business", label: t("calendars.business"), testId: "tab-business" },
        ]}
      />
      <FormError message={problem && !newFiscal && !newBusiness && !holiday ? problem.message : null} />

      {tab === "fiscal" ? (
        <div className="flex flex-col gap-4" data-testid="fiscal-calendars">
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("calendars.calendar")}>
              <SelectField value={selectedFiscal?.id ?? ""} onChange={(e) => { setFiscalId(e.target.value); }} data-testid="fiscal-select">
                {(fiscal.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.code} · {localized(c.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          {selectedFiscal ? (
            <>
              <div className="flex flex-wrap items-center gap-2 text-sm">
                {selectedFiscal.isSystem ? <Badge tone="info">{t("calendars.system")}</Badge> : null}
                <span className="text-fg-muted">{t("calendars.startsIn", { month: monthName(Number(selectedFiscal.startMonth), language) })}</span>
                <span className="text-fg-muted">{t("calendars.periodsPerYear", { count: Number(selectedFiscal.periodsPerYear) })}</span>
                <Button variant="secondary" size="sm" className="ms-auto" onClick={() => { setProblem(null); setOpenYear("open"); }} data-testid="open-next-year">
                  <Plus aria-hidden="true" />
                  {t("calendars.addYear", { year: nextStartYear })}
                </Button>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("calendars.year")}</TableHead>
                    <TableHead>{t("calendars.from")}</TableHead>
                    <TableHead>{t("calendars.to")}</TableHead>
                    <TableHead>{t("calendars.periods")}</TableHead>
                    <TableHead>{t("common.status")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {years.map((y) => (
                    <TableRow key={y.id} data-testid="fiscal-year-row">
                      <TableCell dir="ltr">{y.code}</TableCell>
                      <TableCell>{formatDate(y.startsOn)}</TableCell>
                      <TableCell>{formatDate(y.endsOn)}</TableCell>
                      <TableCell>
                        <span className="text-fg-muted">
                          {y.periods.filter((p) => !p.isAdjustment).length}
                          {y.periods.some((p) => p.isAdjustment) ? ` + ${t("calendars.adjustmentPeriod")}` : ""}
                        </span>
                      </TableCell>
                      <TableCell><DocStatus status={y.status} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <p className="text-sm text-fg-muted">{t("calendars.periodControlHint")}</p>
            </>
          ) : null}
        </div>
      ) : (
        <div className="flex flex-col gap-4" data-testid="business-calendars">
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("calendars.calendar")}>
              <SelectField value={selectedBusiness?.id ?? ""} onChange={(e) => { setBusinessId(e.target.value); setDays(null); }} data-testid="business-select">
                {(business.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.code} · {localized(c.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          {selectedBusiness ? (
            <>
              <fieldset className="flex flex-col gap-3 rounded-md border border-border p-3">
                <legend className="px-1 text-sm font-medium">{t("calendars.workingDays")}</legend>
                <div className="flex flex-wrap gap-4 text-sm">
                  {weekdays.map((d) => (
                    <label key={d} className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        checked={workingDays.includes(d)}
                        disabled={selectedBusiness.isSystem}
                        onChange={(e) => { setDays(e.target.checked ? [...workingDays, d].sort() : workingDays.filter((x) => x !== d)); }}
                        data-testid={`working-day-${String(d)}`}
                      />
                      {weekdayName(d, language)}
                    </label>
                  ))}
                </div>
                {selectedBusiness.isSystem ? <p className="text-sm text-fg-muted">{t("calendars.systemDaysHint")}</p> : null}
                {days ? (
                  <div>
                    <Button size="sm" onClick={() => { saveBusiness.mutate({ id: selectedBusiness.id, code: selectedBusiness.code, name: selectedBusiness.name, workingDays: days }); }} loading={saveBusiness.isPending} data-testid="save-working-days">
                      {t("calendars.saveDays")}
                    </Button>
                  </div>
                ) : null}
              </fieldset>
              <div className="flex items-center gap-2">
                <h2 className="text-base font-semibold">{t("calendars.holidays")}</h2>
                <Button variant="secondary" size="sm" className="ms-auto" onClick={() => { setProblem(null); setHoliday({ onDate: "", nameEn: "", nameAr: "" }); }} data-testid="add-holiday">
                  <Plus aria-hidden="true" />
                  {t("calendars.addHoliday")}
                </Button>
              </div>
              {selectedBusiness.holidays.length === 0 ? <p className="text-sm text-fg-muted">{t("calendars.noHolidays")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("calendars.date")}</TableHead>
                      <TableHead>{t("calendars.name")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {[...selectedBusiness.holidays].sort((a, b) => a.onDate.localeCompare(b.onDate)).map((h) => (
                      <TableRow key={h.id} data-testid="holiday-row">
                        <TableCell>{formatDate(h.onDate)}</TableCell>
                        <TableCell>{localized(h.name)}</TableCell>
                        <TableCell>
                          <Button variant="ghost" size="icon" aria-label={t("calendars.removeHoliday", { name: localized(h.name) })} onClick={() => { removeHoliday.mutate(h.id); }}>
                            <Trash2 aria-hidden="true" />
                          </Button>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </>
          ) : null}
        </div>
      )}

      <Dialog open={Boolean(newFiscal)} onOpenChange={(isOpen) => { if (!isOpen) { setNewFiscal(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {newFiscal ? (
            <form onSubmit={submitFiscal} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("calendars.newFiscal")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("calendars.code")} required error={problem?.fields.code}>
                  <TextField value={newFiscal.code} onChange={(e) => { setNewFiscal({ ...newFiscal, code: e.target.value.toLowerCase() }); }} required dir="ltr" data-testid="fiscal-code" />
                </Field>
                <Field label={t("calendars.startMonth")}>
                  <SelectField value={newFiscal.startMonth} onChange={(e) => { setNewFiscal({ ...newFiscal, startMonth: e.target.value }); }} data-testid="fiscal-start-month">
                    {Array.from({ length: 12 }, (_, i) => i + 1).map((m) => (
                      <option key={m} value={String(m)}>
                        {monthName(m, language)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("calendars.nameEn")} required error={problem?.fields.name}>
                  <TextField value={newFiscal.nameEn} onChange={(e) => { setNewFiscal({ ...newFiscal, nameEn: e.target.value }); }} required data-testid="fiscal-name-en" />
                </Field>
                <Field label={t("calendars.nameAr")}>
                  <TextField value={newFiscal.nameAr} onChange={(e) => { setNewFiscal({ ...newFiscal, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <Field label={t("calendars.periods")} description={t("calendars.periodsHint")}>
                  <SelectField value={newFiscal.periodsPerYear} onChange={(e) => { setNewFiscal({ ...newFiscal, periodsPerYear: e.target.value }); }}>
                    <option value="12">12</option>
                    <option value="13">13</option>
                  </SelectField>
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setNewFiscal(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveFiscal.isPending} data-testid="save-fiscal-calendar">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={openYear !== null} onOpenChange={(isOpen) => { if (!isOpen) { setOpenYear(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          <div className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("calendars.addYear", { year: nextStartYear })}</DialogTitle>
            </DialogHeader>
            <p className="text-sm text-fg-muted">{t("calendars.addYearHint")}</p>
            <FormError message={problem?.message ?? null} />
            <Field label={t("common.status")}>
              <SelectField value={openYear ?? "open"} onChange={(e) => { setOpenYear(e.target.value); }} data-testid="year-status">
                <option value="open">{t("calendars.yearStatuses.open")}</option>
                <option value="future">{t("calendars.yearStatuses.future")}</option>
              </SelectField>
            </Field>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setOpenYear(null); }}>
                {t("common.cancel")}
              </Button>
              <Button onClick={() => { addYear.mutate(); }} loading={addYear.isPending} data-testid="confirm-add-year">
                {t("calendars.createYear")}
              </Button>
            </DialogFooter>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(newBusiness)} onOpenChange={(isOpen) => { if (!isOpen) { setNewBusiness(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {newBusiness ? (
            <form onSubmit={submitBusiness} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("calendars.newBusiness")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("calendars.newBusinessHint")}</p>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("calendars.code")} required error={problem?.fields.code}>
                  <TextField value={newBusiness.code} onChange={(e) => { setNewBusiness({ ...newBusiness, code: e.target.value.toLowerCase() }); }} required dir="ltr" data-testid="business-code" />
                </Field>
                <Field label={t("calendars.nameEn")} required error={problem?.fields.name}>
                  <TextField value={newBusiness.nameEn} onChange={(e) => { setNewBusiness({ ...newBusiness, nameEn: e.target.value }); }} required data-testid="business-name-en" />
                </Field>
                <Field label={t("calendars.nameAr")}>
                  <TextField value={newBusiness.nameAr} onChange={(e) => { setNewBusiness({ ...newBusiness, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setNewBusiness(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveBusiness.isPending} data-testid="save-business-calendar">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(holiday)} onOpenChange={(isOpen) => { if (!isOpen) { setHoliday(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {holiday ? (
            <form onSubmit={submitHoliday} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("calendars.addHoliday")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("calendars.date")} required error={problem?.fields.onDate}>
                  <TextField type="date" value={holiday.onDate} onChange={(e) => { setHoliday({ ...holiday, onDate: e.target.value }); }} required dir="ltr" data-testid="holiday-date" />
                </Field>
                <Field label={t("calendars.nameEn")} required error={problem?.fields.name}>
                  <TextField value={holiday.nameEn} onChange={(e) => { setHoliday({ ...holiday, nameEn: e.target.value }); }} required data-testid="holiday-name-en" />
                </Field>
                <Field label={t("calendars.nameAr")}>
                  <TextField value={holiday.nameAr} onChange={(e) => { setHoliday({ ...holiday, nameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="holiday-name-ar" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setHoliday(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={addHoliday.isPending} data-testid="save-holiday">
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
