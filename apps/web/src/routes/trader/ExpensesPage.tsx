import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";

type Expense = components["schemas"]["ExpenseSummary"];
type OwnerMovement = components["schemas"]["TraderOwnerMovement"];

interface ExpenseForm {
  categoryId: string;
  amount: string;
  date: string;
  paidFrom: string;
  partnerId: string;
  dueDate: string;
  note: string;
}

interface OwnerForm {
  kind: "in" | "out";
  bankAccountId: string;
  amount: string;
  date: string;
  note: string;
}

const n = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));
const pad = (value: number): string => String(value).padStart(2, "0");
const isoDate = (date: Date): string => `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
const emptyExpense: ExpenseForm = { categoryId: "", amount: "", date: "", paidFrom: "", partnerId: "", dueDate: "", note: "" };

/** What an expense is, for the state column: a voucher is paid when it is recorded; a bill carries its payable's state. */
function stateOf(e: Expense): "paid" | "partial" | "unpaid" | "pending" {
  if (e.status === "pending_approval") {
    return "pending";
  }
  if (e.kind === "petty_cash") {
    return "paid";
  }
  return e.paymentStatus === "paid" ? "paid" : e.paymentStatus === "partial" ? "partial" : "unpaid";
}

/**
 * Expenses (trader edition, ADR-0035 §12): what the shop spends, in one step through the expense services (a petty
 * cash voucher when paid from the cash box, the payee's bill paid from the bank the same day, or the bill left owed
 * until Money pays it), the month's spend by kind, and the owner's own money put in or taken out, each a manual journal
 * posted through Accounting. Every figure comes from the modules; the page keeps nothing.
 */
export function ExpensesPage() {
  const { t } = useTranslation();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  // Nothing is formatted in a currency before the company is known.
  const money = (amount: number | string | null | undefined): string => (currency === "" ? "—" : formatMoney(n(amount), currency));
  const queryClient = useQueryClient();
  const today = isoDate(new Date());
  const monthStart = today.slice(0, 8) + "01";
  const categories = useQuery({ queryKey: ["expense-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/expenses/categories", { params: { query: { isActive: true } } })) });
  const expenses = useQuery({ queryKey: ["expenses", companyId, "month"], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/expenses", { params: { query: { companyId, status: "confirmed", from: monthStart, to: today, includeLines: true } } })) });
  const suppliers = useQuery({ queryKey: ["supplier-accounts", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId } } })) });
  const ownerMoney = useQuery({ queryKey: ["owner-money", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/trader/owner-money", { params: { query: { companyId } } })) });
  const accounts = useBankAccounts(companyId);
  const [form, setForm] = useState<ExpenseForm>(emptyExpense);
  const [owner, setOwner] = useState<OwnerForm>({ kind: "in", bankAccountId: "", amount: "", date: "", note: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [ownerProblem, setOwnerProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive && a.currency === currency);
  const paidFromAccount = liveAccounts.find((a) => a.id === form.paidFrom);
  const payeeNeeded = !paidFromAccount || paidFromAccount.kind === "bank";
  const kinds = categories.data ?? [];

  const moneyMoved = async (): Promise<void> => {
    await Promise.all([["expenses"], ["bank-accounts"], ["ap-aging"], ["trader-health"], ["home"], ["owner-money"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const addKinds = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/expenses/categories/defaults", {})),
    onSuccess: async () => { setProblem(null); setNotice(t("trader.expenses.kindsAdded")); await queryClient.invalidateQueries({ queryKey: ["expense-categories"] }); },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const createCash = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency, isActive: true } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["bank-accounts"] }); },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const record = useMutation({
    mutationFn: async (f: ExpenseForm) => unwrap(await api.POST("/api/v1/expenses/quick", {
      body: { companyId, categoryId: f.categoryId, amount: Number(f.amount), expenseDate: f.date || null, paidFromBankAccountId: f.paidFrom || null, partnerId: f.partnerId || null, dueDate: f.dueDate || null, description: f.note || null },
    })),
    onSuccess: async (result) => {
      setProblem(null);
      setNotice(t(`trader.expenses.recorded.${result.outcome}`, { number: result.expense.number, amount: formatMoney(result.expense.totalGross, result.expense.currency) }));
      setForm({ ...emptyExpense, paidFrom: form.paidFrom });
      await moneyMoved();
    },
    onError: (error: unknown) => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const moveOwnerMoney = useMutation({
    mutationFn: async (f: OwnerForm) => unwrap(await api.POST("/api/v1/trader/owner-money", { body: { companyId, kind: f.kind, bankAccountId: f.bankAccountId, amount: Number(f.amount), date: f.date || null, note: f.note || null } })),
    onSuccess: async (result) => {
      setOwnerProblem(null);
      setNotice(t("trader.expenses.owner.recorded", { number: result.movements[0]?.number ?? "" }));
      setOwner({ ...owner, amount: "", note: "" });
      await moneyMoved();
    },
    onError: (error: unknown) => { setNotice(null); setOwnerProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const submitExpense = (event: FormEvent): void => {
    event.preventDefault();
    record.mutate(form);
  };
  const submitOwner = (event: FormEvent): void => {
    event.preventDefault();
    moveOwnerMoney.mutate(owner);
  };

  const month = expenses.data ?? [];
  const monthTotal = month.reduce((sum, e) => sum + n(e.totalGross), 0);
  const byKind = new Map<string, { label: string; value: number }>();
  for (const e of month) {
    for (const line of e.lines ?? []) {
      const entry = byKind.get(line.categoryCode) ?? { label: localized(line.categoryName), value: 0 };
      entry.value += n(line.amount) + n(line.taxAmount);
      byKind.set(line.categoryCode, entry);
    }
  }
  const kindRows = [...byKind.values()].sort((a, b) => b.value - a.value);
  const maxKind = Math.max(1, ...kindRows.map((r) => r.value));
  const whatFor = (e: Expense): string => {
    const names = (e.lines ?? []).map((l) => localized(l.categoryName)).filter((x, i, all) => all.indexOf(x) === i).join(", ");
    return names !== "" ? names : (e.description ?? "");
  };
  const who = (e: Expense): string => (e.partnerName ? `${e.partnerCode ?? ""} · ${localized(e.partnerName)}` : e.fundCode ?? "");
  const stateTone = (state: ReturnType<typeof stateOf>): "success" | "warning" | "neutral" => (state === "paid" ? "success" : state === "pending" ? "neutral" : "warning");
  const movementLabel = (m: OwnerMovement): string => t(`trader.expenses.owner.${m.kind}`);

  return (
    <>
      <PageHeader title={t("trader.expenses.title")} description={t("trader.expenses.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="expenses-notice">{notice}</p> : null}
      <div className="grid gap-4 lg:grid-cols-2">
        <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="record-expense">
          <h2 id="record-expense" className="mb-2 text-base font-semibold">{t("trader.expenses.record")}</h2>
          {kinds.length === 0 ? (
            <div className="mb-3 flex flex-wrap items-center gap-2 text-sm">
              <span>{t("trader.expenses.noKinds")}</span>
              <Button type="button" variant="secondary" size="sm" onClick={() => { addKinds.mutate(); }} loading={addKinds.isPending} data-testid="add-expense-kinds">{t("trader.expenses.addKinds")}</Button>
            </div>
          ) : null}
          <form onSubmit={submitExpense} className="grid items-end gap-3 sm:grid-cols-2" data-testid="expense-form">
            <Field label={t("trader.expenses.kind")} required>
              <SelectField value={form.categoryId} onChange={(e) => { setForm({ ...form, categoryId: e.target.value }); }} required data-testid="expense-kind">
                <option value="">{t("trader.expenses.chooseKind")}</option>
                {kinds.map((k) => <option key={k.id} value={k.id}>{localized(k.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("trader.expenses.amount")} required>
              <DecimalField value={form.amount} onChange={(e) => { setForm({ ...form, amount: e.target.value }); }} dir="ltr" required data-testid="expense-amount" />
            </Field>
            <Field label={t("trader.expenses.paidFrom")}>
              <SelectField value={form.paidFrom} onChange={(e) => { setForm({ ...form, paidFrom: e.target.value }); }} data-testid="expense-paid-from">
                <option value="">{t("trader.expenses.notPaid")}</option>
                {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("trader.expenses.payee")} description={t("trader.expenses.payeeHint")} required={payeeNeeded}>
              <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required={payeeNeeded} data-testid="expense-payee">
                <option value="">{t("trader.expenses.choosePayee")}</option>
                {(suppliers.data ?? []).map((x) => <option key={x.partnerId} value={x.partnerId}>{x.partnerCode} · {localized(x.partnerName)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("trader.expenses.date")}>
              <TextField type="date" value={form.date} onChange={(e) => { setForm({ ...form, date: e.target.value }); }} dir="ltr" data-testid="expense-date" />
            </Field>
            {form.paidFrom === "" ? (
              <Field label={t("trader.expenses.dueDate")}>
                <TextField type="date" value={form.dueDate} onChange={(e) => { setForm({ ...form, dueDate: e.target.value }); }} dir="ltr" data-testid="expense-due" />
              </Field>
            ) : null}
            <Field label={t("trader.expenses.note")} className="sm:col-span-2">
              <TextField value={form.note} onChange={(e) => { setForm({ ...form, note: e.target.value }); }} data-testid="expense-note" />
            </Field>
            {liveAccounts.length === 0 ? (
              <div className="flex flex-wrap items-center gap-2 text-sm sm:col-span-2">
                <span>{t("trader.sales.noAccounts")}</span>
                <Button type="button" variant="secondary" size="sm" onClick={() => { createCash.mutate(); }} loading={createCash.isPending} data-testid="create-cash">{t("trader.sales.createCash")}</Button>
              </div>
            ) : null}
            <p className="text-xs text-fg-muted sm:col-span-2"><Link to="/purchasing/suppliers" className="text-accent underline">{t("trader.expenses.newPayee")}</Link></p>
            <FormError message={problem?.message ?? null} />
            <div className="flex justify-end sm:col-span-2">
              <Button type="submit" loading={record.isPending} disabled={!form.categoryId || !form.amount} data-testid="save-expense">{t("trader.expenses.save")}</Button>
            </div>
          </form>
        </section>

        <section className="rounded-lg border border-border bg-surface p-4" aria-labelledby="owner-money">
          <h2 id="owner-money" className="mb-1 text-base font-semibold">{t("trader.expenses.owner.title")}</h2>
          <p className="mb-3 text-sm text-fg-muted">{t("trader.expenses.owner.description")}</p>
          <div className="mb-3 grid grid-cols-2 gap-3">
            <div>
              <div className="text-xs text-fg-muted">{t("trader.expenses.owner.putIn")}</div>
              <div className="text-lg font-semibold tabular" dir="ltr" data-testid="owner-put-in">{money(ownerMoney.data?.putIn)}</div>
            </div>
            <div>
              <div className="text-xs text-fg-muted">{t("trader.expenses.owner.takenOut")}</div>
              <div className="text-lg font-semibold tabular" dir="ltr" data-testid="owner-taken-out">{money(ownerMoney.data?.takenOut)}</div>
            </div>
          </div>
          <form onSubmit={submitOwner} className="grid items-end gap-3 sm:grid-cols-2" data-testid="owner-form">
            <Field label={t("trader.expenses.owner.kind")} required>
              <SelectField value={owner.kind} onChange={(e) => { setOwner({ ...owner, kind: e.target.value === "out" ? "out" : "in" }); }} data-testid="owner-kind">
                <option value="in">{t("trader.expenses.owner.in")}</option>
                <option value="out">{t("trader.expenses.owner.out")}</option>
              </SelectField>
            </Field>
            <Field label={t("trader.expenses.owner.account")} required>
              <SelectField value={owner.bankAccountId} onChange={(e) => { setOwner({ ...owner, bankAccountId: e.target.value }); }} required data-testid="owner-account">
                <option value="">{t("trader.shipments.chooseAccount")}</option>
                {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("trader.expenses.owner.amount")} required>
              <DecimalField value={owner.amount} onChange={(e) => { setOwner({ ...owner, amount: e.target.value }); }} dir="ltr" required data-testid="owner-amount" />
            </Field>
            <Field label={t("trader.expenses.owner.date")}>
              <TextField type="date" value={owner.date} onChange={(e) => { setOwner({ ...owner, date: e.target.value }); }} dir="ltr" data-testid="owner-date" />
            </Field>
            <Field label={t("trader.expenses.owner.note")} className="sm:col-span-2">
              <TextField value={owner.note} onChange={(e) => { setOwner({ ...owner, note: e.target.value }); }} data-testid="owner-note" />
            </Field>
            <FormError message={ownerProblem?.message ?? null} />
            <div className="flex items-center justify-between gap-2 sm:col-span-2">
              <span className="text-xs text-fg-muted" dir="auto">{ownerMoney.data ? t("trader.expenses.owner.accounts", { capital: ownerMoney.data.capitalAccount, drawings: ownerMoney.data.drawingsAccount }) : ""}</span>
              <Button type="submit" loading={moveOwnerMoney.isPending} disabled={!owner.bankAccountId || !owner.amount} data-testid="save-owner-move">{t("trader.expenses.owner.save")}</Button>
            </div>
          </form>
          <h3 className="mb-1 mt-4 text-sm font-semibold">{t("trader.expenses.owner.list")}</h3>
          {(ownerMoney.data?.movements ?? []).length === 0 ? <p className="text-sm text-fg-muted" data-testid="no-owner-moves">{t("trader.expenses.owner.nothing")}</p> : (
            <ul className="flex flex-col gap-1 text-sm">
              {(ownerMoney.data?.movements ?? []).map((m) => (
                <li key={m.journalId} className="flex flex-wrap items-center justify-between gap-2" data-testid="owner-move-row">
                  <span dir="auto">{formatDate(m.date)} · {movementLabel(m)} · {m.bankAccountCode}{m.note ? ` · ${m.note}` : ""} <span dir="ltr">{m.number}</span>{m.status !== "posted" ? <Badge tone="neutral" className="ms-1">{t("trader.expenses.owner.reversed")}</Badge> : null}</span>
                  <span className={`tabular ${m.kind === "out" ? "text-danger" : ""}`} dir="ltr">{m.kind === "out" ? "−" : "+"}{money(m.amount)}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <section className="mt-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="this-month">
        <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
          <h2 id="this-month" className="text-base font-semibold">{t("trader.expenses.thisMonth")}</h2>
          <span className="text-sm">{t("trader.expenses.monthTotal")}: <span className="font-semibold tabular" dir="ltr" data-testid="month-expenses-total">{money(monthTotal)}</span></span>
        </div>
        {month.length === 0 ? <p className="text-sm text-fg-muted" data-testid="no-expenses">{t("trader.expenses.nothingYet")}</p> : (
          <div className="grid gap-4 lg:grid-cols-[minmax(0,18rem)_1fr]">
            <div>
              <h3 className="mb-1 text-sm font-semibold">{t("trader.expenses.byKind")}</h3>
              <ul className="flex flex-col gap-1" data-testid="expenses-by-kind">
                {kindRows.map((r) => (
                  <li key={r.label} className="grid grid-cols-[minmax(0,8rem)_1fr_auto] items-center gap-2 text-sm">
                    <span className="truncate text-fg-muted">{r.label}</span>
                    <span className="h-2 rounded-full bg-surface-sunken" aria-hidden="true"><span className="block h-2 rounded-full bg-accent" style={{ width: `${String(Math.round((r.value / maxKind) * 100))}%` }} /></span>
                    <span className="tabular" dir="ltr">{money(r.value)}</span>
                  </li>
                ))}
              </ul>
            </div>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("trader.expenses.columns.date")}</TableHead>
                  <TableHead>{t("trader.expenses.columns.what")}</TableHead>
                  <TableHead>{t("trader.expenses.columns.who")}</TableHead>
                  <TableHead className="text-end">{t("trader.expenses.columns.amount")}</TableHead>
                  <TableHead>{t("trader.expenses.columns.state")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {month.map((e) => {
                  const state = stateOf(e);
                  return (
                    <TableRow key={e.id} data-testid="expense-row">
                      <TableCell dir="ltr">{formatDate(e.expenseDate)}</TableCell>
                      <TableCell dir="auto">{whatFor(e)}{e.description && (e.lines ?? []).length > 0 ? <span className="text-fg-muted"> · {e.description}</span> : null}</TableCell>
                      <TableCell dir="auto">{who(e)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{formatMoney(e.totalGross, e.currency)}</TableCell>
                      <TableCell><Badge tone={stateTone(state)} className="whitespace-nowrap" data-testid="expense-state">{t(`trader.expenses.states.${state}`)}</Badge></TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}
        <p className="mt-2 text-xs text-fg-muted"><Link to="/expenses" className="text-accent underline">{t("trader.expenses.seeAll")}</Link></p>
      </section>
    </>
  );
}
