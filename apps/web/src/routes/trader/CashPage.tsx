import { Badge, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { PageHeader } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { rangeParams } from "./period";
import { PeriodFilter, usePeriod } from "./PeriodFilter";

type CashAccount = components["schemas"]["TraderCashReportAccount"];
type Movement = components["schemas"]["TraderCashMovement"];

/**
 * Cash and bank (trader edition, ADR-0035 §18): each cash box and bank account, what it holds now, what it held when
 * the period began, what came in and went out, what it held at the period's end, and every movement newest first with
 * the balance after it.
 */
export function CashPage() {
  const { t } = useTranslation();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const [period, setPeriod, range] = usePeriod("month");
  const [selected, setSelected] = useState<string | null>(null);
  const report = useQuery({
    queryKey: ["trader-cash", companyId, range.from ?? "", range.to ?? ""],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/cash", { params: { query: { companyId, ...rangeParams(range) } } })),
  });
  const currency = report.data?.currency ?? company?.functionalCurrency ?? "";
  const money = (value: number | string): string => (currency === "" ? "—" : formatMoney(value, currency));
  const accounts = report.data?.accounts ?? [];
  const current: CashAccount | undefined = accounts.find((a) => a.id === selected) ?? accounts[0];
  const what = (m: Movement): string => t(`trader.cash.sources.${m.sourceDocumentType ?? "other"}`, { defaultValue: t("trader.cash.sources.other") });

  return (
    <>
      <PageHeader title={t("trader.cash.title")} description={t("trader.cash.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <PeriodFilter value={period} onChange={setPeriod} />
      </div>
      {report.data && accounts.length === 0 ? (
        <div className="rounded-md border border-dashed border-border p-6 text-center text-sm text-fg-muted">
          <p className="font-medium text-fg">{t("trader.cash.empty")}</p>
          <p>{t("trader.cash.emptyDescription")}</p>
        </div>
      ) : null}
      <section className="mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-label={t("trader.cash.accounts")}>
        {accounts.map((a) => (
          <button
            key={a.id}
            type="button"
            onClick={() => { setSelected(a.id); }}
            aria-pressed={current?.id === a.id}
            className={`flex flex-col gap-1 rounded-md border p-3 text-start focus:outline-none focus-visible:ring-2 focus-visible:ring-accent ${current?.id === a.id ? "border-accent bg-accent-soft/30" : "border-border"}`}
            data-testid="cash-account"
          >
            <span className="flex items-center justify-between gap-2">
              <span className="font-semibold" dir="auto">{a.code} · {localized(a.name)}</span>
              <Badge tone={a.kind === "cash" ? "info" : "neutral"}>{t(`trader.cash.kinds.${a.kind}`, { defaultValue: a.kind })}</Badge>
            </span>
            <span className="text-xs text-fg-muted">{t("trader.cash.balanceNow")}</span>
            <span className="text-lg font-semibold tabular" dir="ltr" data-testid="cash-balance">{money(a.balanceNow)}</span>
            <span className="text-xs text-fg-muted" dir="auto">{t("trader.cash.inOut", { in: money(a.in), out: money(a.out) })}</span>
          </button>
        ))}
      </section>
      {current ? (
        <section className="flex flex-col gap-3" data-testid="cash-detail">
          <h2 className="text-base font-semibold" dir="auto">{current.code} · {localized(current.name)}</h2>
          <div className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
            {([["opening", current.opening], ["in", current.in], ["out", current.out], ["closing", current.closing]] as const).map(([key, value]) => (
              <div key={key} className="rounded-md border border-border p-2">
                <div className="text-xs text-fg-muted">{t(`trader.cash.${key}`)}</div>
                <div className="font-semibold tabular" dir="ltr" data-testid={`cash-${key}`}>{money(value)}</div>
              </div>
            ))}
          </div>
          {current.movements.length === 0 ? (
            <p className="text-sm text-fg-muted">{t("trader.cash.noMovements")}</p>
          ) : (
            <Table data-testid="cash-movements">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("trader.cash.date")}</TableHead>
                  <TableHead>{t("trader.cash.what")}</TableHead>
                  <TableHead>{t("trader.cash.reference")}</TableHead>
                  <TableHead className="text-end">{t("trader.cash.in")}</TableHead>
                  <TableHead className="text-end">{t("trader.cash.out")}</TableHead>
                  <TableHead className="text-end">{t("trader.cash.balanceAfter")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {current.movements.map((m) => (
                  <TableRow key={m.id} data-testid="cash-movement">
                    <TableCell dir="ltr">{formatDate(m.date)}</TableCell>
                    <TableCell dir="auto">{what(m)}</TableCell>
                    <TableCell dir="auto">{m.reference ?? ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-move-in">{Number(m.in) > 0 ? money(m.in) : ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-move-out">{Number(m.out) > 0 ? money(m.out) : ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-balance-after">{money(m.balanceAfter)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>
      ) : null}
    </>
  );
}
