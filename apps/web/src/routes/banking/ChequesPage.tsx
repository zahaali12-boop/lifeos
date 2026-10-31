import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Landmark } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatDateTime, formatMoney, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { num } from "../purchasing/shared";
import { RecordActivity } from "../RecordDiscussion";
import { StatusBar } from "../StatusBar";
import { ChequeStatus, chequeFlow, useCheques, type Cheque, type ChequeDeposit } from "./chequeShared";

type ActionKind = "clear" | "bounce" | "return" | "pay" | "cancel" | "replace";

interface ActionForm {
  kind: ActionKind;
  date: string;
  bankCharge: string;
  customerFee: string;
  reason: string;
  chequeNumber: string;
  drawerBank: string;
  dueDate: string;
}

interface DepositForm {
  bankAccountId: string;
  depositDate: string;
  chequeIds: string[];
}

/**
 * Cheques (roadmap 6.3): the received and issued registers with every step of a cheque's life taken from its own
 * page (deposit, clear, bounce, return, replace; pay, bounce, cancel), the deposit slips, and the maturity forecast.
 */
export function ChequesPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [tab, setTab] = useState("received");
  const [openId, setOpenId] = useOpenRecord("/banking/cheques");
  const [openDeposit, setOpenDeposit] = useState<string | null>(null);
  const [action, setAction] = useState<ActionForm | null>(null);
  const [deposit, setDeposit] = useState<DepositForm | null>(null);
  const [clearing, setClearing] = useState<{ date: string; bankCharge: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [forecastFrom, setForecastFrom] = useState(() => `${today().slice(0, 7)}-01`);
  const canManage = can("banking.cheque.manage");
  const direction = tab === "issued" ? "issued" : "received";
  const list = useCheques(companyId, direction);
  const inHand = useCheques(companyId, "received", "in_hand");
  const banks = useBankAccounts(companyId);
  const currency = companies.find((c) => c.id === companyId)?.functionalCurrency ?? "";
  const detail = useQuery({
    queryKey: ["cheque", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheques/{chequeId}", { params: { path: { chequeId: openId ?? "" } } })),
  });
  const deposits = useQuery({
    queryKey: ["cheque-deposits", companyId],
    enabled: Boolean(companyId) && tab === "deposits",
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheques/deposits", { params: { query: { companyId } } })),
  });
  const depositDetail = useQuery({
    queryKey: ["cheque-deposit", openDeposit],
    enabled: Boolean(openDeposit),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheques/deposits/{depositId}", { params: { path: { depositId: openDeposit ?? "" } } })),
  });
  const forecastTo = useMemo(() => { const d = new Date(`${forecastFrom}T00:00:00Z`); d.setUTCMonth(d.getUTCMonth() + 12); d.setUTCDate(0); return d.toISOString().slice(0, 10); }, [forecastFrom]);
  const forecast = useQuery({
    queryKey: ["cheque-forecast", companyId, forecastFrom],
    enabled: Boolean(companyId) && tab === "forecast" && /^\d{4}-\d{2}-\d{2}$/u.test(forecastFrom),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheques/forecast", { params: { query: { companyId, from: forecastFrom, to: forecastTo } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["cheques"], ["cheque"], ["cheque-deposits"], ["cheque-deposit"], ["cheque-forecast"], ["receipts"], ["receipt"], ["payments"], ["payment"], ["ar-open-items"], ["ar-open-item"], ["open-items"], ["bank-accounts"], ["bank-account"], ["activities"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const act = useMutation({
    mutationFn: async (input: { id: string; form: ActionForm; cheque: Cheque }) => {
      const params = { path: { chequeId: input.id } };
      const f = input.form;
      const date = f.date || null;
      switch (f.kind) {
        case "clear": return unwrap(await api.POST("/api/v1/banking/cheques/{chequeId}/clear", { params, body: { date, bankCharge: num(f.bankCharge), reason: f.reason || null } }));
        case "bounce": return unwrap(await api.POST("/api/v1/banking/cheques/{chequeId}/bounce", { params, body: { date, bankCharge: num(f.bankCharge), customerFee: num(f.customerFee), reason: f.reason || null } }));
        case "return": return unwrap(await api.POST("/api/v1/banking/cheques/{chequeId}/return", { params, body: { date, bankCharge: 0, reason: f.reason || null } }));
        case "pay": return unwrap(await api.POST("/api/v1/banking/cheques/{chequeId}/pay", { params, body: { date, bankCharge: num(f.bankCharge), reason: f.reason || null } }));
        case "cancel": return unwrap(await api.POST("/api/v1/banking/cheques/{chequeId}/cancel", { params, body: { date, bankCharge: 0, reason: f.reason || null } }));
        case "replace": {
          // The replacement is a new receipt by cheque for what the bounced one collected, linked to it, posted at once.
          const original = unwrap(await api.GET("/api/v1/banking/receipts/{receiptId}", { params: { path: { receiptId: input.cheque.paymentId } } }));
          const created = unwrap(await api.POST("/api/v1/banking/receipts", {
            body: {
              companyId: input.cheque.companyId,
              partnerId: input.cheque.partnerId,
              bankAccountId: input.cheque.bankAccountId,
              kind: original.kind,
              receiptDate: f.date || null,
              method: "cheque",
              reference: f.reason || null,
              currency: original.currency,
              lines: original.lines.map((l) => ({ openItemId: l.openItemId, amount: l.amountTc, discountTc: l.discountTc, whtTc: l.whtTc })),
              onAccount: original.onAccountTc,
              charges: 0,
              cheque: { chequeNumber: f.chequeNumber, drawerBank: f.drawerBank || null, dueDate: f.dueDate || null, replacesChequeId: input.cheque.id },
            },
          }));
          await api.POST("/api/v1/banking/receipts/{receiptId}/post", { params: { path: { receiptId: created.id } } }).then((r) => unwrap(r));
          return unwrap(await api.GET("/api/v1/banking/cheques/{chequeId}", { params: { path: { chequeId: input.id } } }));
        }
      }
    },
    onSuccess: async () => { setProblem(null); setAction(null); await refresh(); },
    onError: fail,
  });
  const makeDeposit = useMutation({
    mutationFn: async (f: DepositForm) => unwrap(await api.POST("/api/v1/banking/cheques/deposits", { body: { companyId, bankAccountId: f.bankAccountId, chequeIds: f.chequeIds, depositDate: f.depositDate || null } })),
    onSuccess: async (made) => { setProblem(null); setDeposit(null); setOpenId(null); await refresh(); setTab("deposits"); setOpenDeposit(made.id); },
    onError: fail,
  });
  const clearDeposit = useMutation({
    mutationFn: async (input: { id: string; date: string; bankCharge: string }) => unwrap(await api.POST("/api/v1/banking/cheques/deposits/{depositId}/clear", { params: { path: { depositId: input.id } }, body: { date: input.date || null, bankCharge: num(input.bankCharge), reason: null } })),
    onSuccess: async () => { setProblem(null); setClearing(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Cheque, unknown>[]>(
    () => [
      { id: "number", accessorKey: "chequeNumber", header: t("banking.cheques.number"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.chequeNumber}</span> },
      { id: "status", accessorFn: (r) => t(`banking.cheques.statuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <ChequeStatus status={row.original.status} /> },
      { id: "partner", accessorFn: (r) => `${r.partnerCode} · ${localized(r.partnerName)}`, header: direction === "received" ? t("sales.customer") : t("purchasing.supplier"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "amount", accessorKey: "amount", header: t("purchasing.amount"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.amount, row.original.currency)}</span> },
      { id: "dueDate", accessorKey: "dueDate", header: t("banking.cheques.dueDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.dueDate)}</span> },
      { id: "issueDate", accessorKey: "issueDate", header: direction === "received" ? t("banking.receiptDate") : t("banking.paymentDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.issueDate)}</span> },
      { id: "postDated", accessorFn: (r) => (r.isPostDated ? t("common.yes") : t("common.no")), header: t("banking.cheques.postDated"), size: 90 },
      { id: "drawerBank", accessorKey: "drawerBank", header: t("banking.cheques.drawerBank"), size: 140, cell: ({ row }) => <span dir="auto">{row.original.drawerBank ?? "—"}</span> },
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 100, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "document", accessorKey: "paymentNumber", header: direction === "received" ? t("banking.cheques.receiptDocument") : t("banking.cheques.paymentDocument"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.paymentNumber}</span> },
      { id: "deposit", accessorKey: "depositNumber", header: t("banking.cheques.depositNumber"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.depositNumber ?? "—"}</span> },
    ],
    [t, direction],
  );
  const depositColumns = useMemo<ColumnDef<ChequeDeposit, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorFn: (r) => t(`banking.cheques.depositStatuses.${r.status}`), header: t("common.status"), size: 130, cell: ({ row }) => <ChequeStatus status={row.original.status} /> },
      { id: "date", accessorKey: "depositDate", header: t("banking.cheques.fields.date"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.depositDate)}</span> },
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 100, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "count", accessorKey: "chequeCount", header: t("banking.cheques.count"), size: 80, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.chequeCount)}</span> },
      { id: "total", accessorKey: "total", header: t("purchasing.amount"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.total, row.original.currency)}</span> },
    ],
    [t],
  );

  const c = detail.data;
  const d = depositDetail.data;
  const startAction = (kind: ActionKind): void => { setProblem(null); setAction({ kind, date: today(), bankCharge: "", customerFee: "", reason: "", chequeNumber: "", drawerBank: c?.drawerBank ?? "", dueDate: today() }); };
  const startDeposit = (ids: string[]): void => { setProblem(null); setDeposit({ bankAccountId: c?.bankAccountId ?? (banks.data ?? []).find((b) => b.kind === "bank")?.id ?? "", depositDate: today(), chequeIds: ids }); };
  const submitAction = (event: FormEvent): void => { event.preventDefault(); if (c && action) { act.mutate({ id: c.id, form: action, cheque: c }); } };
  const submitDeposit = (event: FormEvent): void => { event.preventDefault(); if (deposit) { makeDeposit.mutate(deposit); } };
  const closeDetail = (): void => { setOpenId(null); setAction(null); setProblem(null); };
  const needsCharge = action?.kind === "clear" || action?.kind === "bounce" || action?.kind === "pay";

  return (
    <>
      <PageHeader
        title={t("nav.cheques")}
        description={t("banking.cheques.description")}
        actions={canManage && tab === "received" ? (
          <Button onClick={() => { startDeposit([]); }} disabled={!companyId || (inHand.data ?? []).length === 0} data-testid="new-deposit">
            <Landmark aria-hidden="true" />
            {t("banking.cheques.actions.deposit")}
          </Button>
        ) : null}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        {tab === "forecast" ? (
          <Field label={t("banking.cheques.forecastFrom")}>
            <TextField type="date" value={forecastFrom} onChange={(e) => { setForecastFrom(e.target.value); }} dir="ltr" data-testid="forecast-from" />
          </Field>
        ) : null}
      </div>
      <Tabs tabs={[{ id: "received", label: t("banking.cheques.received"), testId: "tab-received" }, { id: "issued", label: t("banking.cheques.issued"), testId: "tab-issued" }, { id: "deposits", label: t("banking.cheques.deposits"), testId: "tab-deposits" }, { id: "forecast", label: t("banking.cheques.forecast"), testId: "tab-forecast" }]} value={tab} onChange={setTab} />
      <FormError message={!openId && !deposit ? problem?.message ?? null : null} />
      {tab === "received" || tab === "issued" ? (
        <DataGrid<Cheque> label="nav.cheques" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("banking.cheques.emptyTitle")} emptyDescription={t("banking.cheques.emptyDescription")} onOpen={(row) => { setProblem(null); setAction(null); setOpenId(row.id); }} exportable={false} />
      ) : tab === "deposits" ? (
        <DataGrid<ChequeDeposit> label="banking.cheques.deposits" columns={depositColumns} data={deposits.data ?? []} rowKey={(row) => row.id} loading={deposits.isPending && Boolean(companyId)} emptyTitle={t("banking.cheques.emptyDeposits")} emptyDescription={t("banking.cheques.depositHelp")} onOpen={(row) => { setProblem(null); setClearing(null); setOpenDeposit(row.id); }} exportable={false} />
      ) : (
        <div className="mt-3" data-testid="cheque-forecast">
          {(forecast.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("banking.cheques.emptyForecast")}</p> : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("banking.cheques.month")}</TableHead>
                  <TableHead>{t("banking.cheques.receivedDue")}</TableHead>
                  <TableHead>{t("banking.cheques.issuedDue")}</TableHead>
                  <TableHead>{t("banking.cheques.net")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(forecast.data ?? []).map((r) => (
                  <TableRow key={r.periodStart} data-testid="forecast-row">
                    <TableCell dir="ltr">{formatDate(r.periodStart)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.receivedFc, currency)} ({String(r.receivedCount)})</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.issuedFc, currency)} ({String(r.issuedCount)})</TableCell>
                    <TableCell className="tabular" dir="ltr">{formatMoney(r.netFc, currency)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      )}

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { closeDetail(); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {c ? (
            <div className="flex flex-col gap-4" data-testid="cheque-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{t("banking.cheques.chequeTitle", { number: c.chequeNumber })}</span>
                  <ChequeStatus status={c.status} />
                </DialogTitle>
              </DialogHeader>
              <StatusBar {...chequeFlow(c, t)} actions={canManage ? (
                <>
                  {c.direction === "received" && c.status === "in_hand" ? <Button onClick={() => { startDeposit([c.id]); }} data-testid="deposit-cheque">{t("banking.cheques.actions.deposit")}</Button> : null}
                  {c.direction === "received" && c.status === "in_hand" ? <Button variant="secondary" onClick={() => { startAction("return"); }} data-testid="return-cheque">{t("banking.cheques.actions.return")}</Button> : null}
                  {c.direction === "received" && c.status === "deposited" ? <Button onClick={() => { startAction("clear"); }} data-testid="clear-cheque">{t("banking.cheques.actions.clear")}</Button> : null}
                  {c.direction === "received" && (c.status === "deposited" || c.status === "cleared") ? <Button variant="secondary" onClick={() => { startAction("bounce"); }} data-testid="bounce-cheque">{t("banking.cheques.actions.bounce")}</Button> : null}
                  {c.direction === "received" && (c.status === "bounced" || c.status === "returned") && !c.replacedById ? <Button onClick={() => { startAction("replace"); }} data-testid="replace-cheque">{t("banking.cheques.actions.replace")}</Button> : null}
                  {c.direction === "issued" && (c.status === "issued" || c.status === "bounced") ? <Button onClick={() => { startAction("pay"); }} data-testid="pay-cheque">{t("banking.cheques.actions.pay")}</Button> : null}
                  {c.direction === "issued" && (c.status === "issued" || c.status === "bounced") ? <Button variant="secondary" onClick={() => { startAction("cancel"); }} data-testid="cancel-cheque">{t("banking.cheques.actions.cancel")}</Button> : null}
                  {c.direction === "issued" && c.status === "paid" ? <Button variant="secondary" onClick={() => { startAction("bounce"); }} data-testid="bounce-cheque">{t("banking.cheques.actions.bounce")}</Button> : null}
                </>
              ) : null} />
              <FormError message={problem?.message ?? null} />
              {action ? (
                <form onSubmit={submitAction} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="cheque-action">
                  <h3 className="text-sm font-semibold">{t(`banking.cheques.actions.${action.kind}`)}</h3>
                  <div className="grid gap-3 sm:grid-cols-3">
                    <Field label={action.kind === "replace" ? t("banking.cheques.fields.receiptDate") : t("banking.cheques.fields.date")} required>
                      <TextField type="date" value={action.date} onChange={(e) => { setAction({ ...action, date: e.target.value }); }} dir="ltr" required data-testid="action-date" />
                    </Field>
                    {needsCharge ? (
                      <Field label={t("banking.cheques.fields.bankCharge")}>
                        <TextField inputMode="decimal" value={action.bankCharge} onChange={(e) => { setAction({ ...action, bankCharge: e.target.value }); }} dir="ltr" data-testid="action-bank-charge" />
                      </Field>
                    ) : null}
                    {action.kind === "bounce" && c.direction === "received" ? (
                      <Field label={t("banking.cheques.fields.customerFee")} description={t("banking.cheques.fields.customerFeeHelp")}>
                        <TextField inputMode="decimal" value={action.customerFee} onChange={(e) => { setAction({ ...action, customerFee: e.target.value }); }} dir="ltr" data-testid="action-customer-fee" />
                      </Field>
                    ) : null}
                    {action.kind === "replace" ? (
                      <>
                        <Field label={t("banking.cheques.fields.newChequeNumber")} required>
                          <TextField value={action.chequeNumber} onChange={(e) => { setAction({ ...action, chequeNumber: e.target.value }); }} dir="ltr" required data-testid="action-cheque-number" />
                        </Field>
                        <Field label={t("banking.cheques.drawerBank")}>
                          <TextField value={action.drawerBank} onChange={(e) => { setAction({ ...action, drawerBank: e.target.value }); }} dir="auto" data-testid="action-drawer-bank" />
                        </Field>
                        <Field label={t("banking.cheques.dueDate")} required>
                          <TextField type="date" value={action.dueDate} onChange={(e) => { setAction({ ...action, dueDate: e.target.value }); }} dir="ltr" required data-testid="action-due-date" />
                        </Field>
                      </>
                    ) : null}
                    <Field label={t("banking.cheques.fields.reason")} required={action.kind === "bounce" || action.kind === "return" || action.kind === "cancel"}>
                      <TextField value={action.reason} onChange={(e) => { setAction({ ...action, reason: e.target.value }); }} dir="auto" data-testid="action-reason" />
                    </Field>
                  </div>
                  <div className="flex gap-2">
                    <Button type="submit" loading={act.isPending} data-testid="confirm-action">{t("banking.cheques.actions.confirm")}</Button>
                    <Button type="button" variant="secondary" onClick={() => { setAction(null); }}>{t("common.cancel")}</Button>
                  </div>
                </form>
              ) : null}
              <KeyValues entries={[
                [c.direction === "received" ? t("sales.customer") : t("purchasing.supplier"), `${c.partnerCode} · ${localized(c.partnerName)}`],
                [t("purchasing.amount"), <span key="amount" className="tabular" dir="ltr" data-testid="cheque-amount">{formatMoney(c.amount, c.currency)}</span>],
                [t("banking.cheques.dueDate"), `${formatDate(c.dueDate)}${c.isPostDated ? ` · ${t("banking.cheques.postDated")}` : ""}`],
                [c.direction === "received" ? t("banking.receiptDate") : t("banking.paymentDate"), formatDate(c.issueDate)],
                [t("banking.cheques.drawerBank"), c.drawerBank ?? "—"],
                [t("nav.bankAccounts"), c.bankAccountCode],
                [c.direction === "received" ? t("banking.cheques.receiptDocument") : t("banking.cheques.paymentDocument"), <span key="doc" dir="ltr">{c.paymentNumber}</span>],
                ...(c.depositNumber ? [[t("banking.cheques.depositNumber"), <span key="dep" dir="ltr">{c.depositNumber}</span>] as [string, React.ReactNode]] : []),
                ...(c.replacedById ? [[t("banking.cheques.replacedBy"), <Button key="rb" variant="ghost" size="sm" onClick={() => { setOpenId(c.replacedById ?? null); }} data-testid="open-replacement">{t("banking.cheques.openCheque")}</Button>] as [string, React.ReactNode]] : []),
                ...(c.replacesId ? [[t("banking.cheques.replaces"), <Button key="rp" variant="ghost" size="sm" onClick={() => { setOpenId(c.replacesId ?? null); }}>{t("banking.cheques.openCheque")}</Button>] as [string, React.ReactNode]] : []),
                ...(c.notes ? [[t("fixedAssets.notes"), c.notes] as [string, string]] : []),
              ]} />
              <section className="flex flex-col gap-2" data-testid="cheque-events">
                <h3 className="text-sm font-semibold">{t("banking.cheques.events")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("banking.cheques.fields.date")}</TableHead>
                      <TableHead>{t("banking.cheques.event")}</TableHead>
                      <TableHead>{t("banking.cheques.fields.bankCharge")}</TableHead>
                      <TableHead>{t("banking.cheques.fields.customerFee")}</TableHead>
                      <TableHead>{t("banking.cheques.fields.reason")}</TableHead>
                      <TableHead>{t("banking.cheques.recordedAt")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {c.events.map((e) => (
                      <TableRow key={e.id} data-testid="cheque-event-row">
                        <TableCell dir="ltr">{formatDate(e.effectiveDate)}</TableCell>
                        <TableCell>{t(`banking.cheques.eventKinds.${e.event}`, { defaultValue: e.event })}</TableCell>
                        <TableCell className="tabular" dir="ltr">{Number(e.bankCharge) ? formatMoney(e.bankCharge, c.currency) : "—"}</TableCell>
                        <TableCell className="tabular" dir="ltr">{Number(e.customerFee) ? formatMoney(e.customerFee, c.currency) : "—"}</TableCell>
                        <TableCell dir="auto">{e.reason ?? "—"}</TableCell>
                        <TableCell dir="ltr">{formatDateTime(e.occurredAt)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
              <RecordActivity entityType="bank_cheque" entityId={c.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(deposit)} onOpenChange={(isOpen) => { if (!isOpen) { setDeposit(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {deposit ? (
            <form onSubmit={submitDeposit} className="flex flex-col gap-4" data-testid="deposit-form">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("banking.cheques.depositTitle")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <p className="text-sm text-fg-muted">{t("banking.cheques.depositHelp")}</p>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("nav.bankAccounts")} required>
                  <SelectField value={deposit.bankAccountId} onChange={(e) => { setDeposit({ ...deposit, bankAccountId: e.target.value }); }} required data-testid="deposit-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).filter((b) => b.kind === "bank").map((b) => <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("banking.cheques.fields.date")} required>
                  <TextField type="date" value={deposit.depositDate} onChange={(e) => { setDeposit({ ...deposit, depositDate: e.target.value }); }} dir="ltr" required data-testid="deposit-date" />
                </Field>
              </div>
              {(inHand.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("banking.cheques.nothingInHand")}</p> : (
                <ul className="flex flex-col gap-1 text-sm" data-testid="deposit-cheques">
                  {(inHand.data ?? []).map((x) => (
                    <li key={x.id}>
                      <label className="flex flex-wrap items-center gap-3">
                        <input type="checkbox" checked={deposit.chequeIds.includes(x.id)} onChange={(e) => { setDeposit({ ...deposit, chequeIds: e.target.checked ? [...deposit.chequeIds, x.id] : deposit.chequeIds.filter((id) => id !== x.id) }); }} data-testid={`deposit-cheque-${x.chequeNumber}`} />
                        <span dir="ltr">{x.chequeNumber}</span>
                        <span dir="auto">{x.partnerCode} · {localized(x.partnerName)}</span>
                        <span className="tabular" dir="ltr">{formatMoney(x.amount, x.currency)}</span>
                        <span dir="ltr">{formatDate(x.dueDate)}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setDeposit(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={makeDeposit.isPending} disabled={deposit.chequeIds.length === 0 || !deposit.bankAccountId} data-testid="confirm-deposit">{t("banking.cheques.actions.deposit")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openDeposit)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenDeposit(null); setClearing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {d ? (
            <div className="flex flex-col gap-4" data-testid="deposit-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{d.number}</span>
                  <ChequeStatus status={d.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("nav.bankAccounts"), d.bankAccountCode],
                [t("banking.cheques.fields.date"), formatDate(d.depositDate)],
                [t("purchasing.amount"), <span key="total" className="tabular" dir="ltr">{formatMoney(d.total, d.currency)}</span>],
                [t("banking.cheques.count"), String(d.chequeCount)],
              ]} />
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("banking.cheques.number")}</TableHead>
                    <TableHead>{t("sales.customer")}</TableHead>
                    <TableHead>{t("purchasing.amount")}</TableHead>
                    <TableHead>{t("common.status")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {d.cheques.map((x) => (
                    <TableRow key={x.id} data-testid="deposit-cheque-row">
                      <TableCell dir="ltr">{x.chequeNumber}</TableCell>
                      <TableCell dir="auto">{x.partnerCode} · {localized(x.partnerName)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(x.amount, x.currency)}</TableCell>
                      <TableCell><ChequeStatus status={x.status} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {clearing ? (
                <div className="flex flex-wrap items-end gap-3">
                  <Field label={t("banking.cheques.fields.date")} required>
                    <TextField type="date" value={clearing.date} onChange={(e) => { setClearing({ ...clearing, date: e.target.value }); }} dir="ltr" data-testid="clear-date" />
                  </Field>
                  <Field label={t("banking.cheques.fields.bankCharge")}>
                    <TextField inputMode="decimal" value={clearing.bankCharge} onChange={(e) => { setClearing({ ...clearing, bankCharge: e.target.value }); }} dir="ltr" data-testid="clear-bank-charge" />
                  </Field>
                  <Button onClick={() => { clearDeposit.mutate({ id: d.id, ...clearing }); }} loading={clearDeposit.isPending} data-testid="confirm-clear-deposit">{t("banking.cheques.actions.confirm")}</Button>
                  <Button variant="secondary" onClick={() => { setClearing(null); }}>{t("common.cancel")}</Button>
                </div>
              ) : null}
              <DialogFooter>
                {canManage && d.cheques.some((x) => x.status === "deposited") && !clearing ? <Button onClick={() => { setProblem(null); setClearing({ date: today(), bankCharge: "" }); }} data-testid="clear-deposit">{t("banking.cheques.clearAll")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
