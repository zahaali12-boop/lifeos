import { Badge, Button } from "@quicker/ui";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { localized } from "../../lib/format";
import { TextField } from "../common";
import { useCompanyContext } from "../inventory/shared";
import { PrintDialog, type PrintSubject } from "../print/PrintDialog";
import { money, n, overdueOf, printVariants, useCustomers, useReceivablesAging } from "./shared";

/** Every customer with what he owes; his statement to print or send, and the way to his payment. */
export function PhoneCustomersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { companyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  const customers = useCustomers(companyId);
  const aging = useReceivablesAging(companyId);
  const [query, setQuery] = useState("");
  const [printing, setPrinting] = useState<PrintSubject | null>(null);
  const needle = query.trim().toLowerCase();
  const rows = (customers.data ?? [])
    .map((customer) => ({ customer, aging: aging.data?.rows.find((row) => row.partnerId === customer.partnerId) }))
    .filter(({ customer }) => needle === "" || customer.partnerCode.toLowerCase().includes(needle) || localized(customer.partnerName).toLowerCase().includes(needle))
    .sort((a, b) => n(b.aging?.totalFc) - n(a.aging?.totalFc) || a.customer.partnerCode.localeCompare(b.customer.partnerCode));

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">{t("phone.customers.title")}</h1>
      <TextField type="search" className="h-12 text-base" value={query} onChange={(event) => { setQuery(event.target.value); }} placeholder={t("phone.customers.search")} aria-label={t("phone.customers.search")} data-testid="phone-customer-search" />
      {rows.length === 0 && !customers.isPending ? <p className="text-sm text-fg-muted" data-testid="phone-no-customers">{t("phone.customers.none")}</p> : (
        <ul className="flex flex-col gap-2">
          {rows.map(({ customer, aging: row }) => {
            const owed = n(row?.totalFc);
            return (
              <li key={customer.partnerId} className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3" data-testid="phone-customer-row">
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate font-medium" dir="auto">{customer.partnerCode} · {localized(customer.partnerName)}</span>
                  {row && overdueOf(row) > 0 ? <Badge tone="danger">{t("phone.customers.overdue")}</Badge> : null}
                </div>
                <div className="text-sm tabular" dir="ltr" data-testid="phone-customer-owed">{owed > 0 ? t("phone.customers.owes", { amount: money(owed, currency) }) : t("phone.customers.nothingOwed")}</div>
                <div className="grid grid-cols-2 gap-2">
                  <Button type="button" variant="secondary" className="h-11" onClick={() => { setPrinting({ documentType: "customer_statement", documentId: customer.partnerId, parameters: { companyId }, number: customer.partnerCode, partnerId: customer.partnerId, variants: printVariants }); }} data-testid={`phone-statement-${customer.partnerCode}`}>{t("phone.customers.statement")}</Button>
                  <Button type="button" className="h-11" disabled={owed <= 0} onClick={() => { void navigate({ to: "/p/collect", search: { customer: customer.partnerId } }); }} data-testid={`phone-collect-${customer.partnerCode}`}>{t("phone.customers.collect")}</Button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
      {printing ? <PrintDialog subject={printing} onClose={() => { setPrinting(null); }} /> : null}
    </div>
  );
}
