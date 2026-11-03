import { Button } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link, useSearch } from "@tanstack/react-router";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, SelectField, TextField } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";
import { type Invoice, money, n, printVariants, remainingOf, usePostedInvoices } from "./shared";

interface Collected {
  id: string;
  number: string;
  amount: number;
  currency: string;
  partnerId: string;
}

const oldestFirst = (a: Invoice, b: Invoice): number => a.documentDate.localeCompare(b.documentDate) || a.number.localeCompare(b.number);

/** A customer's payment from the phone: the invoice it settles (oldest first), the amount, the box or account; then its receipt. */
export function PhoneCollectPage() {
  const { t } = useTranslation();
  const search = useSearch({ strict: false });
  const queryClient = useQueryClient();
  const { companyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  const invoices = usePostedInvoices(companyId);
  const accounts = useBankAccounts(companyId);
  const open = useMemo(() => (invoices.data ?? []).filter((invoice) => remainingOf(invoice) > 0), [invoices.data]);
  const [partnerId, setPartnerId] = useState("");
  const [invoiceId, setInvoiceId] = useState("");
  const [amount, setAmount] = useState("");
  const [accountId, setAccountId] = useState("");
  const [date, setDate] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [done, setDone] = useState<Collected | null>(null);
  const [printing, setPrinting] = useState<PrintSubject | null>(null);

  const invoicesOf = (partner: string): Invoice[] => open.filter((invoice) => invoice.partnerId === partner).sort(oldestFirst);
  const choosePartner = (partner: string): void => {
    setPartnerId(partner);
    const first = invoicesOf(partner)[0];
    setInvoiceId(first?.id ?? "");
    setAmount(first ? String(remainingOf(first)) : "");
  };
  const chooseInvoice = (id: string): void => {
    setInvoiceId(id);
    const invoice = open.find((i) => i.id === id);
    setAmount(invoice ? String(remainingOf(invoice)) : "");
  };

  // Opened for one invoice (from a sale just made) or for one customer: the form starts on it once the invoices are known.
  useEffect(() => {
    if (!invoices.data || partnerId) {
      return;
    }
    const wanted = search.invoice ? open.find((invoice) => invoice.id === search.invoice) : undefined;
    if (wanted) {
      setPartnerId(wanted.partnerId);
      setInvoiceId(wanted.id);
      setAmount(String(remainingOf(wanted)));
    } else if (search.customer && open.some((invoice) => invoice.partnerId === search.customer)) {
      choosePartner(search.customer);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once the invoices arrive, never again once a customer is chosen
  }, [invoices.data]);

  const customers = useMemo(() => {
    const seen = new Map<string, Invoice>();
    for (const invoice of open) {
      if (!seen.has(invoice.partnerId)) {
        seen.set(invoice.partnerId, invoice);
      }
    }
    return [...seen.values()].sort((a, b) => a.partnerCode.localeCompare(b.partnerCode));
  }, [open]);
  const partnerInvoices = invoicesOf(partnerId);
  const invoice = partnerInvoices.find((i) => i.id === invoiceId) ?? partnerInvoices[0];
  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive);
  const account = liveAccounts.find((a) => a.id === accountId) ?? liveAccounts[0];

  const collect = useMutation({
    mutationFn: async () => {
      if (!invoice || !account) {
        throw new Error("nothing to collect");
      }
      const item = invoice.openItems.find((i) => n(i.remainingTc) > 0);
      if (!item) {
        throw new Error("nothing open on the invoice");
      }
      const receipt = unwrap(await api.POST("/api/v1/banking/receipts", { body: { companyId, partnerId: invoice.partnerId, bankAccountId: account.id, kind: "customer_receipt", receiptDate: date || null, method: account.kind === "cash" ? "cash" : "transfer", lines: [{ openItemId: item.id, amount: n(amount) }], onAccount: 0, charges: 0 } }));
      return unwrap(await api.POST("/api/v1/banking/receipts/{receiptId}/post", { params: { path: { receiptId: receipt.id } } }));
    },
    onSuccess: async (posted) => {
      setProblem(null);
      setDone({ id: posted.id, number: posted.number, amount: n(posted.amountTc), currency: posted.currency, partnerId: posted.partnerId });
      await Promise.all([["invoices"], ["ar-aging"], ["trader-cash"], ["bank-accounts"], ["receipts"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const createCash = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency, isActive: true } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["bank-accounts"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    collect.mutate();
  };
  const reset = (): void => {
    setDone(null);
    setPartnerId("");
    setInvoiceId("");
    setAmount("");
    setProblem(null);
  };

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("phone.collect.title")}</h1>
      {done ? (
        <div className="flex flex-col gap-3 rounded-lg border border-success/40 bg-success-soft p-4" data-testid="phone-collected">
          <p role="status" className="font-medium">{t("phone.collect.collected", { number: done.number, amount: money(done.amount, done.currency) })}</p>
          <Button type="button" size="lg" className="h-12 text-base" onClick={() => { setPrinting({ documentType: "bank_receipt", documentId: done.id, number: done.number, partnerId: done.partnerId, variants: printVariants }); }} data-testid="phone-receipt-pdf">{t("phone.collect.receiptPdf")}</Button>
          <Button type="button" size="lg" variant="secondary" className="h-12 text-base" onClick={reset} data-testid="phone-collect-another">{t("phone.collect.another")}</Button>
          <Button asChild size="lg" variant="ghost" className="h-12 text-base">
            <Link to="/p" data-testid="phone-collect-home">{t("phone.tabs.home")}</Link>
          </Button>
        </div>
      ) : open.length === 0 && !invoices.isPending ? (
        <div className="flex flex-col gap-3">
          <p className="text-sm text-fg-muted" data-testid="phone-nothing-open">{t("phone.collect.nothingOpen")}</p>
          <Button asChild size="lg" className="h-12 text-base">
            <Link to="/p/sell">{t("phone.home.sell")}</Link>
          </Button>
        </div>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <Field label={t("phone.collect.customer")} required>
            <SelectField className="h-12 text-base" value={partnerId} onChange={(event) => { choosePartner(event.target.value); }} required data-testid="phone-collect-customer">
              <option value="">{t("phone.collect.chooseCustomer")}</option>
              {customers.map((first) => (
                <option key={first.partnerId} value={first.partnerId}>
                  {first.partnerCode} · {localized(first.partnerName)}
                </option>
              ))}
            </SelectField>
          </Field>
          {partnerId ? (
            <Field label={t("phone.collect.invoice")} required>
              <SelectField className="h-12 text-base" value={invoice?.id ?? ""} onChange={(event) => { chooseInvoice(event.target.value); }} required data-testid="phone-collect-invoice">
                {partnerInvoices.map((i) => (
                  <option key={i.id} value={i.id}>
                    {i.number} · {t("phone.collect.remaining", { amount: money(remainingOf(i), i.currency), date: formatDate(i.openItems[0]?.dueDate ?? i.documentDate) })}
                  </option>
                ))}
              </SelectField>
            </Field>
          ) : null}
          <Field label={t("phone.collect.amount")} required>
            <DecimalField className="h-12 text-lg" value={amount} onChange={(event) => { setAmount(event.target.value); }} required data-testid="phone-collect-amount" />
          </Field>
          {liveAccounts.length === 0 && !accounts.isPending ? (
            <div className="flex flex-col gap-2">
              <p className="text-sm text-fg-muted" data-testid="phone-no-account">{t("phone.collect.noAccount")}</p>
              <Button type="button" variant="secondary" loading={createCash.isPending} onClick={() => { createCash.mutate(); }} data-testid="phone-create-cash">{t("phone.collect.createCash")}</Button>
            </div>
          ) : (
            <Field label={t("phone.collect.account")} required>
              <SelectField className="h-12 text-base" value={account?.id ?? ""} onChange={(event) => { setAccountId(event.target.value); }} required data-testid="phone-collect-account">
                {liveAccounts.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.code} · {localized(a.name)} ({a.currency})
                  </option>
                ))}
              </SelectField>
            </Field>
          )}
          <Field label={t("phone.collect.date")}>
            <TextField type="date" className="h-12 text-base" value={date} onChange={(event) => { setDate(event.target.value); }} dir="ltr" data-testid="phone-collect-date" />
          </Field>
          <FormError message={problem?.message ?? null} />
          <Button type="submit" size="lg" className="h-14 text-base" loading={collect.isPending} disabled={!invoice || !account || n(amount) <= 0} data-testid="phone-collect-submit">{t("phone.collect.submit")}</Button>
        </form>
      )}
      {printing ? <PrintDialog subject={printing} onClose={() => { setPrinting(null); }} /> : null}
    </div>
  );
}
