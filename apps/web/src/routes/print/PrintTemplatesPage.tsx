import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Copy, History, Save } from "lucide-react";
import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { documentTypeLabel, languageModes, papers, useBranches, useDocumentTypes, usePrintTemplates, type PrintTemplate } from "./shared";

interface TemplateForm {
  nameEn: string;
  nameAr: string;
  languageMode: string;
  paper: string;
  companyId: string;
  branchId: string;
  isDefault: boolean;
  isActive: boolean;
  html: string;
  css: string;
  note: string;
}

interface CopyForm {
  code: string;
  nameEn: string;
  nameAr: string;
  companyId: string;
  branchId: string;
  languageMode: string;
}

const formOf = (tpl: PrintTemplate): TemplateForm => ({
  nameEn: tpl.name.en ?? "", nameAr: tpl.name.ar ?? "", languageMode: tpl.languageMode, paper: tpl.paper, companyId: tpl.companyId ?? "", branchId: tpl.branchId ?? "",
  isDefault: tpl.isDefault, isActive: tpl.isActive, html: tpl.html ?? "", css: tpl.css ?? "", note: "",
});

/**
 * Print templates (roadmap 5.9, ADR-0022): the shipped layouts of every document type and the tenant's copies of
 * them, by company or branch; the HTML and CSS edited in place (every edit a new version), previewed with sample
 * data, rolled back to an earlier version, and copied for a company or a branch.
 */
