import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { useAssetBooks, type AssetBook } from "./shared";

interface BookForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  postsToGl: boolean;
  isActive: boolean;
}

/** Asset books (roadmap 6.6): the book that posts to the ledger and memo books (a tax book) that only compute. */
export function AssetBooksPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<BookForm | null>(null);
  const list = useAssetBooks(companyId);
  const save = useMutation({
    mutationFn: async (f: BookForm) => {
      const body = { companyId, code: f.code, name: { en: f.nameEn, ar: f.nameAr }, postsToGl: f.postsToGl, isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/fixed-assets/books/{bookId}", { params: { path: { bookId: f.id } }, body })) : unwrap(await api.POST("/api/v1/fixed-assets/books", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await queryClient.invalidateQueries({ queryKey: ["asset-books"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<AssetBook, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 100, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 260, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "gl", accessorKey: "postsToGl", header: t("fixedAssets.postsToGl"), size: 140, cell: ({ row }) => (row.original.postsToGl ? t("common.yes") : t("common.no")) },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const hasGl = (list.data ?? []).some((b) => b.postsToGl);
  const blank = (): BookForm => ({ id: null, code: "", nameEn: "", nameAr: "", postsToGl: !hasGl, isActive: true });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };

  return (
    <>
      <PageHeader
        title={t("nav.assetBooks")}
        description={t("fixedAssets.booksDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-asset-book">
            <Plus aria-hidden="true" />
            {t("fixedAssets.newBook")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<AssetBook> label="nav.assetBooks" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("fixedAssets.emptyBooks")} emptyDescription={t("fixedAssets.emptyBooksDescription")} onOpen={(b) => { setProblem(null); setForm({ id: b.id, code: b.code, nameEn: b.name.en ?? "", nameAr: b.name.ar ?? "", postsToGl: b.postsToGl, isActive: b.isActive }); }} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("fixedAssets.editBook") : t("fixedAssets.newBook")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.code")} required>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="book-code" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.postsToGl} onChange={(e) => { setForm({ ...form, postsToGl: e.target.checked }); }} disabled={Boolean(form.id)} data-testid="book-posts-to-gl" />
                  {t("fixedAssets.postsToGl")}
                </label>
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="book-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="book-name-ar" />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="book-active" />
                  {t("common.active")}
                </label>
              </div>
              <p className="text-xs text-fg-muted">{t("fixedAssets.booksHelp")}</p>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-asset-book">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
