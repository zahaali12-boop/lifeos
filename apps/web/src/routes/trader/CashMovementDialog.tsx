import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, SelectField, TextField } from "../common";
import { useBankAccounts } from "../payables/shared";
import { useCustomers } from "../phone/shared";
import { useSuppliers } from "../purchasing/shared";
import { companyCurrencyOnly, exchangeRate, movementKinds, owedOn, spreadOldestFirst, type MovementKind, type OpenLike } from "./cashMovements";

interface MovementForm {
  kind: MovementKind;
  accountId: string;
  toAccountId: string;
  amount: string;
  received: string;
  partnerId: string;
  categoryId: string;
  date: string;
  note: string;
}

/** The queries a movement changes: every box, both sides' open items and invoices, the expenses, the owner's money and the day's figures. */
const moved = [["trader-cash"], ["bank-accounts"], ["bank-transfers"], ["ar-aging"], ["ap-aging"], ["invoices"], ["purchase-invoices"], ["receipts"], ["expenses"], ["owner-money"], ["trader-health"], ["home"], ["cash-open-items"]];

/**
 * A new movement of a cash box or bank account (ADR-0035 §24): money between two of the boxes (cash paid into the bank,
 * cash drawn from it, dinars changed for dollars), money from a customer or to a supplier (their oldest invoices first,
 * anything beyond on account), an expense paid now, or the owner's own money in or out. Each goes to the record that
 * already keeps it (a transfer, a receipt, a payment, an expense, the owner's journal), so the books move as they would
 * from that record's own screen.
 */
