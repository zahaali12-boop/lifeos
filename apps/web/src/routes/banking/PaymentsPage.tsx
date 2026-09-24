import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { ItemStatus, useBankAccounts, useOpenItems } from "../payables/shared";
import { num, useSuppliers } from "../purchasing/shared";

type Payment = components["schemas"]["PaymentSummary"];

interface PaymentLineForm {
  openItemId: string;
  label: string;
  remaining: number;
  amount: string;
}

interface PaymentForm {
  id: string | null;
  kind: string;
  partnerId: string;
  bankAccountId: string;
  paymentDate: string;
  method: string;
  reference: string;
  currency: string;
  exchangeRate: string;
  onAccount: string;
  charges: string;
  bankAmount: string;
  lines: PaymentLineForm[];
}

/** Supplier payments and advances (roadmap 4.7): invoice items settled at their booked value with discounts, withholding at payment, charges and realised FX; the rest on account; reversible. */
export function PaymentsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<PaymentForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/banking/payments");
  const [reversal, setReversal] = useState<string | null>(null);
  const suppliers = useSuppliers(companyId);
  const banks = useBankAccounts(companyId);
  const payable = useOpenItems(companyId, form?.partnerId ?? "", "live");
  const list = useQuery({
    queryKey: ["payments", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/payments", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["payment", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/payments/{paymentId}", { params: { path: { paymentId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["payments"], ["payment"], ["open-items"], ["open-item"], ["bank-accounts"], ["bank-account"], ["proposals"], ["proposal"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async (f: PaymentForm) => {
      const body = {
        companyId,
        partnerId: f.partnerId,
        bankAccountId: f.bankAccountId,
        kind: f.kind,
        paymentDate: f.paymentDate || null,
        method: f.method,
        reference: f.reference || null,
        currency: f.currency || null,
        exchangeRate: f.exchangeRate ? num(f.exchangeRate) : null,
        lines: f.kind === "supplier_advance" ? [] : f.lines.filter((l) => num(l.amount) > 0).map((l) => ({ openItemId: l.openItemId, amount: num(l.amount) })),
        onAccount: num(f.onAccount),
        charges: num(f.charges),
        bankAmount: f.bankAmount ? num(f.bankAmount) : null,
        applyWht: true,
      };
      return f.id ? unwrap(await api.PUT("/api/v1/banking/payments/{paymentId}", { params: { path: { paymentId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/payments", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { paymentId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/banking/payments/{paymentId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Payment, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <ItemStatus status={row.original.status} /> },
      { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 130, cell: ({ row }) => t(`banking.paymentKinds.${row.original.kind}`) },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "date", accessorKey: "paymentDate", header: t("banking.paymentDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.paymentDate)}</span> },
      { id: "amount", accessorKey: "amountTc", header: t("purchasing.amount"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.amountTc, row.original.currency)}</span> },
    ],
    [t],
  );

  const blank = (): PaymentForm => ({ id: null, kind: "supplier_payment", partnerId: "", bankAccountId: "", paymentDate: today(), method: "transfer", reference: "", currency: "", exchangeRate: "", onAccount: "", charges: "", bankAmount: "", lines: [] });
  const patchLine = (index: number, amount: string): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, amount } : l)) }); } };
  const addItem = (id: string): void => {
    const row = (payable.data ?? []).find((o) => o.item.id === id);
    if (!form || !row || form.lines.some((l) => l.openItemId === id)) {
      return;
    }
    setForm({ ...form, currency: row.item.currency, lines: [...form.lines, { openItemId: id, label: `${row.item.documentNumber} · ${formatMoney(row.item.remainingTc, row.item.currency)}`, remaining: Number(row.item.remainingTc), amount: String(row.item.remainingTc) }] });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const bank = form ? (banks.data ?? []).find((b) => b.id === form.bankAccountId) : undefined;
  const supplierCurrency = form ? ((suppliers.data ?? []).find((s) => s.partnerId === form.partnerId)?.currency ?? "") : "";
  const paymentCurrency = form ? (form.currency !== "" ? form.currency : supplierCurrency) : "";
  const needsBankAmount = bank !== undefined && paymentCurrency !== "" && bank.currency !== paymentCurrency;
  const p = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.payments")}
        description={t("banking.paymentsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-payment">
            <Plus aria-hidden="true" />
            {t("banking.newPayment")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "posted", "reversed"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Payment> label="nav.payments" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("banking.emptyPayments")} emptyDescription={t("banking.emptyPaymentsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("banking.editPayment") : t("banking.newPayment")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("purchasing.kind")}>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value, lines: e.target.value === "supplier_advance" ? [] : form.lines }); }} data-testid="payment-kind">
                    <option value="supplier_payment">{t("banking.paymentKinds.supplier_payment")}</option>
                    <option value="supplier_advance">{t("banking.paymentKinds.supplier_advance")}</option>
                  </SelectField>
                </Field>
                <Field label={t("partners.supplier")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value, lines: [], currency: "" }); }} required disabled={Boolean(form.id)} data-testid="payment-supplier">
                    <option value="">—</option>
                    {(suppliers.data ?? []).map((sup) => (
                      <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("nav.bankAccounts")} required>
                  <SelectField value={form.bankAccountId} onChange={(e) => { setForm({ ...form, bankAccountId: e.target.value }); }} required data-testid="payment-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).map((b) => (
                      <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.paymentDate")}>
                  <TextField type="date" value={form.paymentDate} onChange={(e) => { setForm({ ...form, paymentDate: e.target.value }); }} dir="ltr" data-testid="payment-date" />
                </Field>
                <Field label={t("banking.method")}>
                  <SelectField value={form.method} onChange={(e) => { setForm({ ...form, method: e.target.value }); }} data-testid="payment-method">
                    {["transfer", "cash", "cheque"].map((m) => (
                      <option key={m} value={m}>{t(`banking.methods.${m}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.reference")}>
                  <TextField value={form.reference} onChange={(e) => { setForm({ ...form, reference: e.target.value }); }} dir="ltr" data-testid="payment-reference" />
                </Field>
                {form.kind === "supplier_advance" || form.lines.length === 0 ? (
                  <Field label={t("partners.currency")} description={t("banking.currencyHelp")}>
                    <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} data-testid="payment-currency" />
                  </Field>
                ) : null}
                <Field label={t("banking.exchangeRate")} description={t("banking.exchangeRateHelp")}>
                  <TextField inputMode="decimal" value={form.exchangeRate} onChange={(e) => { setForm({ ...form, exchangeRate: e.target.value }); }} dir="ltr" data-testid="payment-rate" />
                </Field>
                <Field label={form.kind === "supplier_advance" ? t("banking.advanceAmount") : t("banking.onAccount")} required={form.kind === "supplier_advance"}>
                  <TextField inputMode="decimal" value={form.onAccount} onChange={(e) => { setForm({ ...form, onAccount: e.target.value }); }} dir="ltr" data-testid="payment-on-account" />
                </Field>
                <Field label={t("banking.charges")} description={bank ? bank.currency : undefined}>
                  <TextField inputMode="decimal" value={form.charges} onChange={(e) => { setForm({ ...form, charges: e.target.value }); }} dir="ltr" data-testid="payment-charges" />
                </Field>
                {needsBankAmount ? (
                  <Field label={t("banking.bankAmount")} description={t("banking.bankAmountHelp", { currency: bank.currency })} required>
                    <TextField inputMode="decimal" value={form.bankAmount} onChange={(e) => { setForm({ ...form, bankAmount: e.target.value }); }} dir="ltr" required data-testid="payment-bank-amount" />
                  </Field>
                ) : null}
              </div>
              {form.kind === "supplier_payment" && form.partnerId ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="payable-items">
                  <h3 className="text-sm font-semibold">{t("banking.openInvoices")}</h3>
                  {(payable.data ?? []).filter((o) => Number(o.item.originalTc) > 0 && !o.item.paymentBlocked).length === 0 ? <p className="text-xs text-fg-muted">{t("banking.nothingOpen")}</p> : (
                    <ul className="flex flex-col gap-1 text-sm">
                      {(payable.data ?? []).filter((o) => Number(o.item.originalTc) > 0 && !o.item.paymentBlocked).map((o) => (
                        <li key={o.item.id} className="flex flex-wrap items-center gap-3">
                          <span dir="ltr">{o.item.documentNumber}</span>
                          <span dir="ltr">{formatDate(o.item.dueDate)}</span>
                          <span className="tabular" dir="ltr">{formatMoney(o.item.remainingTc, o.item.currency)}</span>
                          <Button type="button" variant="ghost" size="sm" onClick={() => { addItem(o.item.id); }} data-testid={`add-item-${o.item.documentNumber}`}>{t("purchasing.addLine")}</Button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ) : null}
              {form.lines.length > 0 ? (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.number")}</TableHead>
                      <TableHead>{t("purchasing.remaining")}</TableHead>
                      <TableHead>{t("banking.payNow")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => (
                      <TableRow key={line.openItemId} data-testid="payment-line">
                        <TableCell dir="ltr">{line.label}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(line.remaining)}</TableCell>
                        <TableCell><TextField aria-label={t("banking.payNow")} inputMode="decimal" value={line.amount} onChange={(e) => { patchLine(index, e.target.value); }} dir="ltr" className="w-32" data-testid={`pay-amount-${String(index)}`} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || !form.bankAccountId} data-testid="save-payment">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {p ? (
            <div className="flex flex-col gap-4" data-testid="payment-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{p.number}</span>
                  <ItemStatus status={p.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("purchasing.kind"), t(`banking.paymentKinds.${p.kind}`)],
                [t("partners.supplier"), `${p.partnerCode} · ${localized(p.partnerName)}`],
                [t("nav.bankAccounts"), `${p.bankAccountCode} · ${p.bankCurrency}`],
                [t("banking.paymentDate"), formatDate(p.paymentDate)],
                [t("banking.method"), `${t(`banking.methods.${p.method}`)}${p.reference ? ` · ${p.reference}` : ""}`],
                [t("purchasing.amount"), <span key="amount" data-testid="payment-amount">{formatMoney(p.amountTc, p.currency)}{Number(p.exchangeRate) !== 1 ? ` @ ${formatNumber(p.exchangeRate, { maximumFractionDigits: 6 })}` : ""}</span>],
                [t("payables.discount"), formatMoney(p.discountTc, p.currency)],
                [t("purchasing.withheld"), formatMoney(p.whtTc, p.currency)],
                [t("banking.onAccount"), formatMoney(p.onAccountTc, p.currency)],
                [t("banking.charges"), formatMoney(p.chargesBank, p.bankCurrency)],
                [t("banking.bankAmount"), <span key="bank" data-testid="payment-bank-amount-value">{formatMoney(p.bankAmount, p.bankCurrency)}</span>],
                ...(p.reversalReason ? [[t("purchasing.reversalReason"), p.reversalReason] as [string, string]] : []),
              ]} />
              {p.lines.length > 0 ? (
                <Table data-testid="payment-lines">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("purchasing.number")}</TableHead>
                      <TableHead>{t("purchasing.dueDate")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("payables.discount")}</TableHead>
                      <TableHead>{t("purchasing.withheld")}</TableHead>
                      <TableHead>{t("banking.cash")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {p.lines.map((l) => (
                      <TableRow key={l.id} data-testid="payment-line-row">
                        <TableCell>{String(l.lineNo)}</TableCell>
                        <TableCell dir="ltr">{l.documentNumber}{Number(l.instalment) > 1 ? ` / ${String(l.instalment)}` : ""}</TableCell>
                        <TableCell dir="ltr">{formatDate(l.dueDate)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.amountTc, p.currency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.discountTc, p.currency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.whtTc, p.currency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.cashTc, p.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {reversal !== null ? (
                <Field label={t("purchasing.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {p.status === "draft" ? <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: p.id, kind: p.kind, partnerId: p.partnerId, bankAccountId: p.bankAccountId, paymentDate: p.paymentDate, method: p.method, reference: p.reference ?? "", currency: p.currency, exchangeRate: "", onAccount: p.onAccountTc ? String(p.onAccountTc) : "", charges: p.chargesBank ? String(p.chargesBank) : "", bankAmount: p.bankCurrency !== p.currency ? String(p.bankAmount) : "", lines: p.lines.map((l) => ({ openItemId: l.openItemId, label: `${l.documentNumber} · ${formatMoney(l.itemRemainingTc, p.currency)}`, remaining: Number(l.itemRemainingTc), amount: String(l.amountTc) })) }); }} data-testid="edit-payment">{t("common.edit")}</Button> : null}
                {p.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: p.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-payment">{t("purchasing.deleteDraft")}</Button> : null}
                {p.status === "draft" ? <Button onClick={() => { act.mutate({ id: p.id, action: "post" }); }} loading={act.isPending} data-testid="post-payment">{t("banking.postPayment")}</Button> : null}
                {p.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-payment">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: p.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