export function PrintTemplatesPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies } = useCompanyContext();
  const [documentType, setDocumentType] = useState("");
  const [openId, setOpenId] = useOpenRecord("/print-templates");
  const [tab, setTab] = useState("design");
  const [form, setForm] = useState<TemplateForm | null>(null);
  const [copying, setCopying] = useState<CopyForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const types = useDocumentTypes();
  const list = usePrintTemplates(documentType);
  const canManage = can("print.template.manage");
  const detail = useQuery({
    queryKey: ["print-template", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/print/templates/{templateId}", { params: { path: { templateId: openId ?? "" } } })),
  });
  const versions = useQuery({
    queryKey: ["print-template-versions", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/print/templates/{templateId}/versions", { params: { path: { templateId: openId ?? "" } } })),
  });
  const preview = useQuery({
    queryKey: ["print-template-preview", openId, detail.data?.currentVersion],
    enabled: Boolean(openId) && tab === "preview",
    queryFn: async () => unwrap(await api.GET("/api/v1/print/templates/{templateId}/preview", { params: { path: { templateId: openId ?? "" } }, parseAs: "text" })),
    retry: false,
  });
  const branches = useBranches(form?.companyId ?? copying?.companyId ?? "");
  useEffect(() => {
    if (detail.data) {
      setForm(formOf(detail.data));
    }
  }, [detail.data]);
  const refresh = async (): Promise<void> => {
    await Promise.all([["print-templates"], ["print-template"], ["print-template-versions"], ["print-template-preview"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async (f: TemplateForm) => {
      const current = detail.data;
      const body = {
        name: { en: f.nameEn, ar: f.nameAr },
        languageMode: f.languageMode,
        paper: f.paper,
        html: current && f.html !== (current.html ?? "") ? f.html : null,
        css: current && f.css !== (current.css ?? "") ? f.css : null,
        isDefault: f.isDefault,
        isActive: f.isActive,
        companyId: f.companyId || null,
        branchId: f.branchId || null,
        note: f.note || null,
      };
      return unwrap(await api.PUT("/api/v1/print/templates/{templateId}", { params: { path: { templateId: openId ?? "" } }, body }));
    },
    onSuccess: async () => { setProblem(null); setForm((f) => (f ? { ...f, note: "" } : f)); await refresh(); },
    onError: fail,
  });
  const rollback = useMutation({
    mutationFn: async (version: number) => unwrap(await api.POST("/api/v1/print/templates/{templateId}/rollback/{version}", { params: { path: { templateId: openId ?? "", version } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const copy = useMutation({
    mutationFn: async (c: CopyForm) => unwrap(await api.POST("/api/v1/print/templates/{templateId}/copy", { params: { path: { templateId: openId ?? "" } }, body: { code: c.code, name: c.nameEn ? { en: c.nameEn, ar: c.nameAr || c.nameEn } : null, companyId: c.companyId || null, branchId: c.branchId || null, languageMode: c.languageMode || null } })),
    onSuccess: async (created) => { setProblem(null); setCopying(null); await refresh(); setOpenId(created.id); setTab("design"); },
    onError: fail,
  });

  const scopeOf = useCallback((tpl: PrintTemplate): string => {
    if (tpl.branchId) {
      return t("print.templates.branchScope");
    }
    if (tpl.companyId) {
      return companies.find((c) => c.id === tpl.companyId)?.code ?? t("print.templates.companyScope");
    }
    return t("print.templates.allCompanies");
  }, [t, companies]);
  const columns = useMemo<ColumnDef<PrintTemplate, unknown>[]>(
    () => [
      { id: "documentType", accessorFn: (r) => documentTypeLabel(t, r.documentType), header: t("print.templates.documentType"), size: 160, cell: ({ row }) => documentTypeLabel(t, row.original.documentType) },
      { id: "variant", accessorFn: (r) => t(`print.variants.${r.variant}`, { defaultValue: r.variant }), header: t("print.variant"), size: 120 },
      { id: "code", accessorKey: "code", header: t("banking.code"), size: 170, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "languageMode", accessorFn: (r) => t(`print.languageModes.${r.languageMode}`), header: t("print.templates.languageMode"), size: 110 },
      { id: "paper", accessorKey: "paper", header: t("print.templates.paper"), size: 80 },
      { id: "scope", accessorFn: (r) => scopeOf(r), header: t("print.templates.scope"), size: 130 },
      { id: "default", accessorKey: "isDefault", header: t("print.templates.isDefault"), size: 90, cell: ({ row }) => (row.original.isDefault ? t("common.yes") : t("common.no")) },
      { id: "system", accessorKey: "isSystem", header: t("print.templates.system"), size: 90, cell: ({ row }) => (row.original.isSystem ? t("common.yes") : t("common.no")) },
      { id: "active", accessorKey: "isActive", header: t("common.active"), size: 80, cell: ({ row }) => (row.original.isActive ? t("common.yes") : t("common.no")) },
      { id: "version", accessorKey: "currentVersion", header: t("print.version"), size: 80, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.currentVersion)}</span> },
      { id: "updated", accessorKey: "updatedAt", header: t("print.templates.updated"), size: 150, cell: ({ row }) => <span dir="ltr">{formatDateTime(row.original.updatedAt)}</span> },
    ],
    [t, scopeOf],
  );

  const tpl = detail.data;
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const submitCopy = (event: FormEvent): void => { event.preventDefault(); if (copying) { copy.mutate(copying); } };
  const close = (): void => { setOpenId(null); setForm(null); setCopying(null); setProblem(null); setTab("design"); };
  const dirty = Boolean(tpl && form && (form.html !== (tpl.html ?? "") || form.css !== (tpl.css ?? "")));
  const previewError = preview.error ? toFormProblem(preview.error, t("print.previewFailed")).message : null;

  return (
    <>
      <PageHeader title={t("nav.printTemplates")} description={t("print.templates.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <Field label={t("print.templates.documentType")}>
          <SelectField value={documentType} onChange={(e) => { setDocumentType(e.target.value); }} data-testid="template-type-filter">
            <option value="">{t("print.templates.allTypes")}</option>
            {(types.data ?? []).map((d) => <option key={d.documentType} value={d.documentType}>{documentTypeLabel(t, d.documentType)}</option>)}
          </SelectField>
        </Field>
      </div>
      <DataGrid<PrintTemplate> label="nav.printTemplates" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending} emptyTitle={t("print.templates.emptyTitle")} emptyDescription={t("print.templates.emptyDescription")} onOpen={(row) => { setProblem(null); setTab("design"); setOpenId(row.id); }} exportable={false} />

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { close(); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-6xl" data-testid="template-detail">
          {tpl && form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">
                  <span dir="auto">{localized(tpl.name)}</span> <span className="text-sm font-normal text-fg-muted" dir="ltr">{tpl.code} · v{String(tpl.currentVersion)}</span>
                  {tpl.isSystem ? <Badge tone="info" className="ms-2">{t("print.templates.system")}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="flex flex-wrap items-center gap-2">
                {canManage ? (
                  <Button type="submit" loading={save.isPending} data-testid="save-template">
                    <Save aria-hidden="true" />
                    {dirty ? t("print.templates.saveVersion") : t("common.save")}
                  </Button>
                ) : null}
                {canManage ? (
                  <Button type="button" variant="secondary" onClick={() => { setProblem(null); setCopying({ code: `${tpl.code}-COPY`, nameEn: "", nameAr: "", companyId: "", branchId: "", languageMode: "" }); }} data-testid="copy-template">
                    <Copy aria-hidden="true" />
                    {t("print.templates.copy")}
                  </Button>
                ) : null}
                <span className="text-xs text-fg-muted">{t("print.templates.versionHelp")}</span>
              </div>
              <KeyValues entries={[
                [t("print.templates.documentType"), documentTypeLabel(t, tpl.documentType)],
                [t("print.variant"), t(`print.variants.${tpl.variant}`, { defaultValue: tpl.variant })],
                [t("print.templates.scope"), scopeOf(tpl)],
                [t("print.templates.updated"), <span key="u" dir="ltr">{formatDateTime(tpl.updatedAt)}</span>],
              ]} />
              <Tabs tabs={[{ id: "design", label: t("print.templates.design"), testId: "template-tab-design" }, { id: "settings", label: t("print.templates.settings"), testId: "template-tab-settings" }, { id: "preview", label: t("print.preview"), testId: "template-tab-preview" }, { id: "versions", label: t("print.templates.versions"), testId: "template-tab-versions" }]} value={tab} onChange={setTab} />
              {tab === "design" ? (
                <div className="grid gap-4 lg:grid-cols-[2fr_1fr]">
                  <Field label={t("print.templates.html")} description={t("print.templates.htmlHelp")}>
                    <TextareaField value={form.html} onChange={(e) => { setForm({ ...form, html: e.target.value }); }} rows={22} dir="ltr" spellCheck={false} className="font-mono text-xs" readOnly={!canManage} data-testid="template-html" />
                  </Field>
                  <div className="flex flex-col gap-3">
                    <Field label={t("print.templates.css")}>
                      <TextareaField value={form.css} onChange={(e) => { setForm({ ...form, css: e.target.value }); }} rows={16} dir="ltr" spellCheck={false} className="font-mono text-xs" readOnly={!canManage} data-testid="template-css" />
                    </Field>
                    <Field label={t("print.templates.note")} description={t("print.templates.noteHelp")}>
                      <TextField value={form.note} onChange={(e) => { setForm({ ...form, note: e.target.value }); }} dir="auto" readOnly={!canManage} data-testid="template-note" />
                    </Field>
                  </div>
                </div>
              ) : null}
              {tab === "settings" ? (
                <div className="grid gap-4 sm:grid-cols-2">
                  <Field label={t("banking.nameEn")} required>
                    <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} dir="ltr" required readOnly={!canManage} data-testid="template-name-en" />
                  </Field>
                  <Field label={t("banking.nameAr")} required>
                    <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} dir="rtl" required readOnly={!canManage} data-testid="template-name-ar" />
                  </Field>
                  <Field label={t("print.templates.languageMode")} description={t("print.templates.languageModeHelp")}>
                    <SelectField value={form.languageMode} onChange={(e) => { setForm({ ...form, languageMode: e.target.value }); }} disabled={!canManage} data-testid="template-language-mode">
                      {languageModes.map((m) => <option key={m} value={m}>{t(`print.languageModes.${m}`)}</option>)}
                    </SelectField>
                  </Field>
                  <Field label={t("print.templates.paper")}>
                    <SelectField value={form.paper} onChange={(e) => { setForm({ ...form, paper: e.target.value }); }} disabled={!canManage} data-testid="template-paper">
                      {papers.map((p) => <option key={p} value={p}>{p}</option>)}
                    </SelectField>
                  </Field>
                  <Field label={t("print.templates.company")} description={t("print.templates.scopeHelp")}>
                    <SelectField value={form.companyId} onChange={(e) => { setForm({ ...form, companyId: e.target.value, branchId: "" }); }} disabled={!canManage} data-testid="template-company">
                      <option value="">{t("print.templates.allCompanies")}</option>
                      {companies.map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
                    </SelectField>
                  </Field>
                  <Field label={t("print.templates.branch")}>
                    <SelectField value={form.branchId} onChange={(e) => { setForm({ ...form, branchId: e.target.value }); }} disabled={!canManage || !form.companyId} data-testid="template-branch">
                      <option value="">{t("print.templates.wholeCompany")}</option>
                      {(branches.data ?? []).map((b) => <option key={b.id} value={b.id}>{b.code} · {localized(b.name)}</option>)}
                    </SelectField>
                  </Field>
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.isDefault} onChange={(e) => { setForm({ ...form, isDefault: e.target.checked }); }} disabled={!canManage} data-testid="template-default" />
                    {t("print.templates.isDefault")}
                  </label>
                  <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} disabled={!canManage} data-testid="template-active" />
                    {t("common.active")}
                  </label>
                </div>
              ) : null}
              {tab === "preview" ? (
                previewError ? <p className="text-sm text-danger">{previewError}</p> : (
                  <>
                    <p className="text-xs text-fg-muted">{t("print.templates.previewHelp")}</p>
                    <iframe title={t("print.preview")} srcDoc={preview.data ?? ""} sandbox="allow-scripts" className="h-[60vh] w-full rounded-md border border-border bg-white" data-testid="template-preview" />
                  </>
                )
              ) : null}
              {tab === "versions" ? (
                <Table data-testid="template-versions">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("print.version")}</TableHead>
                      <TableHead>{t("print.templates.note")}</TableHead>
                      <TableHead>{t("print.templates.published")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(versions.data ?? []).map((v) => (
                      <TableRow key={v.id} data-testid="template-version-row">
                        <TableCell dir="ltr" className="tabular">{String(v.version)}{v.isCurrent ? <Badge tone="success" className="ms-2">{t("print.templates.current")}</Badge> : null}</TableCell>
                        <TableCell dir="auto">{v.note ?? "—"}</TableCell>
                        <TableCell dir="ltr">{formatDateTime(v.publishedAt)}</TableCell>
                        <TableCell>{!v.isCurrent && canManage ? <Button type="button" variant="ghost" size="sm" onClick={() => { rollback.mutate(Number(v.version)); }} loading={rollback.isPending} data-testid={`rollback-${String(v.version)}`}><History aria-hidden="true" />{t("print.templates.rollback")}</Button> : null}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={close}>{t("common.close")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(copying)} onOpenChange={(isOpen) => { if (!isOpen) { setCopying(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-xl">
          {copying ? (
            <form onSubmit={submitCopy} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("print.templates.copyTitle")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <p className="text-sm text-fg-muted">{t("print.templates.copyHelp")}</p>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("banking.code")} required>
                  <TextField value={copying.code} onChange={(e) => { setCopying({ ...copying, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="copy-code" />
                </Field>
                <Field label={t("print.templates.languageMode")}>
                  <SelectField value={copying.languageMode} onChange={(e) => { setCopying({ ...copying, languageMode: e.target.value }); }} data-testid="copy-language-mode">
                    <option value="">{t("print.templates.sameAsSource")}</option>
                    {languageModes.map((m) => <option key={m} value={m}>{t(`print.languageModes.${m}`)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("banking.nameEn")}>
                  <TextField value={copying.nameEn} onChange={(e) => { setCopying({ ...copying, nameEn: e.target.value }); }} dir="ltr" data-testid="copy-name-en" />
                </Field>
                <Field label={t("banking.nameAr")}>
                  <TextField value={copying.nameAr} onChange={(e) => { setCopying({ ...copying, nameAr: e.target.value }); }} dir="rtl" data-testid="copy-name-ar" />
                </Field>
                <Field label={t("print.templates.company")}>
                  <SelectField value={copying.companyId} onChange={(e) => { setCopying({ ...copying, companyId: e.target.value, branchId: "" }); }} data-testid="copy-company">
                    <option value="">{t("print.templates.allCompanies")}</option>
                    {companies.map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("print.templates.branch")}>
                  <SelectField value={copying.branchId} onChange={(e) => { setCopying({ ...copying, branchId: e.target.value }); }} disabled={!copying.companyId} data-testid="copy-branch">
                    <option value="">{t("print.templates.wholeCompany")}</option>
                    {(branches.data ?? []).map((b) => <option key={b.id} value={b.id}>{b.code} · {localized(b.name)}</option>)}
                  </SelectField>
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCopying(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={copy.isPending} disabled={!copying.code.trim()} data-testid="confirm-copy">{t("print.templates.copy")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
