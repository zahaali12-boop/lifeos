import { Badge, Button } from "@quicker/ui";
import { Link } from "@tanstack/react-router";
import { Printer } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { formatDate, localized } from "../../lib/format";
import { useCompanyContext } from "../inventory/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";
import { money, n, printVariants, remainingOf, useCash, usePayablesAging, usePostedInvoices, useReceivablesAging } from "./shared";

/** The trader's money at a glance: each box and account, who owes him, what he owes; then the latest sales with their invoices. */
export function PhoneHomePage() {
  const { t } = useTranslation();
  const { companyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  const cash = useCash(companyId);
  const ar = useReceivablesAging(companyId);
  const ap = usePayablesAging(companyId);
  const invoices = usePostedInvoices(companyId);
  const [printing, setPrinting] = useState<PrintSubject | null>(null);
  const accounts = (cash.data?.accounts ?? []).filter((a) => a.isActive);
  const latest = (invoices.data ?? []).slice(0, 8);

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("phone.home.title")}</h1>
      <section className="grid grid-cols-2 gap-2" aria-label={t("phone.home.balances")}>
        {accounts.map((account) => (
          <div key={account.id} className="rounded-lg border border-border bg-surface p-3" data-testid="phone-cash-account">
            <div className="truncate text-xs text-fg-muted" dir="auto">{account.code} · {localized(account.name)}</div>
            <div className="text-xl font-semibold tabular" dir="ltr" data-testid="phone-cash-balance">{money(n(account.balanceNow), account.currency)}</div>
          </div>
        ))}
        {accounts.length === 0 && !cash.isPending ? <p className="col-span-2 text-sm text-fg-muted" data-testid="phone-no-accounts">{t("phone.home.noAccounts")}</p> : null}
        <div className="rounded-lg border border-border bg-surface p-3" data-testid="phone-owed-to-you">
          <div className="text-xs text-fg-muted">{t("phone.home.owedToYou")}</div>
          <div className="text-xl font-semibold tabular" dir="ltr">{money(n(ar.data?.totals.totalFc), currency)}</div>
        </div>
        <div className="rounded-lg border border-border bg-surface p-3" data-testid="phone-you-owe">
          <div className="text-xs text-fg-muted">{t("phone.home.youOwe")}</div>
          <div className="text-xl font-semibold tabular" dir="ltr">{money(n(ap.data?.totals.totalFc), currency)}</div>
        </div>
      </section>
      <div className="grid grid-cols-2 gap-2">
        <Button asChild size="lg" className="h-14 text-base">
          <Link to="/p/sell" data-testid="phone-sell">{t("phone.home.sell")}</Link>
        </Button>
        <Button asChild variant="secondary" size="lg" className="h-14 text-base">
          <Link to="/p/collect" data-testid="phone-collect">{t("phone.home.collect")}</Link>
        </Button>
      </div>
      <section className="flex flex-col gap-2">
        <h2 className="text-base font-semibold">{t("phone.home.latestSales")}</h2>
        {latest.length === 0 ? <p className="text-sm text-fg-muted" data-testid="phone-no-sales">{t("phone.home.noSales")}</p> : (
          <ul className="flex flex-col gap-2">
            {latest.map((invoice) => {
              const remaining = remainingOf(invoice);
              return (
                <li key={invoice.id} className="flex items-center gap-2 rounded-lg border border-border bg-surface p-3" data-testid="phone-invoice">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 text-sm">
                      <span className="font-medium" dir="ltr">{invoice.number}</span>
                      <span className="text-xs text-fg-muted" dir="ltr">{formatDate(invoice.documentDate)}</span>
                    </div>
                    <div className="truncate text-sm" dir="auto">{invoice.partnerCode} · {localized(invoice.partnerName)}</div>
                    <div className="flex items-center gap-2 text-sm">
                      <span className="tabular" dir="ltr">{money(n(invoice.totalGross), invoice.currency)}</span>
                      {remaining > 0 ? <Badge tone="warning">{t("phone.home.open", { amount: money(remaining, invoice.currency) })}</Badge> : <Badge tone="success">{t("phone.home.paid")}</Badge>}
                    </div>
                  </div>
                  <Button type="button" variant="ghost" size="icon" aria-label={t("phone.home.printInvoice", { number: invoice.number })} onClick={() => { setPrinting({ documentType: "sales_invoice", documentId: invoice.id, number: invoice.number, partnerId: invoice.partnerId, variants: printVariants }); }} data-testid="phone-print-invoice">
                    <Printer aria-hidden="true" />
                  </Button>
                </li>
              );
            })}
          </ul>
        )}
      </section>
      {printing ? <PrintDialog subject={printing} onClose={() => { setPrinting(null); }} /> : null}
    </div>
  );
}
