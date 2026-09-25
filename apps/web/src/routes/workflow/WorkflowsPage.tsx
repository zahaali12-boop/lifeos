import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDateTime, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField, TextareaField } from "../common";
import { WorkflowStatus } from "./ApprovalsPage";

type Definition = components["schemas"]["DefinitionSummary"];
type Spec = components["schemas"]["ApproverSpecRequest"];

interface StepForm {
  name: string;
  nameAr: string;
  approverKind: "users" | "role";
  roleCode: string;
  membershipIds: string[];
  mode: string;
  quorum: string;
  timeoutHours: string;
  escalationRoleCode: string;
  escalationMembershipIds: string[];
  allowDelegate: boolean;
  requireComment: boolean;
}

interface RuleForm {
  name: string;
  nameAr: string;
  condition: string;
  steps: StepForm[];
}

interface DefinitionForm {
  id: string | null;
  status: string;
  entityType: string;
  trigger: string;
  blockKind: string;
  name: string;
  nameAr: string;
  description: string;
  reapprovalPolicy: string;
  overrideValidHours: string;
  rules: RuleForm[];
}

const emptyStep = (): StepForm => ({ name: "", nameAr: "", approverKind: "role", roleCode: "", membershipIds: [], mode: "any", quorum: "", timeoutHours: "", escalationRoleCode: "", escalationMembershipIds: [], allowDelegate: true, requireComment: false });
const emptyRule = (): RuleForm => ({ name: "", nameAr: "", condition: "", steps: [emptyStep()] });

function specOf(kind: "users" | "role", roleCode: string, membershipIds: string[]): Spec | null {
  if (kind === "role") {
    return roleCode.trim() ? { roleCode: roleCode.trim(), scopeToCompany: true } : null;
  }
  return membershipIds.length > 0 ? { membershipIds, scopeToCompany: true } : null;
}

function stepFromSummary(step: components["schemas"]["StepSummary"]): StepForm {
  return {
    name: step.name.en ?? "",
    nameAr: step.name.ar ?? "",
    approverKind: step.approverKind === "role" ? "role" : "users",
    roleCode: step.approvers.roleCode ?? "",
    membershipIds: step.approvers.membershipIds ?? [],
    mode: step.mode,
    quorum: step.quorum === null ? "" : String(step.quorum),
    timeoutHours: step.timeoutHours === null ? "" : String(step.timeoutHours),
    escalationRoleCode: step.escalation?.roleCode ?? "",
    escalationMembershipIds: step.escalation?.membershipIds ?? [],
    allowDelegate: step.allowDelegate,
    requireComment: step.requireComment,
  };
}

function formFromSummary(d: Definition): DefinitionForm {
  return {
    id: d.id,
    status: d.status,
    entityType: d.entityType,
    trigger: d.trigger,
    blockKind: d.blockKind ?? "",
    name: d.name.en ?? "",
    nameAr: d.name.ar ?? "",
    description: d.description.en ?? "",
    reapprovalPolicy: d.reapprovalPolicy,
    overrideValidHours: String(d.overrideValidHours),
    rules: d.rules.map((r) => ({ name: r.name.en ?? "", nameAr: r.name.ar ?? "", condition: r.condition, steps: r.steps.map(stepFromSummary) })),
  };
}

