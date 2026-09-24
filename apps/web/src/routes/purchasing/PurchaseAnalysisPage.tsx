import { Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatMoney, formatNumber, localized } from "../../lib/format";
import { today } from "../accounting/shared";
import { Field, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, Tabs, useCompanyContext } from "../inventory/shared";
import { useSuppliers } from "./shared";

type Row = components["schemas"]["PurchaseAnalysisRow"];

const groupings = ["month", "supplier", "item"] as const;

/** The first day of the month eleven months before the given day: twelve whole months up to it. */
function yearBefore(day: string): string {
  const [year, month] = day.split("-").map(Number);
  const start = new Date(Date.UTC(year ?? 2000, (month ?? 1) - 12, 1));
  return start.toISOString().slice(0, 10);
}

/**
 * What was bought over a period, by month, supplier or item: approved orders valued at the net order price in the
 * company's currency, with how much of it has been received and invoiced. Each row carries a bar for its share of the
 * largest row, with the received part shaded, so the trend and the leaders read at a glance.
 */
export function PurchaseAnalysisPage() {
  const { t } = useTranslation();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const suppliers = useSuppliers(companyId);
  const [groupBy, setGroupBy] = useState<(typeof groupings)[number]>("month");
  const [to, setTo] = useState(today());
  const [from, setFrom] = useState(() => yearBefore(today()));
  const [partnerId, setPartnerId] = useState("");

  const analysis = useQuery({
    queryKey: ["purchase-analysis", companyId, groupBy, from, to, partnerId],
    enabled: Boolean(companyId) && Boolean(from) && Boolean(to),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/reports/analysis", { params: { query: { companyId, from, to, groupBy, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const data = analysis.data;
  const rows = data?.rows ?? [];
  const largest = Math.max(1, ...rows.map((r) => Number(r.ordered)));
  const label = (row: Row): string => (groupBy === "month" ? row.key : `${row.code} · ${localized(row.name)}`);

  return (
    <>
      <PageHeader title={t("nav.purchaseAnalysis")} description={t("purchasing.analysis.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("purchasing.analysis.from")}>
          <TextField type="date" value={from} onChange={(e) => { setFrom(e.target.value); }} dir="ltr" data-testid="analysis-from" />
        </Field>
        <Field label={t("purchasing.analysis.to")}>
          <TextField type="date" value={to} onChange={(e) => { setTo(e.target.value); }} dir="ltr" data-testid="analysis-to" />
        </Field>
        <Field label={t("partners.supplier")}>
          <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="analysis-supplier">
            <option value="">{t("common.all")}</option>
            {(suppliers.data ?? []).map((s) => (
              <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <Tabs tabs={groupings.map((g) => ({ id: g, label: t(`purchasing.analysis.by.${g}`), testId: `analysis-by-${g}` }))} value={groupBy} onChange={(id) => { setGroupBy(id as (typeof groupings)[number]); }} />
      {data ? (
        <dl className="my-4 grid gap-3 sm:grid-cols-4" data-testid="analysis-totals">
          {([["ordered", data.ordered], ["received", data.received], ["invoiced", data.invoiced]] as const).map(([key, value]) => (
            <div key={key} className="rounded-lg border border-border bg-surface p-3">
              <dt className="text-xs uppercase tracking-wide text-fg-muted">{t(`purchasing.analysis.${key}`)}</dt>
              <dd className="tabular text-lg font-semibold" dir="ltr" data-testid={`analysis-total-${key}`}>{formatMoney(value, data.currency)}</dd>
            </div>
          ))}
          <div className="rounded-lg border border-border bg-surface p-3">
            <dt className="text-xs uppercase tracking-wide text-fg-muted">{t("purchasing.analysis.orders")}</dt>
            <dd className="tabular text-lg font-semibold" dir="ltr">{formatNumber(data.orders)}</dd>
          </div>
        </dl>
      ) : null}
      {rows.length === 0 && data ? <p className="text-sm text-fg-muted">{t("purchasing.analysis.empty")}</p> : null}
      {rows.length > 0 && data ? (
        <Table data-testid="analysis-table">
          <TableHeader>
            <TableRow>
              <TableHead>{t(`purchasing.analysis.by.${groupBy}`)}</TableHead>
              <TableHead className="w-2/5">{t("purchasing.analysis.share")}</TableHead>
              <TableHead className="text-end">{t("purchasing.analysis.orders")}</TableHead>
              {groupBy === "item" ? <TableHead className="text-end">{t("purchasing.quantity")}</TableHead> : null}
              <TableHead className="text-end">{t("purchasing.analysis.ordered")}</TableHead>
              <TableHead className="text-end">{t("purchasing.analysis.received")}</TableHead>
              <TableHead className="text-end">{t("purchasing.analysis.invoiced")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row) => {
              const share = (Number(row.ordered) / largest) * 100;
              const received = Number(row.ordered) === 0 ? 0 : (Number(row.received) / Number(row.ordered)) * 100;
              return (
                <TableRow key={row.key} data-testid="analysis-row">
                  <TableCell dir={groupBy === "month" ? "ltr" : "auto"} className="whitespace-nowrap">{label(row)}</TableCell>
                  <TableCell>
                    <div className="h-3 rounded-sm bg-surface-sunken" role="presentation">
                      <div className="h-3 rounded-sm bg-accent/30" style={{ inlineSize: `${String(share)}%` }}>
                        <div className="h-3 rounded-sm bg-accent" style={{ inlineSize: `${String(received)}%` }} />
                      </div>
                    </div>
                  </TableCell>
                  <TableNumberCell>{formatNumber(row.orders)}</TableNumberCell>
                  {groupBy === "item" ? <TableNumberCell>{row.quantity === null ? "" : `${formatNumber(row.quantity, { maximumFractionDigits: 3 })} ${row.uomCode ?? ""}`}</TableNumberCell> : null}
                  <TableNumberCell>{formatMoney(row.ordered, data.currency)}</TableNumberCell>
                  <TableNumberCell>{formatMoney(row.received, data.currency)}</TableNumberCell>
                  <TableNumberCell>{formatMoney(row.invoiced, data.currency)}</TableNumberCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      ) : null}
      <p className="mt-2 text-xs text-fg-muted">{t("purchasing.analysis.legend")}</p>
    </>
  );
}
