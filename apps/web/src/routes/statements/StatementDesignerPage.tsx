import { Button, useFieldControl } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { ArrowDown, ArrowUp, Play, Plus, Save, Trash, X } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { accountTypes, columnTypes, derivedColumnTypes, layoutKinds, rowTypes, StatementTable, useLayouts, type Layout, type LayoutColumn, type LayoutRow } from "./shared";

interface RowDraft {
  key: number;
  code: string;
  rowType: string;
  labelEn: string;
  labelAr: string;
  types: string[];
  subtypes: string[];
  categories: string[];
  roles: string[];
  codeFrom: string;
  codeTo: string;
  accountCodes: string;
  excludeSubtypes: string[];
  excludeCategories: string[];
  formula: string;
  flipSign: boolean;
  indent: string;
  bold: boolean;
  showAccounts: boolean;
  hideWhenZero: boolean;
}

interface ColumnDraft {
  key: number;
  code: string;
  columnType: string;
  labelEn: string;
  labelAr: string;
  of: string;
  against: string;
  from: string;
  to: string;
  currencyMode: string;
}

interface Draft {
  id: string | null;
  code: string;
  kind: string;
  nameEn: string;
  nameAr: string;
  descriptionEn: string;
  descriptionAr: string;
  everyCompany: boolean;
  isPublished: boolean;
  templateCode: string | null;
  sortOrder: number;
  rows: RowDraft[];
  columns: ColumnDraft[];
}

let nextKey = 1;
const key = (): number => nextKey++;

const emptyRow = (): RowDraft => ({ key: key(), code: "", rowType: "accounts", labelEn: "", labelAr: "", types: [], subtypes: [], categories: [], roles: [], codeFrom: "", codeTo: "", accountCodes: "", excludeSubtypes: [], excludeCategories: [], formula: "", flipSign: false, indent: "0", bold: false, showAccounts: false, hideWhenZero: false });
const emptyColumn = (): ColumnDraft => ({ key: key(), code: "", columnType: "period", labelEn: "", labelAr: "", of: "", against: "", from: "", to: "", currencyMode: "functional" });
const emptyDraft = (): Draft => ({ id: null, code: "", kind: "management", nameEn: "", nameAr: "", descriptionEn: "", descriptionAr: "", everyCompany: true, isPublished: true, templateCode: null, sortOrder: 0, rows: [emptyRow()], columns: [{ ...emptyColumn(), code: "PERIOD", labelEn: "Period", labelAr: "الفترة" }] });

function rowOf(r: LayoutRow): RowDraft {
  const a = r.accounts ?? null;
  return {
    key: key(),
    code: r.code,
    rowType: r.rowType,
    labelEn: r.label.en ?? "",
    labelAr: r.label.ar ?? "",
    types: a?.types ?? [],
    subtypes: a?.subtypes ?? [],
    categories: a?.categories ?? [],
    roles: a?.roles ?? [],
    codeFrom: a?.codeFrom ?? "",
    codeTo: a?.codeTo ?? "",
    accountCodes: (a?.accountCodes ?? []).join(", "),
    excludeSubtypes: a?.excludeSubtypes ?? [],
    excludeCategories: a?.excludeCategories ?? [],
    formula: r.formula ?? "",
    flipSign: r.flipSign,
    indent: String(r.indent),
    bold: r.bold,
    showAccounts: r.showAccounts,
    hideWhenZero: r.hideWhenZero,
  };
}

function columnOf(c: LayoutColumn): ColumnDraft {
  return { key: key(), code: c.code, columnType: c.columnType, labelEn: c.label.en ?? "", labelAr: c.label.ar ?? "", of: c.parameters?.of ?? "", against: c.parameters?.against ?? "", from: c.parameters?.from ?? "", to: c.parameters?.to ?? "", currencyMode: c.currencyMode };
}

