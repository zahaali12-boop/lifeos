import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDateTime, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField, TextareaField } from "../common";
import { WorkflowStatus } from "./ApprovalsPage";
const emptyStep = () => ({ name: "", nameAr: "", approverKind: "role", roleCode: "", membershipIds: [], mode: "any", quorum: "", timeoutHours: "", escalationRoleCode: "", escalationMembershipIds: [], allowDelegate: true, requireComment: false });
const emptyRule = () => ({ name: "", nameAr: "", condition: "", steps: [emptyStep()] });
function specOf(kind, roleCode, membershipIds) {
    if (kind === "role") {
        return roleCode.trim() ? { roleCode: roleCode.trim(), scopeToCompany: true } : null;
    }
    return membershipIds.length > 0 ? { membershipIds, scopeToCompany: true } : null;
}
function stepFromSummary(step) {
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
function formFromSummary(d) {
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
    const [editing, setEditing] = useState(null);
    const [problem, setProblem] = useState(null);
    const [checks, setChecks] = useState({});
    const catalogue = useQuery({ queryKey: ["workflow-catalogue"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/catalogue")) });
    const definitions = useQuery({ queryKey: ["workflow-definitions"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/definitions")) });
    const members = useQuery({ queryKey: ["members"], enabled: Boolean(editing), queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
    const roles = useQuery({ queryKey: ["roles"], enabled: Boolean(editing), queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
    const refresh = async () => { await queryClient.invalidateQueries({ queryKey: ["workflow-definitions"] }); };
    const body = (f) => ({
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
        mutationFn: async (f) => (f.id ? unwrap(await api.PUT("/api/v1/workflow/definitions/{definitionId}", { params: { path: { definitionId: f.id } }, body: body(f) })) : unwrap(await api.POST("/api/v1/workflow/definitions", { body: body(f) }))),
        onSuccess: async (saved) => {
            setProblem(null);
            setEditing(formFromSummary(saved));
            await refresh();
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const lifecycle = useMutation({
        mutationFn: async (input) => input.action === "activate"
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
        mutationFn: async (input) => ({ index: input.index, result: unwrap(await api.POST("/api/v1/workflow/expressions/validate", { body: { entityType: editing?.entityType ?? "", expression: input.expression, trigger: editing?.trigger ?? null } })) }),
        onSuccess: ({ index, result }) => { setChecks((prev) => ({ ...prev, [index]: { valid: result.valid, message: result.message, fields: result.fields } })); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const columns = useMemo(() => [
        { id: "name", accessorFn: (row) => localized(row.name), header: t("workflow.name"), size: 220, cell: ({ row }) => _jsx("span", { dir: "auto", children: localized(row.original.name) }) },
        { id: "entityType", accessorKey: "entityType", header: t("workflow.entityType"), size: 160, cell: ({ row }) => t(`workflow.entityTypes.${row.original.entityType}`, { defaultValue: row.original.entityType }) },
        { id: "trigger", accessorKey: "trigger", header: t("workflow.trigger"), size: 140, cell: ({ row }) => `${t(`workflow.triggers.${row.original.trigger}`, { defaultValue: row.original.trigger })}${row.original.blockKind ? ` · ${row.original.blockKind}` : ""}` },
        { id: "rules", accessorFn: (row) => row.rules.length, header: t("workflow.rules"), size: 80, cell: ({ row }) => String(row.original.rules.length) },
        { id: "version", accessorKey: "version", header: t("workflow.version"), size: 80, cell: ({ row }) => String(row.original.version) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(WorkflowStatus, { status: row.original.status }) },
        { id: "updated", accessorKey: "updatedAt", header: t("common.updated"), size: 160, cell: ({ row }) => formatDateTime(row.original.updatedAt) },
    ], [t]);
    const subject = catalogue.data?.subjects.find((s) => s.entityType === editing?.entityType);
    const setForm = (patch) => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
    const setRule = (index, patch) => { setEditing((prev) => (prev ? { ...prev, rules: prev.rules.map((r, i) => (i === index ? { ...r, ...patch } : r)) } : prev)); };
    const setStep = (ruleIndex, stepIndex, patch) => {
        setEditing((prev) => (prev ? { ...prev, rules: prev.rules.map((r, i) => (i === ruleIndex ? { ...r, steps: r.steps.map((s, j) => (j === stepIndex ? { ...s, ...patch } : s)) } : r)) } : prev));
    };
    const submitForm = (event) => {
        event.preventDefault();
        if (editing) {
            save.mutate(editing);
        }
    };
    const selectedIds = (select) => Array.from(select.selectedOptions).map((o) => o.value);
    const activeMembers = (members.data ?? []).filter((m) => m.status === "active");
    const editable = editing?.status !== "retired";
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.workflows"), description: t("workflow.definitionsDescription"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setChecks({}); setEditing({ id: null, status: "draft", entityType: catalogue.data?.subjects[0]?.entityType ?? "", trigger: "on_submit", blockKind: "", name: "", nameAr: "", description: "", reapprovalPolicy: "reset", overrideValidHours: "168", rules: [emptyRule()] }); }, disabled: !catalogue.data, "data-testid": "new-definition", children: [_jsx(Plus, { "aria-hidden": "true" }), t("workflow.newDefinition")] }) }), _jsx(DataGrid, { label: "nav.workflows", columns: columns, data: definitions.data ?? [], rowKey: (row) => row.id, loading: definitions.isPending, emptyTitle: t("workflow.emptyDefinitions"), emptyDescription: t("workflow.emptyDefinitionsDescription"), onOpen: (row) => { setProblem(null); setChecks({}); setEditing(formFromSummary(row)); } }), _jsx(Dialog, { open: Boolean(editing), onOpenChange: (isOpen) => { if (!isOpen) {
                    setEditing(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-5xl", children: editing ? (_jsxs("form", { onSubmit: submitForm, className: "flex flex-col gap-4", "data-testid": "definition-form", children: [_jsx(DialogHeader, { children: _jsxs(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: [editing.id ? `${editing.name} · v${String(definitions.data?.find((d) => d.id === editing.id)?.version ?? "")}` : t("workflow.newDefinition"), editing.id ? _jsx("span", { className: "ms-2", children: _jsx(WorkflowStatus, { status: editing.status }) }) : null] }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-3", children: [_jsx(Field, { label: t("workflow.entityType"), required: true, children: _jsx(SelectField, { value: editing.entityType, onChange: (e) => { setForm({ entityType: e.target.value, blockKind: "" }); setChecks({}); }, disabled: Boolean(editing.id), required: true, "data-testid": "definition-entity-type", children: (catalogue.data?.subjects ?? []).map((s) => (_jsx("option", { value: s.entityType, children: localized(s.label) }, s.entityType))) }) }), _jsx(Field, { label: t("workflow.trigger"), required: true, children: _jsx(SelectField, { value: editing.trigger, onChange: (e) => { setForm({ trigger: e.target.value }); setChecks({}); }, disabled: Boolean(editing.id), "data-testid": "definition-trigger", children: (catalogue.data?.triggers ?? []).map((trigger) => (_jsx("option", { value: trigger, children: t(`workflow.triggers.${trigger}`, { defaultValue: trigger }) }, trigger))) }) }), editing.trigger === "on_block" ? (_jsx(Field, { label: t("workflow.blockKind"), required: true, children: _jsxs(SelectField, { value: editing.blockKind, onChange: (e) => { setForm({ blockKind: e.target.value }); }, disabled: Boolean(editing.id), required: true, children: [_jsx("option", { value: "", children: "\u2014" }), (subject?.blockKinds ?? []).map((kind) => (_jsx("option", { value: kind, children: kind }, kind)))] }) })) : null, _jsx(Field, { label: t("workflow.name"), required: true, children: _jsx(TextField, { value: editing.name, onChange: (e) => { setForm({ name: e.target.value }); }, required: true, "data-testid": "definition-name" }) }), _jsx(Field, { label: t("workflow.nameAr"), children: _jsx(TextField, { value: editing.nameAr, onChange: (e) => { setForm({ nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) }), _jsx(Field, { label: t("workflow.description"), children: _jsx(TextField, { value: editing.description, onChange: (e) => { setForm({ description: e.target.value }); }, lang: i18n.language }) }), _jsx(Field, { label: t("workflow.reapprovalPolicy"), children: _jsx(SelectField, { value: editing.reapprovalPolicy, onChange: (e) => { setForm({ reapprovalPolicy: e.target.value }); }, children: ["reset", "none"].map((p) => (_jsx("option", { value: p, children: t(`workflow.reapprovalPolicies.${p}`) }, p))) }) }), editing.trigger === "on_block" ? (_jsx(Field, { label: t("workflow.overrideValidHours"), children: _jsx(TextField, { type: "number", min: 1, value: editing.overrideValidHours, onChange: (e) => { setForm({ overrideValidHours: e.target.value }); }, dir: "ltr" }) })) : null] }), _jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h3", { className: "text-sm font-semibold", children: t("workflow.rules") }), _jsx(Button, { type: "button", variant: "secondary", size: "sm", onClick: () => { setForm({ rules: [...editing.rules, emptyRule()] }); }, "data-testid": "add-rule", children: t("workflow.addRule") })] }), editing.rules.map((rule, ruleIndex) => (_jsxs("fieldset", { className: "flex flex-col gap-3 rounded-md border border-border p-3", "data-testid": "rule", children: [_jsx("legend", { className: "px-1 text-sm font-medium", children: `${t("workflow.rule")} ${String(ruleIndex + 1)}` }), _jsxs("div", { className: "grid gap-3 sm:grid-cols-2", children: [_jsx(Field, { label: t("workflow.name"), required: true, children: _jsx(TextField, { value: rule.name, onChange: (e) => { setRule(ruleIndex, { name: e.target.value }); }, required: true, "data-testid": "rule-name" }) }), _jsx(Field, { label: t("workflow.nameAr"), children: _jsx(TextField, { value: rule.nameAr, onChange: (e) => { setRule(ruleIndex, { nameAr: e.target.value }); }, dir: "rtl", lang: "ar" }) })] }), _jsx(Field, { label: t("workflow.condition"), required: true, description: t("workflow.conditionHelp", { fields: (subject?.fields ?? []).map((f) => f.name).join(", "), functions: (catalogue.data?.functions ?? []).join(", ") }), children: _jsx(TextareaField, { value: rule.condition, onChange: (e) => { setRule(ruleIndex, { condition: e.target.value }); }, rows: 2, dir: "ltr", className: "font-mono", required: true, "data-testid": "rule-condition" }) }), _jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(Button, { type: "button", variant: "secondary", size: "sm", onClick: () => { check.mutate({ index: ruleIndex, expression: rule.condition }); }, loading: check.isPending, "data-testid": "check-condition", children: t("workflow.check") }), checks[ruleIndex] ? (_jsx("span", { className: checks[ruleIndex].valid ? "text-success" : "text-danger", "data-testid": "condition-check", children: checks[ruleIndex].valid ? t("workflow.conditionValid", { fields: checks[ruleIndex].fields.join(", ") }) : checks[ruleIndex].message })) : null, _jsx("span", { className: "grow" }), editing.rules.length > 1 ? (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setForm({ rules: editing.rules.filter((_, i) => i !== ruleIndex) }); }, children: t("workflow.removeRule") })) : null] }), _jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h4", { className: "text-sm font-medium", children: t("workflow.steps") }), _jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setRule(ruleIndex, { steps: [...rule.steps, emptyStep()] }); }, "data-testid": "add-step", children: t("workflow.addStep") })] }), rule.steps.map((step, stepIndex) => (_jsxs("div", { className: "grid gap-3 rounded-md bg-surface-muted p-3 sm:grid-cols-4", "data-testid": "step", children: [_jsx(Field, { label: t("workflow.step"), required: true, children: _jsx(TextField, { value: step.name, onChange: (e) => { setStep(ruleIndex, stepIndex, { name: e.target.value }); }, required: true, "data-testid": "step-name" }) }), _jsx(Field, { label: t("workflow.approverKind"), children: _jsx(SelectField, { value: step.approverKind, onChange: (e) => { setStep(ruleIndex, stepIndex, { approverKind: e.target.value === "role" ? "role" : "users" }); }, "data-testid": "step-approver-kind", children: ["role", "users"].map((kind) => (_jsx("option", { value: kind, children: t(`workflow.approverKinds.${kind}`) }, kind))) }) }), step.approverKind === "role" ? (_jsx(Field, { label: t("workflow.roleCode"), required: true, children: _jsxs(SelectField, { value: step.roleCode, onChange: (e) => { setStep(ruleIndex, stepIndex, { roleCode: e.target.value }); }, required: true, "data-testid": "step-role", children: [_jsx("option", { value: "", children: "\u2014" }), (roles.data ?? []).map((role) => (_jsxs("option", { value: role.code, children: [role.code, " \u00B7 ", localized(role.name)] }, role.id)))] }) })) : (_jsx(Field, { label: t("workflow.members"), required: true, children: _jsx(SelectField, { multiple: true, value: step.membershipIds, onChange: (e) => { setStep(ruleIndex, stepIndex, { membershipIds: selectedIds(e.target) }); }, required: true, "data-testid": "step-members", children: activeMembers.map((m) => (_jsx("option", { value: m.membershipId, children: m.displayName }, m.membershipId))) }) })), _jsx(Field, { label: t("workflow.mode"), children: _jsx(SelectField, { value: step.mode, onChange: (e) => { setStep(ruleIndex, stepIndex, { mode: e.target.value }); }, children: ["any", "all", "quorum"].map((mode) => (_jsx("option", { value: mode, children: t(`workflow.modes.${mode}`) }, mode))) }) }), step.mode === "quorum" ? (_jsx(Field, { label: t("workflow.quorum"), required: true, children: _jsx(TextField, { type: "number", min: 1, value: step.quorum, onChange: (e) => { setStep(ruleIndex, stepIndex, { quorum: e.target.value }); }, dir: "ltr", required: true }) })) : null, _jsx(Field, { label: t("workflow.timeoutHours"), children: _jsx(TextField, { type: "number", min: 1, value: step.timeoutHours, onChange: (e) => { setStep(ruleIndex, stepIndex, { timeoutHours: e.target.value }); }, dir: "ltr" }) }), _jsx(Field, { label: t("workflow.escalation"), children: _jsxs(SelectField, { value: step.escalationRoleCode, onChange: (e) => { setStep(ruleIndex, stepIndex, { escalationRoleCode: e.target.value, escalationMembershipIds: [] }); }, children: [_jsx("option", { value: "", children: "\u2014" }), (roles.data ?? []).map((role) => (_jsxs("option", { value: role.code, children: [role.code, " \u00B7 ", localized(role.name)] }, role.id)))] }) }), _jsxs("div", { className: "flex flex-col justify-end gap-2 text-sm", children: [_jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: step.allowDelegate, onChange: (e) => { setStep(ruleIndex, stepIndex, { allowDelegate: e.target.checked }); } }), t("workflow.allowDelegate")] }), _jsxs("label", { className: "flex items-center gap-2", children: [_jsx("input", { type: "checkbox", checked: step.requireComment, onChange: (e) => { setStep(ruleIndex, stepIndex, { requireComment: e.target.checked }); } }), t("workflow.requireComment")] }), rule.steps.length > 1 ? (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { setRule(ruleIndex, { steps: rule.steps.filter((_, j) => j !== stepIndex) }); }, children: t("workflow.removeStep") })) : null] })] }, stepIndex)))] }, ruleIndex)))] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setEditing(null); }, children: t("common.close") }), editing.id && editing.status === "draft" ? (_jsx(Button, { type: "button", onClick: () => { lifecycle.mutate({ id: editing.id ?? "", action: "activate" }); }, loading: lifecycle.isPending, "data-testid": "activate-definition", children: t("workflow.activate") })) : null, editing.id && editing.status === "active" ? (_jsx(Button, { type: "button", variant: "danger", onClick: () => { lifecycle.mutate({ id: editing.id ?? "", action: "retire" }); }, loading: lifecycle.isPending, "data-testid": "retire-definition", children: t("workflow.retire") })) : null, editable ? (_jsx(Button, { type: "submit", loading: save.isPending, "data-testid": "save-definition", children: t("common.save") })) : null] })] })) : null }) })] }));
}
