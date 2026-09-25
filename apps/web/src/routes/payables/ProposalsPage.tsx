import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { ItemStatus, useBankAccounts } from "./shared";
import { RecordActivity } from "../RecordDiscussion";

type Proposal = components["schemas"]["ProposalSummary"];

interface ProposalForm {
  payThrough: string;
  currency: string;
  partnerId: string;
  bankAccountId: string;
  takeDiscounts: boolean;
}

/** Payment proposals (roadmap 4.7): what is due by a date in one currency, edited line by line, approved, then drafted into payments per supplier. */
export function ProposalsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ProposalForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/payables/proposals");
  const [edits, setEdits] = useState<Record<string, { selected: boolean; amount: string }>>({});
  const [payFrom, setPayFrom] = useState<string | null>(null);
  const suppliers = useSuppliers(companyId);
  const banks = useBankAccounts(companyId);
  const list = useQuery({
    queryKey: ["proposals", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/proposals", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["proposal", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/proposals/{proposalId}", { params: { path: { proposalId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["proposals"], ["proposal"], ["payments"], ["open-items"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const create = useMutation({
    mutationFn: async (f: ProposalForm) => unwrap(await api.POST("/api/v1/payables/proposals", { body: { companyId, payThrough: f.payThrough, currency: f.currency, partnerId: f.partnerId || null, bankAccountId: f.bankAccountId || null, takeDiscounts: f.takeDiscounts } })),
    onSuccess: async (saved) => { setProblem(null); setForm(null); setEdits({}); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { action: "lines" | "approve" | "cancel" | "delete" | "pay"; id: string; bankAccountId?: string }) => {
      const params = { path: { proposalId: input.id } };
      switch (input.action) {
        case "lines": return unwrap(await api.PUT("/api/v1/payables/proposals/{proposalId}/lines", { params, body: { lines: Object.entries(edits).map(([lineId, e]) => ({ lineId, selected: e.selected, amountTc: e.amount ? num(e.amount) : null })) } }));
        case "approve": return unwrap(await api.POST("/api/v1/payables/proposals/{proposalId}/approve", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/payables/proposals/{proposalId}/cancel", { params }));
        case "delete": { unwrap(await api.DELETE("/api/v1/payables/proposals/{proposalId}", { params })); return null; }
        case "pay": { unwrap(await api.POST("/api/v1/banking/payments/from-proposal", { body: { proposalId: input.id, bankAccountId: input.bankAccountId ?? "", method: "transfer" } })); return undefined; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setEdits({}); setPayFrom(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Proposal, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <ItemStatus status={row.original.status} /> },
      { id: "payThrough", accessorKey: "payThrough", header: t("payables.payThrough"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.payThrough)}</span> },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
      { id: "lines", accessorFn: (r) => r.lines.length, header: t("purchasing.lines"), size: 80, cell: ({ row }) => String(row.original.lines.length) },
      { id: "total", accessorKey: "totalTc", header: t("payables.toPay"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalTc, row.original.currency)}</span> },
      { id: "discount", accessorKey: "discountTc", header: t("payables.discounts"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.discountTc, row.original.currency)}</span> },
    ],
    [t],
  );

  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const p = detail.data;
  const edited = (lineId: string, fallback: { selected: boolean; amount: number }) => edits[lineId] ?? { selected: fallback.selected, amount: String(fallback.amount) };

  return (
    <>
      <PageHeader
        title={t("nav.paymentProposals")}
        description={t("payables.proposalsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm({ payThrough: today(), currency: companies.find((c) => c.id === companyId)?.functionalCurrency ?? "", partnerId: "", bankAccountId: "", takeDiscounts: true }); }} disabled={!companyId} data-testid="new-proposal">
            <Plus aria-hidden="true" />
            {t("payables.newProposal")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "approved", "executed", "cancelled"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Proposal> label="nav.paymentProposals" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("payables.emptyProposals")} emptyDescription={t("payables.emptyProposalsDescription")} onOpen={(row) => { setProblem(null); setEdits({}); setPayFrom(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("payables.newProposal")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("payables.payThrough")} required>
                  <TextField type="date" value={form.payThrough} onChange={(e) => { setForm({ ...form, payThrough: e.target.value }); }} dir="ltr" required data-testid="proposal-pay-through" />
                </Field>
                <Field label={t("partners.currency")} required>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="proposal-currency" />
                </Field>
                <Field label={t("partners.supplier")}>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} data-testid="proposal-supplier">
                    <option value="">{t("common.all")}</option>
                    {(suppliers.data ?? []).map((sup) => (
                      <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("nav.bankAccounts")}>
                  <SelectField value={form.bankAccountId} onChange={(e) => { setForm({ ...form, bankAccountId: e.target.value }); }} data-testid="proposal-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).map((b) => (
                      <option key={b.id} value={b.id}>{b.code} · {localized(b.name)}</option>
                    ))}
                  </SelectField>
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.takeDiscounts} onChange={(e) => { setForm({ ...form, takeDiscounts: e.target.checked }); }} data-testid="proposal-discounts" />
                  {t("payables.takeDiscounts")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-proposal">{t("payables.propose")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {p ? (
            <div className="flex flex-col gap-4" data-testid="proposal-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{p.number}</span>
                  <ItemStatus status={p.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("payables.payThrough"), formatDate(p.payThrough)],
                [t("partners.currency"), p.currency],
                [t("payables.toPay"), <span key="total" data-testid="proposal-total">{formatMoney(p.totalTc, p.currency)}</span>],
                [t("payables.discounts"), formatMoney(p.discountTc, p.currency)],
              ]} />
              <Table data-testid="proposal-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("payables.pay")}</TableHead>
                    <TableHead>{t("partners.supplier")}</TableHead>
                    <TableHead>{t("purchasing.number")}</TableHead>
                    <TableHead>{t("purchasing.dueDate")}</TableHead>
                    <TableHead>{t("purchasing.remaining")}</TableHead>
                    <TableHead>{t("purchasing.amount")}</TableHead>
                    <TableHead>{t("payables.discount")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {p.lines.map((l, index) => {
                    const e = edited(l.id, { selected: l.selected, amount: Number(l.amountTc) });
                    return (
                      <TableRow key={l.id} data-testid="proposal-line">
                        <TableCell>{p.status === "draft" ? <input type="checkbox" aria-label={t("payables.pay")} checked={e.selected} onChange={(ev) => { setEdits({ ...edits, [l.id]: { ...e, selected: ev.target.checked } }); }} data-testid={`line-selected-${String(index)}`} /> : (l.selected ? "✓" : "")}</TableCell>
                        <TableCell dir="auto">{l.partnerCode} · {localized(l.partnerName)}</TableCell>
                        <TableCell dir="ltr">{l.documentNumber}</TableCell>
                        <TableCell dir="ltr">{formatDate(l.dueDate)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.remainingTc, p.currency)}</TableCell>
                        <TableCell>{p.status === "draft" ? <TextField aria-label={t("purchasing.amount")} inputMode="decimal" value={e.amount} onChange={(ev) => { setEdits({ ...edits, [l.id]: { ...e, amount: ev.target.value } }); }} dir="ltr" className="w-32" data-testid={`line-amount-${String(index)}`} /> : <span className="tabular" dir="ltr">{formatMoney(l.amountTc, p.currency)}</span>}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.discountTc, p.currency)}</TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              {payFrom !== null ? (
                <Field label={t("nav.bankAccounts")} required>
                  <SelectField value={payFrom} onChange={(e) => { setPayFrom(e.target.value); }} data-testid="pay-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).map((b) => (
                      <option key={b.id} value={b.id}>{b.code} · {localized(b.name)} · {b.currency}</option>
                    ))}
                  </SelectField>
                </Field>
              ) : null}
              <RecordActivity entityType="payment_proposal" entityId={p.id} />
              <DialogFooter>
                {p.status === "draft" && Object.keys(edits).length > 0 ? <Button variant="secondary" onClick={() => { act.mutate({ action: "lines", id: p.id }); }} loading={act.isPending} data-testid="save-lines">{t("common.save")}</Button> : null}
                {p.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ action: "delete", id: p.id }); }} loading={act.isPending} data-testid="delete-proposal">{t("purchasing.deleteDraft")}</Button> : null}
                {p.status === "draft" ? <Button onClick={() => { act.mutate({ action: "approve", id: p.id }); }} loading={act.isPending} data-testid="approve-proposal">{t("payables.approve")}</Button> : null}
                {p.status === "draft" || p.status === "approved" ? <Button variant="secondary" onClick={() => { act.mutate({ action: "cancel", id: p.id }); }} loading={act.isPending} data-testid="cancel-proposal">{t("common.cancel")}</Button> : null}
                {p.status === "approved" && payFrom === null ? <Button onClick={() => { setPayFrom(p.bankAccountId ?? ""); }} data-testid="pay-proposal">{t("payables.payProposal")}</Button> : null}
                {payFrom !== null ? <Button onClick={() => { act.mutate({ action: "pay", id: p.id, bankAccountId: payFrom }); }} loading={act.isPending} disabled={!payFrom} data-testid="confirm-pay-proposal">{t("payables.draftPayments")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
