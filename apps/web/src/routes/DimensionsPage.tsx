import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDate, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { useCompanies } from "./accounting/shared";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { DocStatus } from "./inventory/shared";

type Dimension = components["schemas"]["DimensionSummary"];
type DimensionValue = components["schemas"]["DimensionValueSummary"];

interface DimensionForm {
  code: string;
  nameEn: string;
  nameAr: string;
  isHierarchical: boolean;
  sortOrder: string;
  isActive: boolean;
}

interface ValueForm {
  code: string;
  nameEn: string;
  nameAr: string;
  parentId: string;
  companyId: string;
  validFrom: string;
  validTo: string;
  isActive: boolean;
}

const emptyValue: ValueForm = { code: "", nameEn: "", nameAr: "", parentId: "", companyId: "", validFrom: "", validTo: "", isActive: true };

function names(en: string, ar: string): Record<string, string> {
  return { en, ...(ar ? { ar } : {}) };
}

/** Dimensions (roadmap 1.8, DOMAIN_MODEL §4): the analysis axes journals and documents carry (cost centre, department, project and any an admin adds) and their values; the branch dimension's values come from the branches themselves. */
export function DimensionsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editingDimension, setEditingDimension] = useState<{ id: string | null; form: DimensionForm } | null>(null);
  const [editingValue, setEditingValue] = useState<{ id: string | null; form: ValueForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const dimensions = useQuery({ queryKey: ["dimensions"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions")) });
  const selected = dimensions.data?.find((d) => d.id === selectedId) ?? dimensions.data?.[0];
  const values = useQuery({
    queryKey: ["dimension-values", selected?.id],
    enabled: Boolean(selected),
    queryFn: async () => unwrap(await api.GET("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId: selected?.id ?? "" } } })),
  });

  const saveDimension = useMutation({
    mutationFn: async (input: { id: string | null; form: DimensionForm }) => {
      const body = { code: input.form.code, name: names(input.form.nameEn, input.form.nameAr), isHierarchical: input.form.isHierarchical, sortOrder: Number(input.form.sortOrder) || 0, isActive: input.form.isActive };
      return input.id
        ? unwrap(await api.PUT("/api/v1/organization/dimensions/{dimensionId}", { params: { path: { dimensionId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/organization/dimensions", { body }));
    },
    onSuccess: async (saved) => {
      setEditingDimension(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["dimensions"] });
      setSelectedId(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const saveValue = useMutation({
    mutationFn: async (input: { id: string | null; form: ValueForm }) => {
      const f = input.form;
      const dimensionId = selected?.id ?? "";
      const body = { code: f.code, name: names(f.nameEn, f.nameAr), parentId: f.parentId || null, companyId: f.companyId || null, validFrom: f.validFrom || null, validTo: f.validTo || null, isActive: f.isActive };
      return input.id
        ? unwrap(await api.PUT("/api/v1/organization/dimensions/{dimensionId}/values/{valueId}", { params: { path: { dimensionId, valueId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/organization/dimensions/{dimensionId}/values", { params: { path: { dimensionId } }, body }));
    },
    onSuccess: async () => {
      setEditingValue(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["dimension-values"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const dimensionColumns = useMemo<ColumnDef<Dimension, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("dimensions.code"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("dimensions.name"), size: 200 },
      { id: "kind", accessorFn: (row) => (row.isSystem ? t("dimensions.system") : t("dimensions.custom")), header: t("dimensions.kind"), size: 100 },
      { id: "status", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <DocStatus status={row.original.isActive ? "active" : "inactive"} /> },
    ],
    [t],
  );
  const valueColumns = useMemo<ColumnDef<DimensionValue, unknown>[]>(() => {
    const valueCodes = new Map((values.data ?? []).map((v) => [v.id, v.code]));
    const companyCodes = new Map((companies.data ?? []).map((c) => [c.id, c.code]));
    return [
      { id: "code", accessorKey: "code", header: t("dimensions.code"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("dimensions.name"), size: 220 },
      { id: "parent", accessorKey: "parentId", header: t("dimensions.parent"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.parentId ? valueCodes.get(row.original.parentId) : ""}</span> },
      { id: "company", accessorKey: "companyId", header: t("dimensions.company"), size: 140, cell: ({ row }) => (row.original.companyId ? companyCodes.get(row.original.companyId) : t("dimensions.allCompanies")) },
      { id: "valid", accessorFn: (row) => `${row.validFrom ?? ""}${row.validTo ?? ""}`, header: t("dimensions.valid"), size: 200, cell: ({ row }) => (row.original.validFrom || row.original.validTo ? `${formatDate(row.original.validFrom)} – ${formatDate(row.original.validTo)}` : "") },
      { id: "status", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <DocStatus status={row.original.isActive ? "active" : "inactive"} /> },
    ];
  }, [t, values.data, companies.data]);

  const submitDimension = (event: FormEvent): void => {
    event.preventDefault();
    if (editingDimension) {
      saveDimension.mutate(editingDimension);
    }
  };
  const submitValue = (event: FormEvent): void => {
    event.preventDefault();
    if (editingValue) {
      saveValue.mutate(editingValue);
    }
  };
  const setDimensionForm = (patch: Partial<DimensionForm>): void => { setEditingDimension((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const setValueForm = (patch: Partial<ValueForm>): void => { setEditingValue((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const branchManaged = selected?.code === "BRANCH";

  return (
    <>
      <PageHeader
        title={t("nav.dimensions")}
        description={t("dimensions.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditingDimension({ id: null, form: { code: "", nameEn: "", nameAr: "", isHierarchical: false, sortOrder: "0", isActive: true } }); }} data-testid="new-dimension">
            <Plus aria-hidden="true" />
            {t("dimensions.new")}
          </Button>
        }
      />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        <section>
          <DataGrid<Dimension> label="nav.dimensions" columns={dimensionColumns} data={dimensions.data ?? []} rowKey={(row) => row.id} loading={dimensions.isPending} height={420} onOpen={(row) => { setSelectedId(row.id); }} emptyTitle={t("dimensions.emptyTitle")} emptyDescription={t("dimensions.emptyDescription")} />
          <p className="mt-2 text-sm text-fg-muted">{t("dimensions.openHint")}</p>
        </section>
        <section data-testid="dimension-values">
          {selected ? (
            <>
              <div className="mb-3 flex flex-wrap items-center gap-2">
                <h2 className="text-lg font-semibold" dir="auto">
                  {selected.code} · {localized(selected.name)}
                </h2>
                <div className="ms-auto flex gap-2">
                  {selected.isSystem ? null : (
                    <Button variant="secondary" onClick={() => { setProblem(null); setEditingDimension({ id: selected.id, form: { code: selected.code, nameEn: selected.name.en ?? "", nameAr: selected.name.ar ?? "", isHierarchical: selected.isHierarchical, sortOrder: String(selected.sortOrder), isActive: selected.isActive } }); }}>
                      {t("common.edit")}
                    </Button>
                  )}
                  {branchManaged ? null : (
                    <Button onClick={() => { setProblem(null); setEditingValue({ id: null, form: { ...emptyValue } }); }} data-testid="new-value">
                      <Plus aria-hidden="true" />
                      {t("dimensions.newValue")}
                    </Button>
                  )}
                </div>
              </div>
              {branchManaged ? <p className="mb-2 text-sm text-fg-muted">{t("dimensions.branchHint")}</p> : null}
              <DataGrid<DimensionValue>
                label="dimensions.values"
                columns={valueColumns}
                data={values.data ?? []}
                rowKey={(row) => row.id}
                loading={values.isPending}
                height={420}
                {...(branchManaged ? {} : { onOpen: (row: DimensionValue) => { setProblem(null); setEditingValue({ id: row.id, form: { code: row.code, nameEn: row.name.en ?? "", nameAr: row.name.ar ?? "", parentId: row.parentId ?? "", companyId: row.companyId ?? "", validFrom: row.validFrom ?? "", validTo: row.validTo ?? "", isActive: row.isActive } }); } })}
                emptyTitle={t("dimensions.noValues")}
                emptyDescription={t("dimensions.noValuesHint")}
              />
            </>
          ) : null}
        </section>
      </div>

      <Dialog open={Boolean(editingDimension)} onOpenChange={(isOpen) => { if (!isOpen) { setEditingDimension(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {editingDimension ? (
            <form onSubmit={submitDimension} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editingDimension.id ? t("dimensions.edit") : t("dimensions.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("dimensions.code")} required error={problem?.fields.code}>
                  <TextField value={editingDimension.form.code} onChange={(e) => { setDimensionForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="dimension-code" />
                </Field>
                <Field label={t("dimensions.sortOrder")}>
                  <TextField inputMode="numeric" value={editingDimension.form.sortOrder} onChange={(e) => { setDimensionForm({ sortOrder: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("dimensions.nameEn")} required error={problem?.fields.name}>
                  <TextField value={editingDimension.form.nameEn} onChange={(e) => { setDimensionForm({ nameEn: e.target.value }); }} required data-testid="dimension-name-en" />
                </Field>
                <Field label={t("dimensions.nameAr")}>
                  <TextField value={editingDimension.form.nameAr} onChange={(e) => { setDimensionForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="dimension-name-ar" />
                </Field>
              </div>
              <div className="flex flex-wrap gap-4 text-sm">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={editingDimension.form.isHierarchical} onChange={(e) => { setDimensionForm({ isHierarchical: e.target.checked }); }} />
                  {t("dimensions.hierarchical")}
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={editingDimension.form.isActive} onChange={(e) => { setDimensionForm({ isActive: e.target.checked }); }} />
                  {t("dimensions.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditingDimension(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveDimension.isPending} data-testid="save-dimension">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editingValue)} onOpenChange={(isOpen) => { if (!isOpen) { setEditingValue(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {editingValue ? (
            <form onSubmit={submitValue} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editingValue.id ? t("dimensions.editValue") : t("dimensions.newValue")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("dimensions.code")} required error={problem?.fields.code}>
                  <TextField value={editingValue.form.code} onChange={(e) => { setValueForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="value-code" />
                </Field>
                <Field label={t("dimensions.company")} description={t("dimensions.companyHint")}>
                  <SelectField value={editingValue.form.companyId} onChange={(e) => { setValueForm({ companyId: e.target.value }); }}>
                    <option value="">{t("dimensions.allCompanies")}</option>
                    {(companies.data ?? []).map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.code} · {localized(c.legalName)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("dimensions.nameEn")} required error={problem?.fields.name}>
                  <TextField value={editingValue.form.nameEn} onChange={(e) => { setValueForm({ nameEn: e.target.value }); }} required data-testid="value-name-en" />
                </Field>
                <Field label={t("dimensions.nameAr")}>
                  <TextField value={editingValue.form.nameAr} onChange={(e) => { setValueForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                {selected?.isHierarchical ? (
                  <Field label={t("dimensions.parent")} error={problem?.fields.parentId}>
                    <SelectField value={editingValue.form.parentId} onChange={(e) => { setValueForm({ parentId: e.target.value }); }}>
                      <option value="">{t("dimensions.noParent")}</option>
                      {(values.data ?? []).filter((v) => v.id !== editingValue.id).map((v) => (
                        <option key={v.id} value={v.id}>
                          {v.code} · {localized(v.name)}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("dimensions.validFrom")}>
                  <TextField type="date" value={editingValue.form.validFrom} onChange={(e) => { setValueForm({ validFrom: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("dimensions.validTo")}>
                  <TextField type="date" value={editingValue.form.validTo} onChange={(e) => { setValueForm({ validTo: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={editingValue.form.isActive} onChange={(e) => { setValueForm({ isActive: e.target.checked }); }} />
                {t("dimensions.active")}
              </label>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditingValue(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveValue.isPending} data-testid="save-value">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
