import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { ItemStatus, useBankAccounts } from "../payables/shared";
import { num } from "../purchasing/shared";
import { useCompanyCustomers } from "../sales/pricing/shared";
import { RecordActivity } from "../RecordDiscussion";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { CurrencyField } from "../CurrencyField";
import { usePref } from "../../lib/prefs";

type Receipt = components["schemas"]["PaymentSummary"];

interface ReceiptLineForm {
  openItemId: string;
  label: string;
  remaining: number;
  amount: string;
}

interface ReceiptForm {
  id: string | null;
  kind: string;
  partnerId: string;
  bankAccountId: string;
  receiptDate: string;
  method: string;
  reference: string;
  currency: string;
  exchangeRate: string;
  onAccount: string;
  charges: string;
  bankAmount: string;
  whtCodeId: string;
  chequeNumber: string;
  drawerBank: string;
  chequeDueDate: string;
  customFields: CustomFieldValues;
  lines: ReceiptLineForm[];
}

/** Customer receipts and deposits (roadmap 5.7): invoice items settled at their booked value with the discount given, tax the customer withheld, bank charges and realised FX; the rest on account; reversible. */
export function ReceiptsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ReceiptForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/banking/receipts");
  const [reversal, setReversal] = useState<string | null>(null);
  const customers = useCompanyCustomers(companyId);
  const banks = useBankAccounts(companyId);
  const canManage = can("banking.receipt.manage");
  const canPost = can("banking.receipt.post");
  const receivable = useQuery({
    queryKey: ["ar-open-items", companyId, form?.partnerId ?? "", "live"],
    enabled: Boolean(companyId) && Boolean(form?.partnerId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items", { params: { query: { companyId, partnerId: form?.partnerId ?? "", status: "live" } } })),
  });
  const whtCodes = useQuery({
    queryKey: ["wht-codes"],
    enabled: Boolean(form),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/wht-codes")),
  });
  const list = useQuery({
    queryKey: ["receipts", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/receipts", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["receipt", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/receipts/{receiptId}", { params: { path: { receiptId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["receipts"], ["receipt"], ["ar-open-items"], ["ar-open-item"], ["ar-settlements"], ["ar-aging"], ["ar-statement"], ["bank-accounts"], ["bank-account"], ["sales-invoice"], ["sales-invoices"], ["customer-360"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async (f: ReceiptForm) => {
      const body = {
        companyId,
        partnerId: f.partnerId,
        bankAccountId: f.bankAccountId,
        kind: f.kind,
        receiptDate: f.receiptDate || null,
        method: f.method,
        reference: f.reference || null,
        currency: f.currency || null,
        exchangeRate: f.exchangeRate ? num(f.exchangeRate) : null,
        lines: f.kind === "customer_deposit" ? [] : f.lines.filter((l) => num(l.amount) > 0).map((l) => ({ openItemId: l.openItemId, amount: num(l.amount) })),
        onAccount: num(f.onAccount),
        charges: num(f.charges),
        bankAmount: f.bankAmount ? num(f.bankAmount) : null,
        whtCodeId: f.whtCodeId || null,
        customFields: f.customFields,
        cheque: f.method === "cheque" ? { chequeNumber: f.chequeNumber || null, drawerBank: f.drawerBank || null, dueDate: f.chequeDueDate || null } : null,
      };
      return f.id ? unwrap(await api.PUT("/api/v1/banking/receipts/{receiptId}", { params: { path: { receiptId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/receipts", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { receiptId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/banking/receipts/{receiptId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/banking/receipts/{receiptId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/banking/receipts/{receiptId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Receipt, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <ItemStatus status={row.original.status} /> },
      { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 130, cell: ({ row }) => t(`banking.receiptKinds.${row.original.kind}`) },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "date", accessorKey: "paymentDate", header: t("banking.receiptDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.paymentDate)}</span> },
      { id: "amount", accessorKey: "amountTc", header: t("purchasing.amount"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.amountTc, row.original.currency)}</span> },
    ],
    [t],
  );

  const [lastBank, rememberBank] = usePref(`bank:${companyId}`);
  const blank = (): ReceiptForm => ({ id: null, kind: "customer_receipt", partnerId: "", bankAccountId: lastBank, receiptDate: today(), method: "transfer", reference: "", currency: "", exchangeRate: "", onAccount: "", charges: "", bankAmount: "", whtCodeId: "", chequeNumber: "", drawerBank: "", chequeDueDate: "", customFields: {}, lines: [] });
  const patchLine = (index: number, amount: string): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, amount } : l)) }); } };
  const addItem = (id: string): void => {
    const row = (receivable.data ?? []).find((o) => o.item.id === id);
    if (!form || !row || form.lines.some((l) => l.openItemId === id)) {
      return;
    }
    setForm({ ...form, currency: row.item.currency, lines: [...form.lines, { openItemId: id, label: `${row.item.documentNumber} · ${formatMoney(row.item.remainingTc, row.item.currency)}`, remaining: Number(row.item.remainingTc), amount: String(row.item.remainingTc) }] });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { rememberBank(form.bankAccountId); save.mutate(form); } };
  const bank = form ? (banks.data ?? []).find((b) => b.id === form.bankAccountId) : undefined;
  const customerCurrency = form ? ((customers.data ?? []).find((c) => c.partnerId === form.partnerId)?.currency ?? "") : "";
  const receiptCurrency = form ? (form.currency !== "" ? form.currency : customerCurrency) : "";
  const needsBankAmount = bank !== undefined && receiptCurrency !== "" && bank.currency !== receiptCurrency;
  const collectible = (receivable.data ?? []).filter((o) => Number(o.item.originalTc) > 0 && !o.item.onHold);
  const p = detail.data;

  useNewFromUrl("/banking/receipts", () => { setProblem(null); setForm(blank()); }, Boolean(companyId));
  return (
    <>
      <PageHeader
        title={t("nav.customerReceipts")}
        description={t("banking.receiptsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId || !canManage} data-testid="new-receipt">
            <Plus aria-hidden="true" />
            {t("banking.newReceipt")}
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
      <DataGrid<Receipt> label="nav.customerReceipts" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("banking.emptyReceipts")} emptyDescription={t("banking.emptyReceiptsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("banking.editReceipt") : t("banking.newReceipt")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("purchasing.kind")}>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value, lines: e.target.value === "customer_deposit" ? [] : form.lines }); }} data-testid="receipt-kind">
                    <option value="customer_receipt">{t("banking.receiptKinds.customer_receipt")}</option>
                    <option value="customer_deposit">{t("banking.receiptKinds.customer_deposit")}</option>
                  </SelectField>
                </Field>
                <Field label={t("sales.customer")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value, lines: [], currency: "" }); }} required disabled={Boolean(form.id)} data-testid="receipt-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => (
                      <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("nav.bankAccounts")} required>
                  <SelectField value={form.bankAccountId} onChange={(e) => { setForm({ ...form, bankAccountId: e.target.value }); }} required data-testid="receipt-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).map((b) => (
                      <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.receiptDate")}>
                  <TextField type="date" value={form.receiptDate} onChange={(e) => { setForm({ ...form, receiptDate: e.target.value }); }} dir="ltr" data-testid="receipt-date" />
                </Field>
                <Field label={t("banking.method")}>
                  <SelectField value={form.method} onChange={(e) => { setForm({ ...form, method: e.target.value }); }} data-testid="receipt-method">
                    {["transfer", "cash", "cheque"].map((m) => (
                      <option key={m} value={m}>{t(`banking.methods.${m}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.reference")}>
                  <TextField value={form.reference} onChange={(e) => { setForm({ ...form, reference: e.target.value }); }} dir="ltr" data-testid="receipt-reference" />
                </Field>
                {form.method === "cheque" ? (
                  <>
                    <Field label={t("banking.chequeNumber")} required>
                      <TextField value={form.chequeNumber} onChange={(e) => { setForm({ ...form, chequeNumber: e.target.value }); }} dir="ltr" required data-testid="receipt-cheque-number" />
                    </Field>
                    <Field label={t("banking.drawerBank")}>
                      <TextField value={form.drawerBank} onChange={(e) => { setForm({ ...form, drawerBank: e.target.value }); }} dir="auto" data-testid="receipt-drawer-bank" />
                    </Field>
                    <Field label={t("banking.chequeDueDate")} description={t("banking.chequeDueDateHelp")}>
                      <TextField type="date" value={form.chequeDueDate} onChange={(e) => { setForm({ ...form, chequeDueDate: e.target.value }); }} dir="ltr" data-testid="receipt-cheque-due" />
                    </Field>
                  </>
                ) : null}
                {form.kind === "customer_deposit" || form.lines.length === 0 ? (
                  <Field label={t("partners.currency")} description={t("banking.receiptCurrencyHelp")}>
                    <CurrencyField value={form.currency} onChange={(code) => { setForm({ ...form, currency: code }); }} allowEmpty data-testid="receipt-currency" />
                  </Field>
                ) : null}
                <Field label={t("banking.exchangeRate")} description={t("banking.exchangeRateHelp")}>
                  <DecimalField value={form.exchangeRate} onChange={(e) => { setForm({ ...form, exchangeRate: e.target.value }); }} dir="ltr" data-testid="receipt-rate" />
                </Field>
                <Field label={form.kind === "customer_deposit" ? t("banking.depositAmount") : t("banking.onAccount")} required={form.kind === "customer_deposit"}>
                  <DecimalField value={form.onAccount} onChange={(e) => { setForm({ ...form, onAccount: e.target.value }); }} dir="ltr" data-testid="receipt-on-account" />
                </Field>
                {form.method !== "cheque" ? (
                  <Field label={t("banking.charges")} description={bank ? bank.currency : undefined}>
                    <DecimalField value={form.charges} onChange={(e) => { setForm({ ...form, charges: e.target.value }); }} dir="ltr" data-testid="receipt-charges" />
                  </Field>
                ) : null}
                {needsBankAmount ? (
                  <Field label={t("banking.bankCredited")} description={t("banking.bankCreditedHelp", { currency: bank.currency })} required>
                    <DecimalField value={form.bankAmount} onChange={(e) => { setForm({ ...form, bankAmount: e.target.value }); }} dir="ltr" required data-testid="receipt-bank-amount" />
                  </Field>
                ) : null}
                {form.kind === "customer_receipt" ? (
                  <Field label={t("banking.withheldByCustomer")} description={t("banking.withheldByCustomerHelp")}>
                    <SelectField value={form.whtCodeId} onChange={(e) => { setForm({ ...form, whtCodeId: e.target.value }); }} data-testid="receipt-wht">
                      <option value="">—</option>
                      {(whtCodes.data ?? []).filter((w) => w.isActive).map((w) => (
                        <option key={w.id} value={w.id}>{w.code} · {formatNumber(w.ratePct, { maximumFractionDigits: 2 })}%</option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
              </div>
              {form.kind === "customer_receipt" && form.partnerId ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="receivable-items">
                  <h3 className="text-sm font-semibold">{t("banking.openInvoices")}</h3>
                  {collectible.length === 0 ? <p className="text-xs text-fg-muted">{t("banking.nothingOpenCustomer")}</p> : (
                    <ul className="flex flex-col gap-1 text-sm">
                      {collectible.map((o) => (
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
                      <TableHead>{t("banking.collectNow")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => (
                      <TableRow key={line.openItemId} data-testid="receipt-line">
                        <TableCell dir="ltr">{line.label}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(line.remaining)}</TableCell>
                        <TableCell><DecimalField aria-label={t("banking.collectNow")} value={line.amount} onChange={(e) => { patchLine(index, e.target.value); }} dir="ltr" className="w-32" data-testid={`collect-amount-${String(index)}`} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              <CustomFieldsFieldset entityType="bank_receipt" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || !form.bankAccountId} data-testid="save-receipt">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {p ? (
            <div className="flex flex-col gap-4" data-testid="receipt-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{p.number}</span>
                  <ItemStatus status={p.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("purchasing.kind"), t(`banking.receiptKinds.${p.kind}`)],
                [t("sales.customer"), `${p.partnerCode} · ${localized(p.partnerName)}`],
                [t("nav.bankAccounts"), `${p.bankAccountCode} · ${p.bankCurrency}`],
                [t("banking.receiptDate"), formatDate(p.paymentDate)],
                [t("banking.method"), `${t(`banking.methods.${p.method}`)}${p.reference ? ` · ${p.reference}` : ""}`],
                ...(p.cheque ? [[t("banking.chequeDetails"), <span key="cheque" dir="ltr" data-testid="receipt-cheque">{p.cheque.chequeNumber} · {t(`banking.cheques.statuses.${p.cheque.status}`)} · {formatDate(p.cheque.dueDate)}</span>] as [string, React.ReactNode]] : []),
                [t("purchasing.amount"), <span key="amount" data-testid="receipt-amount">{formatMoney(p.amountTc, p.currency)}{Number(p.exchangeRate) !== 1 ? ` @ ${formatNumber(p.exchangeRate, { maximumFractionDigits: 6 })}` : ""}</span>],
                [t("banking.discountGiven"), formatMoney(p.discountTc, p.currency)],
                [t("banking.withheldByCustomer"), `${formatMoney(p.whtTc, p.currency)}${p.whtCode ? ` · ${p.whtCode}` : ""}`],
                [t("banking.onAccount"), formatMoney(p.onAccountTc, p.currency)],
                [t("banking.charges"), formatMoney(p.chargesBank, p.bankCurrency)],
                [t("banking.bankCredited"), <span key="bank" data-testid="receipt-bank-amount-value">{formatMoney(p.bankAmount, p.bankCurrency)}</span>],
                ...(p.reversalReason ? [[t("purchasing.reversalReason"), p.reversalReason] as [string, string]] : []),
              ]} />
              {p.lines.length > 0 ? (
                <Table data-testid="receipt-lines">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("purchasing.number")}</TableHead>
                      <TableHead>{t("purchasing.dueDate")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("banking.discountGiven")}</TableHead>
                      <TableHead>{t("purchasing.withheld")}</TableHead>
                      <TableHead>{t("banking.cashReceived")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {p.lines.map((l) => (
                      <TableRow key={l.id} data-testid="receipt-line-row">
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
              <CustomFieldValuesList entityType="bank_receipt" values={p.customFields} />
              <RecordActivity entityType="bank_receipt" entityId={p.id} />
              <DialogFooter>
                {p.status === "draft" && canManage ? <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: p.id, customFields: asCustomFieldValues(p.customFields), kind: p.kind, partnerId: p.partnerId, bankAccountId: p.bankAccountId, receiptDate: p.paymentDate, method: p.method, reference: p.reference ?? "", currency: p.currency, exchangeRate: "", onAccount: p.onAccountTc ? String(p.onAccountTc) : "", charges: p.chargesBank ? String(p.chargesBank) : "", bankAmount: p.bankCurrency !== p.currency ? String(p.bankAmount) : "", whtCodeId: p.whtCodeId ?? "", chequeNumber: p.cheque?.chequeNumber ?? "", drawerBank: p.cheque?.drawerBank ?? "", chequeDueDate: p.cheque?.dueDate ?? "", lines: p.lines.map((l) => ({ openItemId: l.openItemId, label: `${l.documentNumber} · ${formatMoney(l.itemRemainingTc, p.currency)}`, remaining: Number(l.itemRemainingTc), amount: String(l.amountTc) })) }); }} data-testid="edit-receipt">{t("common.edit")}</Button> : null}
                {p.status === "draft" && canManage ? <Button variant="secondary" onClick={() => { act.mutate({ id: p.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-receipt">{t("purchasing.deleteDraft")}</Button> : null}
                {p.status === "draft" && canPost ? <Button onClick={() => { act.mutate({ id: p.id, action: "post" }); }} loading={act.isPending} data-testid="post-receipt">{t("banking.postReceipt")}</Button> : null}
                {p.status === "posted" && canPost && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-receipt">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: p.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