/** A shipped layout opens as a copy to customise; a tenant layout opens as itself. */
function draftOf(layout: Layout, asCopy: boolean): Draft {
  return {
    id: asCopy ? null : layout.id,
    code: asCopy ? `${layout.code}_1` : layout.code,
    kind: layout.kind,
    nameEn: layout.name.en ?? "",
    nameAr: layout.name.ar ?? "",
    descriptionEn: layout.description.en ?? "",
    descriptionAr: layout.description.ar ?? "",
    everyCompany: layout.companyId === null,
    isPublished: layout.isPublished,
    templateCode: asCopy ? layout.code : layout.templateCode,
    sortOrder: asCopy ? 0 : Number(layout.sortOrder),
    rows: layout.definition.rows.map(rowOf),
    columns: layout.definition.columns.map(columnOf),
  };
}

const list = (values: string[]): string[] | null => (values.length > 0 ? values : null);

function definitionOf(draft: Draft): components["schemas"]["LayoutDefinition"] {
  return {
    rows: draft.rows.map((r) => ({
      code: r.code.trim().toUpperCase(),
      rowType: r.rowType,
      label: { en: r.labelEn, ar: r.labelAr },
      accounts: r.rowType === "accounts" ? {
        types: list(r.types),
        subtypes: list(r.subtypes),
        categories: list(r.categories),
        roles: list(r.roles),
        codeFrom: r.codeFrom.trim() || null,
        codeTo: r.codeTo.trim() || null,
        accountCodes: list(r.accountCodes.split(",").map((c) => c.trim()).filter(Boolean)),
        excludeSubtypes: list(r.excludeSubtypes),
        excludeCategories: list(r.excludeCategories),
      } : null,
      formula: r.rowType === "formula" ? r.formula : null,
      flipSign: r.flipSign,
      indent: Number(r.indent) || 0,
      bold: r.bold,
      showAccounts: r.showAccounts,
      hideWhenZero: r.hideWhenZero,
    })),
    columns: draft.columns.map((c) => ({
      code: c.code.trim().toUpperCase(),
      columnType: c.columnType,
      label: { en: c.labelEn, ar: c.labelAr },
      parameters: { of: c.of || null, against: c.against || null, from: c.from || null, to: c.to || null },
      currencyMode: c.currencyMode,
    })),
  };
}

function MultiSelect({ label, values, options, onChange, testId, render }: { label: string; values: string[]; options: string[]; onChange: (next: string[]) => void; testId?: string; render?: (value: string) => string }) {
  return (
    <Field label={label}>
      <MultiSelectControl values={values} options={options} onChange={onChange} testId={testId} render={render} />
    </Field>
  );
}

/** The list itself, wired to the enclosing Field's label and description like the single-value fields are. */
function MultiSelectControl({ values, options, onChange, testId, render }: { values: string[]; options: string[]; onChange: (next: string[]) => void; testId?: string | undefined; render?: ((value: string) => string) | undefined }) {
  const control = useFieldControl();
  return (
    <select {...control} multiple value={values} onChange={(e) => { onChange([...e.target.selectedOptions].map((o) => o.value)); }} className="min-h-20 w-full rounded-md border border-border bg-surface px-2 py-1 text-sm" data-testid={testId}>
      {options.map((o) => <option key={o} value={o}>{render ? render(o) : o}</option>)}
    </select>
  );
}

/** The statement designer (founder programme item 9, slice 9c): rows of headers, account selections and formulas, columns of periods and comparisons, previewed before it is saved and published. */
export function StatementDesignerPage() {
  const search = useSearch({ strict: false });
  return <Designer key={`${search.layout ?? ""}:${search.template ?? ""}`} layoutId={search.layout ?? null} templateCode={search.template ?? null} />;
}

