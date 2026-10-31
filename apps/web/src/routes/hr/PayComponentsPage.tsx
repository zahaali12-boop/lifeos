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
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { num } from "../purchasing/shared";
import { componentCalculations, componentKinds, usePayComponents, type PayComponent } from "./shared";

interface ComponentForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
  calculation: string;
  defaultAmount: string;
  defaultRatePct: string;
  appliesToAll: boolean;
  expenseAccountCode: string;
  liabilityAccountCode: string;
  sortOrder: string;
  isActive: boolean;
}

/** Pay components (founder programme item 8): earnings, deductions and employer contributions, how each is calculated and the accounts it lands on; BASIC comes with the company. */
export function PayComponentsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ComponentForm | null>(null);
  const list = usePayComponents(companyId);
  const save = useMutation({
    mutationFn: async (f: ComponentForm) => {
      const body = {
        companyId,
        code: f.code.trim() || null,
        name: { en: f.nameEn, ar: f.nameAr },
        kind: f.kind,
        calculation: f.calculation,
        defaultAmount: f.defaultAmount.trim() ? num(f.defaultAmount) : 0,
        defaultRatePct: f.defaultRatePct.trim() ? num(f.defaultRatePct) : 0,
        appliesToAll: f.appliesToAll,
        expenseAccountCode: f.expenseAccountCode.trim() || null,
        liabilityAccountCode: f.liabilityAccountCode.trim() || null,
        sortOrder: f.sortOrder.trim() ? Math.trunc(num(f.sortOrder)) : 0,
        isActive: f.isActive,
      };
      return f.id ? unwrap(await api.PUT("/api/v1/payroll/components/{componentId}", { params: { path: { componentId: f.id } }, body })) : unwrap(await api.POST("/api/v1/payroll/components", { body }));
    },
    onSuccess: async () => { setProblem(null); setForm(null); await queryClient.invalidateQueries({ queryKey: ["pay-components"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<PayComponent, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "kind", accessorFn: (r) => t(`hr.componentKinds.${r.kind}`), header: t("hr.kind"), size: 150 },
      { id: "calculation", accessorFn: (r) => t(`hr.calculations.${r.calculation}`), header: t("hr.calculation"), size: 170 },
      { id: "amount", accessorKey: "defaultAmount", header: t("hr.defaultAmount"), size: 120, cell: ({ row }) => <span className="tabular" dir="ltr">{Number(row.original.defaultAmount) === 0 ? "—" : formatNumber(row.original.defaultAmount)}</span> },
      { id: "rate", accessorKey: "defaultRatePct", header: t("hr.defaultRate"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{Number(row.original.defaultRatePct) === 0 ? "—" : `${formatNumber(row.original.defaultRatePct)} %`}</span> },
      { id: "all", accessorKey: "appliesToAll", header: t("hr.appliesToAll"), size: 110, cell: ({ row }) => (row.original.appliesToAll ? t("common.yes") : t("common.no")) },
      { id: "expense", accessorKey: "expenseAccountCode", header: t("hr.expenseAccount"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.expenseAccountCode ?? "—"}</span> },
      { id: "liability", accessorKey: "liabilityAccountCode", header: t("hr.liabilityAccount"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.liabilityAccountCode ?? "—"}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
    ],
    [t],
  );

  const blank = (): ComponentForm => ({ id: null, code: "", nameEn: "", nameAr: "", kind: "earning", calculation: "fixed", defaultAmount: "", defaultRatePct: "", appliesToAll: false, expenseAccountCode: "", liabilityAccountCode: "", sortOrder: "", isActive: true });
  const edit = (c: PayComponent): void => {
    setProblem(null);
    setForm({ id: c.id, code: c.code, nameEn: c.name.en ?? "", nameAr: c.name.ar ?? "", kind: c.kind, calculation: c.calculation, defaultAmount: Number(c.defaultAmount) === 0 ? "" : String(c.defaultAmount), defaultRatePct: Number(c.defaultRatePct) === 0 ? "" : String(c.defaultRatePct), appliesToAll: c.appliesToAll, expenseAccountCode: c.expenseAccountCode ?? "", liabilityAccountCode: c.liabilityAccountCode ?? "", sortOrder: String(c.sortOrder), isActive: c.isActive });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const isBasic = form?.calculation === "basic";
  const needsExpense = form?.kind === "earning" || form?.kind === "employer";
  const needsLiability = form?.kind === "deduction" || form?.kind === "employer";
  const usesRate = form?.calculation === "percent_of_basic" || form?.calculation === "percent_of_gross";

  return (
    <>
      <PageHeader
        title={t("nav.payComponents")}
        description={t("hr.componentsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-component">
            <Plus aria-hidden="true" />
            {t("hr.newComponent")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<PayComponent> label="nav.payComponents" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("hr.emptyComponents")} emptyDescription={t("hr.emptyComponentsDescription")} onOpen={edit} exportable={false} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("hr.editComponent") : t("hr.newComponent")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required data-testid="component-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required data-testid="component-name-ar" />
                </Field>
                <Field label={t("banking.code")} description={t("pos.codeHelp")}>
                  <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} dir="ltr" disabled={isBasic} data-testid="component-code" />
                </Field>
                <Field label={t("hr.kind")} required>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value }); }} disabled={isBasic} data-testid="component-kind">
                    {componentKinds.map((k) => (
                      <option key={k} value={k}>{t(`hr.componentKinds.${k}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("hr.calculation")} description={t("hr.calculationHelp")} required>
                  <SelectField value={form.calculation} onChange={(e) => { setForm({ ...form, calculation: e.target.value }); }} disabled={isBasic} data-testid="component-calculation">
                    {isBasic ? <option value="basic">{t("hr.calculations.basic")}</option> : null}
                    {componentCalculations.map((c) => (
                      <option key={c} value={c}>{t(`hr.calculations.${c}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                {usesRate ? (
                  <Field label={t("hr.defaultRate")} description={t("hr.defaultRateHelp")}>
                    <TextField inputMode="decimal" value={form.defaultRatePct} onChange={(e) => { setForm({ ...form, defaultRatePct: e.target.value }); }} dir="ltr" data-testid="component-rate" />
                  </Field>
                ) : null}
                {form.calculation === "fixed" ? (
                  <Field label={t("hr.defaultAmount")} description={t("hr.defaultAmountHelp")}>
                    <TextField inputMode="decimal" value={form.defaultAmount} onChange={(e) => { setForm({ ...form, defaultAmount: e.target.value }); }} dir="ltr" data-testid="component-amount" />
                  </Field>
                ) : null}
                {needsExpense ? (
                  <Field label={t("hr.expenseAccount")} description={t("hr.expenseAccountHelp")} required>
                    <TextField value={form.expenseAccountCode} onChange={(e) => { setForm({ ...form, expenseAccountCode: e.target.value }); }} dir="ltr" required data-testid="component-expense-account" />
                  </Field>
                ) : null}
                {needsLiability ? (
                  <Field label={t("hr.liabilityAccount")} description={t("hr.liabilityAccountHelp")} required>
                    <TextField value={form.liabilityAccountCode} onChange={(e) => { setForm({ ...form, liabilityAccountCode: e.target.value }); }} dir="ltr" required data-testid="component-liability-account" />
                  </Field>
                ) : null}
                <Field label={t("hr.sortOrder")} description={t("hr.sortOrderHelp")}>
                  <TextField inputMode="numeric" value={form.sortOrder} onChange={(e) => { setForm({ ...form, sortOrder: e.target.value }); }} dir="ltr" data-testid="component-sort" />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.appliesToAll || isBasic} disabled={isBasic} onChange={(e) => { setForm({ ...form, appliesToAll: e.target.checked }); }} data-testid="component-all" />
                  {t("hr.appliesToAll")}
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="component-active" />
                  {t("common.active")}
                </label>
              </div>
              <p className="text-sm text-muted-foreground">{t("hr.appliesToAllHelp")}</p>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-component">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
