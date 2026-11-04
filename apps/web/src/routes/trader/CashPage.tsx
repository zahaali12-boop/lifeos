import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { useNewFromUrl } from "../../lib/documents";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { CashMovementDialog, type MovementFollowUp } from "./CashMovementDialog";
import { WhatsAppDialog, type WhatsAppSubject } from "./WhatsAppDialog";
import { rangeParams } from "./period";
import { PeriodFilter, usePeriod } from "./PeriodFilter";

type CashAccount = components["schemas"]["TraderCashReportAccount"];
type Movement = components["schemas"]["TraderCashMovement"];

interface Undoing {
  id: string;
  reference: string;
  reason: string;
  date: string;
}

/**
 * Cash and bank (trader edition, ADR-0035 §18, §24): each cash box and bank account in its own currency, what it holds
 * now, what it held when the period began, what came in and went out, what it held at the period's end, and every
 * movement newest first with the balance after it. "New movement" records money between the boxes (a deposit, a
 * withdrawal, dinars changed for dollars), from a customer, to a supplier, an expense or the owner's own money; a
 * transfer can be undone from its row.
 */
export function CashPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const [period, setPeriod, range] = usePeriod("month");
  const [selected, setSelected] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [followUp, setFollowUp] = useState<MovementFollowUp | null>(null);
  const [whatsapp, setWhatsapp] = useState<WhatsAppSubject | null>(null);
  const [undoing, setUndoing] = useState<Undoing | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const report = useQuery({
    queryKey: ["trader-cash", companyId, range.from ?? "", range.to ?? ""],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/trader/cash", { params: { query: { companyId, ...rangeParams(range) } } })),
  });
  const companyCurrency = report.data?.currency ?? company?.functionalCurrency ?? "";
  // Every amount of a box is in the box's own currency: a dollar box shows dollars.
  const money = (value: number | string, currency: string): string => (currency === "" ? "—" : formatMoney(value, currency));
  const accounts = report.data?.accounts ?? [];
  const current: CashAccount | undefined = accounts.find((a) => a.id === selected) ?? accounts[0];
  const what = (m: Movement): string => (m.sourceDocumentType === "bank_transfer"
    ? t(`trader.cash.transferKinds.${m.kind}`, { defaultValue: t("trader.cash.sources.bank_transfer") })
    : t(`trader.cash.sources.${m.sourceDocumentType ?? "other"}`, { defaultValue: t("trader.cash.sources.other") }));
  // A transfer whose reversal is in view is not offered for undoing again (the server refuses it either way).
  const undone = new Set((current?.movements ?? []).filter((m) => m.sourceDocumentType === "bank_transfer" && m.kind === "reversal").map((m) => m.sourceDocumentId));
  const canUndo = (m: Movement): boolean => m.sourceDocumentType === "bank_transfer" && m.kind !== "reversal" && Boolean(m.sourceDocumentId) && !undone.has(m.sourceDocumentId);

  useNewFromUrl("/trader/cash", () => { setAdding(true); }, Boolean(companyId));

  const undo = useMutation({
    mutationFn: async (u: Undoing) => unwrap(await api.POST("/api/v1/banking/transfers/{transferId}/reverse", { params: { path: { transferId: u.id } }, body: { reason: u.reason, reversalDate: u.date || null } })),
    onSuccess: async (transfer) => {
      setUndoing(null);
      setProblem(null);
      setNotice(t("trader.cash.undone", { number: transfer.number }));
      setFollowUp(null);
      await Promise.all([["trader-cash"], ["bank-accounts"], ["bank-transfers"], ["trader-health"], ["home"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
    },
    onError: (error: unknown) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submitUndo = (event: FormEvent): void => {
    event.preventDefault();
    if (undoing) {
      undo.mutate(undoing);
    }
  };

  return (
    <>
      <PageHeader
        title={t("trader.cash.title")}
        description={t("trader.cash.description")}
        actions={companyId ? <Button onClick={() => { setNotice(null); setFollowUp(null); setAdding(true); }} data-testid="cash-new-movement"><Plus aria-hidden="true" />{t("trader.cash.newMovement")}</Button> : null}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <PeriodFilter value={period} onChange={setPeriod} />
      </div>
      {notice ? (
        <div role="status" className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="cash-notice">
          <span>{notice}</span>
          {followUp ? <Button type="button" size="sm" variant="secondary" onClick={() => { setWhatsapp({ kind: "receipt", receiptId: followUp.receiptId }); }} data-testid="cash-receipt-whatsapp">{t("trader.whatsapp.sendReceipt")}</Button> : null}
        </div>
      ) : null}
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
            <span className="text-lg font-semibold tabular" dir="ltr" data-testid="cash-balance">{money(a.balanceNow, a.currency)}</span>
            <span className="text-xs text-fg-muted" dir="auto">{t("trader.cash.inOut", { in: money(a.in, a.currency), out: money(a.out, a.currency) })}</span>
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
                <div className="font-semibold tabular" dir="ltr" data-testid={`cash-${key}`}>{money(value, current.currency)}</div>
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
                  <TableHead><span className="sr-only">{t("trader.cash.actions")}</span></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {current.movements.map((m) => (
                  <TableRow key={m.id} data-testid="cash-movement">
                    <TableCell dir="ltr">{formatDate(m.date)}</TableCell>
                    <TableCell dir="auto">{what(m)}</TableCell>
                    <TableCell dir="auto">{m.reference ?? ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-move-in">{Number(m.in) > 0 ? money(m.in, current.currency) : ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-move-out">{Number(m.out) > 0 ? money(m.out, current.currency) : ""}</TableCell>
                    <TableCell className="tabular text-end" dir="ltr" data-testid="cash-balance-after">{money(m.balanceAfter, current.currency)}</TableCell>
                    <TableCell className="text-end">
                      {canUndo(m) && m.sourceDocumentId ? (
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setUndoing({ id: m.sourceDocumentId ?? "", reference: m.reference ?? "", reason: "", date: "" }); }} data-testid="cash-undo-transfer">{t("trader.cash.undo")}</Button>
                      ) : null}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </section>
      ) : null}

      {adding && companyId ? (
        <CashMovementDialog open onClose={() => { setAdding(false); }} companyId={companyId} companyCurrency={companyCurrency} accountId={current?.id} onDone={(text, follow) => { setNotice(text); setFollowUp(follow); }} />
      ) : null}

      {whatsapp && companyId ? <WhatsAppDialog subject={whatsapp} companyId={companyId} onClose={() => { setWhatsapp(null); }} /> : null}

      <Dialog open={undoing !== null} onOpenChange={(isOpen) => { if (!isOpen) { setUndoing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-md">
          {undoing ? (
            <form onSubmit={submitUndo} className="flex flex-col gap-4" data-testid="undo-transfer-dialog">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.cash.undoTitle")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("trader.cash.undoHint")}</p>
              <p className="text-sm"><bdi dir="ltr">{undoing.reference}</bdi></p>
              <FormError message={problem?.message ?? null} />
              <Field label={t("trader.cash.undoReason")} required>
                <TextField value={undoing.reason} onChange={(e) => { setUndoing({ ...undoing, reason: e.target.value }); }} dir="auto" required data-testid="undo-transfer-reason" />
              </Field>
              <Field label={t("trader.cash.movement.date")}>
                <TextField type="date" value={undoing.date} onChange={(e) => { setUndoing({ ...undoing, date: e.target.value }); }} dir="ltr" data-testid="undo-transfer-date" />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setUndoing(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" variant="danger" loading={undo.isPending} disabled={undoing.reason.trim() === ""} data-testid="save-undo-transfer">{t("trader.cash.undoConfirm")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
