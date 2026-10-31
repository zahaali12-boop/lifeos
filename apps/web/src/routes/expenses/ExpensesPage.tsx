import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { usePref } from "../../lib/prefs";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { CurrencyField } from "../CurrencyField";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { lineKeys, pastedCode, pastedNumber } from "../lineKeys";
import { MoreFields } from "../MoreFields";
import { useBankAccounts } from "../payables/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { QuickCreateButton } from "../QuickCreate";
import { RecordActivity } from "../RecordDiscussion";
import { StatusBar } from "../StatusBar";
import { expenseFlow, expenseKinds, expenseStatuses, ExpenseStatus, payeeOf, PaymentStatus, useExpenseCategories, useFunds, useTaxCodeOptions, type Expense } from "./shared";

interface LineForm {
  categoryCode: string;
  description: string;
  amount: string;
  taxCodeId: string;
}

interface ExpenseForm {
  id: string | null;
  kind: string;
  partnerId: string;
  fundId: string;
  expenseDate: string;
  dueDate: string;
  currency: string;
  reference: string;
  description: string;
  customFields: CustomFieldValues;
  lines: LineForm[];
}

interface PayForm {
  bankAccountId: string;
  paymentDate: string;
  amount: string;
  method: string;
  reference: string;
}

const emptyLine = (): LineForm => ({ categoryCode: "", description: "", amount: "", taxCodeId: "" });

/**
 * Expenses (founder programme item 2, roadmap 6.4): a supplier bill without an order, an employee's claim or a petty cash
 * voucher, recorded with its date and lines; confirming posts the accrual, paying settles it on the payment's date.
 */
