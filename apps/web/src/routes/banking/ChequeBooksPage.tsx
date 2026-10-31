import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";
import { useChequeBooks, type ChequeBook } from "./chequeShared";

interface BookForm {
  id: string | null;
  bankAccountId: string;
  prefix: string;
  firstNumber: string;
  lastNumber: string;
  isActive: boolean;
}

/** Cheque books (roadmap 6.3): a numbered run of leaves of a bank account; payments by cheque take the next leaf. */
export function ChequeBooksPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<BookForm | null>(null);
  const list = useChequeBooks(companyId);
  const banks = useBankAccounts(companyId);
  const canManage = can("banking.cheque.manage");
  const save = useMutation({
    mutationFn: async (f: BookForm) => {
      const body = { companyId, bankAccountId: f.bankAccountId, prefix: f.prefix || null, firstNumber: Number(f.firstNumber), lastNumber: Number(f.lastNumber), isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/banking/cheque-books/{bookId}", { params: { path: { bookId: f.id } }, body })) : unwrap(await api.POST("/api/v1/banking/cheque-books", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await queryClient.invalidateQueries({ queryKey: ["cheque-books"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<ChequeBook, unknown>[]>(
    () => [
      { id: "bank", accessorKey: "bankAccountCode", header: t("nav.bankAccounts"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.bankAccountCode}</span> },
      { id: "prefix", accessorKey: "prefix", header: t("banking.cheques.books.prefix"), size: 90, cell: ({ row }) => <span dir="ltr">{row.original.prefix || "—"}</span> },
      { id: "first", accessorKey: "firstNumber", header: t("banking.cheques.books.firstNumber"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.firstNumber)}</span> },
      { id: "last", accessorKey: "lastNumber", header: t("banking.cheques.books.lastNumber"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.lastNumber)}</span> },
      { id: "next", accessorKey: "nextNumber", header: t("banking.cheques.books.nextNumber"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.nextNumber)}</span> },
      { id: "remaining", accessorKey: "remaining", header: t("banking.cheques.books.remaining"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.remaining)}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const blank = (): BookForm => ({ id: null, bankAccountId: (banks.data ?? []).find((b) => b.kind === "bank")?.id ?? "", prefix: "", firstNumber: "1", lastNumber: "50", isActive: true });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };

  return (
    <>
      <PageHeader
        title={t("nav.chequeBooks")}
        description={t("banking.cheques.books.description")}
        actions={canManage ? (
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-cheque-book">
            <Plus aria-hidden="true" />
            {t("banking.cheques.books.newBook")}
          </Button>
        ) : null}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<ChequeBook> label="nav.chequeBooks" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("banking.cheques.books.emptyTitle")} emptyDescription={t("banking.cheques.books.emptyDescription")} onOpen={(b) => { if (canManage) { setProblem(null); setForm({ id: b.id, bankAccountId: b.bankAccountId, prefix: b.prefix, firstNumber: String(b.firstNumber), lastNumber: String(b.lastNumber), isActive: b.isActive }); } }} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("banking.cheques.books.editBook") : t("banking.cheques.books.newBook")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("nav.bankAccounts")} required>
                  <SelectField value={form.bankAccountId} onChange={(e) => { setForm({ ...form, bankAccountId: e.target.value }); }} required disabled={Boolean(form.id)} data-testid="book-bank">
                    <option value="">—</option>
                    {(banks.data ?? []).filter((b) => b.kind === "bank").map((b) => <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("banking.cheques.books.prefix")} description={t("banking.cheques.books.prefixHelp")}>
                  <TextField value={form.prefix} onChange={(e) => { setForm({ ...form, prefix: e.target.value.toUpperCase() }); }} dir="ltr" data-testid="book-prefix" />
                </Field>
                <Field label={t("banking.cheques.books.firstNumber")} required>
                  <TextField inputMode="numeric" value={form.firstNumber} onChange={(e) => { setForm({ ...form, firstNumber: e.target.value }); }} dir="ltr" required data-testid="book-first" />
                </Field>
                <Field label={t("banking.cheques.books.lastNumber")} required>
                  <TextField inputMode="numeric" value={form.lastNumber} onChange={(e) => { setForm({ ...form, lastNumber: e.target.value }); }} dir="ltr" required data-testid="book-last" />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="book-active" />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-cheque-book">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
