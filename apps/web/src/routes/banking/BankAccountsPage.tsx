import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { useBankAccounts, type BankAccount } from "../payables/shared";

interface AccountForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
  currency: string;
  bankName: string;
  branchName: string;
  accountNumber: string;
  iban: string;
  swift: string;
  isActive: boolean;
}

/** Bank, cash and petty-cash accounts (roadmap 4.7): each on its control account with the bank transactions as its subledger. */
export function BankAccountsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<AccountForm | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const list = useBankAccounts(companyId);
  const detail = useQuery({
    queryKey: ["bank-account", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts/{bankAccountId}", { params: { path: { bankAccountId: openId ?? "" } } })),
  });
  const transactions = useQuery({
    queryKey: ["bank-transactions", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts/{bankAccountId}/transactions", { params: { path: { bankAccountId: openId ?? "" } } })),
  });
  const save = useMutation({
    mutationFn: async (f: AccountForm) => {
      const body = { companyId, code: f.code, name: { en: f.nameEn, ar: f.nameAr }, kind: f.kind, currency: f.currency, bankName: f.bankName || null, branchName: f.branchName || null, accountNumber: f.accountNumber || null, iban: f.iban || null, swift: f.swift || null, isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/banking/bank-accounts/{bankAccountId}", { params: { path: { bankAccountId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/bank-accounts", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await Promise.all([["bank-accounts"], ["bank-account"]].map((key) => queryClient.invalidateQueries({ queryKey: key }))); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<BankAccount, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 110, cell: ({ row }) => t(`banking.kinds.${row.original.kind}`) },
      { id: "currency", accessorKey: "currency", header: t("partners.currency"), size: 90 },
      { id: "gl", accessorKey: "glAccountCode", header: t("banking.glAccount"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.glAccountCode}</span> },
      { id: "balance", accessorKey: "balanceTc", header: t("banking.balance"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.balanceTc, row.original.currency)}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const blank = (): AccountForm => ({ id: null, code: "", nameEn: "", nameAr: "", kind: "bank", currency: companies.find((c) => c.id === companyId)?.functionalCurrency ?? "", bankName: "", branchName: "", accountNumber: "", iban: "", swift: "", isActive: true });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const a = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.bankAccounts")}
        description={t("banking.accountsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-bank-account">
            <Plus aria-hidden="true" />
            {t("banking.newAccount")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<BankAccount> label="nav.bankAccounts" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("banking.emptyAccounts")} emptyDescription={t("banking.emptyAccountsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("banking.editAccount") : t("banking.newAccount")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.code")} required>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="bank-code" />
                </Field>
                <Field label={t("purchasing.kind")} required>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value }); }} data-testid="bank-kind">
                    {["bank", "cash", "petty_cash"].map((k) => (
                      <option key={k} value={k}>{t(`banking.kinds.${k}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="bank-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="bank-name-ar" />
                </Field>
                <Field label={t("partners.currency")} required>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="bank-currency" />
                </Field>
                <Field label={t("banking.bankName")}>
                  <TextField value={form.bankName} onChange={(e) => { setForm({ ...form, bankName: e.target.value }); }} data-testid="bank-bank-name" />
                </Field>
                <Field label={t("banking.iban")}>
                  <TextField value={form.iban} onChange={(e) => { setForm({ ...form, iban: e.target.value }); }} dir="ltr" data-testid="bank-iban" />
                </Field>
                <Field label={t("banking.swift")}>
                  <TextField value={form.swift} onChange={(e) => { setForm({ ...form, swift: e.target.value }); }} dir="ltr" data-testid="bank-swift" />
                </Field>
                <Field label={t("banking.accountNumber")}>
                  <TextField value={form.accountNumber} onChange={(e) => { setForm({ ...form, accountNumber: e.target.value }); }} dir="ltr" data-testid="bank-account-number" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="bank-active" />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-bank-account">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {a ? (
            <div className="flex flex-col gap-4" data-testid="bank-account-detail">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold"><span dir="ltr">{a.code}</span> · <span dir="auto">{localized(a.name)}</span></DialogTitle>
              </DialogHeader>
              <KeyValues entries={[
                [t("purchasing.kind"), t(`banking.kinds.${a.kind}`)],
                [t("partners.currency"), a.currency],
                [t("banking.glAccount"), a.glAccountCode],
                [t("banking.bankName"), a.bankName ?? "—"],
                [t("banking.iban"), a.iban ?? "—"],
                [t("banking.balance"), <span key="bal" data-testid="bank-balance">{formatMoney(a.balanceTc, a.currency)}{a.currency !== a.functionalCurrency ? ` (${formatMoney(a.balanceFc, a.functionalCurrency)})` : ""}</span>],
              ]} />
              <h3 className="text-sm font-semibold">{t("banking.transactions")}</h3>
              {(transactions.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("banking.noTransactions")}</p> : (
                <Table data-testid="bank-transactions">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.postingDate")}</TableHead>
                      <TableHead>{t("purchasing.kind")}</TableHead>
                      <TableHead>{t("banking.reference")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(transactions.data ?? []).map((tx) => (
                      <TableRow key={tx.id} data-testid="bank-transaction-row">
                        <TableCell dir="ltr">{formatDate(tx.postingDate)}</TableCell>
                        <TableCell>{t(`banking.transactionKinds.${tx.kind}`)}</TableCell>
                        <TableCell dir="ltr">{tx.reference ?? ""}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(tx.amountTc, a.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              <DialogFooter>
                <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: a.id, code: a.code, nameEn: a.name.en ?? "", nameAr: a.name.ar ?? "", kind: a.kind, currency: a.currency, bankName: a.bankName ?? "", branchName: a.branchName ?? "", accountNumber: "", iban: a.iban ?? "", swift: a.swift ?? "", isActive: a.isActive }); }} data-testid="edit-bank-account">{t("common.edit")}</Button>
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
