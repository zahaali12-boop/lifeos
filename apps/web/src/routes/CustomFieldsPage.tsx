import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, SelectField, TextField } from "./common";
import { CustomFieldControl } from "./CustomFieldsFieldset";

type CustomField = components["schemas"]["CustomFieldView"];

/** The option values typed as a comma-separated list, each once, in the order typed. */
function optionValues(text: string): string[] {
  return [...new Set(text.split(",").map((o) => o.trim()).filter(Boolean))];
}

/** An option's labels as saved: the English label or the value itself, and the Arabic one when given. */
function optionLabel(value: string, labels: { en: string; ar: string } | undefined): Record<string, string> {
  const en = labels?.en.trim() ?? "";
  const ar = labels?.ar.trim() ?? "";
  return { en: en === "" ? value : en, ...(ar === "" ? {} : { ar }) };
}

/** Entity types that carry custom fields today; each host registers itself on the API as it lands. */
const types = ["text", "number", "date", "boolean", "select", "multi_select", "reference"] as const;

const emptyForm = { key: "", labelEn: "", labelAr: "", type: "text" as (typeof types)[number], required: false, indexed: false, options: "", min: "", max: "", maxLength: "", pattern: "", referenceType: "", defaultValue: undefined as unknown, helpEn: "", helpAr: "", optionLabels: {} as Record<string, { en: string; ar: string }>, position: "0", active: true };