function Designer({ layoutId, templateCode }: { layoutId: string | null; templateCode: string | null }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const layouts = useLayouts(companyId);
  const options = useQuery({
    queryKey: ["statement-layout-options", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/reporting/statements/layouts/options", { params: { query: { companyId } } })),
  });
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [loaded, setLoaded] = useState(false);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [preview, setPreview] = useState<components["schemas"]["StatementDesignPreview"] | null>(null);
  const [previewTo, setPreviewTo] = useState("");
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  // The layout to edit comes from the list (a tenant's by id, a shipped one by code as a copy), once.
  useEffect(() => {
    if (loaded || !layouts.data) {
      return;
    }
    const source = layoutId ? layouts.data.find((l) => l.id === layoutId) : templateCode ? layouts.data.find((l) => l.isSystem && l.code === templateCode) : null;
    if (source) {
      setDraft(draftOf(source, source.isSystem));
    }
    setLoaded(true);
  }, [layouts.data, layoutId, templateCode, loaded]);

  const rowCodes = draft.rows.map((r) => r.code.trim().toUpperCase()).filter(Boolean);
  const baseColumns = draft.columns.filter((c) => !(derivedColumnTypes as readonly string[]).includes(c.columnType)).map((c) => c.code.trim().toUpperCase()).filter(Boolean);
  const categoryName = useMemo(() => new Map((options.data?.categories ?? []).map((c) => [c.code, localized(c.name)])), [options.data]);

  const setRow = (k: number, patch: Partial<RowDraft>): void => { setDraft({ ...draft, rows: draft.rows.map((r) => (r.key === k ? { ...r, ...patch } : r)) }); };
  const setColumn = (k: number, patch: Partial<ColumnDraft>): void => { setDraft({ ...draft, columns: draft.columns.map((c) => (c.key === k ? { ...c, ...patch } : c)) }); };
  const move = <T,>(items: T[], index: number, delta: number): T[] => {
    const next = [...items];
    const target = index + delta;
    if (target < 0 || target >= next.length) {
      return next;
    }
    const [item] = next.splice(index, 1);
    next.splice(target, 0, item as T);
    return next;
  };

  const body = (): components["schemas"]["SaveLayoutRequest"] => ({
    code: draft.code.trim().toUpperCase(),
    kind: draft.kind,
    name: { en: draft.nameEn, ar: draft.nameAr },
    description: { en: draft.descriptionEn, ar: draft.descriptionAr },
    companyId: draft.everyCompany ? null : companyId,
    isPublished: draft.isPublished,
    templateCode: draft.templateCode,
    sortOrder: draft.sortOrder,
    definition: definitionOf(draft),
  });
  const save = useMutation({
    mutationFn: async () => (draft.id
      ? unwrap(await api.PUT("/api/v1/reporting/statements/layouts/{layoutId}", { params: { path: { layoutId: draft.id } }, body: body() }))
      : unwrap(await api.POST("/api/v1/reporting/statements/layouts", { body: body() }))),
    onSuccess: async (saved) => {
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["statement-layouts"] });
      void navigate({ to: "/statements", search: { layout: saved.id ?? "" } });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const remove = useMutation({
    mutationFn: async () => {
      if (draft.id) {
        unwrap(await api.DELETE("/api/v1/reporting/statements/layouts/{layoutId}", { params: { path: { layoutId: draft.id } } }));
      }
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["statement-layouts"] });
      void navigate({ to: "/statements", search: {} });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const previewQuery = useQuery({
    queryKey: ["statement-preview", preview],
    enabled: preview !== null,
    retry: false,
    queryFn: async () => unwrap(await api.POST("/api/v1/reporting/statements/preview", { body: preview ?? { request: { companyId }, kind: draft.kind, definition: definitionOf(draft) } })),
  });
  const previewProblem = previewQuery.error ? toFormProblem(previewQuery.error, t("statements.runFailed")) : null;
  const runPreview = (): void => { setProblem(null); setExpanded(new Set()); setPreview({ request: { companyId, ...(previewTo ? { to: previewTo } : {}) }, kind: draft.kind, definition: definitionOf(draft) }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const toggle = (code: string): void => {
    setExpanded((current) => { const next = new Set(current); if (next.has(code)) { next.delete(code); } else { next.add(code); } return next; });
  };

  return (
    <div>
      <PageHeader
        title={draft.id ? t("statements.editLayout") : t("statements.newLayout")}
        description={t("statements.designerDescription")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { void navigate({ to: "/statements", search: {} }); }} data-testid="back-to-statements">{t("nav.statements")}</Button>
            <Button variant="secondary" onClick={runPreview} disabled={!companyId} data-testid="preview-layout"><Play aria-hidden="true" /> {t("statements.preview")}</Button>
            {draft.id ? <Button variant="danger" onClick={() => { if (window.confirm(t("statements.confirmDelete"))) { remove.mutate(); } }} loading={remove.isPending} data-testid="delete-layout"><Trash aria-hidden="true" /> {t("common.delete")}</Button> : null}
            <Button onClick={() => { save.mutate(); }} loading={save.isPending} data-testid="save-layout"><Save aria-hidden="true" /> {t("common.save")}</Button>
          </>
        }
      />
      <FormError message={problem?.message ?? previewProblem?.message ?? null} />
      <form onSubmit={submit} className="flex flex-col gap-4">
        <div className="grid gap-4 rounded-md border border-border bg-surface p-3 sm:grid-cols-2 lg:grid-cols-4">
          <Field label={t("statements.code")} required error={problem?.fields.code}>
            <TextField value={draft.code} onChange={(e) => { setDraft({ ...draft, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="layout-code" />
          </Field>
          <Field label={t("statements.kind")}>
            <SelectField value={draft.kind} onChange={(e) => { setDraft({ ...draft, kind: e.target.value }); }} data-testid="layout-kind">
              {layoutKinds.map((k) => <option key={k} value={k}>{t(`statements.kinds.${k}`)}</option>)}
            </SelectField>
          </Field>
          <Field label={t("reports.nameEn")} required>
            <TextField value={draft.nameEn} onChange={(e) => { setDraft({ ...draft, nameEn: e.target.value }); }} dir="ltr" required data-testid="layout-name-en" />
          </Field>
          <Field label={t("reports.nameAr")} required>
            <TextField value={draft.nameAr} onChange={(e) => { setDraft({ ...draft, nameAr: e.target.value }); }} dir="rtl" required data-testid="layout-name-ar" />
          </Field>
          <Field label={t("reports.descriptionEn")}>
            <TextField value={draft.descriptionEn} onChange={(e) => { setDraft({ ...draft, descriptionEn: e.target.value }); }} dir="ltr" />
          </Field>
          <Field label={t("reports.descriptionAr")}>
            <TextField value={draft.descriptionAr} onChange={(e) => { setDraft({ ...draft, descriptionAr: e.target.value }); }} dir="rtl" />
          </Field>
          <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
          <div className="flex flex-col justify-end gap-1 text-sm">
            <label className="flex items-center gap-2"><input type="checkbox" checked={draft.everyCompany} onChange={(e) => { setDraft({ ...draft, everyCompany: e.target.checked }); }} data-testid="layout-every-company" /> {t("statements.everyCompany")}</label>
            <label className="flex items-center gap-2"><input type="checkbox" checked={draft.isPublished} onChange={(e) => { setDraft({ ...draft, isPublished: e.target.checked }); }} data-testid="layout-published" /> {t("statements.published")}</label>
          </div>
        </div>

        <section aria-labelledby="rows-heading">
          <div className="mb-2 flex items-center justify-between">
            <h2 id="rows-heading" className="text-base font-semibold">{t("statements.rows")}</h2>
            <Button variant="secondary" size="sm" onClick={() => { setDraft({ ...draft, rows: [...draft.rows, emptyRow()] }); }} data-testid="add-row"><Plus aria-hidden="true" /> {t("statements.addRow")}</Button>
          </div>
          <div className="flex flex-col gap-2">
            {draft.rows.map((row, index) => (
              <div key={row.key} className="rounded-md border border-border bg-surface p-3" data-testid={`row-${index + 1}`}>
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-6">
                  <Field label={t("statements.code")} required>
                    <TextField value={row.code} onChange={(e) => { setRow(row.key, { code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid={`row-${index + 1}-code`} />
                  </Field>
                  <Field label={t("statements.rowType")}>
                    <SelectField value={row.rowType} onChange={(e) => { setRow(row.key, { rowType: e.target.value }); }} data-testid={`row-${index + 1}-type`}>
                      {rowTypes.map((r) => <option key={r} value={r}>{t(`statements.rowTypes.${r}`)}</option>)}
                    </SelectField>
                  </Field>
                  {row.rowType !== "blank" ? (
                    <>
                      <Field label={t("reports.labelEn")}>
                        <TextField value={row.labelEn} onChange={(e) => { setRow(row.key, { labelEn: e.target.value }); }} dir="ltr" data-testid={`row-${index + 1}-label-en`} />
                      </Field>
                      <Field label={t("reports.labelAr")}>
                        <TextField value={row.labelAr} onChange={(e) => { setRow(row.key, { labelAr: e.target.value }); }} dir="rtl" data-testid={`row-${index + 1}-label-ar`} />
                      </Field>
                    </>
                  ) : null}
                  <div className="flex items-end gap-1 lg:col-start-6">
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, rows: move(draft.rows, index, -1) }); }} aria-label={t("statements.moveUp")}><ArrowUp aria-hidden="true" /></Button>
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, rows: move(draft.rows, index, 1) }); }} aria-label={t("statements.moveDown")}><ArrowDown aria-hidden="true" /></Button>
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, rows: draft.rows.filter((r) => r.key !== row.key) }); }} aria-label={t("statements.removeRow")} data-testid={`row-${index + 1}-remove`}><X aria-hidden="true" /></Button>
                  </div>
                </div>
                {row.rowType === "formula" ? (
                  <div className="mt-3">
                    <Field label={t("reports.expression")} required description={t("statements.formulaHelp", { rows: rowCodes.filter((c) => c !== row.code.trim().toUpperCase()).join(", ") || "—" })}>
                      <TextField value={row.formula} onChange={(e) => { setRow(row.key, { formula: e.target.value }); }} dir="ltr" className="font-mono" required data-testid={`row-${index + 1}-formula`} />
                    </Field>
                  </div>
                ) : null}
                {row.rowType === "accounts" ? (
                  <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                    <fieldset>
                      <legend className="text-sm font-medium">{t("statements.accountTypes")}</legend>
                      <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-sm">
                        {accountTypes.map((type) => (
                          <label key={type} className="flex items-center gap-1">
                            <input type="checkbox" checked={row.types.includes(type)} onChange={(e) => { setRow(row.key, { types: e.target.checked ? [...row.types, type] : row.types.filter((x) => x !== type) }); }} data-testid={`row-${index + 1}-type-${type}`} />
                            {t(`statements.accountTypeNames.${type}`)}
                          </label>
                        ))}
                      </div>
                    </fieldset>
                    <MultiSelect label={t("statements.subtypes")} values={row.subtypes} options={options.data?.subtypes ?? []} onChange={(next) => { setRow(row.key, { subtypes: next }); }} testId={`row-${index + 1}-subtypes`} />
                    <MultiSelect label={t("statements.categories")} values={row.categories} options={(options.data?.categories ?? []).map((c) => c.code)} onChange={(next) => { setRow(row.key, { categories: next }); }} render={(c) => categoryName.get(c) ?? c} testId={`row-${index + 1}-categories`} />
                    <MultiSelect label={t("statements.roles")} values={row.roles} options={options.data?.roles ?? []} onChange={(next) => { setRow(row.key, { roles: next }); }} />
                    <MultiSelect label={t("statements.excludeSubtypes")} values={row.excludeSubtypes} options={options.data?.subtypes ?? []} onChange={(next) => { setRow(row.key, { excludeSubtypes: next }); }} />
                    <MultiSelect label={t("statements.excludeCategories")} values={row.excludeCategories} options={(options.data?.categories ?? []).map((c) => c.code)} onChange={(next) => { setRow(row.key, { excludeCategories: next }); }} render={(c) => categoryName.get(c) ?? c} />
                    <Field label={t("statements.codeRange")}>
                      <div className="flex items-center gap-1">
                        <TextField value={row.codeFrom} onChange={(e) => { setRow(row.key, { codeFrom: e.target.value }); }} dir="ltr" placeholder={t("reports.from")} />
                        <TextField value={row.codeTo} onChange={(e) => { setRow(row.key, { codeTo: e.target.value }); }} dir="ltr" placeholder={t("reports.to")} />
                      </div>
                    </Field>
                    <Field label={t("statements.accountCodes")} description={t("reports.listHint")}>
                      <TextField value={row.accountCodes} onChange={(e) => { setRow(row.key, { accountCodes: e.target.value }); }} dir="ltr" />
                    </Field>
                  </div>
                ) : null}
                {row.rowType !== "blank" ? (
                  <div className="mt-3 flex flex-wrap items-end gap-4 text-sm">
                    {row.rowType === "accounts" ? <label className="flex items-center gap-2"><input type="checkbox" checked={row.flipSign} onChange={(e) => { setRow(row.key, { flipSign: e.target.checked }); }} data-testid={`row-${index + 1}-flip`} /> {t("statements.flipSign")}</label> : null}
                    {row.rowType === "accounts" ? <label className="flex items-center gap-2"><input type="checkbox" checked={row.showAccounts} onChange={(e) => { setRow(row.key, { showAccounts: e.target.checked }); }} /> {t("statements.showAccounts")}</label> : null}
                    <label className="flex items-center gap-2"><input type="checkbox" checked={row.bold} onChange={(e) => { setRow(row.key, { bold: e.target.checked }); }} data-testid={`row-${index + 1}-bold`} /> {t("statements.bold")}</label>
                    {row.rowType !== "header" ? <label className="flex items-center gap-2"><input type="checkbox" checked={row.hideWhenZero} onChange={(e) => { setRow(row.key, { hideWhenZero: e.target.checked }); }} /> {t("statements.hideWhenZero")}</label> : null}
                    <Field label={t("statements.indent")}>
                      <TextField type="number" inputMode="numeric" min={0} max={6} value={row.indent} onChange={(e) => { setRow(row.key, { indent: e.target.value }); }} dir="ltr" className="w-20" />
                    </Field>
                  </div>
                ) : null}
              </div>
            ))}
          </div>
        </section>

        <section aria-labelledby="columns-heading">
          <div className="mb-2 flex items-center justify-between">
            <h2 id="columns-heading" className="text-base font-semibold">{t("statements.columns")}</h2>
            <Button variant="secondary" size="sm" onClick={() => { setDraft({ ...draft, columns: [...draft.columns, emptyColumn()] }); }} data-testid="add-column"><Plus aria-hidden="true" /> {t("statements.addColumn")}</Button>
          </div>
          <div className="flex flex-col gap-2">
            {draft.columns.map((column, index) => {
              const derived = (derivedColumnTypes as readonly string[]).includes(column.columnType);
              return (
                <div key={column.key} className="grid gap-3 rounded-md border border-border bg-surface p-3 sm:grid-cols-2 lg:grid-cols-7" data-testid={`column-${index + 1}`}>
                  <Field label={t("statements.code")} required>
                    <TextField value={column.code} onChange={(e) => { setColumn(column.key, { code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid={`column-${index + 1}-code`} />
                  </Field>
                  <Field label={t("statements.columnType")}>
                    <SelectField value={column.columnType} onChange={(e) => { setColumn(column.key, { columnType: e.target.value }); }} data-testid={`column-${index + 1}-type`}>
                      {columnTypes.map((c) => <option key={c} value={c}>{t(`statements.columnTypes.${c}`)}</option>)}
                    </SelectField>
                  </Field>
                  <Field label={t("reports.labelEn")}>
                    <TextField value={column.labelEn} onChange={(e) => { setColumn(column.key, { labelEn: e.target.value }); }} dir="ltr" data-testid={`column-${index + 1}-label-en`} />
                  </Field>
                  <Field label={t("reports.labelAr")}>
                    <TextField value={column.labelAr} onChange={(e) => { setColumn(column.key, { labelAr: e.target.value }); }} dir="rtl" data-testid={`column-${index + 1}-label-ar`} />
                  </Field>
                  {derived ? (
                    <>
                      <Field label={t("statements.of")}>
                        <SelectField value={column.of} onChange={(e) => { setColumn(column.key, { of: e.target.value }); }} data-testid={`column-${index + 1}-of`}>
                          <option value="">—</option>
                          {baseColumns.map((c) => <option key={c} value={c}>{c}</option>)}
                        </SelectField>
                      </Field>
                      <Field label={t("statements.against")}>
                        <SelectField value={column.against} onChange={(e) => { setColumn(column.key, { against: e.target.value }); }} data-testid={`column-${index + 1}-against`}>
                          <option value="">—</option>
                          {baseColumns.map((c) => <option key={c} value={c}>{c}</option>)}
                        </SelectField>
                      </Field>
                    </>
                  ) : column.columnType === "custom_range" ? (
                    <>
                      <Field label={t("reports.from")}>
                        <TextField type="date" value={column.from} onChange={(e) => { setColumn(column.key, { from: e.target.value }); }} dir="ltr" />
                      </Field>
                      <Field label={t("reports.to")}>
                        <TextField type="date" value={column.to} onChange={(e) => { setColumn(column.key, { to: e.target.value }); }} dir="ltr" />
                      </Field>
                    </>
                  ) : (
                    <Field label={t("statements.currency")}>
                      <SelectField value={column.currencyMode} onChange={(e) => { setColumn(column.key, { currencyMode: e.target.value }); }}>
                        <option value="functional">{t("statements.functionalCurrency")}</option>
                        <option value="reporting">{t("statements.reportingCurrency")}</option>
                      </SelectField>
                    </Field>
                  )}
                  <div className="flex items-end gap-1 lg:col-start-7">
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, columns: move(draft.columns, index, -1) }); }} aria-label={t("statements.moveUp")}><ArrowUp aria-hidden="true" /></Button>
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, columns: move(draft.columns, index, 1) }); }} aria-label={t("statements.moveDown")}><ArrowDown aria-hidden="true" /></Button>
                    <Button variant="secondary" size="icon" onClick={() => { setDraft({ ...draft, columns: draft.columns.filter((c) => c.key !== column.key) }); }} aria-label={t("statements.removeColumn")} data-testid={`column-${index + 1}-remove`}><X aria-hidden="true" /></Button>
                  </div>
                </div>
              );
            })}
          </div>
        </section>

        <section aria-labelledby="preview-heading" className="rounded-md border border-border bg-surface p-3">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <h2 id="preview-heading" className="text-base font-semibold">{t("statements.preview")}</h2>
            <div className="flex items-end gap-2">
              <Field label={t("statements.previewTo")}>
                <TextField type="date" value={previewTo} onChange={(e) => { setPreviewTo(e.target.value); }} dir="ltr" data-testid="preview-to" />
              </Field>
              <Button type="button" variant="secondary" onClick={runPreview} disabled={!companyId} data-testid="run-preview"><Play aria-hidden="true" /> {t("reports.run")}</Button>
            </div>
          </div>
          {previewQuery.data ? (
            <div className="mt-3">
              <StatementTable statement={previewQuery.data} expanded={expanded} onToggle={toggle} testId="preview-table" />
            </div>
          ) : preview && previewQuery.isPending ? <p className="mt-3 text-sm text-fg-muted">{t("common.loading")}</p> : <p className="mt-3 text-sm text-fg-muted">{t("statements.previewHint")}</p>}
        </section>
      </form>
    </div>
  );
}
