import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { recordRoute } from "../../lib/documents";
import { formatDate, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { useCompanyCustomers } from "../sales/pricing/shared";
import { money, num, SalesStatus } from "../sales/shared";
import { DunningPanel } from "./DunningPanel";
import { PrintButton } from "../print/PrintDialog";

type ReceivableRow = components["schemas"]["ReceivableSummary"];

function useReceivables(companyId: string, partnerId: string, status: string) {
  return useQuery({
    queryKey: ["ar-open-items", companyId, partnerId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items", { params: { query: { companyId, ...(partnerId ? { partnerId } : {}), ...(status ? { status } : {}) } } })),
  });
}

/**
 * Receivable open items (roadmap 5.6): what each customer owes per invoice instalment, credit note, deposit and receipt
 * on account, aged at any date from the items and their settlements; holds; credits and deposits applied to invoices;
 * a customer's statement for a period.
 */
export function ReceivablesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [partnerId, setPartnerId] = useState("");
  const [status, setStatus] = useState("live");
  const [tab, setTab] = useState("items");
  const [asOf, setAsOf] = useState(today());
  const [statementFrom, setStatementFrom] = useState(`${today().slice(0, 7)}-01`);
  const [openId, setOpenId] = useState<string | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [hold, setHold] = useState<string | null>(null);
  const [apply, setApply] = useState<{ settledItemId: string; amount: string } | null>(null);
  const [writeOff, setWriteOff] = useState<{ amount: string; kind: string; reason: string } | null>(null);
  const [promise, setPromise] = useState<{ promisedOn: string; note: string } | null>(null);
  const [netting, setNetting] = useState<{ payableItemId: string; amount: string } | null>(null);
  const customers = useCompanyCustomers(companyId);
  const list = useReceivables(companyId, partnerId, status);
  const canManage = can("receivables.open_item.manage");
  const canSettle = can("receivables.settlement.post");
  const canWriteOff = can("receivables.writeoff.post");
  const canDun = can("receivables.dunning.read");
  const aging = useQuery({
    queryKey: ["ar-aging", companyId, asOf, partnerId],
    enabled: Boolean(companyId) && tab === "aging",
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items/aging", { params: { query: { companyId, asOf, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const statement = useQuery({
    queryKey: ["ar-statement", companyId, partnerId, statementFrom, asOf],
    enabled: Boolean(companyId) && Boolean(partnerId) && tab === "statement",
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/statement", { params: { query: { companyId, partnerId, from: statementFrom, to: asOf } } })),
  });
  const detail = useQuery({
    queryKey: ["ar-open-item", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items/{itemId}", { params: { path: { itemId: openId ?? "" } } })),
  });
  const settlements = useQuery({
    queryKey: ["ar-settlements", companyId, openId],
    enabled: Boolean(openId) && Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/settlements", { params: { query: { companyId, openItemId: openId ?? "" } } })),
  });
  const targets = useReceivables(companyId, detail.data?.item.partnerId ?? "", "live");
  const payables = useQuery({
    queryKey: ["open-items", companyId, detail.data?.item.partnerId ?? "", "live"],
    enabled: Boolean(companyId) && Boolean(detail.data?.item.partnerId) && netting !== null,
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: detail.data?.item.partnerId ?? "", status: "live" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["ar-open-items"], ["ar-open-item"], ["ar-aging"], ["ar-statement"], ["ar-settlements"], ["open-items"], ["open-item"], ["sales-invoice"], ["sales-invoices"], ["customer-360"], ["dunning-preview"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const act = useMutation({
    mutationFn: async (input: { action: "hold" | "release" | "apply" | "reverse" | "write_off" | "promise" | "net"; id: string; reason?: string; settledItemId?: string; amount?: number; kind?: string; promisedOn?: string | null; note?: string; payableItemId?: string }) => {
      switch (input.action) {
        case "hold": return unwrap(await api.POST("/api/v1/receivables/open-items/{itemId}/hold", { params: { path: { itemId: input.id } }, body: { reason: input.reason ?? "" } }));
        case "release": return unwrap(await api.POST("/api/v1/receivables/open-items/{itemId}/release", { params: { path: { itemId: input.id } } }));
        case "apply": return unwrap(await api.POST("/api/v1/receivables/settlements/apply", { body: { settlingItemId: input.id, settledItemId: input.settledItemId ?? "", amount: input.amount ?? 0 } }));
        case "reverse": return unwrap(await api.POST("/api/v1/receivables/settlements/{settlementId}/reverse", { params: { path: { settlementId: input.id } }, body: { reason: input.reason ?? "" } }));
        case "write_off": return unwrap(await api.POST("/api/v1/receivables/open-items/{itemId}/write-off", { params: { path: { itemId: input.id } }, body: { reason: input.reason ?? "", amount: input.amount ?? null, kind: input.kind ?? "bad_debt" } }));
        case "promise": return unwrap(await api.POST("/api/v1/receivables/open-items/{itemId}/promise", { params: { path: { itemId: input.id } }, body: { promisedOn: input.promisedOn ?? null, note: input.note ?? null } }));
        case "net": return unwrap(await api.POST("/api/v1/receivables/nettings", { body: { receivableItemId: input.id, payableItemId: input.payableItemId ?? "", amount: input.amount ?? null } }));
      }
    },
    onSuccess: async () => { setProblem(null); setHold(null); setApply(null); setWriteOff(null); setPromise(null); setNetting(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<ReceivableRow, unknown>[]>(
    () => [
      { id: "document", accessorFn: (r) => r.item.documentNumber, header: t("sales.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.item.documentNumber}{Number(row.original.item.instalment) > 1 ? ` / ${String(row.original.item.instalment)}` : ""}</span> },
      { id: "kind", accessorFn: (r) => r.item.kind, header: t("sales.invoicing.kind"), size: 130, cell: ({ row }) => t(`receivables.kinds.${row.original.item.kind}`, { defaultValue: row.original.item.kind }) },
      { id: "status", accessorFn: (r) => r.item.status, header: t("common.status"), size: 150, cell: ({ row }) => <span className="flex items-center gap-1"><SalesStatus status={row.original.item.status} />{row.original.item.onHold ? <Badge tone="danger">{t("payables.held")}</Badge> : null}</span> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "due", accessorFn: (r) => r.item.dueDate, header: t("sales.invoicing.dueDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.item.dueDate)}</span> },
      { id: "original", accessorFn: (r) => r.item.originalTc, header: t("sales.amount"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.item.originalTc, row.original.item.currency)}</span> },
      { id: "remaining", accessorFn: (r) => r.item.remainingTc, header: t("purchasing.remaining"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.item.remainingTc, row.original.item.currency)}</span> },
      { id: "remainingFc", accessorFn: (r) => r.item.remainingFc, header: t("payables.remainingFc"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.item.remainingFc, row.original.functionalCurrency)}</span> },
    ],
    [t],
  );

  const d = detail.data;
  const item = d?.item;
  const canApply = item !== undefined && Number(item.originalTc) < 0 && (item.status === "open" || item.status === "partially_settled");
  const canHold = item !== undefined && (item.status === "open" || item.status === "partially_settled");
  const bucket = (value: number | string, currency: string): string => (Number(value) === 0 ? "" : money(value, currency));
  const documentLink = (type: string, id: string, number: string) => {
    const route = recordRoute(type, id);
    return route ? <Link to={route.to} search={route.search} dir="ltr" className="underline-offset-2 hover:underline">{number}</Link> : <span dir="ltr">{number}</span>;
  };

  return (
    <>
      <PageHeader title={t("nav.receivables")} description={t("receivables.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("sales.customer")}>
          <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="customer-filter">
            <option value="">{t("common.all")}</option>
            {(customers.data ?? []).map((c) => (
              <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
            ))}
          </SelectField>
        </Field>
        {tab === "items" ? (
          <Field label={t("common.status")}>
            <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
              <option value="live">{t("payables.live")}</option>
              {["open", "partially_settled", "settled", "reversed"].map((s) => (
                <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
              ))}
            </SelectField>
          </Field>
        ) : tab === "dunning" ? null : tab === "statement" ? (
          <>
            <Field label={t("purchasing.analysis.from")}>
              <TextField type="date" value={statementFrom} onChange={(e) => { setStatementFrom(e.target.value); }} dir="ltr" data-testid="statement-from" />
            </Field>
            <Field label={t("purchasing.analysis.to")}>
              <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="statement-to" />
            </Field>
            {partnerId ? <PrintButton subject={{ documentType: "customer_statement", parameters: { companyId, partnerId, from: statementFrom, to: asOf }, number: t("payables.statement.title"), partnerId }} testId="print-statement" /> : null}
          </>
        ) : (
          <Field label={t("payables.asOf")}>
            <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="aging-as-of" />
          </Field>
        )}
      </div>
      <Tabs tabs={[{ id: "items", label: t("payables.openItems"), testId: "tab-items" }, { id: "aging", label: t("payables.aging"), testId: "tab-aging" }, { id: "statement", label: t("payables.statement.title"), testId: "tab-statement" }, ...(canDun ? [{ id: "dunning", label: t("receivables.dunning.title"), testId: "tab-dunning" }] : [])]} value={tab} onChange={setTab} />
      {tab === "items" ? (
        <DataGrid<ReceivableRow> label="nav.receivables" columns={columns} data={list.data ?? []} rowKey={(row) => row.item.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("receivables.emptyItems")} emptyDescription={t("receivables.emptyItemsDescription")} onOpen={(row) => { setProblem(null); setHold(null); setApply(null); setOpenId(row.item.id); }} />
      ) : tab === "aging" ? (
        <div className="mt-3" data-testid="aging-report">
          {aging.data ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("sales.customer")}</TableHead>
                  <TableHead>{t("payables.notDue")}</TableHead>
                  <TableHead>1–30</TableHead>
                  <TableHead>31–60</TableHead>
                  <TableHead>61–90</TableHead>
                  <TableHead>90+</TableHead>
                  <TableHead>{t("payables.total")}</TableHead>
                  <TableHead>{t("receivables.deposits")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {aging.data.rows.map((r) => (
                  <TableRow key={r.partnerId} data-testid="aging-row">
                    <TableCell dir="auto">{r.partnerCode} · {localized(r.partnerName)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.notDue, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days1To30, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days31To60, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days61To90, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.over90, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular font-semibold" dir="ltr">{money(r.totalFc, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.depositsFc, aging.data.functionalCurrency)}</TableCell>
                  </TableRow>
                ))}
                <TableRow data-testid="aging-totals">
                  <TableCell className="font-semibold">{t("payables.total")}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.notDue, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days1To30, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days31To60, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days61To90, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.over90, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular font-semibold" dir="ltr" data-testid="aging-total">{money(aging.data.totals.totalFc, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.depositsFc, aging.data.functionalCurrency)}</TableCell>
                </TableRow>
              </TableBody>
            </Table>
          ) : <p className="text-sm text-fg-muted">{companyId ? t("common.loading") : t("payables.chooseCompany")}</p>}
        </div>
      ) : tab === "dunning" ? (
        <DunningPanel companyId={companyId} partnerId={partnerId} customers={customers.data ?? []} />
      ) : (
        <div className="mt-3 flex flex-col gap-4" data-testid="customer-statement">
          {!partnerId ? <p className="text-sm text-fg-muted" data-testid="statement-choose">{t("receivables.statement.chooseCustomer")}</p>
            : !statement.data ? <p className="text-sm text-fg-muted">{t("common.loading")}</p>
              : statement.data.currencies.length === 0 ? <p className="text-sm text-fg-muted">{t("receivables.statement.empty")}</p>
                : statement.data.currencies.map((c) => (
                  <section key={c.currency} className="flex flex-col gap-2" aria-label={c.currency}>
                    <KeyValues entries={[
                      [t("accounting.currency"), c.currency],
                      [t("receivables.statement.opening"), money(c.opening, c.currency)],
                      [t("receivables.statement.increases"), money(c.increases, c.currency)],
                      [t("receivables.statement.decreases"), money(c.decreases, c.currency)],
                      [t("receivables.statement.closing"), <span key="closing" data-testid="statement-closing">{money(c.closing, c.currency)}</span>],
                    ]} />
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("payables.statement.date")}</TableHead>
                          <TableHead>{t("sales.number")}</TableHead>
                          <TableHead>{t("sales.invoicing.kind")}</TableHead>
                          <TableHead>{t("sales.invoicing.customerReference")}</TableHead>
                          <TableHead>{t("sales.invoicing.dueDate")}</TableHead>
                          <TableHead>{t("payables.statement.amount")}</TableHead>
                          <TableHead>{t("payables.statement.balance")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {c.lines.map((line, index) => (
                          <TableRow key={`${line.documentId}-${String(index)}`} data-testid="statement-line">
                            <TableCell dir="ltr">{formatDate(line.date)}</TableCell>
                            <TableCell>{documentLink(line.documentType, line.documentId, line.documentNumber)}</TableCell>
                            <TableCell>{t(`receivables.kinds.${line.kind}`, { defaultValue: line.kind })}{line.reversal ? ` (${t("payables.statement.reversal")})` : ""}</TableCell>
                            <TableCell dir="ltr">{line.customerReference ?? ""}</TableCell>
                            <TableCell dir="ltr">{line.dueDate ? formatDate(line.dueDate) : ""}</TableCell>
                            <TableCell className="tabular" dir="ltr">{money(line.amount, c.currency)}</TableCell>
                            <TableCell className="tabular" dir="ltr">{money(line.balance, c.currency)}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </section>
                ))}
        </div>
      )}

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {d && item ? (
            <div className="flex flex-col gap-4" data-testid="open-item-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  {documentLink(item.documentType, item.documentId, item.documentNumber)}
                  <SalesStatus status={item.status} />
                  {item.onHold ? <Badge tone="danger">{t("payables.held")}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.invoicing.kind"), t(`receivables.kinds.${item.kind}`, { defaultValue: item.kind })],
                [t("sales.customer"), `${d.partnerCode} · ${localized(d.partnerName)}`],
                [t("purchasing.postingDate"), formatDate(item.postingDate)],
                [t("sales.invoicing.dueDate"), formatDate(item.dueDate)],
                [t("sales.amount"), money(item.originalTc, item.currency)],
                [t("purchasing.remaining"), <span key="rem" data-testid="item-remaining">{money(item.remainingTc, item.currency)}</span>],
                [t("payables.remainingFc"), money(item.remainingFc, d.functionalCurrency)],
                ...(item.holdReason ? [[t("payables.holdReason"), item.holdReason] as [string, string]] : []),
                ...(Number(item.dunningLevel) > 0 ? [[t("receivables.dunning.level"), <span key="dl" data-testid="item-dunning-level">{String(item.dunningLevel)}</span>] as [string, ReactNode]] : []),
                ...(item.promisedPaymentDate ? [[t("receivables.promise.title"), <span key="pr" dir="ltr" data-testid="item-promise">{formatDate(item.promisedPaymentDate)}</span>] as [string, ReactNode]] : []),
              ]} />
              <h3 className="text-sm font-semibold">{t("purchasing.settlements")}</h3>
              {(settlements.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.noSettlements")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.postingDate")}</TableHead>
                      <TableHead>{t("sales.invoicing.kind")}</TableHead>
                      <TableHead>{t("purchasing.appliedTo")}</TableHead>
                      <TableHead>{t("sales.amount")}</TableHead>
                      <TableHead>{t("purchasing.fxGainLoss")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(settlements.data ?? []).map((st) => (
                      <TableRow key={st.id} data-testid="settlement-row">
                        <TableCell dir="ltr">{formatDate(st.settlementDate)}</TableCell>
                        <TableCell>{t(`receivables.settlementKinds.${st.kind}`, { defaultValue: st.kind })}</TableCell>
                        <TableCell dir="ltr">{st.settlingDocumentNumber} → {st.settledDocumentNumber}</TableCell>
                        <TableCell className="tabular" dir="ltr">{money(st.amountTc, st.currency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{money(st.fxGainLossFc, d.functionalCurrency)}</TableCell>
                        <TableCell>{canSettle && st.status === "posted" && !st.reversesSettlementId && (st.kind === "credit_application" || st.kind === "deposit_application") ? <Button variant="ghost" size="sm" onClick={() => { act.mutate({ action: "reverse", id: st.id, reason: t("payables.unappliedReason") }); }} data-testid="unapply">{t("payables.unapply")}</Button> : null}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              {hold !== null ? (
                <Field label={t("payables.holdReason")} required>
                  <TextField value={hold} onChange={(e) => { setHold(e.target.value); }} data-testid="hold-reason" />
                </Field>
              ) : null}
              {apply ? (
                <div className="grid gap-3 sm:grid-cols-2" data-testid="apply-form">
                  <Field label={t("purchasing.appliedTo")} required>
                    <SelectField value={apply.settledItemId} onChange={(e) => { setApply({ ...apply, settledItemId: e.target.value }); }} data-testid="apply-target">
                      <option value="">—</option>
                      {(targets.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === item.currency).map((o) => (
                        <option key={o.id} value={o.id}>{o.documentNumber} · {money(o.remainingTc, o.currency)}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("purchasing.creditAmount")} required>
                    <TextField inputMode="decimal" value={apply.amount} onChange={(e) => { setApply({ ...apply, amount: e.target.value }); }} dir="ltr" data-testid="apply-amount" />
                  </Field>
                </div>
              ) : null}
              {writeOff ? (
                <div className="grid gap-3 sm:grid-cols-3" data-testid="write-off-form">
                  <Field label={t("sales.amount")} required>
                    <TextField inputMode="decimal" value={writeOff.amount} onChange={(e) => { setWriteOff({ ...writeOff, amount: e.target.value }); }} dir="ltr" data-testid="write-off-amount" />
                  </Field>
                  <Field label={t("sales.invoicing.kind")}>
                    <SelectField value={writeOff.kind} onChange={(e) => { setWriteOff({ ...writeOff, kind: e.target.value }); }} data-testid="write-off-kind">
                      <option value="bad_debt">{t("receivables.writeOff.kinds.bad_debt")}</option>
                      <option value="small_balance">{t("receivables.writeOff.kinds.small_balance")}</option>
                    </SelectField>
                  </Field>
                  <Field label={t("receivables.writeOff.reason")} required>
                    <TextField value={writeOff.reason} onChange={(e) => { setWriteOff({ ...writeOff, reason: e.target.value }); }} data-testid="write-off-reason" />
                  </Field>
                </div>
              ) : null}
              {promise ? (
                <div className="grid gap-3 sm:grid-cols-2" data-testid="promise-form">
                  <Field label={t("receivables.promise.date")} description={t("receivables.promise.hint")}>
                    <TextField type="date" value={promise.promisedOn} onChange={(e) => { setPromise({ ...promise, promisedOn: e.target.value }); }} dir="ltr" data-testid="promise-date" />
                  </Field>
                  <Field label={t("receivables.promise.note")}>
                    <TextField value={promise.note} onChange={(e) => { setPromise({ ...promise, note: e.target.value }); }} data-testid="promise-note" />
                  </Field>
                </div>
              ) : null}
              {netting ? (
                <div className="grid gap-3 sm:grid-cols-2" data-testid="netting-form">
                  <Field label={t("receivables.netting.payable")} required description={t("receivables.netting.hint")}>
                    <SelectField value={netting.payableItemId} onChange={(e) => { const chosen = (payables.data ?? []).find((o) => o.item.id === e.target.value); setNetting({ payableItemId: e.target.value, amount: chosen ? String(Math.min(Number(chosen.item.remainingTc), Number(item.remainingTc))) : netting.amount }); }} data-testid="netting-payable">
                      <option value="">—</option>
                      {(payables.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === item.currency && !o.paymentBlocked).map((o) => (
                        <option key={o.id} value={o.id}>{o.documentNumber} · {money(o.remainingTc, o.currency)}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("sales.amount")} required>
                    <TextField inputMode="decimal" value={netting.amount} onChange={(e) => { setNetting({ ...netting, amount: e.target.value }); }} dir="ltr" data-testid="netting-amount" />
                  </Field>
                </div>
              ) : null}
              <DialogFooter>
                {canManage && canHold && !item.onHold && hold === null ? <Button variant="secondary" onClick={() => { setHold(""); }} data-testid="hold-item">{t("payables.hold")}</Button> : null}
                {hold !== null ? <Button onClick={() => { act.mutate({ action: "hold", id: item.id, reason: hold }); }} loading={act.isPending} disabled={!hold.trim()} data-testid="confirm-hold">{t("payables.hold")}</Button> : null}
                {canManage && item.onHold ? <Button variant="secondary" onClick={() => { act.mutate({ action: "release", id: item.id }); }} loading={act.isPending} data-testid="release-item">{t("payables.release")}</Button> : null}
                {canSettle && canApply && !apply ? <Button onClick={() => { setApply({ settledItemId: "", amount: String(Math.abs(Number(item.remainingTc))) }); }} data-testid="apply-item">{t("purchasing.applyCredit")}</Button> : null}
                {apply ? <Button onClick={() => { act.mutate({ action: "apply", id: item.id, settledItemId: apply.settledItemId, amount: num(apply.amount) }); }} loading={act.isPending} disabled={!apply.settledItemId || num(apply.amount) <= 0} data-testid="confirm-apply">{t("purchasing.applyCredit")}</Button> : null}
                {canManage && canHold && Number(item.originalTc) > 0 && !promise ? <Button variant="secondary" onClick={() => { setPromise({ promisedOn: item.promisedPaymentDate ?? "", note: "" }); }} data-testid="promise-item">{t("receivables.promise.action")}</Button> : null}
                {promise ? <Button onClick={() => { act.mutate({ action: "promise", id: item.id, promisedOn: promise.promisedOn || null, note: promise.note }); }} loading={act.isPending} data-testid="confirm-promise">{promise.promisedOn ? t("receivables.promise.record") : t("receivables.promise.clear")}</Button> : null}
                {canSettle && canHold && Number(item.originalTc) > 0 && !item.onHold && !netting ? <Button variant="secondary" onClick={() => { setNetting({ payableItemId: "", amount: "" }); }} data-testid="net-item">{t("receivables.netting.action")}</Button> : null}
                {netting ? <Button onClick={() => { act.mutate({ action: "net", id: item.id, payableItemId: netting.payableItemId, amount: num(netting.amount) }); }} loading={act.isPending} disabled={!netting.payableItemId || num(netting.amount) <= 0} data-testid="confirm-netting">{t("receivables.netting.action")}</Button> : null}
                {canWriteOff && canHold && Number(item.originalTc) > 0 && !writeOff ? <Button variant="secondary" onClick={() => { setWriteOff({ amount: String(item.remainingTc), kind: "bad_debt", reason: "" }); }} data-testid="write-off-item">{t("receivables.writeOff.action")}</Button> : null}
                {writeOff ? <Button variant="danger" onClick={() => { act.mutate({ action: "write_off", id: item.id, amount: num(writeOff.amount), kind: writeOff.kind, reason: writeOff.reason }); }} loading={act.isPending} disabled={!writeOff.reason.trim() || num(writeOff.amount) <= 0} data-testid="confirm-write-off">{t("receivables.writeOff.confirm")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