export function CustomFieldsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [entityType, setEntityType] = useState("company");
  const hostList = useQuery({ queryKey: ["custom-field-hosts"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields/hosts")), staleTime: Infinity });
  const hosts = hostList.data?.entityTypes ?? ["company"];
  const [editing, setEditing] = useState<{ id: string | null; form: typeof emptyForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const fields = useQuery({ queryKey: ["custom-fields", entityType], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/custom-fields", { params: { query: { entityType } } })) });

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: typeof emptyForm }) => {
      const f = input.form;
      const body = {
        entityType,
        key: f.key,
        label: { en: f.labelEn, ...(f.labelAr ? { ar: f.labelAr } : {}) },
        type: f.type,
        required: f.required,
        indexed: f.indexed,
        options: f.type === "select" || f.type === "multi_select" ? optionValues(f.options).map((value) => ({ value, label: optionLabel(value, f.optionLabels[value]) })) : null,
        rules: { min: f.min ? Number(f.min) : null, max: f.max ? Number(f.max) : null, maxLength: f.maxLength ? Number(f.maxLength) : null, pattern: f.pattern || null, referenceType: f.referenceType || null },
        position: Number(f.position) || 0,
        active: f.active,
        description: f.helpEn || f.helpAr ? { ...(f.helpEn ? { en: f.helpEn } : {}), ...(f.helpAr ? { ar: f.helpAr } : {}) } : null,
        defaultValue: f.defaultValue ?? null,
      };
      return input.id ? unwrap(await api.PUT("/api/v1/collaboration/custom-fields/{fieldId}", { params: { path: { fieldId: input.id } }, body })) : unwrap(await api.POST("/api/v1/collaboration/custom-fields", { body }));
    },
    onSuccess: async () => {
      setEditing(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["custom-fields"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/collaboration/custom-fields/{fieldId}", { params: { path: { fieldId: id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["custom-fields"] }),
  });

  const columns = useMemo<ColumnDef<CustomField, unknown>[]>(
    () => [
      { id: "key", accessorKey: "key", header: t("customFields.key"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.key}</span> },
      { id: "label", accessorFn: (row) => localized(row.label), header: t("customFields.label"), size: 220 },
      { id: "type", accessorKey: "type", header: t("customFields.type"), size: 120 },
      { id: "required", accessorKey: "required", header: t("customFields.required"), size: 100, cell: ({ row }) => (row.original.required ? t("common.yes") : t("common.no")) },
      { id: "indexed", accessorKey: "indexed", header: t("customFields.indexed"), size: 100, cell: ({ row }) => (row.original.indexed ? <Badge tone="accent">{t("common.yes")}</Badge> : t("common.no")) },
      { id: "active", accessorKey: "active", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.active ? "success" : "neutral"}>{row.original.active ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );

  const openEdit = (field: CustomField): void => {
    setProblem(null);
    setEditing({
      id: field.id,
      form: {
        key: field.key,
        labelEn: field.label.en ?? "",
        labelAr: field.label.ar ?? "",
        type: field.type as (typeof types)[number],
        required: field.required,
        indexed: field.indexed,
        position: String(field.position),
        active: field.active,
        options: field.options.map((o) => o.value).join(", "),
        min: field.rules.min == null ? "" : String(field.rules.min),
        max: field.rules.max == null ? "" : String(field.rules.max),
        maxLength: field.rules.maxLength == null ? "" : String(field.rules.maxLength),
        pattern: field.rules.pattern ?? "",
        referenceType: field.rules.referenceType ?? "",
        defaultValue: field.defaultValue ?? undefined,
        helpEn: field.description.en ?? "",
        helpAr: field.description.ar ?? "",
        optionLabels: Object.fromEntries(field.options.map((o) => [o.value, { en: o.label.en ?? "", ar: o.label.ar ?? "" }])),
      },
    });
  };

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const form = editing?.form;
  const isEdit = editing?.id != null;
  const setForm = (patch: Partial<typeof emptyForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };

  return (
    <>
      <PageHeader
        title={t("nav.customFields")}
        description={t("customFields.description")}
        actions={
          <>
            <SelectField value={entityType} onChange={(e) => { setEntityType(e.target.value); }} aria-label={t("customFields.entityType")} className="w-56" data-testid="custom-field-host">
              {hosts.map((host) => (
                <option key={host} value={host}>
                  {t(`customFields.hosts.${host}`, { defaultValue: host })}
                </option>
              ))}
            </SelectField>
            <Button onClick={() => { setProblem(null); setEditing({ id: null, form: emptyForm }); }} data-testid="new-custom-field">
              <Plus aria-hidden="true" />
              {t("customFields.new")}
            </Button>
          </>
        }
      />
      <DataGrid<CustomField>
        label="nav.customFields"
        columns={columns}
        data={fields.data ?? []}
        rowKey={(row) => row.id}
        loading={fields.isPending}
        onOpen={openEdit}
        selectable
        emptyTitle={t("customFields.emptyTitle")}
        emptyDescription={t("customFields.emptyDescription")}
        bulkActions={(selected, clear) => (
          <Button size="sm" variant="danger" onClick={() => { selected.forEach((id) => { remove.mutate(id); }); clear(); }}>
            {t("common.delete")}
          </Button>
        )}
      />
      <Dialog open={editing !== null} onOpenChange={(open) => { if (!open) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="sm:max-w-2xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{isEdit ? t("customFields.edit") : t("customFields.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("customFields.key")} required description={t("customFields.keyHint")} error={problem?.fields.key}>
                  <TextField value={form.key} onChange={(e) => { setForm({ key: e.target.value }); }} required disabled={isEdit} dir="ltr" pattern="[a-z][a-z0-9_]{0,39}" />
                </Field>
                <Field label={t("customFields.type")} required error={problem?.fields.type}>
                  <SelectField value={form.type} onChange={(e) => { setForm({ type: e.target.value as (typeof types)[number], defaultValue: undefined }); }} disabled={isEdit}>
                    {types.map((type) => (
                      <option key={type} value={type}>
                        {t(`customFields.types.${type}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("customFields.labelEn")} required error={problem?.fields.label}>
                  <TextField value={form.labelEn} onChange={(e) => { setForm({ labelEn: e.target.value }); }} required />
                </Field>
                <Field label={t("customFields.labelAr")}>
                  <TextField value={form.labelAr} onChange={(e) => { setForm({ labelAr: e.target.value }); }} dir="rtl" />
                </Field>
                {form.type === "select" || form.type === "multi_select" ? (
                  <Field label={t("customFields.options")} required description={t("customFields.optionsHint")} error={problem?.fields.options} className="sm:col-span-2">
                    <TextField value={form.options} onChange={(e) => { setForm({ options: e.target.value }); }} required dir="ltr" />
                  </Field>
                ) : null}
                {(form.type === "select" || form.type === "multi_select") && optionValues(form.options).length > 0 ? (
                  <fieldset className="grid gap-2 sm:col-span-2" data-testid="option-labels">
                    <legend className="mb-1 text-sm font-medium">{t("customFields.optionLabels")}</legend>
                    {optionValues(form.options).map((value) => (
                      <div key={value} className="grid items-center gap-2 sm:grid-cols-[8rem_1fr_1fr]">
                        <span className="text-sm" dir="ltr">{value}</span>
                        <TextField aria-label={t("customFields.optionLabelEn", { value })} placeholder={value} value={form.optionLabels[value]?.en ?? ""} onChange={(e) => { setForm({ optionLabels: { ...form.optionLabels, [value]: { en: e.target.value, ar: form.optionLabels[value]?.ar ?? "" } } }); }} />
                        <TextField aria-label={t("customFields.optionLabelAr", { value })} value={form.optionLabels[value]?.ar ?? ""} onChange={(e) => { setForm({ optionLabels: { ...form.optionLabels, [value]: { en: form.optionLabels[value]?.en ?? "", ar: e.target.value } } }); }} dir="rtl" data-testid={`option-ar-${value}`} />
                      </div>
                    ))}
                  </fieldset>
                ) : null}
                <Field label={t("customFields.helpEn")}>
                  <TextField value={form.helpEn} onChange={(e) => { setForm({ helpEn: e.target.value }); }} />
                </Field>
                <Field label={t("customFields.helpAr")}>
                  <TextField value={form.helpAr} onChange={(e) => { setForm({ helpAr: e.target.value }); }} dir="rtl" />
                </Field>
                {form.type === "number" ? (
                  <>
                    <Field label={t("customFields.min")} error={problem?.fields.rules}>
                      <TextField inputMode="decimal" value={form.min} onChange={(e) => { setForm({ min: e.target.value }); }} dir="ltr" />
                    </Field>
                    <Field label={t("customFields.max")}>
                      <TextField inputMode="decimal" value={form.max} onChange={(e) => { setForm({ max: e.target.value }); }} dir="ltr" />
                    </Field>
                  </>
                ) : null}
                {form.type === "text" ? (
                  <>
                    <Field label={t("customFields.maxLength")}>
                      <TextField inputMode="numeric" value={form.maxLength} onChange={(e) => { setForm({ maxLength: e.target.value }); }} dir="ltr" />
                    </Field>
                    <Field label={t("customFields.pattern")} error={problem?.fields.rules}>
                      <TextField value={form.pattern} onChange={(e) => { setForm({ pattern: e.target.value }); }} dir="ltr" />
                    </Field>
                  </>
                ) : null}
                {form.type === "reference" ? (
                  <Field label={t("customFields.referenceType")} required error={problem?.fields.rules}>
                    <TextField value={form.referenceType} onChange={(e) => { setForm({ referenceType: e.target.value }); }} required dir="ltr" />
                  </Field>
                ) : null}
                <Field label={t("customFields.defaultValue")} description={t("customFields.defaultValueHint")} error={problem?.fields[`customFields.${form.key}`]} className="sm:col-span-2">
                  <CustomFieldControl
                    definition={{
                      id: "default",
                      entityType,
                      key: "default_value",
                      label: { en: t("customFields.defaultValue") },
                      description: {},
                      type: form.type,
                      required: false,
                      options: optionValues(form.options).map((value) => ({ value, label: optionLabel(value, form.optionLabels[value]) })),
                      rules: { maxLength: form.maxLength ? Number(form.maxLength) : null, referenceType: form.referenceType || null },
                      indexed: false,
                      position: 0,
                      active: true,
                      createdAt: "",
                      updatedAt: "",
                    }}
                    value={form.defaultValue}
                    onChange={(next) => { setForm({ defaultValue: next }); }}
                  />
                </Field>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.required} onChange={(e) => { setForm({ required: e.target.checked }); }} />
                  {t("customFields.required")}
                </label>
                <Field label={t("customFields.position")} description={t("customFields.positionHint")}>
                  <TextField type="number" inputMode="numeric" value={form.position} onChange={(e) => { setForm({ position: e.target.value }); }} dir="ltr" className="w-28" data-testid="custom-field-position" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.active} onChange={(e) => { setForm({ active: e.target.checked }); }} data-testid="custom-field-active" />
                  {t("customFields.activeHint")}
                </label>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.indexed} onChange={(e) => { setForm({ indexed: e.target.checked }); }} />
                  {t("customFields.indexedHint")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-custom-field">
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