/** Approval definitions (roadmap 4.0, ADR-0020): the catalogue of document types and fields, rules in the safe grammar checked as they are typed, steps with named members or a role, escalation, activation and versions. */
export function WorkflowsPage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<DefinitionForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [checks, setChecks] = useState<Record<number, { valid: boolean; message: string | null; fields: string[] }>>({});

  const catalogue = useQuery({ queryKey: ["workflow-catalogue"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/catalogue")) });
  const definitions = useQuery({ queryKey: ["workflow-definitions"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/definitions")) });
  const members = useQuery({ queryKey: ["members"], enabled: Boolean(editing), queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
  const roles = useQuery({ queryKey: ["roles"], enabled: Boolean(editing), queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
  const refresh = async (): Promise<void> => { await queryClient.invalidateQueries({ queryKey: ["workflow-definitions"] }); };

  const body = (f: DefinitionForm) => ({
    entityType: f.entityType,
    trigger: f.trigger,
    blockKind: f.trigger === "on_block" ? f.blockKind || null : null,
    name: { en: f.name, ar: f.nameAr || f.name },
    description: f.description ? { en: f.description, ar: f.description } : null,
    reapprovalPolicy: f.reapprovalPolicy,
    overrideValidHours: Number(f.overrideValidHours || "168"),
    rules: f.rules.map((r) => ({
      name: { en: r.name, ar: r.nameAr || r.name },
      condition: r.condition,
      steps: r.steps.map((s) => ({
        name: { en: s.name, ar: s.nameAr || s.name },
        approverKind: s.approverKind,
        approvers: specOf(s.approverKind, s.roleCode, s.membershipIds) ?? { scopeToCompany: true },
        mode: s.mode,
        quorum: s.mode === "quorum" && s.quorum ? Number(s.quorum) : null,
        timeoutHours: s.timeoutHours ? Number(s.timeoutHours) : null,
        escalation: specOf(s.escalationRoleCode.trim() ? "role" : "users", s.escalationRoleCode, s.escalationMembershipIds),
        allowDelegate: s.allowDelegate,
        requireComment: s.requireComment,
        requireStepUp: false,
      })),
    })),
  });
  const save = useMutation({
    mutationFn: async (f: DefinitionForm) => (f.id ? unwrap(await api.PUT("/api/v1/workflow/definitions/{definitionId}", { params: { path: { definitionId: f.id } }, body: body(f) })) : unwrap(await api.POST("/api/v1/workflow/definitions", { body: body(f) }))),
    onSuccess: async (saved) => {
      setProblem(null);
      setEditing(formFromSummary(saved));
      await refresh();
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const lifecycle = useMutation({
    mutationFn: async (input: { id: string; action: "activate" | "retire" }) =>
      input.action === "activate"
        ? unwrap(await api.POST("/api/v1/workflow/definitions/{definitionId}/activate", { params: { path: { definitionId: input.id } } }))
        : unwrap(await api.POST("/api/v1/workflow/definitions/{definitionId}/retire", { params: { path: { definitionId: input.id } } })),
    onSuccess: async (saved) => {
      setProblem(null);
      setEditing(formFromSummary(saved));
      await refresh();
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const check = useMutation({
    mutationFn: async (input: { index: number; expression: string }) => ({ index: input.index, result: unwrap(await api.POST("/api/v1/workflow/expressions/validate", { body: { entityType: editing?.entityType ?? "", expression: input.expression, trigger: editing?.trigger ?? null } })) }),
    onSuccess: ({ index, result }) => { setChecks((prev) => ({ ...prev, [index]: { valid: result.valid, message: result.message, fields: result.fields } })); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Definition, unknown>[]>(
    () => [
      { id: "name", accessorFn: (row) => localized(row.name), header: t("workflow.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "entityType", accessorKey: "entityType", header: t("workflow.entityType"), size: 160, cell: ({ row }) => t(`workflow.entityTypes.${row.original.entityType}`, { defaultValue: row.original.entityType }) },
      { id: "trigger", accessorKey: "trigger", header: t("workflow.trigger"), size: 140, cell: ({ row }) => `${t(`workflow.triggers.${row.original.trigger}`, { defaultValue: row.original.trigger })}${row.original.blockKind ? ` · ${row.original.blockKind}` : ""}` },
      { id: "rules", accessorFn: (row) => row.rules.length, header: t("workflow.rules"), size: 80, cell: ({ row }) => String(row.original.rules.length) },
      { id: "version", accessorKey: "version", header: t("workflow.version"), size: 80, cell: ({ row }) => String(row.original.version) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <WorkflowStatus status={row.original.status} /> },
      { id: "updated", accessorKey: "updatedAt", header: t("common.updated"), size: 160, cell: ({ row }) => formatDateTime(row.original.updatedAt) },
    ],
    [t],
  );

  const subject = catalogue.data?.subjects.find((s) => s.entityType === editing?.entityType);
  const setForm = (patch: Partial<DefinitionForm>): void => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
  const setRule = (index: number, patch: Partial<RuleForm>): void => { setEditing((prev) => (prev ? { ...prev, rules: prev.rules.map((r, i) => (i === index ? { ...r, ...patch } : r)) } : prev)); };
  const setStep = (ruleIndex: number, stepIndex: number, patch: Partial<StepForm>): void => {
    setEditing((prev) => (prev ? { ...prev, rules: prev.rules.map((r, i) => (i === ruleIndex ? { ...r, steps: r.steps.map((s, j) => (j === stepIndex ? { ...s, ...patch } : s)) } : r)) } : prev));
  };
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const selectedIds = (select: HTMLSelectElement): string[] => Array.from(select.selectedOptions).map((o) => o.value);
  const activeMembers = (members.data ?? []).filter((m) => m.status === "active");
  const editable = editing?.status !== "retired";

  return (
    <>
      <PageHeader
        title={t("nav.workflows")}
        description={t("workflow.definitionsDescription")}
        actions={
          <Button onClick={() => { setProblem(null); setChecks({}); setEditing({ id: null, status: "draft", entityType: catalogue.data?.subjects[0]?.entityType ?? "", trigger: "on_submit", blockKind: "", name: "", nameAr: "", description: "", reapprovalPolicy: "reset", overrideValidHours: "168", rules: [emptyRule()] }); }} disabled={!catalogue.data} data-testid="new-definition">
            <Plus aria-hidden="true" />
            {t("workflow.newDefinition")}
          </Button>
        }
      />
      <DataGrid<Definition> label="nav.workflows" columns={columns} data={definitions.data ?? []} rowKey={(row) => row.id} loading={definitions.isPending} emptyTitle={t("workflow.emptyDefinitions")} emptyDescription={t("workflow.emptyDefinitionsDescription")} onOpen={(row) => { setProblem(null); setChecks({}); setEditing(formFromSummary(row)); }} />

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4" data-testid="definition-form">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold" dir="auto">
                  {editing.id ? `${editing.name} · v${String(definitions.data?.find((d) => d.id === editing.id)?.version ?? "")}` : t("workflow.newDefinition")}
                  {editing.id ? <span className="ms-2"><WorkflowStatus status={editing.status} /></span> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("workflow.entityType")} required>
                  <SelectField value={editing.entityType} onChange={(e) => { setForm({ entityType: e.target.value, blockKind: "" }); setChecks({}); }} disabled={Boolean(editing.id)} required data-testid="definition-entity-type">
                    {(catalogue.data?.subjects ?? []).map((s) => (
                      <option key={s.entityType} value={s.entityType}>
                        {localized(s.label)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("workflow.trigger")} required>
                  <SelectField value={editing.trigger} onChange={(e) => { setForm({ trigger: e.target.value }); setChecks({}); }} disabled={Boolean(editing.id)} data-testid="definition-trigger">
                    {(catalogue.data?.triggers ?? []).map((trigger) => (
                      <option key={trigger} value={trigger}>
                        {t(`workflow.triggers.${trigger}`, { defaultValue: trigger })}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                {editing.trigger === "on_block" ? (
                  <Field label={t("workflow.blockKind")} required>
                    <SelectField value={editing.blockKind} onChange={(e) => { setForm({ blockKind: e.target.value }); }} disabled={Boolean(editing.id)} required>
                      <option value="">—</option>
                      {(subject?.blockKinds ?? []).map((kind) => (
                        <option key={kind} value={kind}>
                          {kind}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("workflow.name")} required>
                  <TextField value={editing.name} onChange={(e) => { setForm({ name: e.target.value }); }} required data-testid="definition-name" />
                </Field>
                <Field label={t("workflow.nameAr")}>
                  <TextField value={editing.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <Field label={t("workflow.description")}>
                  <TextField value={editing.description} onChange={(e) => { setForm({ description: e.target.value }); }} lang={i18n.language} />
                </Field>
                <Field label={t("workflow.reapprovalPolicy")}>
                  <SelectField value={editing.reapprovalPolicy} onChange={(e) => { setForm({ reapprovalPolicy: e.target.value }); }}>
                    {["reset", "none"].map((p) => (
                      <option key={p} value={p}>
                        {t(`workflow.reapprovalPolicies.${p}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                {editing.trigger === "on_block" ? (
                  <Field label={t("workflow.overrideValidHours")}>
                    <TextField type="number" min={1} value={editing.overrideValidHours} onChange={(e) => { setForm({ overrideValidHours: e.target.value }); }} dir="ltr" />
                  </Field>
                ) : null}
              </div>

              <div className="flex flex-col gap-4">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-semibold">{t("workflow.rules")}</h3>
                  <Button type="button" variant="secondary" size="sm" onClick={() => { setForm({ rules: [...editing.rules, emptyRule()] }); }} data-testid="add-rule">
                    {t("workflow.addRule")}
                  </Button>
                </div>
                {editing.rules.map((rule, ruleIndex) => (
                  <fieldset key={ruleIndex} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="rule">
                    <legend className="px-1 text-sm font-medium">{`${t("workflow.rule")} ${String(ruleIndex + 1)}`}</legend>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Field label={t("workflow.name")} required>
                        <TextField value={rule.name} onChange={(e) => { setRule(ruleIndex, { name: e.target.value }); }} required data-testid="rule-name" />
                      </Field>
                      <Field label={t("workflow.nameAr")}>
                        <TextField value={rule.nameAr} onChange={(e) => { setRule(ruleIndex, { nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                      </Field>
                    </div>
                    <Field label={t("workflow.condition")} required description={t("workflow.conditionHelp", { fields: (subject?.fields ?? []).map((f) => f.name).join(", "), functions: (catalogue.data?.functions ?? []).join(", ") })}>
                      <TextareaField value={rule.condition} onChange={(e) => { setRule(ruleIndex, { condition: e.target.value }); }} rows={2} dir="ltr" className="font-mono" required data-testid="rule-condition" />
                    </Field>
                    <div className="flex flex-wrap items-center gap-2 text-sm">
                      <Button type="button" variant="secondary" size="sm" onClick={() => { check.mutate({ index: ruleIndex, expression: rule.condition }); }} loading={check.isPending} data-testid="check-condition">
                        {t("workflow.check")}
                      </Button>
                      {checks[ruleIndex] ? (
                        <span className={checks[ruleIndex].valid ? "text-success" : "text-danger"} data-testid="condition-check">
                          {checks[ruleIndex].valid ? t("workflow.conditionValid", { fields: checks[ruleIndex].fields.join(", ") }) : checks[ruleIndex].message}
                        </span>
                      ) : null}
                      <span className="grow" />
                      {editing.rules.length > 1 ? (
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ rules: editing.rules.filter((_, i) => i !== ruleIndex) }); }}>
                          {t("workflow.removeRule")}
                        </Button>
                      ) : null}
                    </div>
                    <div className="flex items-center justify-between">
                      <h4 className="text-sm font-medium">{t("workflow.steps")}</h4>
                      <Button type="button" variant="ghost" size="sm" onClick={() => { setRule(ruleIndex, { steps: [...rule.steps, emptyStep()] }); }} data-testid="add-step">
                        {t("workflow.addStep")}
                      </Button>
                    </div>
                    {rule.steps.map((step, stepIndex) => (
                      <div key={stepIndex} className="grid gap-3 rounded-md bg-surface-muted p-3 sm:grid-cols-4" data-testid="step">
                        <Field label={t("workflow.step")} required>
                          <TextField value={step.name} onChange={(e) => { setStep(ruleIndex, stepIndex, { name: e.target.value }); }} required data-testid="step-name" />
                        </Field>
                        <Field label={t("workflow.approverKind")}>
                          <SelectField value={step.approverKind} onChange={(e) => { setStep(ruleIndex, stepIndex, { approverKind: e.target.value === "role" ? "role" : "users" }); }} data-testid="step-approver-kind">
                            {(["role", "users"] as const).map((kind) => (
                              <option key={kind} value={kind}>
                                {t(`workflow.approverKinds.${kind}`)}
                              </option>
                            ))}
                          </SelectField>
                        </Field>
                        {step.approverKind === "role" ? (
                          <Field label={t("workflow.roleCode")} required>
                            <SelectField value={step.roleCode} onChange={(e) => { setStep(ruleIndex, stepIndex, { roleCode: e.target.value }); }} required data-testid="step-role">
                              <option value="">—</option>
                              {(roles.data ?? []).map((role) => (
                                <option key={role.id} value={role.code}>
                                  {role.code} · {localized(role.name)}
                                </option>
                              ))}
                            </SelectField>
                          </Field>
                        ) : (
                          <Field label={t("workflow.members")} required>
                            <SelectField multiple value={step.membershipIds} onChange={(e) => { setStep(ruleIndex, stepIndex, { membershipIds: selectedIds(e.target) }); }} required data-testid="step-members">
                              {activeMembers.map((m) => (
                                <option key={m.membershipId} value={m.membershipId}>
                                  {m.displayName}
                                </option>
                              ))}
                            </SelectField>
                          </Field>
                        )}
                        <Field label={t("workflow.mode")}>
                          <SelectField value={step.mode} onChange={(e) => { setStep(ruleIndex, stepIndex, { mode: e.target.value }); }}>
                            {["any", "all", "quorum"].map((mode) => (
                              <option key={mode} value={mode}>
                                {t(`workflow.modes.${mode}`)}
                              </option>
                            ))}
                          </SelectField>
                        </Field>
                        {step.mode === "quorum" ? (
                          <Field label={t("workflow.quorum")} required>
                            <TextField type="number" min={1} value={step.quorum} onChange={(e) => { setStep(ruleIndex, stepIndex, { quorum: e.target.value }); }} dir="ltr" required />
                          </Field>
                        ) : null}
                        <Field label={t("workflow.timeoutHours")}>
                          <TextField type="number" min={1} value={step.timeoutHours} onChange={(e) => { setStep(ruleIndex, stepIndex, { timeoutHours: e.target.value }); }} dir="ltr" />
                        </Field>
                        <Field label={t("workflow.escalation")}>
                          <SelectField value={step.escalationRoleCode} onChange={(e) => { setStep(ruleIndex, stepIndex, { escalationRoleCode: e.target.value, escalationMembershipIds: [] }); }}>
                            <option value="">—</option>
                            {(roles.data ?? []).map((role) => (
                              <option key={role.id} value={role.code}>
                                {role.code} · {localized(role.name)}
                              </option>
                            ))}
                          </SelectField>
                        </Field>
                        <div className="flex flex-col justify-end gap-2 text-sm">
                          <label className="flex items-center gap-2">
                            <input type="checkbox" checked={step.allowDelegate} onChange={(e) => { setStep(ruleIndex, stepIndex, { allowDelegate: e.target.checked }); }} />
                            {t("workflow.allowDelegate")}
                          </label>
                          <label className="flex items-center gap-2">
                            <input type="checkbox" checked={step.requireComment} onChange={(e) => { setStep(ruleIndex, stepIndex, { requireComment: e.target.checked }); }} />
                            {t("workflow.requireComment")}
                          </label>
                          {rule.steps.length > 1 ? (
                            <Button type="button" variant="ghost" size="sm" onClick={() => { setRule(ruleIndex, { steps: rule.steps.filter((_, j) => j !== stepIndex) }); }}>
                              {t("workflow.removeStep")}
                            </Button>
                          ) : null}
                        </div>
                      </div>
                    ))}
                  </fieldset>
                ))}
              </div>

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.close")}
                </Button>
                {editing.id && editing.status === "draft" ? (
                  <Button type="button" onClick={() => { lifecycle.mutate({ id: editing.id ?? "", action: "activate" }); }} loading={lifecycle.isPending} data-testid="activate-definition">
                    {t("workflow.activate")}
                  </Button>
                ) : null}
                {editing.id && editing.status === "active" ? (
                  <Button type="button" variant="danger" onClick={() => { lifecycle.mutate({ id: editing.id ?? "", action: "retire" }); }} loading={lifecycle.isPending} data-testid="retire-definition">
                    {t("workflow.retire")}
                  </Button>
                ) : null}
                {editable ? (
                  <Button type="submit" loading={save.isPending} data-testid="save-definition">
                    {t("common.save")}
                  </Button>
                ) : null}
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
