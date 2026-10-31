import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { num } from "../purchasing/shared";
import { useExpenseCategories, useTaxCodeOptions, type ExpenseCategory } from "./shared";

interface CategoryForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  accountCode: string;
  taxCodeId: string;
  receiptRequired: boolean;
  maxAmount: string;
  isActive: boolean;
}

/** Expense categories (roadmap 6.4): what an expense line is for, the account it lands on in every company's chart, the usual tax code and the limit a line must respect. */
export function ExpenseCategoriesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<CategoryForm | null>(null);
  const list = useExpenseCategories();
  const taxCodes = useTaxCodeOptions();
  const save = useMutation({
    mutationFn: async (f: CategoryForm) => {
      const body = { code: f.code, name: { en: f.nameEn, ar: f.nameAr }, accountCode: f.accountCode.trim(), taxCodeId: f.taxCodeId || null, receiptRequired: f.receiptRequired, maxAmount: f.maxAmount.trim() ? num(f.maxAmount) : null, isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/expenses/categories/{categoryId}", { params: { path: { categoryId: f.id } }, body })) : unwrap(await api.POST("/api/v1/expenses/categories", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await queryClient.invalidateQueries({ queryKey: ["expense-categories"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const taxLabel = (id: string | null): string => (id ? (taxCodes.find((c) => c.id === id)?.label ?? "…") : "—");
  const columns = useMemo<ColumnDef<ExpenseCategory, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "account", accessorKey: "accountCode", header: t("expenses.accountCode"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.accountCode}</span> },
      { id: "tax", accessorFn: (r) => taxLabel(r.taxCodeId), header: t("expenses.taxCode"), size: 220, cell: ({ row }) => <span dir="auto">{taxLabel(row.original.taxCodeId)}</span> },
      { id: "receipt", accessorKey: "receiptRequired", header: t("expenses.receiptRequired"), size: 110, cell: ({ row }) => (row.original.receiptRequired ? t("common.yes") : t("common.no")) },
      { id: "max", accessorKey: "maxAmount", header: t("expenses.maxAmount"), size: 120, cell: ({ row }) => <span className="tabular" dir="ltr">{row.original.maxAmount === null ? "—" : formatNumber(row.original.maxAmount)}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the tax labels change with the codes loaded
    [t, taxCodes],
  );

  const blank = (): CategoryForm => ({ id: null, code: "", nameEn: "", nameAr: "", accountCode: "", taxCodeId: "", receiptRequired: false, maxAmount: "", isActive: true });
  const edit = (c: ExpenseCategory): void => { setProblem(null); setForm({ id: c.id, code: c.code, nameEn: c.name.en ?? "", nameAr: c.name.ar ?? "", accountCode: c.accountCode, taxCodeId: c.taxCodeId ?? "", receiptRequired: c.receiptRequired, maxAmount: c.maxAmount === null ? "" : String(c.maxAmount), isActive: c.isActive }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };

  return (
    <>
      <PageHeader
        title={t("nav.expenseCategories")}
        description={t("expenses.categoriesDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} data-testid="new-category">
            <Plus aria-hidden="true" />
            {t("expenses.newCategory")}
          </Button>
        }
      />
      <DataGrid<ExpenseCategory> label="nav.expenseCategories" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending} emptyTitle={t("expenses.emptyCategories")} emptyDescription={t("expenses.emptyCategoriesDescription")} onOpen={edit} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("expenses.editCategory") : t("expenses.newCategory")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.code")} required>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="category-code" />
                </Field>
                <Field label={t("expenses.accountCode")} description={t("expenses.accountCodeHelp")} required>
                  <TextField value={form.accountCode} onChange={(e) => { setForm({ ...form, accountCode: e.target.value }); }} dir="ltr" required data-testid="category-account" />
                </Field>
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="category-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="category-name-ar" />
                </Field>
                <Field label={t("expenses.taxCode")} description={t("expenses.taxCodeHelp")}>
                  <SelectField value={form.taxCodeId} onChange={(e) => { setForm({ ...form, taxCodeId: e.target.value }); }} data-testid="category-tax-code">
                    <option value="">{t("expenses.taxByRules")}</option>
                    {taxCodes.map((c) => (
                      <option key={c.id} value={c.id}>{c.label}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("expenses.maxAmount")} description={t("expenses.maxAmountHelp")}>
                  <TextField inputMode="decimal" value={form.maxAmount} onChange={(e) => { setForm({ ...form, maxAmount: e.target.value }); }} dir="ltr" data-testid="category-max" />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.receiptRequired} onChange={(e) => { setForm({ ...form, receiptRequired: e.target.checked }); }} data-testid="category-receipt" />
                  {t("expenses.receiptRequired")}
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="category-active" />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-category">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