export function ExpensesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [kind, setKind] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ExpenseForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/expenses");
  const [pay, setPay] = useState<PayForm | null>(null);
  const [reversal, setReversal] = useState<string | null>(null);
  const suppliers = useSuppliers(companyId);
  const employees = useQuery({
    queryKey: ["partners", "employees"],
    queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "employee", limit: 200 } } })),
  });
  const funds = useFunds(companyId);
  const banks = useBankAccounts(companyId);
  const categories = useExpenseCategories(true);
  const taxCodes = useTaxCodeOptions();
  const list = useQuery({
    queryKey: ["expenses", companyId, status, kind],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses", { params: { query: { companyId, ...(status ? { status } : {}), ...(kind ? { kind } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["expense", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/{expenseId}", { params: { path: { expenseId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["expenses"], ["expense"], ["expense-funds"], ["expense-fund"], ["open-items"], ["open-item"], ["bank-accounts"], ["bank-account"], ["bank-transactions"], ["payments"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const categoryId = (code: string): string => (categories.data ?? []).find((c) => c.code === code.trim().toUpperCase())?.id ?? "";
  const save = useMutation({
    mutationFn: async (f: ExpenseForm) => {
      const pettyCash = f.kind === "petty_cash";
      const body = {
        companyId,
        kind: f.kind,
        partnerId: pettyCash ? null : f.partnerId || null,
        fundId: pettyCash ? f.fundId || null : null,
        expenseDate: f.expenseDate,
        dueDate: f.dueDate || null,
        currency: f.currency || null,
        reference: f.reference || null,
        description: f.description || null,
        customFields: f.customFields,
        lines: f.lines.filter((l) => l.categoryCode.trim() || l.amount.trim()).map((l) => ({ categoryId: categoryId(l.categoryCode), amount: num(l.amount), description: l.description || null, taxCodeId: l.taxCodeId || null, dimensions: null })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/expenses/{expenseId}", { params: { path: { expenseId: f.id } }, body })) : unwrap(await api.POST("/api/v1/expenses", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "confirm" | "cancel" | "delete" } | { id: string; action: "reverse"; reason: string } | { id: string; action: "pay"; pay: PayForm }) => {
      const params = { path: { expenseId: input.id } };
      switch (input.action) {
        case "confirm": return unwrap(await api.POST("/api/v1/expenses/{expenseId}/confirm", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/expenses/{expenseId}/cancel", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/expenses/{expenseId}/reverse", { params, body: { reason: input.reason, reversalDate: null } }));
        case "pay": return unwrap(await api.POST("/api/v1/expenses/{expenseId}/pay", { params, body: { bankAccountId: input.pay.bankAccountId, paymentDate: input.pay.paymentDate || null, amount: input.pay.amount.trim() ? num(input.pay.amount) : null, method: input.pay.method, reference: input.pay.reference || null } })).expense;
        case "delete": { unwrap(await api.DELETE("/api/v1/expenses/{expenseId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); setPay(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Expense, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorFn: (r) => t(`expenses.statuses.${r.status}`), header: t("common.status"), size: 130, cell: ({ row }) => <ExpenseStatus status={row.original.status} /> },
      { id: "kind", accessorFn: (r) => t(`expenses.kinds.${r.kind}`), header: t("expenses.kind"), size: 150 },
      { id: "payee", accessorFn: (r) => payeeOf(r), header: t("expenses.payee"), size: 220, cell: ({ row }) => <span dir="auto">{payeeOf(row.original)}</span> },
      { id: "date", accessorKey: "expenseDate", header: t("expenses.expenseDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.expenseDate)}</span> },
      { id: "dueDate", accessorKey: "dueDate", header: t("expenses.dueDate"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.dueDate ? formatDate(row.original.dueDate) : ""}</span> },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 80 },
      { id: "net", accessorKey: "totalNet", header: t("expenses.net"), size: 130, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalNet, row.original.currency)}</span> },
      { id: "tax", accessorKey: "totalTax", header: t("expenses.tax"), size: 110, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalTax, row.original.currency)}</span> },
      { id: "gross", accessorKey: "totalGross", header: t("expenses.total"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
      { id: "payment", accessorFn: (r) => (r.paymentStatus === "n/a" ? "" : t(`expenses.paymentStatuses.${r.paymentStatus}`)), header: t("expenses.payment"), size: 120, cell: ({ row }) => <PaymentStatus status={row.original.paymentStatus} /> },
      { id: "reference", accessorKey: "reference", header: t("expenses.reference"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.reference ?? ""}</span> },
      { id: "description", accessorKey: "description", header: t("expenses.descriptionLabel"), size: 240, cell: ({ row }) => <span dir="auto">{row.original.description ?? ""}</span> },
    ],
    [t],
  );

  const [lastBank, rememberBank] = usePref(`bank:${companyId}`);
  const blank = (): ExpenseForm => ({ id: null, kind: "supplier", partnerId: "", fundId: "", expenseDate: today(), dueDate: "", currency: "", reference: "", description: "", customFields: {}, lines: [emptyLine(), emptyLine()] });
  const patchLine = (index: number, patch: Partial<LineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...patch } : l)) }); } };
  const keys = lineKeys<LineForm>({
    lines: form?.lines ?? [],
    onChange: (lines) => { if (form) { setForm({ ...form, lines }); } },
    empty: emptyLine,
    columns: ["categoryCode", "description", "amount"],
    parse: { categoryCode: pastedCode, amount: pastedNumber },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const e = detail.data;
  const flow = e ? expenseFlow(e, t) : null;
  const canEdit = e?.status === "draft" || e?.status === "rejected";
  const payable = e?.status === "confirmed" && e.kind !== "petty_cash" && Number(e.remainingTc) > 0;
  const openForm = (x: Expense): void => {
    setProblem(null);
    setForm({ id: x.id, kind: x.kind, partnerId: x.partnerId ?? "", fundId: x.fundId ?? "", expenseDate: x.expenseDate, dueDate: x.dueDate ?? "", currency: x.currency, reference: x.reference ?? "", description: x.description ?? "", customFields: asCustomFieldValues(x.customFields), lines: (x.lines ?? []).map((l) => ({ categoryCode: l.categoryCode, description: l.description ?? "", amount: String(l.amount), taxCodeId: l.taxCodeId ?? "" })) });
  };

  useNewFromUrl("/expenses", () => { setProblem(null); setForm(blank()); }, Boolean(companyId));
  return (
    <>
      <PageHeader
        title={t("nav.expenses")}
        description={t("expenses.description")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-expense">
            <Plus aria-hidden="true" />
            {t("expenses.newExpense")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(ev) => { setStatus(ev.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {expenseStatuses.map((s) => (
              <option key={s} value={s}>{t(`expenses.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("expenses.kind")}>
          <SelectField value={kind} onChange={(ev) => { setKind(ev.target.value); }} data-testid="kind-filter">
            <option value="">{t("common.all")}</option>
            {expenseKinds.map((k) => (
              <option key={k} value={k}>{t(`expenses.kinds.${k}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Expense> label="nav.expenses" entityType="expense" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("expenses.emptyExpenses")} emptyDescription={t("expenses.emptyExpensesDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setPay(null); setOpenId(row.id); }} groupable searchable viewExtras={() => ({ status, kind })} onApplyView={(extras) => { setStatus(typeof extras?.status === "string" ? extras.status : ""); setKind(typeof extras?.kind === "string" ? extras.kind : ""); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("expenses.editExpense") : t("expenses.newExpense")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("expenses.kind")} required>
                  <SelectField value={form.kind} onChange={(ev) => { setForm({ ...form, kind: ev.target.value, partnerId: "", fundId: "" }); }} disabled={Boolean(form.id)} data-testid="expense-kind">
                    {expenseKinds.map((k) => (
                      <option key={k} value={k}>{t(`expenses.kinds.${k}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                {form.kind === "supplier" ? (
                  <Field label={t("partners.supplier")} required>
                    <div className="flex items-center gap-1">
                      <SelectField value={form.partnerId} onChange={(ev) => { setForm({ ...form, partnerId: ev.target.value }); }} required data-testid="expense-supplier">
                        <option value="">—</option>
                        {(suppliers.data ?? []).map((sup) => (
                          <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
                        ))}
                      </SelectField>
                      <QuickCreateButton kind="supplier" companyId={companyId} currency={form.currency || (companies.find((c) => c.id === companyId)?.functionalCurrency ?? "")} onCreated={(created) => { setForm({ ...form, partnerId: created.id }); }} />
                    </div>
                  </Field>
                ) : null}
                {form.kind === "employee" ? (
                  <Field label={t("expenses.employee")} description={t("expenses.employeeHelp")} required>
                    <SelectField value={form.partnerId} onChange={(ev) => { setForm({ ...form, partnerId: ev.target.value }); }} required data-testid="expense-employee">
                      <option value="">—</option>
                      {(employees.data?.items ?? []).map((p) => (
                        <option key={p.id} value={p.id}>{p.code} · {localized(p.legalName)}</option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                {form.kind === "petty_cash" ? (
                  <Field label={t("expenses.fund")} required>
                    <SelectField value={form.fundId} onChange={(ev) => { const fund = (funds.data ?? []).find((x) => x.id === ev.target.value); setForm({ ...form, fundId: ev.target.value, currency: fund?.currency ?? form.currency }); }} required data-testid="expense-fund">
                      <option value="">—</option>
                      {(funds.data ?? []).filter((x) => x.isActive).map((x) => (
                        <option key={x.id} value={x.id}>{x.code} · {localized(x.name)} · {x.currency}</option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("expenses.expenseDate")} required>
                  <TextField type="date" value={form.expenseDate} onChange={(ev) => { setForm({ ...form, expenseDate: ev.target.value }); }} dir="ltr" required data-testid="expense-date" />
                </Field>
                {form.kind !== "petty_cash" ? (
                  <Field label={t("expenses.dueDate")}>
                    <TextField type="date" value={form.dueDate} onChange={(ev) => { setForm({ ...form, dueDate: ev.target.value }); }} dir="ltr" data-testid="expense-due-date" />
                  </Field>
                ) : null}
                <Field label={t("expenses.reference")}>
                  <TextField value={form.reference} onChange={(ev) => { setForm({ ...form, reference: ev.target.value }); }} dir="ltr" data-testid="expense-reference" />
                </Field>
                <Field label={t("expenses.descriptionLabel")} className="sm:col-span-2">
                  <TextareaField rows={1} value={form.description} onChange={(ev) => { setForm({ ...form, description: ev.target.value }); }} data-testid="expense-description" />
                </Field>
              </div>
              <MoreFields filled={Boolean(form.currency)}>
                <div className="grid gap-4 sm:grid-cols-3">
                  <Field label={t("partners.currency")} description={t("expenses.currencyHelp")}>
                    <CurrencyField value={form.currency} onChange={(code) => { setForm({ ...form, currency: code }); }} allowEmpty disabled={form.kind === "petty_cash"} data-testid="expense-currency" />
                  </Field>
                </div>
                <CustomFieldsFieldset entityType="expense" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
              </MoreFields>
              <div className="flex flex-col gap-2" data-testid="expense-lines">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-semibold">{t("expenses.lines")}</h3>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: [...form.lines, emptyLine()] }); }} data-testid="add-line">{t("purchasing.addLine")}</Button>
                </div>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("expenses.category")}</TableHead>
                      <TableHead>{t("expenses.descriptionLabel")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("expenses.taxCode")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody onKeyDown={keys.onKeyDown} onPaste={keys.onPaste}>
                    {form.lines.map((line, index) => (
                      <TableRow key={String(index)} data-testid="expense-line">
                        <TableCell>
                          <SelectField aria-label={t("expenses.category")} value={line.categoryCode} onChange={(ev) => { patchLine(index, { categoryCode: ev.target.value }); }} {...keys.cell(index, "categoryCode")} data-testid={`line-category-${String(index)}`}>
                            <option value="">—</option>
                            {(categories.data ?? []).map((c) => (
                              <option key={c.id} value={c.code}>{c.code} · {localized(c.name)}</option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell><TextField aria-label={t("expenses.descriptionLabel")} value={line.description} onChange={(ev) => { patchLine(index, { description: ev.target.value }); }} {...keys.cell(index, "description")} data-testid={`line-description-${String(index)}`} /></TableCell>
                        <TableCell><TextField aria-label={t("purchasing.amount")} inputMode="decimal" value={line.amount} onChange={(ev) => { patchLine(index, { amount: ev.target.value }); }} dir="ltr" className="w-32" {...keys.cell(index, "amount")} data-testid={`line-amount-${String(index)}`} /></TableCell>
                        <TableCell>
                          <SelectField aria-label={t("expenses.taxCode")} value={line.taxCodeId} onChange={(ev) => { patchLine(index, { taxCodeId: ev.target.value }); }} data-testid={`line-tax-${String(index)}`}>
                            <option value="">{t("expenses.taxOfCategory")}</option>
                            {taxCodes.map((c) => (
                              <option key={c.id} value={c.id}>{c.label}</option>
                            ))}
                          </SelectField>
                        </TableCell>
                        <TableCell>
                          <Button type="button" variant="ghost" size="sm" aria-label={t("expenses.removeLine")} onClick={() => { setForm({ ...form, lines: form.lines.filter((_, i) => i !== index) }); }} data-testid={`remove-line-${String(index)}`}><Trash2 aria-hidden="true" /></Button>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={(form.kind === "petty_cash" ? !form.fundId : !form.partnerId) || !form.lines.some((l) => l.categoryCode && num(l.amount) > 0)} data-testid="save-expense">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); setPay(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {e && flow ? (
            <div className="flex flex-col gap-4" data-testid="expense-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{e.number}</span>
                  <ExpenseStatus status={e.status} />
                  <PaymentStatus status={e.paymentStatus} />
                </DialogTitle>
              </DialogHeader>
              <StatusBar {...flow} actions={(
                <>
                  {canEdit ? <Button onClick={() => { act.mutate({ id: e.id, action: "confirm" }); }} loading={act.isPending} data-testid="confirm-expense">{t("expenses.confirm")}</Button> : null}
                  {canEdit ? <Button variant="secondary" onClick={() => { openForm(e); }} data-testid="edit-expense">{t("common.edit")}</Button> : null}
                  {payable && !pay ? <Button onClick={() => { setProblem(null); setPay({ bankAccountId: lastBank, paymentDate: today(), amount: String(e.remainingTc), method: "transfer", reference: e.reference ?? "" }); }} data-testid="pay-expense">{t("expenses.pay")}</Button> : null}
                  {e.status === "confirmed" && reversal === null ? <Button variant="secondary" onClick={() => { setPay(null); setReversal(""); }} data-testid="reverse-expense">{t("purchasing.reverse")}</Button> : null}
                  {e.status === "draft" || e.status === "pending_approval" || e.status === "rejected" ? <Button variant="secondary" onClick={() => { act.mutate({ id: e.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-expense">{t("expenses.cancel")}</Button> : null}
                  {e.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: e.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-expense">{t("purchasing.deleteDraft")}</Button> : null}
                </>
              )} />
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("expenses.kind"), t(`expenses.kinds.${e.kind}`)],
                [t("expenses.payee"), payeeOf(e)],
                [t("expenses.expenseDate"), formatDate(e.expenseDate)],
                ...(e.dueDate ? [[t("expenses.dueDate"), formatDate(e.dueDate)] as [string, string]] : []),
                ...(e.reference ? [[t("expenses.reference"), e.reference] as [string, string]] : []),
                ...(e.description ? [[t("expenses.descriptionLabel"), e.description] as [string, string]] : []),
                [t("expenses.total"), <span key="total" data-testid="expense-total">{formatMoney(e.totalGross, e.currency)}</span>],
                ...(e.kind !== "petty_cash" && e.status === "confirmed" ? [[t("expenses.paid"), <span key="paid" data-testid="expense-paid">{formatMoney(e.paidTc, e.currency)}</span>] as [string, React.ReactNode], [t("expenses.remaining"), <span key="remaining" data-testid="expense-remaining">{formatMoney(e.remainingTc, e.currency)}</span>] as [string, React.ReactNode]] : []),
                ...(e.rejectionReason ? [[t("expenses.rejectionReason"), e.rejectionReason] as [string, string]] : []),
              ]} />
              <Table data-testid="expense-line-rows">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("expenses.category")}</TableHead>
                    <TableHead>{t("expenses.descriptionLabel")}</TableHead>
                    <TableHead>{t("expenses.net")}</TableHead>
                    <TableHead>{t("expenses.tax")}</TableHead>
                    <TableHead>{t("expenses.total")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(e.lines ?? []).map((l) => (
                    <TableRow key={l.id} data-testid="expense-line-row">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">{l.categoryCode} · {localized(l.categoryName)}</TableCell>
                      <TableCell dir="auto">{l.description ?? ""}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(l.amount, e.currency)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(l.taxAmount, e.currency)}{Number(l.taxRatePct) !== 0 ? ` (${formatNumber(l.taxRatePct)} %)` : ""}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(Number(l.amount) + Number(l.taxAmount), e.currency)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {pay ? (
                <form onSubmit={(event) => { event.preventDefault(); rememberBank(pay.bankAccountId); act.mutate({ id: e.id, action: "pay", pay }); }} className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-3" data-testid="pay-form">
                  <Field label={t("nav.bankAccounts")} required>
                    <SelectField value={pay.bankAccountId} onChange={(ev) => { setPay({ ...pay, bankAccountId: ev.target.value }); }} required data-testid="pay-bank">
                      <option value="">—</option>
                      {(banks.data ?? []).map((b) => (
                        <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("expenses.paymentDate")} required>
                    <TextField type="date" value={pay.paymentDate} onChange={(ev) => { setPay({ ...pay, paymentDate: ev.target.value }); }} dir="ltr" required data-testid="pay-date" />
                  </Field>
                  <Field label={t("expenses.amountToPay")} description={formatMoney(e.remainingTc, e.currency)}>
                    <TextField inputMode="decimal" value={pay.amount} onChange={(ev) => { setPay({ ...pay, amount: ev.target.value }); }} dir="ltr" data-testid="pay-amount" />
                  </Field>
                  <Field label={t("banking.method")}>
                    <SelectField value={pay.method} onChange={(ev) => { setPay({ ...pay, method: ev.target.value }); }} data-testid="pay-method">
                      {["transfer", "cash", "cheque"].map((m) => (
                        <option key={m} value={m}>{t(`banking.methods.${m}`)}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("banking.reference")}>
                    <TextField value={pay.reference} onChange={(ev) => { setPay({ ...pay, reference: ev.target.value }); }} dir="ltr" data-testid="pay-reference" />
                  </Field>
                  <div className="flex items-end gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setPay(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={act.isPending} disabled={!pay.bankAccountId} data-testid="confirm-pay">{t("expenses.payNow")}</Button>
                  </div>
                </form>
              ) : null}
              {reversal !== null ? (
                <div className="flex flex-wrap items-end gap-3">
                  <Field label={t("purchasing.reversalReason")} required>
                    <TextField value={reversal} onChange={(ev) => { setReversal(ev.target.value); }} data-testid="reversal-reason" />
                  </Field>
                  <Button onClick={() => { act.mutate({ id: e.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button>
                </div>
              ) : null}
              <CustomFieldValuesList entityType="expense" values={e.customFields} />
              <RecordActivity entityType="expense" entityId={e.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
