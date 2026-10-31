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
import { depreciationConventions, depreciationMethods, useAssetCategories, type AssetCategory } from "./shared";

interface CategoryForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  defaultMethod: string;
  defaultLifeMonths: string;
  defaultSalvagePct: string;
  defaultRatePct: string;
  convention: string;
  costAccountCode: string;
  accumulatedDepreciationAccountCode: string;
  depreciationExpenseAccountCode: string;
  isActive: boolean;
}

/** Asset categories (roadmap 6.6): the defaults an asset starts with and the accounts its postings land on in every company's chart. */
export function AssetCategoriesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<CategoryForm | null>(null);
  const list = useAssetCategories();
  const save = useMutation({
    mutationFn: async (f: CategoryForm) => {
      const body = {
        code: f.code,
        name: { en: f.nameEn, ar: f.nameAr },
        defaultMethod: f.defaultMethod,
        defaultLifeMonths: Math.trunc(num(f.defaultLifeMonths)),
        defaultSalvagePct: num(f.defaultSalvagePct),
        defaultRatePct: f.defaultRatePct.trim() ? num(f.defaultRatePct) : null,
        convention: f.convention,
        costAccountCode: f.costAccountCode.trim() || null,
        accumulatedDepreciationAccountCode: f.accumulatedDepreciationAccountCode.trim() || null,
        depreciationExpenseAccountCode: f.depreciationExpenseAccountCode.trim() || null,
        isActive: f.isActive,
      };
      return f.id ? unwrap(await api.PUT("/api/v1/fixed-assets/categories/{categoryId}", { params: { path: { categoryId: f.id } }, body })) : unwrap(await api.POST("/api/v1/fixed-assets/categories", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await queryClient.invalidateQueries({ queryKey: ["asset-categories"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<AssetCategory, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 100, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "method", accessorFn: (r) => t(`fixedAssets.methods.${r.defaultMethod}`), header: t("fixedAssets.method"), size: 170 },
      { id: "life", accessorKey: "defaultLifeMonths", header: t("fixedAssets.lifeMonths"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.defaultLifeMonths)}</span> },
      { id: "salvage", accessorKey: "defaultSalvagePct", header: t("fixedAssets.salvagePct"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.defaultSalvagePct)} %</span> },
      { id: "convention", accessorFn: (r) => t(`fixedAssets.conventions.${r.convention}`), header: t("fixedAssets.convention"), size: 150 },
      { id: "accounts", accessorFn: (r) => [r.costAccountCode, r.accumulatedDepreciationAccountCode, r.depreciationExpenseAccountCode].filter(Boolean).join(" / "), header: t("fixedAssets.accounts"), size: 180, cell: ({ row }) => <span dir="ltr">{[row.original.costAccountCode, row.original.accumulatedDepreciationAccountCode, row.original.depreciationExpenseAccountCode].filter(Boolean).join(" / ") || "—"}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const blank = (): CategoryForm => ({ id: null, code: "", nameEn: "", nameAr: "", defaultMethod: "straight_line", defaultLifeMonths: "60", defaultSalvagePct: "0", defaultRatePct: "", convention: "full_month", costAccountCode: "", accumulatedDepreciationAccountCode: "", depreciationExpenseAccountCode: "", isActive: true });
  const edit = (c: AssetCategory): void => {
    setProblem(null);
    setForm({ id: c.id, code: c.code, nameEn: c.name.en ?? "", nameAr: c.name.ar ?? "", defaultMethod: c.defaultMethod, defaultLifeMonths: String(c.defaultLifeMonths), defaultSalvagePct: String(c.defaultSalvagePct), defaultRatePct: c.defaultRatePct === null ? "" : String(c.defaultRatePct), convention: c.convention, costAccountCode: c.costAccountCode ?? "", accumulatedDepreciationAccountCode: c.accumulatedDepreciationAccountCode ?? "", depreciationExpenseAccountCode: c.depreciationExpenseAccountCode ?? "", isActive: c.isActive });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const declining = form?.defaultMethod === "declining_balance" || form?.defaultMethod === "double_declining";

  return (
    <>
      <PageHeader
        title={t("nav.assetCategories")}
        description={t("fixedAssets.categoriesDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} data-testid="new-asset-category">
            <Plus aria-hidden="true" />
            {t("fixedAssets.newCategory")}
          </Button>
        }
      />
      <DataGrid<AssetCategory> label="nav.assetCategories" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending} emptyTitle={t("fixedAssets.emptyCategories")} emptyDescription={t("fixedAssets.emptyCategoriesDescription")} onOpen={edit} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("fixedAssets.editCategory") : t("fixedAssets.newCategory")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("banking.code")} required>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="category-code" />
                </Field>
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="category-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="category-name-ar" />
                </Field>
                <Field label={t("fixedAssets.method")} required>
                  <SelectField value={form.defaultMethod} onChange={(e) => { setForm({ ...form, defaultMethod: e.target.value }); }} data-testid="category-method">
                    {depreciationMethods.map((m) => (
                      <option key={m} value={m}>{t(`fixedAssets.methods.${m}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("fixedAssets.lifeMonths")} required={form.defaultMethod !== "none" && form.defaultMethod !== "units_of_production"}>
                  <TextField inputMode="numeric" value={form.defaultLifeMonths} onChange={(e) => { setForm({ ...form, defaultLifeMonths: e.target.value }); }} dir="ltr" data-testid="category-life" />
                </Field>
                <Field label={t("fixedAssets.convention")} description={t("fixedAssets.conventionHelp")}>
                  <SelectField value={form.convention} onChange={(e) => { setForm({ ...form, convention: e.target.value }); }} data-testid="category-convention">
                    {depreciationConventions.map((c) => (
                      <option key={c} value={c}>{t(`fixedAssets.conventions.${c}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("fixedAssets.salvagePct")} description={t("fixedAssets.salvagePctHelp")}>
                  <TextField inputMode="decimal" value={form.defaultSalvagePct} onChange={(e) => { setForm({ ...form, defaultSalvagePct: e.target.value }); }} dir="ltr" data-testid="category-salvage" />
                </Field>
                {declining ? (
                  <Field label={t("fixedAssets.ratePct")} description={t("fixedAssets.ratePctHelp")}>
                    <TextField inputMode="decimal" value={form.defaultRatePct} onChange={(e) => { setForm({ ...form, defaultRatePct: e.target.value }); }} dir="ltr" data-testid="category-rate" />
                  </Field>
                ) : null}
                <Field label={t("fixedAssets.costAccount")} description={t("fixedAssets.accountsHelp")}>
                  <TextField value={form.costAccountCode} onChange={(e) => { setForm({ ...form, costAccountCode: e.target.value }); }} dir="ltr" data-testid="category-cost-account" />
                </Field>
                <Field label={t("fixedAssets.accumulatedDepreciationAccount")}>
                  <TextField value={form.accumulatedDepreciationAccountCode} onChange={(e) => { setForm({ ...form, accumulatedDepreciationAccountCode: e.target.value }); }} dir="ltr" data-testid="category-accdep-account" />
                </Field>
                <Field label={t("fixedAssets.depreciationExpenseAccount")}>
                  <TextField value={form.depreciationExpenseAccountCode} onChange={(e) => { setForm({ ...form, depreciationExpenseAccountCode: e.target.value }); }} dir="ltr" data-testid="category-expense-account" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="category-active" />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-asset-category">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
