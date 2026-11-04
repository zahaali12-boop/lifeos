import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { DecimalField, Field, FormError, TextareaField, TextField } from "../common";
import { KeyValues } from "../inventory/shared";
import { money, num } from "../sales/shared";

type Level = components["schemas"]["DunningLevelSummary"];
type Letter = components["schemas"]["DunningLetterSummary"];

interface LevelForm {
  id: string | null;
  level: string;
  daysOverdue: string;
  nameEn: string;
  nameAr: string;
  textEn: string;
  textAr: string;
  feeAmount: string;
  blockCustomer: boolean;
  isActive: boolean;
}

/** The reminder ladder and its runs (roadmap 5.7b): levels by days past due with fee and credit hold, a preview of the letters a run would issue, the runs issued and their letters. */
export function DunningPanel({ companyId, partnerId, customers }: { companyId: string; partnerId: string; customers: { partnerId: string; partnerCode: string; partnerName: Record<string, string> }[] }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const canManage = can("receivables.dunning.manage");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<LevelForm | null>(null);
  const [runDate, setRunDate] = useState(today());
  const [openRun, setOpenRun] = useState<string | null>(null);
  const [openLetter, setOpenLetter] = useState<Letter | null>(null);
  const levels = useQuery({
    queryKey: ["dunning-levels", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/dunning/levels", { params: { query: { companyId } } })),
  });
  const preview = useQuery({
    queryKey: ["dunning-preview", companyId, runDate, partnerId],
    enabled: Boolean(companyId) && (levels.data ?? []).length > 0,
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/dunning/preview", { params: { query: { companyId, runDate, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const runs = useQuery({
    queryKey: ["dunning-runs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/dunning/runs", { params: { query: { companyId } } })),
  });
  const run = useQuery({
    queryKey: ["dunning-run", openRun],
    enabled: Boolean(openRun),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/dunning/runs/{runId}", { params: { path: { runId: openRun ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["dunning-levels"], ["dunning-preview"], ["dunning-runs"], ["dunning-run"], ["ar-open-items"], ["ar-open-item"], ["ar-aging"], ["customers"], ["customer-360"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const saveLevel = useMutation({
    mutationFn: async (f: LevelForm) => {
      const body = { companyId, level: Math.trunc(num(f.level)), daysOverdue: Math.trunc(num(f.daysOverdue)), name: { en: f.nameEn, ar: f.nameAr }, text: { en: f.textEn, ar: f.textAr }, feeAmount: num(f.feeAmount), blockCustomer: f.blockCustomer, isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/receivables/dunning/levels/{levelId}", { params: { path: { levelId: f.id } }, body })) : unwrap(await api.POST("/api/v1/receivables/dunning/levels", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await refresh(); },
    onError: fail,
  });
  const issue = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/receivables/dunning/runs", { body: { companyId, runDate, partnerId: partnerId || null, chargeFees: true } })),
    onSuccess: async (issued) => { setProblem(null); setOpenRun(issued.id); await refresh(); },
    onError: fail,
  });
  const sent = useMutation({
    mutationFn: async (letterId: string) => unwrap(await api.POST("/api/v1/receivables/dunning/letters/{letterId}/sent", { params: { path: { letterId } } })),
    onSuccess: async (letter) => { setProblem(null); setOpenLetter(letter); await refresh(); },
    onError: fail,
  });

  const blank = (): LevelForm => ({ id: null, level: String((levels.data ?? []).length + 1), daysOverdue: "", nameEn: "", nameAr: "", textEn: "", textAr: "", feeAmount: "", blockCustomer: false, isActive: true });
  const edit = (l: Level): LevelForm => ({ id: l.id, level: String(l.level), daysOverdue: String(l.daysOverdue), nameEn: l.name.en ?? "", nameAr: l.name.ar ?? "", textEn: l.text.en ?? "", textAr: l.text.ar ?? "", feeAmount: Number(l.feeAmount) > 0 ? String(l.feeAmount) : "", blockCustomer: l.blockCustomer, isActive: l.isActive });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { saveLevel.mutate(form); } };
  const customerName = (id: string): string => { const c = customers.find((x) => x.partnerId === id); return c ? `${c.partnerCode} · ${localized(c.partnerName)}` : ""; };
  const fc = levels.data?.[0]?.feeCurrency ?? preview.data?.functionalCurrency ?? "";

  return (
    <div className="mt-3 flex flex-col gap-6" data-testid="dunning-panel">
      <FormError message={problem?.message ?? null} />
      <section className="flex flex-col gap-2" aria-labelledby="dunning-levels-title">
        <div className="flex items-center justify-between">
          <h3 id="dunning-levels-title" className="text-sm font-semibold">{t("receivables.dunning.levels")}</h3>
          {canManage ? <Button size="sm" variant="secondary" onClick={() => { setProblem(null); setForm(blank()); }} data-testid="new-dunning-level"><Plus aria-hidden="true" />{t("receivables.dunning.newLevel")}</Button> : null}
        </div>
        {(levels.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("receivables.dunning.noLevels")}</p> : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("receivables.dunning.level")}</TableHead>
                <TableHead>{t("common.name")}</TableHead>
                <TableHead>{t("receivables.dunning.daysOverdue")}</TableHead>
                <TableHead>{t("receivables.dunning.fee")}</TableHead>
                <TableHead>{t("receivables.dunning.blocks")}</TableHead>
                <TableHead>{t("common.status")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(levels.data ?? []).map((l) => (
                <TableRow key={l.id} data-testid="dunning-level-row">
                  <TableCell>{String(l.level)}</TableCell>
                  <TableCell dir="auto">{localized(l.name)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{String(l.daysOverdue)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{Number(l.feeAmount) > 0 ? money(l.feeAmount, l.feeCurrency) : "—"}</TableCell>
                  <TableCell>{l.blockCustomer ? t("common.yes") : t("common.no")}</TableCell>
                  <TableCell><Badge tone={l.isActive ? "success" : "neutral"}>{l.isActive ? t("common.active") : t("common.inactive")}</Badge></TableCell>
                  <TableCell>{canManage ? <Button variant="ghost" size="sm" onClick={() => { setProblem(null); setForm(edit(l)); }}>{t("common.edit")}</Button> : null}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      <section className="flex flex-col gap-2" aria-labelledby="dunning-preview-title">
        <div className="flex flex-wrap items-end gap-3">
          <h3 id="dunning-preview-title" className="text-sm font-semibold">{t("receivables.dunning.due")}</h3>
          <Field label={t("receivables.dunning.runDate")}>
            <TextField type="date" value={runDate} onChange={(e) => { setRunDate(e.target.value); }} dir="ltr" data-testid="dunning-run-date" />
          </Field>
          {canManage ? <Button onClick={() => { issue.mutate(); }} loading={issue.isPending} disabled={(preview.data?.letters.length ?? 0) === 0} data-testid="issue-dunning">{t("receivables.dunning.issue")}</Button> : null}
        </div>
        {(preview.data?.letters.length ?? 0) === 0 ? <p className="text-xs text-fg-muted" data-testid="dunning-nothing-due">{t("receivables.dunning.nothingDue")}</p> : (
          <Table data-testid="dunning-preview">
            <TableHeader>
              <TableRow>
                <TableHead>{t("sales.customer")}</TableHead>
                <TableHead>{t("receivables.dunning.level")}</TableHead>
                <TableHead>{t("receivables.dunning.items")}</TableHead>
                <TableHead>{t("receivables.dunning.overdue")}</TableHead>
                <TableHead>{t("receivables.dunning.fee")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(preview.data?.letters ?? []).map((l) => (
                <TableRow key={l.partnerId} data-testid="dunning-preview-row">
                  <TableCell dir="auto">{l.partnerCode} · {localized(l.partnerName)}</TableCell>
                  <TableCell>{String(l.level)} · {localized(l.levelName)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{String(l.itemCount)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{money(l.overdueFc, l.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{Number(l.feeAmount) > 0 ? money(l.feeAmount, l.functionalCurrency) : "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      <section className="flex flex-col gap-2" aria-labelledby="dunning-runs-title">
        <h3 id="dunning-runs-title" className="text-sm font-semibold">{t("receivables.dunning.runs")}</h3>
        {(runs.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("receivables.dunning.noRuns")}</p> : (
          <Table data-testid="dunning-runs">
            <TableHeader>
              <TableRow>
                <TableHead>{t("sales.number")}</TableHead>
                <TableHead>{t("receivables.dunning.runDate")}</TableHead>
                <TableHead>{t("receivables.dunning.letters")}</TableHead>
                <TableHead>{t("receivables.dunning.fees")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(runs.data ?? []).map((r) => (
                <TableRow key={r.id} data-testid="dunning-run-row">
                  <TableCell dir="ltr">{r.number}</TableCell>
                  <TableCell dir="ltr">{formatDate(r.runDate)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{String(r.letters)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{money(r.feesFc, r.functionalCurrency)}</TableCell>
                  <TableCell><Button variant="ghost" size="sm" onClick={() => { setOpenRun(r.id); }} data-testid="open-dunning-run">{t("common.open")}</Button></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("receivables.dunning.editLevel") : t("receivables.dunning.newLevel")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("receivables.dunning.level")} required>
                  <TextField inputMode="numeric" value={form.level} onChange={(e) => { setForm({ ...form, level: e.target.value }); }} dir="ltr" required data-testid="level-number" />
                </Field>
                <Field label={t("receivables.dunning.daysOverdue")} required description={t("receivables.dunning.daysOverdueHint")}>
                  <TextField inputMode="numeric" value={form.daysOverdue} onChange={(e) => { setForm({ ...form, daysOverdue: e.target.value }); }} dir="ltr" required data-testid="level-days" />
                </Field>
                <Field label={t("common.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} required data-testid="level-name-en" />
                </Field>
                <Field label={t("common.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} required dir="rtl" data-testid="level-name-ar" />
                </Field>
                <Field label={t("receivables.dunning.textEn")}>
                  <TextareaField value={form.textEn} onChange={(e) => { setForm({ ...form, textEn: e.target.value }); }} rows={3} data-testid="level-text-en" />
                </Field>
                <Field label={t("receivables.dunning.textAr")}>
                  <TextareaField value={form.textAr} onChange={(e) => { setForm({ ...form, textAr: e.target.value }); }} rows={3} dir="rtl" data-testid="level-text-ar" />
                </Field>
                <Field label={t("receivables.dunning.fee")} description={fc}>
                  <DecimalField value={form.feeAmount} onChange={(e) => { setForm({ ...form, feeAmount: e.target.value }); }} dir="ltr" data-testid="level-fee" />
                </Field>
                <div className="flex flex-col gap-2 self-end text-sm">
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.blockCustomer} onChange={(e) => { setForm({ ...form, blockCustomer: e.target.checked }); }} data-testid="level-block" />{t("receivables.dunning.blockCustomer")}</label>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="level-active" />{t("common.active")}</label>
                </div>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={saveLevel.isPending} data-testid="save-dunning-level">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openRun)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenRun(null); setOpenLetter(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {run.data ? (
            <div className="flex flex-col gap-4" data-testid="dunning-run-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold"><span dir="ltr">{run.data.number}</span><span dir="ltr">{formatDate(run.data.runDate)}</span></DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              {openLetter ? (
                <div className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="dunning-letter">
                  <KeyValues entries={[
                    [t("sales.number"), <span key="n" dir="ltr">{openLetter.number}</span>],
                    [t("sales.customer"), customerName(openLetter.partnerId) || `${openLetter.partnerCode} · ${localized(openLetter.partnerName)}`],
                    [t("receivables.dunning.level"), `${String(openLetter.level)} · ${localized(openLetter.levelName)}`],
                    [t("receivables.dunning.overdue"), money(openLetter.overdueFc, openLetter.functionalCurrency)],
                    [t("receivables.dunning.fee"), Number(openLetter.feeAmount) > 0 ? money(openLetter.feeAmount, openLetter.functionalCurrency) : "—"],
                    [t("receivables.dunning.blocks"), openLetter.blockedCustomer ? t("common.yes") : t("common.no")],
                    [t("receivables.dunning.sent"), openLetter.sentAt ? formatDate(openLetter.sentAt) : t("receivables.dunning.notSent")],
                  ]} />
                  {localized(openLetter.text) ? <p className="text-sm" dir="auto">{localized(openLetter.text)}</p> : null}
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("sales.number")}</TableHead>
                        <TableHead>{t("sales.invoicing.dueDate")}</TableHead>
                        <TableHead>{t("receivables.dunning.daysLate")}</TableHead>
                        <TableHead>{t("purchasing.remaining")}</TableHead>
                        <TableHead>{t("receivables.dunning.level")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {openLetter.items.map((i) => (
                        <TableRow key={i.itemId} data-testid="dunning-letter-item">
                          <TableCell dir="ltr">{i.documentNumber}{Number(i.instalment) > 1 ? ` / ${String(i.instalment)}` : ""}</TableCell>
                          <TableCell dir="ltr">{formatDate(i.dueDate)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{String(i.daysOverdue)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{money(i.remainingTc, i.currency)}</TableCell>
                          <TableCell>{String(i.level)}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  <div className="flex gap-2">
                    <Button variant="secondary" size="sm" onClick={() => { setOpenLetter(null); }}>{t("common.back")}</Button>
                    {canManage && !openLetter.sentAt ? <Button size="sm" onClick={() => { sent.mutate(openLetter.id); }} loading={sent.isPending} data-testid="mark-letter-sent">{t("receivables.dunning.markSent")}</Button> : null}
                  </div>
                </div>
              ) : (
                <Table data-testid="dunning-letters">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("sales.number")}</TableHead>
                      <TableHead>{t("sales.customer")}</TableHead>
                      <TableHead>{t("receivables.dunning.level")}</TableHead>
                      <TableHead>{t("receivables.dunning.items")}</TableHead>
                      <TableHead>{t("receivables.dunning.overdue")}</TableHead>
                      <TableHead>{t("receivables.dunning.sent")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {run.data.letterList.map((l) => (
                      <TableRow key={l.id} data-testid="dunning-letter-row">
                        <TableCell dir="ltr">{l.number}</TableCell>
                        <TableCell dir="auto">{l.partnerCode} · {localized(l.partnerName)}</TableCell>
                        <TableCell>{String(l.level)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{String(l.itemCount)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{money(l.overdueFc, l.functionalCurrency)}</TableCell>
                        <TableCell>{l.sentAt ? formatDate(l.sentAt) : "—"}</TableCell>
                        <TableCell><Button variant="ghost" size="sm" onClick={() => { setOpenLetter(l); }} data-testid="open-dunning-letter">{t("common.open")}</Button></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}