export function CashMovementDialog({ open, onClose, companyId, companyCurrency, accountId, onDone }: { open: boolean; onClose: () => void; companyId: string; companyCurrency: string; accountId: string | undefined; onDone: (notice: string) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const accounts = useBankAccounts(companyId);
  const customers = useCustomers(companyId);
  const suppliers = useSuppliers(companyId);
  const categories = useQuery({ queryKey: ["expense-categories"], enabled: open, queryFn: async () => unwrap(await api.GET("/api/v1/expenses/categories", { params: { query: { isActive: true } } })) });
  const blank: MovementForm = { kind: "transfer", accountId: accountId ?? "", toAccountId: "", amount: "", received: "", partnerId: "", categoryId: "", date: "", note: "" };
  const [form, setForm] = useState<MovementForm>(blank);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const live = (accounts.data ?? []).filter((a) => a.isActive);
  const boxesFor = (kind: MovementKind) => (companyCurrencyOnly.includes(kind) ? live.filter((a) => a.currency === companyCurrency) : live);
  const boxes = boxesFor(form.kind);
  const box = boxes.find((a) => a.id === form.accountId) ?? boxes[0];
  const target = live.find((a) => a.id === form.toAccountId);
  const currency = box?.currency ?? companyCurrency;
  const changes = Boolean(box && target && box.currency !== target.currency);
  const rate = box && target && changes ? exchangeRate(form.amount, box.currency, form.received, target.currency, companyCurrency) : null;
  const money = (value: number | string, code: string): string => (code === "" ? "—" : formatMoney(value, code));

  // The chosen customer's or supplier's open invoices in the box's currency, for what is owed and how a payment spreads.
  const side = form.kind === "customer" ? "receivables" : form.kind === "supplier" ? "payables" : null;
  const openItems = useQuery({
    queryKey: ["cash-open-items", side, companyId, form.partnerId],
    enabled: open && side !== null && Boolean(form.partnerId),
    queryFn: async (): Promise<(OpenLike & { currency: string })[]> => {
      const rows = side === "receivables"
        ? unwrap(await api.GET("/api/v1/receivables/open-items", { params: { query: { companyId, partnerId: form.partnerId, status: "live" } } }))
        : unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: form.partnerId, status: "live" } } }));
      return rows.filter((r) => r.item.kind === "invoice").map((r) => ({ id: r.item.id, remaining: r.item.remainingTc, dueDate: r.item.dueDate, documentDate: r.item.documentDate, currency: r.item.currency }));
    },
  });
  const owedItems = (openItems.data ?? []).filter((i) => i.currency === currency);
  const owed = owedOn(owedItems);

  const choose = (patch: Partial<MovementForm>): void => { setProblem(null); setForm({ ...form, ...patch }); };
  const chooseKind = (kind: MovementKind): void => {
    const allowed = boxesFor(kind);
    choose({ kind, partnerId: "", amount: "", received: "", accountId: allowed.some((a) => a.id === form.accountId) ? form.accountId : (allowed[0]?.id ?? "") });
  };
  const close = (): void => { setForm(blank); setProblem(null); onClose(); };

  const record = useMutation({
    mutationFn: async (f: MovementForm): Promise<string> => {
      if (!box) {
        throw new Error("no box");
      }
      const date = f.date || null;
      const note = f.note.trim() || null;
      const method = box.kind === "bank" ? "transfer" : "cash";
      switch (f.kind) {
        case "transfer": {
          const done = unwrap(await api.POST("/api/v1/banking/transfers", { body: { companyId, fromBankAccountId: box.id, toBankAccountId: f.toAccountId, amount: Number(f.amount), amountReceived: changes ? Number(f.received) : null, transferDate: date, notes: note } }));
          return done.number;
        }
        case "customer": {
          const spread = spreadOldestFirst(owedItems, f.amount);
          const draft = unwrap(await api.POST("/api/v1/banking/receipts", { body: { companyId, partnerId: f.partnerId, bankAccountId: box.id, kind: "customer_receipt", receiptDate: date, method, currency: box.currency, lines: spread.lines, onAccount: spread.onAccount, charges: 0, notes: note } }));
          return unwrap(await api.POST("/api/v1/banking/receipts/{receiptId}/post", { params: { path: { receiptId: draft.id } } })).number;
        }
        case "supplier": {
          const spread = spreadOldestFirst(owedItems, f.amount);
          const draft = unwrap(await api.POST("/api/v1/banking/payments", { body: { companyId, partnerId: f.partnerId, bankAccountId: box.id, kind: "supplier_payment", paymentDate: date, method, currency: box.currency, lines: spread.lines, onAccount: spread.onAccount, charges: 0, applyWht: false, notes: note } }));
          return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params: { path: { paymentId: draft.id } } })).number;
        }
        case "expense": {
          const done = unwrap(await api.POST("/api/v1/expenses/quick", { body: { companyId, categoryId: f.categoryId, amount: Number(f.amount), expenseDate: date, paidFromBankAccountId: box.id, partnerId: null, dueDate: null, description: note } }));
          return done.expense.number;
        }
        case "ownerIn":
        case "ownerOut": {
          const done = unwrap(await api.POST("/api/v1/trader/owner-money", { body: { companyId, kind: f.kind === "ownerIn" ? "in" : "out", bankAccountId: box.id, amount: Number(f.amount), date, note } }));
          return done.movements[0]?.number ?? "";
        }
      }
    },
    onSuccess: async (number) => {
      await Promise.all(moved.map((key) => queryClient.invalidateQueries({ queryKey: key })));
      onDone(t("trader.cash.movement.recorded", { number }));
      close();
    },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const addKinds = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/expenses/categories/defaults", {})),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["expense-categories"] }); },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    record.mutate({ ...form, accountId: box?.id ?? "" });
  };
  const amountOk = Number(form.amount) > 0;
  const ready = Boolean(box) && amountOk && (
    form.kind === "transfer" ? Boolean(target) && target?.id !== box?.id && (!changes || Number(form.received) > 0)
      : form.kind === "customer" || form.kind === "supplier" ? Boolean(form.partnerId)
        : form.kind === "expense" ? Boolean(form.categoryId) : true);
  const boxLabel = form.kind === "transfer" ? t("trader.cash.movement.from") : form.kind === "customer" || form.kind === "ownerIn" ? t("trader.cash.movement.into") : t("trader.cash.movement.outOf");
  const kinds = categories.data ?? [];

  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { close(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
        <form onSubmit={submit} className="flex flex-col gap-4" data-testid="cash-movement-dialog">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("trader.cash.movement.title")}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          <fieldset className="grid gap-2 sm:grid-cols-2" data-testid="movement-kinds">
            <legend className="mb-1 text-sm font-medium">{t("trader.cash.movement.kind")}</legend>
            {movementKinds.map((kind) => (
              <label key={kind} className={`flex cursor-pointer items-start gap-2 rounded-md border p-2 text-sm ${form.kind === kind ? "border-accent bg-accent-soft/30" : "border-border"}`}>
                <input type="radio" name="movement-kind" className="mt-1" checked={form.kind === kind} onChange={() => { chooseKind(kind); }} data-testid={`movement-kind-${kind}`} />
                <span className="flex flex-col">
                  <span className="font-medium">{t(`trader.cash.movement.kinds.${kind}`)}</span>
                  <span className="text-xs text-fg-muted">{t(`trader.cash.movement.hints.${kind}`)}</span>
                </span>
              </label>
            ))}
          </fieldset>

          {companyCurrencyOnly.includes(form.kind) && boxes.length < live.length ? <p className="text-xs text-fg-muted">{t("trader.cash.movement.companyCurrencyOnly", { currency: companyCurrency })}</p> : null}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={boxLabel} required>
              <SelectField value={box?.id ?? ""} onChange={(e) => { choose({ accountId: e.target.value }); }} required data-testid="movement-account">
                {boxes.length === 0 ? <option value="" /> : null}
                {boxes.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
              </SelectField>
            </Field>
            {form.kind === "transfer" ? (
              <Field label={t("trader.cash.movement.to")} required>
                <SelectField value={form.toAccountId} onChange={(e) => { choose({ toAccountId: e.target.value }); }} required data-testid="movement-to-account">
                  <option value="" />
                  {live.filter((a) => a.id !== box?.id).map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
                </SelectField>
              </Field>
            ) : null}
            {form.kind === "customer" ? (
              <Field label={t("trader.cash.movement.customer")} required>
                <SelectField value={form.partnerId} onChange={(e) => { choose({ partnerId: e.target.value, amount: "" }); }} required data-testid="movement-customer">
                  <option value="" />
                  {(customers.data ?? []).map((c) => <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>)}
                </SelectField>
              </Field>
            ) : null}
            {form.kind === "supplier" ? (
              <Field label={t("trader.cash.movement.supplier")} required>
                <SelectField value={form.partnerId} onChange={(e) => { choose({ partnerId: e.target.value, amount: "" }); }} required data-testid="movement-supplier">
                  <option value="" />
                  {(suppliers.data ?? []).map((s) => <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>)}
                </SelectField>
              </Field>
            ) : null}
            {form.kind === "expense" ? (
              <Field label={t("trader.cash.movement.category")} required>
                <SelectField value={form.categoryId} onChange={(e) => { choose({ categoryId: e.target.value }); }} required data-testid="movement-category">
                  <option value="" />
                  {kinds.map((k) => <option key={k.id} value={k.id}>{localized(k.name)}</option>)}
                </SelectField>
              </Field>
            ) : null}
            <Field label={t("trader.cash.movement.amount", { currency })} required>
              <DecimalField value={form.amount} onChange={(e) => { choose({ amount: e.target.value }); }} dir="ltr" required data-testid="movement-amount" />
            </Field>
            {form.kind === "transfer" && changes && target ? (
              <Field label={t("trader.cash.movement.received", { currency: target.currency })} required>
                <DecimalField value={form.received} onChange={(e) => { choose({ received: e.target.value }); }} dir="ltr" required data-testid="movement-received" />
              </Field>
            ) : null}
            <Field label={t("trader.cash.movement.date")}>
              <TextField type="date" value={form.date} onChange={(e) => { choose({ date: e.target.value }); }} dir="ltr" data-testid="movement-date" />
            </Field>
            <Field label={t("trader.cash.movement.note")}>
              <TextField value={form.note} onChange={(e) => { choose({ note: e.target.value }); }} dir="auto" data-testid="movement-note" />
            </Field>
          </div>

          {rate ? (
            <p className="flex flex-wrap gap-1 text-sm" data-testid="movement-rate">
              <span>{t("trader.cash.movement.rate")}</span>
              <bdi dir="ltr">1 {rate.foreign} = {formatNumber(rate.rate)} {companyCurrency}</bdi>
            </p>
          ) : null}
          {(form.kind === "customer" || form.kind === "supplier") && form.partnerId && openItems.data ? (
            <div className="flex flex-wrap items-center gap-2 text-sm" data-testid="movement-owed">
              {owed > 0 ? (
                <>
                  <span>{t(form.kind === "customer" ? "trader.cash.movement.owes" : "trader.cash.movement.owed", { amount: money(owed, currency) })}</span>
                  <Button type="button" variant="secondary" size="sm" onClick={() => { choose({ amount: String(owed) }); }} data-testid="movement-pay-all">{t("trader.cash.movement.all")}</Button>
                </>
              ) : <span className="text-fg-muted">{t(form.kind === "customer" ? "trader.cash.movement.nothingOwedCustomer" : "trader.cash.movement.nothingOwedSupplier", { currency })}</span>}
            </div>
          ) : null}
          {form.kind === "expense" && categories.data && kinds.length === 0 ? (
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <span>{t("trader.expenses.noKinds")}</span>
              <Button type="button" variant="secondary" size="sm" onClick={() => { addKinds.mutate(); }} loading={addKinds.isPending} data-testid="movement-add-kinds">{t("trader.expenses.addKinds")}</Button>
            </div>
          ) : null}

          <DialogFooter>
            <Button type="button" variant="secondary" onClick={close}>{t("common.cancel")}</Button>
            <Button type="submit" loading={record.isPending} disabled={!ready} data-testid="save-movement">{t("trader.cash.movement.save")}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
