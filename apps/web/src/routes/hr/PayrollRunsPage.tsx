import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { usePref } from "../../lib/prefs";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { PrintButton } from "../print/PrintDialog";
import { RecordActivity } from "../RecordDiscussion";
import { StatusBar } from "../StatusBar";
import { periodLabel, runFlow, RunStatus, runStatuses, type PayrollRun, type PayrollRunLine } from "./shared";

interface RunForm {
  year: string;
  month: string;
  payDate: string;
  postingDate: string;
  notes: string;
}

interface PayForm {
  bankAccountId: string;
  payDate: string;
  reference: string;
}

/**
 * Payroll runs (founder programme item 8): a month calculated for every employee employed in it, submitted (held for
 * approval when a workflow says so), posted through the posting engine with each employee's net as a payable, paid
 * from the bank one employee at a time, and printed as payslips.
 */
export function PayrollRunsPage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<RunForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/hr/payroll");
  const [pay, setPay] = useState<PayForm | null>(null);
  const [reversal, setReversal] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const banks = useBankAccounts(companyId);
  const list = useQuery({
    queryKey: ["payroll-runs", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/runs", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["payroll-run", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payroll/runs/{runId}", { params: { path: { runId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["payroll-runs"], ["payroll-run"], ["open-items"], ["bank-accounts"], ["bank-transactions"], ["payments"], ["leaves"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const create = useMutation({
    mutationFn: async (f: RunForm) => unwrap(await api.POST("/api/v1/payroll/runs", { body: { companyId, year: Number(f.year), month: Number(f.month), payDate: f.payDate || null, postingDate: f.postingDate || null, notes: f.notes || null } })),
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "calculate" | "submit" | "post" | "cancel" } | { id: string; action: "reverse"; reason: string } | { id: string; action: "pay"; pay: PayForm }) => {
      const params = { path: { runId: input.id } };
      switch (input.action) {
        case "calculate": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/calculate", { params }));
        case "submit": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/submit", { params }));
        case "post": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/post", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/cancel", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/reverse", { params, body: { reason: input.reason, reversalDate: null } }));
        case "pay": return unwrap(await api.POST("/api/v1/payroll/runs/{runId}/pay", { params, body: { bankAccountId: input.pay.bankAccountId, payDate: input.pay.payDate || null, reference: input.pay.reference || null } }));
      }
    },
    onSuccess: async () => { setProblem(null); setReversal(null); setPay(null); await refresh(); },
    onError: fail,
  });

  const period = (r: Pick<PayrollRun, "periodYear" | "periodMonth">): string => periodLabel(Number(r.periodYear), Number(r.periodMonth), i18n.language);
  const columns = useMemo<ColumnDef<PayrollRun, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "period", accessorFn: (r) => period(r), header: t("hr.period"), size: 150 },
      { id: "status", accessorFn: (r) => t(`hr.runStatuses.${r.status}`), header: t("common.status"), size: 140, cell: ({ row }) => <RunStatus status={row.original.status} /> },
      { id: "employees", accessorKey: "employeesCount", header: t("nav.employees"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.employeesCount)}</span> },
      { id: "gross", accessorKey: "totalGross", header: t("hr.gross"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
      { id: "deductions", accessorKey: "totalDeductions", header: t("hr.deductions"), size: 130, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalDeductions, row.original.currency)}</span> },
      { id: "net", accessorKey: "totalNet", header: t("hr.net"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalNet, row.original.currency)}</span> },
      { id: "employer", accessorKey: "totalEmployer", header: t("hr.employerContributions"), size: 150, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalEmployer, row.original.currency)}</span> },
      { id: "payDate", accessorKey: "payDate", header: t("hr.payDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.payDate)}</span> },
      { id: "postingDate", accessorKey: "postingDate", header: t("accounting.postingDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the period label follows the interface language
    [t, i18n.language],
  );

  const [lastBank, rememberBank] = usePref(`bank:${companyId}`);
  const now = new Date();
  const blank = (): RunForm => ({ year: String(now.getFullYear()), month: String(now.getMonth() + 1), payDate: "", postingDate: "", notes: "" });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const r = detail.data;
  const flow = r ? runFlow(r, t) : null;
  const money = (value: number | string): string => (r ? formatMoney(value, r.currency) : String(value));

  useNewFromUrl("/hr/payroll", () => { setProblem(null); setForm(blank()); }, Boolean(companyId));
  return (
    <>
      <PageHeader
        title={t("nav.payroll")}
        description={t("hr.runsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-run">
            <Plus aria-hidden="true" />
            {t("hr.newRun")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(ev) => { setStatus(ev.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {runStatuses.map((s) => (
              <option key={s} value={s}>{t(`hr.runStatuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<PayrollRun> label="nav.payroll" entityType="payroll_run" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("hr.emptyRuns")} emptyDescription={t("hr.emptyRunsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setPay(null); setExpanded(null); setOpenId(row.id); }} groupable searchable viewExtras={() => ({ status })} onApplyView={(extras) => { setStatus(typeof extras?.status === "string" ? extras.status : ""); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("hr.newRun")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <p className="text-sm text-muted-foreground">{t("hr.newRunHelp")}</p>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("hr.year")} required>
                  <TextField inputMode="numeric" value={form.year} onChange={(ev) => { setForm({ ...form, year: ev.target.value }); }} dir="ltr" required data-testid="run-year" />
                </Field>
                <Field label={t("hr.month")} required>
                  <SelectField value={form.month} onChange={(ev) => { setForm({ ...form, month: ev.target.value }); }} data-testid="run-month">
                    {Array.from({ length: 12 }, (_, i) => i + 1).map((m) => (
                      <option key={m} value={String(m)}>{periodLabel(Number(form.year) || now.getFullYear(), m, i18n.language)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("hr.payDate")} description={t("hr.payDateHelp")}>
                  <TextField type="date" value={form.payDate} onChange={(ev) => { setForm({ ...form, payDate: ev.target.value }); }} dir="ltr" data-testid="run-pay-date" />
                </Field>
                <Field label={t("accounting.postingDate")} description={t("hr.postingDateHelp")}>
                  <TextField type="date" value={form.postingDate} onChange={(ev) => { setForm({ ...form, postingDate: ev.target.value }); }} dir="ltr" data-testid="run-posting-date" />
                </Field>
                <Field label={t("hr.notes")} className="sm:col-span-2">
                  <TextareaField rows={1} value={form.notes} onChange={(ev) => { setForm({ ...form, notes: ev.target.value }); }} data-testid="run-notes" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} disabled={!form.year.trim()} data-testid="save-run">{t("hr.calculate")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); setPay(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {r && flow ? (
            <div className="flex flex-col gap-4" data-testid="run-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <span>{period(r)}</span>
                  <RunStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <StatusBar {...flow} actions={(
                <>
                  {r.status === "calculated" ? <Button onClick={() => { act.mutate({ id: r.id, action: "submit" }); }} loading={act.isPending} data-testid="submit-run">{t("hr.submit")}</Button> : null}
                  {r.status === "calculated" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "calculate" }); }} loading={act.isPending} data-testid="recalculate-run">{t("hr.recalculate")}</Button> : null}
                  {r.status === "approved" ? <Button onClick={() => { act.mutate({ id: r.id, action: "post" }); }} loading={act.isPending} data-testid="post-run">{t("hr.post")}</Button> : null}
                  {r.status === "posted" && !pay && can("payroll.run.pay") ? <Button onClick={() => { setProblem(null); setReversal(null); setPay({ bankAccountId: lastBank, payDate: r.payDate, reference: r.number }); }} data-testid="pay-run">{t("hr.pay")}</Button> : null}
                  {r.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setPay(null); setReversal(""); }} data-testid="reverse-run">{t("purchasing.reverse")}</Button> : null}
                  {r.status === "calculated" || r.status === "pending_approval" || r.status === "approved" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-run">{t("hr.cancelRun")}</Button> : null}
                </>
              )} />
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("hr.period"), `${formatDate(r.periodStart)} – ${formatDate(r.periodEnd)}`],
                [t("hr.payDate"), formatDate(r.payDate)],
                [t("accounting.postingDate"), formatDate(r.postingDate)],
                [t("nav.employees"), String(r.employeesCount)],
                [t("hr.gross"), <span key="gross" data-testid="run-gross">{money(r.totalGross)}</span>],
                [t("hr.deductions"), money(r.totalDeductions)],
                [t("hr.net"), <span key="net" data-testid="run-net">{money(r.totalNet)}</span>],
                [t("hr.employerContributions"), money(r.totalEmployer)],
                ...(r.journalEntryId ? [[t("pos.journal"), <Link key="journal" to="/accounting/journal-entries" search={{ open: r.journalEntryId }} className="underline" data-testid="run-journal">{t("pos.openJournal")}</Link>] as [string, React.ReactNode]] : []),
                ...(r.rejectionReason ? [[t("expenses.rejectionReason"), r.rejectionReason] as [string, string]] : []),
                ...(r.notes ? [[t("hr.notes"), r.notes] as [string, string]] : []),
              ]} />
              <Table data-testid="run-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("hr.employee")}</TableHead>
                    <TableHead>{t("hr.paidDays")}</TableHead>
                    <TableHead>{t("hr.basic")}</TableHead>
                    <TableHead>{t("hr.otherEarnings")}</TableHead>
                    <TableHead>{t("hr.gross")}</TableHead>
                    <TableHead>{t("hr.deductions")}</TableHead>
                    <TableHead>{t("hr.net")}</TableHead>
                    <TableHead>{t("hr.payment")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {r.lines.map((l: PayrollRunLine) => (
                    <TableRow key={l.id} data-testid="run-line">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">
                        <button type="button" className="text-start underline-offset-2 hover:underline" onClick={() => { setExpanded(expanded === l.id ? null : l.id); }} aria-expanded={expanded === l.id} data-testid={`line-toggle-${String(l.lineNo)}`}>
                          <span dir="ltr">{l.employeeNumber}</span> · {localized(l.employeeName)}
                        </button>
                        {expanded === l.id ? (
                          <ul className="mt-1 flex flex-col gap-0.5 text-xs text-muted-foreground" data-testid={`line-items-${String(l.lineNo)}`}>
                            {l.items.map((i) => (
                              <li key={i.id} className="flex justify-between gap-3">
                                <span dir="auto">{i.componentCode} · {localized(i.componentName)}{Number(i.ratePct) !== 0 ? ` (${formatNumber(i.ratePct)} %)` : ""}</span>
                                <span className="tabular" dir="ltr">{i.kind === "deduction" ? "−" : ""}{money(i.amount)}</span>
                              </li>
                            ))}
                          </ul>
                        ) : null}
                      </TableCell>
                      <TableCell className="tabular" dir="ltr" data-testid={`line-days-${String(l.lineNo)}`}>{formatNumber(l.paidDays)} / {String(l.daysInPeriod)}</TableCell>
                      <TableCell className="tabular" dir="ltr" data-testid={`line-basic-${String(l.lineNo)}`}>{money(l.basicAmount)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{money(l.earningsTotal)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{money(l.gross)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{money(l.deductionsTotal)}</TableCell>
                      <TableCell className="tabular" dir="ltr" data-testid={`line-net-${String(l.lineNo)}`}>{money(l.net)}</TableCell>
                      <TableCell dir="ltr">{l.paymentNumber ? `${l.paymentNumber} · ${formatDate(l.paidOn ?? "")}` : "—"}</TableCell>
                      <TableCell>
                        {r.status === "posted" || r.status === "paid" ? <PrintButton subject={{ documentType: "payslip", documentId: l.id, number: `${r.number}-${String(l.lineNo).padStart(2, "0")}`, partnerId: l.partnerId }} testId={`print-payslip-${String(l.lineNo)}`} /> : null}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {pay ? (
                <form onSubmit={(event) => { event.preventDefault(); rememberBank(pay.bankAccountId); act.mutate({ id: r.id, action: "pay", pay }); }} className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-4" data-testid="pay-form">
                  <Field label={t("nav.bankAccounts")} description={t("hr.payBankHelp")} required>
                    <SelectField value={pay.bankAccountId} onChange={(ev) => { setPay({ ...pay, bankAccountId: ev.target.value }); }} required data-testid="pay-bank">
                      <option value="">—</option>
                      {(banks.data ?? []).filter((b) => b.currency === r.currency).map((b) => (
                        <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("hr.payDate")} required>
                    <TextField type="date" value={pay.payDate} onChange={(ev) => { setPay({ ...pay, payDate: ev.target.value }); }} dir="ltr" required data-testid="pay-date" />
                  </Field>
                  <Field label={t("banking.reference")}>
                    <TextField value={pay.reference} onChange={(ev) => { setPay({ ...pay, reference: ev.target.value }); }} dir="ltr" data-testid="pay-reference" />
                  </Field>
                  <div className="flex items-end gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setPay(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={act.isPending} disabled={!pay.bankAccountId} data-testid="confirm-pay">{t("hr.payNow", { amount: money(r.totalNet) })}</Button>
                  </div>
                </form>
              ) : null}
              {reversal !== null ? (
                <div className="flex flex-wrap items-end gap-3">
                  <Field label={t("purchasing.reversalReason")} required>
                    <TextField value={reversal} onChange={(ev) => { setReversal(ev.target.value); }} data-testid="reversal-reason" />
                  </Field>
                  <Button onClick={() => { act.mutate({ id: r.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button>
                </div>
              ) : null}
              <RecordActivity entityType="payroll_run" entityId={r.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
