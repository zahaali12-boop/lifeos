import { Badge, Button } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, SelectField } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { type AgingLike, chooseFullProgram, money, n, overdueOf, usePayablesAging } from "./shared";

type OpenItem = components["schemas"]["OpenItemSummary"];

interface Paying {
  partnerId: string;
  partnerName: string;
  items: OpenItem[];
  currency: string;
  amount: string;
  accountId: string;
}

/** What the trader owes his suppliers and a payment to one of them from a box or account, the oldest invoices first. */
export function PhoneBuyPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  const payables = usePayablesAging(companyId);
  const accounts = useBankAccounts(companyId);
  const [paying, setPaying] = useState<Paying | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const rows = (payables.data?.rows ?? []).filter((row) => n(row.totalFc) > 0).sort((a, b) => overdueOf(b) - overdueOf(a) || n(b.totalFc) - n(a.totalFc));
  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive);

  const startPaying = async (row: AgingLike): Promise<void> => {
    setProblem(null);
    setNotice(null);
    const open = unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: row.partnerId, status: "live" } } }))
      .filter((i) => i.item.kind === "invoice" && n(i.item.remainingTc) > 0)
      .sort((a, b) => a.item.dueDate.localeCompare(b.item.dueDate));
    const itemCurrency = open[0]?.item.currency ?? currency;
    const items = open.filter((i) => i.item.currency === itemCurrency);
    const owed = items.reduce((sum, i) => sum + n(i.item.remainingTc), 0);
    const usable = liveAccounts.filter((a) => a.currency === itemCurrency);
    setPaying({ partnerId: row.partnerId, partnerName: localized(row.partnerName), items, currency: itemCurrency, amount: String(owed), accountId: usable[0]?.id ?? "" });
  };
  const usableAccounts = paying ? liveAccounts.filter((a) => a.currency === paying.currency) : [];
  const payAccount = usableAccounts.find((a) => a.id === paying?.accountId);

  const pay = useMutation({
    mutationFn: async (f: Paying) => {
      if (!payAccount) {
        throw new Error("no account");
      }
      // The oldest invoices are paid first, each up to what it still asks for; anything beyond stays on account.
      let left = n(f.amount);
      const lines: { openItemId: string; amount: number }[] = [];
      for (const i of f.items) {
        if (left <= 0) {
          break;
        }
        const take = Math.min(left, n(i.item.remainingTc));
        lines.push({ openItemId: i.item.id, amount: take });
        left -= take;
      }
      const created = unwrap(await api.POST("/api/v1/banking/payments", {
        body: { companyId, partnerId: f.partnerId, bankAccountId: payAccount.id, kind: "supplier_payment", method: payAccount.kind === "bank" ? "transfer" : "cash", currency: f.currency, lines, onAccount: left > 0 ? left : 0, charges: 0, applyWht: false },
      }));
      return unwrap(await api.POST("/api/v1/banking/payments/{paymentId}/post", { params: { path: { paymentId: created.id } } }));
    },
    onSuccess: async (posted) => {
      setPaying(null);
      setProblem(null);
      setNotice(t("phone.buy.paid", { number: posted.number, amount: money(n(posted.amountTc), posted.currency) }));
      await Promise.all([["ap-aging"], ["bank-accounts"], ["trader-cash"], ["trader-health"], ["home"], ["shipments"], ["shipment"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (paying) {
      pay.mutate(paying);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("phone.buy.title")}</h1>
      {notice ? <p role="status" className="rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="phone-paid">{notice}</p> : null}
      {paying ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-3" data-testid="phone-pay-form">
          <p className="font-medium" dir="auto">{paying.partnerName}</p>
          <Field label={t("phone.buy.amount")} required>
            <DecimalField className="h-12 text-lg" value={paying.amount} onChange={(event) => { setPaying({ ...paying, amount: event.target.value }); }} required data-testid="phone-pay-amount" />
          </Field>
          {usableAccounts.length === 0 ? <p className="text-sm text-danger" data-testid="phone-pay-no-account">{t("phone.buy.noAccount", { currency: paying.currency })}</p> : (
            <Field label={t("phone.buy.account")} required>
              <SelectField className="h-12 text-base" value={paying.accountId} onChange={(event) => { setPaying({ ...paying, accountId: event.target.value }); }} required data-testid="phone-pay-account">
                {usableAccounts.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.code} · {localized(a.name)} ({a.currency})
                  </option>
                ))}
              </SelectField>
            </Field>
          )}
          <FormError message={problem?.message ?? null} />
          <div className="grid grid-cols-2 gap-2">
            <Button type="button" variant="secondary" className="h-12" onClick={() => { setPaying(null); }} data-testid="phone-pay-cancel">{t("phone.buy.cancel")}</Button>
            <Button type="submit" className="h-12" loading={pay.isPending} disabled={!payAccount || n(paying.amount) <= 0} data-testid="phone-pay-submit">{t("phone.buy.submit")}</Button>
          </div>
        </form>
      ) : null}
      <section className="flex flex-col gap-2">
        <h2 className="text-base font-semibold">{t("phone.buy.youOwe")}</h2>
        {rows.length === 0 && !payables.isPending ? <p className="text-sm text-fg-muted" data-testid="phone-nothing-to-pay">{t("phone.buy.nothingToPay")}</p> : (
          <ul className="flex flex-col gap-2">
            {rows.map((row) => (
              <li key={row.partnerId} className="flex items-center gap-2 rounded-lg border border-border bg-surface p-3" data-testid="phone-supplier-row">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm" dir="auto">{row.partnerCode} · {localized(row.partnerName)}</div>
                  <div className="flex items-center gap-2 text-sm">
                    <span className="tabular font-medium" dir="ltr">{money(n(row.totalFc), currency)}</span>
                    {overdueOf(row) > 0 ? <Badge tone="danger">{t("phone.customers.overdue")}</Badge> : null}
                  </div>
                </div>
                <Button type="button" variant="secondary" size="sm" onClick={() => { void startPaying(row); }} data-testid={`phone-pay-${row.partnerCode}`}>{t("phone.buy.pay")}</Button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section className="rounded-lg border border-border bg-surface p-3 text-sm">
        <p className="text-fg-muted">{t("phone.buy.recordHint")}</p>
        <Link to="/trader/shipments" className="mt-2 inline-block text-accent underline-offset-4 hover:underline" onClick={chooseFullProgram} data-testid="phone-open-shipments">{t("phone.buy.openShipments")}</Link>
      </section>
    </div>
  );
}
