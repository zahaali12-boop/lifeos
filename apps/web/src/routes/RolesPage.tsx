import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, isApiProblem, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatNumber, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { PageHeader, SelectField, TextField } from "./common";
import { Tabs } from "./inventory/shared";
import { PermissionPicker, sodConflicts } from "./PermissionPicker";

type Role = components["schemas"]["RoleSummary"];
type FieldRule = components["schemas"]["FieldRule"];
type DocumentTypeRule = components["schemas"]["DocumentTypeRule"];

interface Draft {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  description: string;
  isActive: boolean;
  isSystem: boolean;
  grants: string[];
  fieldRules: FieldRule[];
  documentTypeRules: DocumentTypeRule[];
}

const fieldAccess = ["hidden", "read_only", "editable"];
const documentActions = ["create", "approve", "post", "reverse", "export"];

function draftOf(role: Role): Draft {
  return {
    id: role.id,
    code: role.code,
    nameEn: role.name.en ?? "",
    nameAr: role.name.ar ?? "",
    description: role.description,
    isActive: role.isActive,
    isSystem: role.isSystem,
    grants: [...role.grants],
    fieldRules: role.fieldRules.map((r) => ({ ...r })),
    documentTypeRules: role.documentTypeRules.map((r) => ({ ...r })),
  };
}

/** The keys a `role.grant_unknown` or `role.sod_conflict` problem names in its `why`, for the form to show. */
function whyList(error: unknown): string[] {
  if (!isApiProblem(error) || !error.why) {
    return [];
  }
  const { keys, conflicts } = error.why as { keys?: unknown; conflicts?: unknown };
  if (Array.isArray(keys)) {
    return keys.map(String);
  }
  if (Array.isArray(conflicts)) {
    return (conflicts as { permissionA?: string; permissionB?: string }[]).map((c) => `${c.permissionA ?? ""} + ${c.permissionB ?? ""}`);
  }
  return [];
}

/**
 * Roles (ADR-0014): the role designer. A role bundles permissions from the code-defined catalogue, optionally whole
 * modules or areas, plus field rules (hide or lock a field) and document-type rules (allow or deny an action on a
 * document type). New roles start empty or from a template; system roles can be edited but not deleted, and the owner
 * role is fixed. The active segregation-of-duties rules are checked as grants are ticked, as the server will on save.
 */
