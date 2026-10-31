import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { num } from "../purchasing/shared";
import { ItemStatus } from "../payables/shared";
import { RecordActivity } from "../RecordDiscussion";
import { useFunds, type Fund } from "./shared";

interface FundForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  bankAccountId: string;
  floatAmount: string;
  isActive: boolean;
}

interface ReplenishForm {
  fromBankAccountId: string;
  amount: string;
  replenishedOn: string;
  reference: string;
}

/** Petty cash funds (roadmap 6.4): a cash box with its float; vouchers spend it, a replenishment from the bank brings it back to the float, every movement in the bank subledger. */
export function PettyCashFundsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<FundForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/expenses/funds");
  const [replenish, setReplenish] = useState<ReplenishForm | null>(null);
  const [reversal, setReversal] = useState<{ id: string; reason: string } | null>(null);
  const list = useFunds(companyId);
  const banks = useBankAccounts(companyId);
  const detail = useQuery({
    queryKey: ["expense-fund", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/funds/{fundId}", { params: { path: { fundId: openId ?? "" } } })),
  });
  const replenishments = useQuery({
    queryKey: ["fund-replenishments", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/funds/{fundId}/replenishments", { params: { path: { fundId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["expense-funds"], ["expense-fund"], ["fund-replenishments"], ["bank-accounts"], ["bank-account"], ["bank-transactions"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async (f: FundForm) => {
      const body = { companyId, code: f.code, name: { en: f.nameEn, ar: f.nameAr }, bankAccountId: f.bankAccountId, custodianMembershipId: null, floatAmount: num(f.floatAmount), isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/expenses/funds/{fundId}", { params: { path: { fundId: f.id } }, body })) : unwrap(await api.POST("/api/v1/expenses/funds", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const top = useMutation({
    mutationFn: async (input: { fundId: string; form: ReplenishForm }) =>
      unwrap(await api.POST("/api/v1/expenses/funds/{fundId}/replenish", { params: { path: { fundId: input.fundId } }, body: { fromBankAccountId: input.form.fromBankAccountId, amount: num(input.form.amount), replenishedOn: input.form.replenishedOn || null, reference: input.form.reference || null } })),
    onSuccess: async () => { setProblem(null); setReplenish(null); await refresh(); },
    onError: fail,
  });
  const undo = useMutation({
    mutationFn: async (input: { id: string; reason: string }) =>
      unwrap(await api.POST("/api/v1/expenses/replenishments/{replenishmentId}/reverse", { params: { path: { replenishmentId: input.id } }, body: { reason: input.reason, reversalDate: null } })),
    onSuccess: async () => { setProblem(null); setReversal(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Fund, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "box", accessorKey: "bankAccountCode", header: t("expenses.cashBox"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
      { id: "float", accessorKey: "floatAmount", header: t("expenses.floatAmount"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.floatAmount, row.original.currency)}</span> },
      { id: "balance", accessorKey: "balance", header: t("banking.balance"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.balance, row.original.currency)}</span> },
      { id: "toReplenish", accessorKey: "toReplenish", header: t("expenses.toReplenish"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.toReplenish, row.original.currency)}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const boxes = (banks.data ?? []).filter((b) => b.kind === "cash" || b.kind === "petty_cash");
  const sources = (banks.data ?? []).filter((b) => b.kind === "bank");
  const blank = (): FundForm => ({ id: null, code: "", nameEn: "", nameAr: "", bankAccountId: "", floatAmount: "", isActive: true });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const f = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.pettyCashFunds")}
        description={t("expenses.fundsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-fund">
            <Plus aria-hidden="true" />
            {t("expenses.newFund")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Fund> label="nav.pettyCashFunds" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("expenses.emptyFunds")} emptyDescription={t("expenses.emptyFundsDescription")} onOpen={(row) => { setProblem(null); setReplenish(null); setReversal(null); setOpenId(row.id); }} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("expenses.editFund") : t("expenses.newFund")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.code")} required>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="fund-code" />
                </Field>
                <Field label={t("expenses.cashBox")} description={t("expenses.cashBoxHelp")} required>
                  <SelectField value={form.bankAccountId} onChange={(e) => { setForm({ ...form, bankAccountId: e.target.value }); }} required disabled={Boolean(form.id)} data-testid="fund-box">
                    <option value="">—</option>
                    {boxes.map((b) => (
                      <option key={b.id} value={b.id}>{b.code} · {localized(b.name)} · {b.currency}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="fund-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="fund-name-ar" />
                </Field>
                <Field label={t("expenses.floatAmount")} description={t("expenses.floatHelp")} required>
                  <TextField inputMode="decimal" value={form.floatAmount} onChange={(e) => { setForm({ ...form, floatAmount: e.target.value }); }} dir="ltr" required data-testid="fund-float" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="fund-active" />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-fund">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReplenish(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {f ? (
            <div className="flex flex-col gap-4" data-testid="fund-detail">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold"><span dir="ltr">{f.code}</span> · <span dir="auto">{localized(f.name)}</span></DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("expenses.cashBox"), f.bankAccountCode],
                [t("expenses.floatAmount"), formatMoney(f.floatAmount, f.currency)],
                [t("banking.balance"), <span key="balance" data-testid="fund-balance">{formatMoney(f.balance, f.currency)}</span>],
                [t("expenses.toReplenish"), <span key="top" data-testid="fund-to-replenish">{formatMoney(f.toReplenish, f.currency)}</span>],
              ]} />
              {replenish ? (
                <form onSubmit={(event) => { event.preventDefault(); top.mutate({ fundId: f.id, form: replenish }); }} className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-2" data-testid="replenish-form">
                  <Field label={t("expenses.fromBankAccount")} required>
                    <SelectField value={replenish.fromBankAccountId} onChange={(e) => { setReplenish({ ...replenish, fromBankAccountId: e.target.value }); }} required data-testid="replenish-from">
                      <option value="">—</option>
                      {sources.map((b) => (
                        <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("purchasing.amount")} required>
                    <TextField inputMode="decimal" value={replenish.amount} onChange={(e) => { setReplenish({ ...replenish, amount: e.target.value }); }} dir="ltr" required data-testid="replenish-amount" />
                  </Field>
                  <Field label={t("expenses.replenishedOn")}>
                    <TextField type="date" value={replenish.replenishedOn} onChange={(e) => { setReplenish({ ...replenish, replenishedOn: e.target.value }); }} dir="ltr" data-testid="replenish-date" />
                  </Field>
                  <Field label={t("banking.reference")}>
                    <TextField value={replenish.reference} onChange={(e) => { setReplenish({ ...replenish, reference: e.target.value }); }} dir="ltr" data-testid="replenish-reference" />
                  </Field>
                  <div className="flex gap-2 sm:col-span-2">
                    <Button type="button" variant="secondary" onClick={() => { setReplenish(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={top.isPending} disabled={!replenish.fromBankAccountId || num(replenish.amount) <= 0} data-testid="confirm-replenish">{t("expenses.replenishNow")}</Button>
                  </div>
                </form>
              ) : null}
              <h3 className="text-sm font-semibold">{t("expenses.replenishments")}</h3>
              {(replenishments.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("expenses.noReplenishments")}</p> : (
                <Table data-testid="replenishment-rows">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.number")}</TableHead>
                      <TableHead>{t("expenses.replenishedOn")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                      <TableHead>{t("banking.reference")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(replenishments.data ?? []).map((r) => (
                      <TableRow key={r.id} data-testid="replenishment-row">
                        <TableCell dir="ltr">{r.number}</TableCell>
                        <TableCell dir="ltr">{formatDate(r.replenishedOn)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(r.amount, f.currency)}</TableCell>
                        <TableCell><ItemStatus status={r.status} /></TableCell>
                        <TableCell dir="ltr">{r.reference ?? ""}</TableCell>
                        <TableCell>
                          {r.status === "posted" && reversal?.id !== r.id ? <Button variant="ghost" size="sm" onClick={() => { setReversal({ id: r.id, reason: "" }); }} data-testid="reverse-replenishment">{t("purchasing.reverse")}</Button> : null}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              {reversal ? (
                <div className="flex flex-wrap items-end gap-3">
                  <Field label={t("purchasing.reversalReason")} required>
                    <TextField value={reversal.reason} onChange={(e) => { setReversal({ ...reversal, reason: e.target.value }); }} data-testid="reversal-reason" />
                  </Field>
                  <Button onClick={() => { undo.mutate(reversal); }} loading={undo.isPending} disabled={!reversal.reason.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button>
                </div>
              ) : null}
              <RecordActivity entityType="petty_cash_fund" entityId={f.id} />
              <DialogFooter>
                <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: f.id, code: f.code, nameEn: f.name.en ?? "", nameAr: f.name.ar ?? "", bankAccountId: f.bankAccountId, floatAmount: String(f.floatAmount), isActive: f.isActive }); }} data-testid="edit-fund">{t("common.edit")}</Button>
                {f.isActive && !replenish ? <Button onClick={() => { setProblem(null); setReplenish({ fromBankAccountId: sources[0]?.id ?? "", amount: Number(f.toReplenish) > 0 ? String(f.toReplenish) : "", replenishedOn: today(), reference: "" }); }} data-testid="replenish-fund">{t("expenses.replenish")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