export function RolesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [tab, setTab] = useState("permissions");
  const [problem, setProblem] = useState<(FormProblem & { details: string[] }) | null>(null);
  const [pending, setPending] = useState<string[]>([]);
  const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
  const catalogue = useQuery({ queryKey: ["permission-catalog"], queryFn: async () => unwrap(await api.GET("/api/v1/meta/permissions")) });
  const templates = useQuery({ queryKey: ["role-templates"], queryFn: async () => unwrap(await api.GET("/api/v1/meta/role-templates")) });
  const sodRules = useQuery({ queryKey: ["sod-rules"], queryFn: async () => unwrap(await api.GET("/api/v1/sod/rules")), retry: false });
  const fail = (error: unknown): void => { setProblem({ ...toFormProblem(error, t("common.saveFailed")), details: whyList(error) }); };

  const save = useMutation({
    mutationFn: async () => {
      if (!draft) {
        return;
      }
      const body = {
        code: draft.code.trim(),
        name: { en: draft.nameEn, ...(draft.nameAr ? { ar: draft.nameAr } : {}) },
        description: draft.description,
        grants: draft.grants,
        fieldRules: draft.fieldRules.filter((r) => r.entityType.trim() && r.field.trim()),
        documentTypeRules: draft.documentTypeRules.filter((r) => r.documentType.trim()),
        isActive: draft.isActive,
      };
      if (draft.id) {
        unwrap(await api.PUT("/api/v1/roles/{roleId}", { params: { path: { roleId: draft.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/roles", { body }));
      }
    },
    onSuccess: async () => { setDraft(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["roles"] }); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: async (roleId: string) => { await api.DELETE("/api/v1/roles/{roleId}", { params: { path: { roleId } } }).then(unwrap); },
    onSuccess: async () => { setDraft(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["roles"] }); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Role, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("roles.code"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("roles.name"), size: 220 },
      { id: "description", accessorKey: "description", header: t("roles.descriptionColumn"), size: 300 },
      { id: "grants", accessorFn: (row) => row.grants.length, header: t("roles.grants"), size: 100, cell: ({ row }) => <span className="tabular">{formatNumber(row.original.grants.length)}</span> },
      { id: "isSystem", accessorKey: "isSystem", header: t("roles.kind"), size: 110, cell: ({ row }) => <Badge tone={row.original.isSystem ? "accent" : "neutral"}>{row.original.isSystem ? t("roles.system") : t("roles.custom")}</Badge> },
      { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => (row.original.isActive ? t("common.active") : t("common.inactive")) },
    ],
    [t],
  );

  const locked = draft?.isSystem === true && draft.code === "owner";
  const conflicts = draft ? sodConflicts(draft.grants, sodRules.data ?? []) : [];
  const open = (next: Draft): void => { setProblem(null); setPending([]); setTab("permissions"); setDraft(next); };
  const applyTemplate = (code: string): void => {
    const template = templates.data?.find((x) => x.code === code);
    if (!draft || !template) {
      return;
    }
    setDraft({ ...draft, code: draft.code || `${template.code}_custom`, nameEn: draft.nameEn || (template.name.en ?? ""), nameAr: draft.nameAr || (template.name.ar ?? ""), description: draft.description || template.description, grants: [...template.grants] });
    setPending(template.pendingGrants);
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };

  return (
    <>
      <PageHeader
        title={t("nav.roles")}
        description={t("roles.description")}
        actions={
          <Button onClick={() => { open({ id: null, code: "", nameEn: "", nameAr: "", description: "", isActive: true, isSystem: false, grants: [], fieldRules: [], documentTypeRules: [] }); }} data-testid="new-role">
            <Plus aria-hidden="true" />
            {t("roles.new")}
          </Button>
        }
      />
      <DataGrid<Role> label="nav.roles" columns={columns} data={roles.data ?? []} rowKey={(row) => row.id} loading={roles.isPending} onOpen={(role) => { open(draftOf(role)); }} emptyTitle={t("roles.emptyTitle")} />

      <Dialog open={draft !== null} onOpenChange={(isOpen) => { if (!isOpen) { setDraft(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {draft ? (
            <form onSubmit={submit} className="flex flex-col gap-4" data-testid="role-editor">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{draft.id ? t("roles.edit", { code: draft.code }) : t("roles.new")}</DialogTitle>
              </DialogHeader>
              {locked ? <p className="rounded-md border border-border bg-surface-sunken p-2 text-sm">{t("roles.ownerLocked")}</p> : null}
              {problem ? (
                <div role="alert" className="rounded-md border border-danger/40 bg-danger-soft p-3 text-sm" data-testid="role-problem">
                  <p>{problem.message}</p>
                  {problem.details.length > 0 ? (
                    <ul className="mt-1 list-disc ps-5">
                      {problem.details.map((d) => (
                        <li key={d} dir="ltr" className="font-mono text-xs">
                          {d}
                        </li>
                      ))}
                    </ul>
                  ) : null}
                </div>
              ) : null}
              {draft.id ? null : (
                <Field label={t("roles.template")} description={t("roles.templateHint")}>
                  <SelectField value="" onChange={(e) => { applyTemplate(e.target.value); }} data-testid="role-template">
                    <option value="">{t("roles.noTemplate")}</option>
                    {(templates.data ?? []).map((x) => (
                      <option key={x.code} value={x.code}>
                        {localized(x.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
              )}
              {pending.length > 0 ? (
                <p className="text-sm text-fg-muted" data-testid="template-pending">
                  {t("roles.templatePending", { count: pending.length })} <span dir="ltr" className="font-mono text-xs">{pending.join(", ")}</span>
                </p>
              ) : null}
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("roles.code")} required description={t("roles.codeHint")} error={problem?.fields.code}>
                  <TextField value={draft.code} onChange={(e) => { setDraft({ ...draft, code: e.target.value.toLowerCase() }); }} required disabled={Boolean(draft.id)} dir="ltr" data-testid="role-code" />
                </Field>
                <Field label={t("roles.descriptionColumn")}>
                  <TextField value={draft.description} onChange={(e) => { setDraft({ ...draft, description: e.target.value }); }} disabled={locked} data-testid="role-description" />
                </Field>
                <Field label={t("roles.nameEn")} required error={problem?.fields.name}>
                  <TextField value={draft.nameEn} onChange={(e) => { setDraft({ ...draft, nameEn: e.target.value }); }} required disabled={locked} data-testid="role-name-en" />
                </Field>
                <Field label={t("roles.nameAr")}>
                  <TextField value={draft.nameAr} onChange={(e) => { setDraft({ ...draft, nameAr: e.target.value }); }} disabled={locked} dir="rtl" lang="ar" />
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={draft.isActive} disabled={locked} onChange={(e) => { setDraft({ ...draft, isActive: e.target.checked }); }} />
                {t("common.active")}
              </label>

              {conflicts.length > 0 ? (
                <div className="rounded-md border border-warning/40 bg-warning-soft p-3 text-sm" data-testid="role-conflicts">
                  <p className="font-medium">{t("roles.conflicts")}</p>
                  <ul className="mt-1 flex flex-col gap-1">
                    {conflicts.map((c) => (
                      <li key={c.id} className="flex flex-wrap items-center gap-1">
                        <Badge tone={c.severity === "block" ? "danger" : "warning"}>{t(`security.severities.${c.severity}`)}</Badge>
                        <span dir="ltr" className="font-mono text-xs">{c.permissionA}</span> + <span dir="ltr" className="font-mono text-xs">{c.permissionB}</span>
                        <span className="text-fg-muted">{localized(c.rationale)}</span>
                      </li>
                    ))}
                  </ul>
                  <p className="mt-1 text-fg-muted">{conflicts.some((c) => c.severity === "block") ? t("roles.conflictsBlock") : t("roles.conflictsWarn")}</p>
                </div>
              ) : null}

              <Tabs
                value={tab}
                onChange={setTab}
                tabs={[
                  { id: "permissions", label: t("roles.permissionsTab", { count: draft.grants.length }), testId: "tab-role-permissions" },
                  { id: "fields", label: t("roles.fieldRules"), testId: "tab-role-fields" },
                  { id: "documents", label: t("roles.documentRules"), testId: "tab-role-documents" },
                ]}
              />
              {tab === "permissions" ? (
                <PermissionPicker catalogue={catalogue.data ?? []} grants={draft.grants} onChange={(grants) => { setDraft({ ...draft, grants }); }} readOnly={locked} />
              ) : null}
              {tab === "fields" ? (
                <div className="flex flex-col gap-2" data-testid="field-rules">
                  <p className="text-sm text-fg-muted">{t("roles.fieldRulesHint")}</p>
                  {draft.fieldRules.map((rule, index) => (
                    <div key={index} className="grid items-end gap-2 sm:grid-cols-[1fr_1fr_10rem_auto]">
                      <Field label={t("roles.entityType")}>
                        <TextField value={rule.entityType} onChange={(e) => { setDraft({ ...draft, fieldRules: draft.fieldRules.map((r, i) => (i === index ? { ...r, entityType: e.target.value } : r)) }); }} disabled={locked} dir="ltr" />
                      </Field>
                      <Field label={t("roles.field")}>
                        <TextField value={rule.field} onChange={(e) => { setDraft({ ...draft, fieldRules: draft.fieldRules.map((r, i) => (i === index ? { ...r, field: e.target.value } : r)) }); }} disabled={locked} dir="ltr" />
                      </Field>
                      <Field label={t("roles.access")}>
                        <SelectField value={rule.access} onChange={(e) => { setDraft({ ...draft, fieldRules: draft.fieldRules.map((r, i) => (i === index ? { ...r, access: e.target.value } : r)) }); }} disabled={locked}>
                          {fieldAccess.map((a) => (
                            <option key={a} value={a}>
                              {t(`roles.accessValues.${a}`)}
                            </option>
                          ))}
                        </SelectField>
                      </Field>
                      <Button type="button" variant="ghost" size="icon" aria-label={t("roles.removeRule")} disabled={locked} onClick={() => { setDraft({ ...draft, fieldRules: draft.fieldRules.filter((_, i) => i !== index) }); }}>
                        <Trash2 aria-hidden="true" />
                      </Button>
                    </div>
                  ))}
                  {locked ? null : (
                    <Button type="button" variant="secondary" size="sm" className="self-start" onClick={() => { setDraft({ ...draft, fieldRules: [...draft.fieldRules, { entityType: "", field: "", access: "read_only" }] }); }} data-testid="add-field-rule">
                      <Plus aria-hidden="true" />
                      {t("roles.addFieldRule")}
                    </Button>
                  )}
                </div>
              ) : null}
              {tab === "documents" ? (
                <div className="flex flex-col gap-2" data-testid="document-rules">
                  <p className="text-sm text-fg-muted">{t("roles.documentRulesHint")}</p>
                  {draft.documentTypeRules.map((rule, index) => (
                    <div key={index} className="grid items-end gap-2 sm:grid-cols-[1fr_10rem_8rem_auto]">
                      <Field label={t("roles.documentType")}>
                        <TextField value={rule.documentType} onChange={(e) => { setDraft({ ...draft, documentTypeRules: draft.documentTypeRules.map((r, i) => (i === index ? { ...r, documentType: e.target.value } : r)) }); }} disabled={locked} dir="ltr" />
                      </Field>
                      <Field label={t("roles.action")}>
                        <SelectField value={rule.action} onChange={(e) => { setDraft({ ...draft, documentTypeRules: draft.documentTypeRules.map((r, i) => (i === index ? { ...r, action: e.target.value } : r)) }); }} disabled={locked}>
                          {documentActions.map((a) => (
                            <option key={a} value={a}>
                              {t(`roles.actions.${a}`)}
                            </option>
                          ))}
                        </SelectField>
                      </Field>
                      <label className="flex h-10 items-center gap-2 text-sm">
                        <input type="checkbox" checked={rule.allowed} disabled={locked} onChange={(e) => { setDraft({ ...draft, documentTypeRules: draft.documentTypeRules.map((r, i) => (i === index ? { ...r, allowed: e.target.checked } : r)) }); }} />
                        {t("roles.allowed")}
                      </label>
                      <Button type="button" variant="ghost" size="icon" aria-label={t("roles.removeRule")} disabled={locked} onClick={() => { setDraft({ ...draft, documentTypeRules: draft.documentTypeRules.filter((_, i) => i !== index) }); }}>
                        <Trash2 aria-hidden="true" />
                      </Button>
                    </div>
                  ))}
                  {locked ? null : (
                    <Button type="button" variant="secondary" size="sm" className="self-start" onClick={() => { setDraft({ ...draft, documentTypeRules: [...draft.documentTypeRules, { documentType: "", action: "approve", allowed: false }] }); }} data-testid="add-document-rule">
                      <Plus aria-hidden="true" />
                      {t("roles.addDocumentRule")}
                    </Button>
                  )}
                </div>
              ) : null}

              <DialogFooter>
                {draft.id && !draft.isSystem ? (
                  <Button type="button" variant="ghost" className="me-auto text-danger" onClick={() => { if (draft.id) { remove.mutate(draft.id); } }} loading={remove.isPending} data-testid="delete-role">
                    <Trash2 aria-hidden="true" />
                    {t("roles.delete")}
                  </Button>
                ) : null}
                <Button type="button" variant="secondary" onClick={() => { setDraft(null); }}>
                  {t("common.cancel")}
                </Button>
                {locked ? null : (
                  <Button type="submit" loading={save.isPending} data-testid="save-role">
                    {t("common.save")}
                  </Button>
                )}
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
