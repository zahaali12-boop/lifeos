import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField, TextareaField } from "../common";
import { KeyValues, Tabs, plain } from "../inventory/shared";
const tones = {
    approved: "success", auto_approved: "success", cleared: "success",
    pending: "accent", overridden: "accent",
    rejected: "danger", expired: "danger",
    open: "warning", waiting: "neutral", skipped: "neutral", cancelled: "neutral",
};
export function WorkflowStatus({ status }) {
    const { t } = useTranslation();
    return (_jsx(Badge, { tone: tones[status] ?? "neutral", "data-testid": "request-status", children: t(`workflow.statuses.${status}`, { defaultValue: status }) }));
}
const allStatuses = ["", "pending", "approved", "rejected", "cancelled", "expired", "auto_approved"];
/** The approvals inbox (roadmap 4.0, ADR-0020): what waits for the member, the why panel, the history, and the decision; plus every request for readers and the member's delegations. */
export function ApprovalsPage() {
    const { t, i18n } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const search = useSearch({ strict: false });
    const openId = search.open;
    const [tab, setTab] = useState("inbox");
    const [status, setStatus] = useState("pending");
    const [comment, setComment] = useState("");
    const [delegateTo, setDelegateTo] = useState("");
    const [problem, setProblem] = useState(null);
    const [delegation, setDelegation] = useState(null);
    const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
    const permissions = useMemo(() => new Set(me.data?.permissions ?? []), [me.data]);
    const canReadAll = permissions.has("*") || permissions.has("workflow.request.read");
    const inbox = useQuery({ queryKey: ["approvals", "inbox"], queryFn: async () => unwrap(await api.GET("/api/v1/workflow/requests")) });
    const all = useQuery({
        queryKey: ["approvals", "all", status],
        enabled: tab === "all" && canReadAll,
        queryFn: async () => unwrap(await api.GET("/api/v1/workflow/requests", { params: { query: { mine: false, ...(status ? { status } : {}) } } })),
    });
    const detail = useQuery({
        queryKey: ["approval", openId],
        enabled: Boolean(openId),
        queryFn: async () => unwrap(await api.GET("/api/v1/workflow/requests/{requestId}", { params: { path: { requestId: openId ?? "" } } })),
    });
    const members = useQuery({ queryKey: ["members"], queryFn: async () => unwrap(await api.GET("/api/v1/users")), enabled: Boolean(openId) || tab === "delegations" });
    const delegations = useQuery({ queryKey: ["delegations"], enabled: tab === "delegations", queryFn: async () => unwrap(await api.GET("/api/v1/workflow/delegations")) });
    const refresh = async () => {
        await queryClient.invalidateQueries({ queryKey: ["approvals"] });
        await queryClient.invalidateQueries({ queryKey: ["approval", openId] });
    };
    const open = (id) => { setComment(""); setDelegateTo(""); setProblem(null); void navigate({ to: "/approvals", search: id ? { open: id } : {} }); };
    const act = useMutation({
        mutationFn: async (action) => {
            const requestId = openId ?? "";
            const text = comment.trim() || null;
            switch (action) {
                case "approve":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/approve", { params: { path: { requestId } }, body: { comment: text } }));
                case "reject":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/reject", { params: { path: { requestId } }, body: { comment: text } }));
                case "request-changes":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/request-changes", { params: { path: { requestId } }, body: { comment: text } }));
                case "delegate":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/delegate", { params: { path: { requestId } }, body: { toMembershipId: delegateTo, comment: text } }));
                case "comment":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/comment", { params: { path: { requestId } }, body: { comment: text } }));
                case "cancel":
                    return unwrap(await api.POST("/api/v1/workflow/requests/{requestId}/cancel", { params: { path: { requestId } }, body: { reason: text ?? "" } }));
            }
        },
        onSuccess: async () => {
            setProblem(null);
            setComment("");
            await refresh();
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const saveDelegation = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/workflow/delegations", { body: { toMembershipId: delegation?.toMembershipId ?? "", validFrom: delegation?.validFrom ?? "", validTo: delegation?.validTo ?? "", reason: delegation?.reason ?? null } })),
        onSuccess: async () => {
            setDelegation(null);
            setProblem(null);
            await queryClient.invalidateQueries({ queryKey: ["delegations"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const removeDelegation = useMutation({
        mutationFn: async (id) => unwrap(await api.DELETE("/api/v1/workflow/delegations/{delegationId}", { params: { path: { delegationId: id } } })),
        onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["delegations"] }); },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const memberName = useCallback((membershipId) => members.data?.find((m) => m.membershipId === membershipId)?.displayName ?? membershipId.slice(0, 8), [members.data]);
    const columns = useMemo(() => [
        { id: "display", accessorKey: "display", header: t("workflow.document"), size: 240, cell: ({ row }) => _jsx("span", { dir: "auto", children: row.original.display }) },
        { id: "entityType", accessorKey: "entityType", header: t("workflow.entityType"), size: 140, cell: ({ row }) => t(`workflow.entityTypes.${row.original.entityType}`, { defaultValue: row.original.entityType }) },
        { id: "rule", accessorFn: (row) => localized(row.ruleName), header: t("workflow.rule"), size: 180, cell: ({ row }) => localized(row.original.ruleName) || t("workflow.noRuleMatched") },
        { id: "requestedBy", accessorKey: "requestedByName", header: t("workflow.requestedBy"), size: 150 },
        { id: "step", accessorKey: "currentStepNo", header: t("workflow.step"), size: 70, cell: ({ row }) => (row.original.currentStepNo === null ? "" : String(row.original.currentStepNo)) },
        { id: "due", accessorKey: "dueAt", header: t("workflow.due"), size: 150, cell: ({ row }) => formatDateTime(row.original.dueAt) },
        { id: "created", accessorKey: "createdAt", header: t("workflow.created"), size: 150, cell: ({ row }) => formatDateTime(row.original.createdAt) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => _jsx(WorkflowStatus, { status: row.original.status }) },
    ], [t]);
    const delegationColumns = useMemo(() => [
        { id: "from", accessorKey: "fromMembershipId", header: t("workflow.from"), size: 180, cell: ({ row }) => memberName(row.original.fromMembershipId) },
        { id: "to", accessorKey: "toMembershipId", header: t("workflow.to"), size: 180, cell: ({ row }) => memberName(row.original.toMembershipId) },
        { id: "validFrom", accessorKey: "validFrom", header: t("workflow.validFrom"), size: 120, cell: ({ row }) => formatDate(row.original.validFrom) },
        { id: "validTo", accessorKey: "validTo", header: t("workflow.validTo"), size: 120, cell: ({ row }) => formatDate(row.original.validTo) },
        { id: "reason", accessorKey: "reason", header: t("workflow.reason"), size: 200 },
        { id: "remove", header: "", size: 100, cell: ({ row }) => (_jsx(Button, { type: "button", variant: "ghost", size: "sm", onClick: () => { removeDelegation.mutate(row.original.id); }, children: t("workflow.remove") })) },
    ], [t, memberName, removeDelegation]);
    const request = detail.data?.request;
    const evaluation = detail.data?.evaluation;
    const subject = (detail.data?.subject ?? {});
    const myId = me.data?.membershipId;
    const isRequester = myId !== undefined && request?.requestedBy === myId;
    const currentStep = detail.data?.steps.find((s) => String(s.stepNo) === String(request?.currentStepNo ?? ""));
    const submitDelegation = (event) => {
        event.preventDefault();
        saveDelegation.mutate();
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.approvals"), description: t("workflow.inboxDescription"), actions: tab === "delegations" ? (_jsxs(Button, { onClick: () => { setProblem(null); setDelegation({ toMembershipId: "", validFrom: today(), validTo: today(), reason: "" }); }, "data-testid": "new-delegation", children: [_jsx(Plus, { "aria-hidden": "true" }), t("workflow.newDelegation")] })) : null }), _jsx(Tabs, { tabs: [
                    { id: "inbox", label: t("workflow.inbox"), testId: "tab-inbox" },
                    ...(canReadAll ? [{ id: "all", label: t("workflow.all"), testId: "tab-all" }] : []),
                    { id: "delegations", label: t("workflow.delegations"), testId: "tab-delegations" },
                ], value: tab, onChange: setTab }), tab === "inbox" ? (_jsx(DataGrid, { label: "workflow.inbox", columns: columns, data: inbox.data ?? [], rowKey: (row) => row.id, loading: inbox.isPending, emptyTitle: t("workflow.emptyInbox"), emptyDescription: t("workflow.emptyInboxDescription"), onOpen: (row) => { open(row.id); } })) : null, tab === "all" ? (_jsxs(_Fragment, { children: [_jsx("div", { className: "mb-4 grid gap-3 sm:grid-cols-3", children: _jsx(Field, { label: t("common.status"), children: _jsx(SelectField, { value: status, onChange: (e) => { setStatus(e.target.value); }, "data-testid": "status-filter", children: allStatuses.map((s) => (_jsx("option", { value: s, children: s ? t(`workflow.statuses.${s}`) : t("accounting.anyStatus") }, s))) }) }) }), _jsx(DataGrid, { label: "workflow.all", columns: columns, data: all.data ?? [], rowKey: (row) => row.id, loading: all.isPending, emptyTitle: t("workflow.emptyAll"), emptyDescription: t("workflow.emptyAllDescription"), onOpen: (row) => { open(row.id); } })] })) : null, tab === "delegations" ? (_jsxs(_Fragment, { children: [_jsx(FormError, { message: problem?.message ?? null }), _jsx(DataGrid, { label: "workflow.delegations", columns: delegationColumns, data: delegations.data ?? [], rowKey: (row) => row.id, loading: delegations.isPending, emptyTitle: t("workflow.emptyDelegations"), emptyDescription: t("workflow.emptyDelegationsDescription") })] })) : null, _jsx(Dialog, { open: Boolean(openId), onOpenChange: (isOpen) => { if (!isOpen) {
                    open(null);
                } }, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-4xl", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", dir: "auto", children: request ? request.display : t("common.loading") }) }), detail.data && request ? (_jsxs("div", { className: "flex flex-col gap-4", "data-testid": "request-detail", children: [_jsxs("div", { className: "flex flex-wrap items-center gap-2 text-sm", children: [_jsx(WorkflowStatus, { status: request.status }), _jsx("span", { children: t(`workflow.entityTypes.${request.entityType}`, { defaultValue: request.entityType }) }), request.requestedByName ? _jsxs("span", { className: "text-fg-muted", children: [t("workflow.requestedBy"), ": ", request.requestedByName] }) : null, request.dueAt ? _jsxs("span", { className: "text-fg-muted", children: [t("workflow.due"), ": ", formatDateTime(request.dueAt)] }) : null, request.decisionAction ? _jsxs("span", { className: "text-fg-muted", children: [t("workflow.decision"), ": ", t(`workflow.actions.${request.decisionAction}`, { defaultValue: request.decisionAction })] }) : null] }), _jsxs("div", { className: "grid gap-4 md:grid-cols-2", children: [_jsxs("section", { className: "rounded-md border border-border p-3", "data-testid": "why-panel", children: [_jsx("h3", { className: "mb-2 text-sm font-semibold", children: t("workflow.why") }), evaluation?.rules && evaluation.rules.length > 0 ? (_jsx("ul", { className: "flex flex-col gap-1 text-sm", children: evaluation.rules.map((rule) => (_jsxs("li", { className: "flex flex-wrap items-baseline gap-2", children: [_jsx(Badge, { tone: rule.matched ? "accent" : "neutral", children: rule.matched ? t("workflow.matched") : t("workflow.notMatched") }), _jsx("span", { dir: "auto", children: localized(rule.name) }), _jsx("code", { className: "text-xs text-fg-muted", dir: "ltr", children: rule.condition })] }, rule.rule))) })) : (_jsx("p", { className: "text-sm text-fg-muted", children: t("workflow.noRuleMatched") })), evaluation?.matched === null ? _jsx("p", { className: "mt-2 text-sm text-fg-muted", children: t("workflow.noRuleMatched") }) : null] }), _jsxs("section", { className: "rounded-md border border-border p-3", children: [_jsx("h3", { className: "mb-2 text-sm font-semibold", children: t("workflow.values") }), _jsx(KeyValues, { entries: Object.entries(subject).map(([key, value]) => [key, plain(value)]) })] })] }), detail.data.block ? (_jsxs("section", { className: "rounded-md border border-border p-3", "data-testid": "block-panel", children: [_jsx("h3", { className: "mb-2 text-sm font-semibold", children: t("workflow.block") }), _jsxs("div", { className: "mb-2 flex items-center gap-2 text-sm", children: [_jsx(WorkflowStatus, { status: detail.data.block.status }), _jsx("span", { children: detail.data.block.kind })] }), _jsx(KeyValues, { entries: Object.entries((detail.data.block.why ?? {})).map(([key, value]) => [key, plain(value)]) }), detail.data.override ? (_jsxs("p", { className: "mt-2 text-sm", children: [t("workflow.override"), ": ", memberName(detail.data.override.approvedBy ?? ""), " \u00B7 ", detail.data.override.reason, " \u00B7 ", t("workflow.overrideExpires"), " ", formatDateTime(detail.data.override.expiresAt), detail.data.override.consumed ? ` · ${t("workflow.overrideConsumed")}` : ""] })) : null] })) : null, _jsxs("section", { children: [_jsx("h3", { className: "mb-2 text-sm font-semibold", children: t("workflow.steps") }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: "#" }), _jsx(TableHead, { children: t("workflow.step") }), _jsx(TableHead, { children: t("workflow.mode") }), _jsx(TableHead, { children: t("workflow.approvers") }), _jsx(TableHead, { children: t("workflow.approvedBy") }), _jsx(TableHead, { children: t("workflow.due") }), _jsx(TableHead, { children: t("common.status") })] }) }), _jsx(TableBody, { children: detail.data.steps.map((step) => (_jsxs(TableRow, { "data-testid": "request-step", children: [_jsx(TableCell, { children: String(step.stepNo) }), _jsx(TableCell, { dir: "auto", children: localized(step.name) }), _jsxs(TableCell, { children: [t(`workflow.modes.${step.mode}`, { defaultValue: step.mode }), step.mode === "quorum" && step.quorum !== null ? ` (${String(step.quorum)})` : ""] }), _jsx(TableCell, { children: step.approverNames.join(", ") }), _jsx(TableCell, { children: step.approvedBy.map((id) => memberName(id)).join(", ") }), _jsxs(TableCell, { children: [formatDateTime(step.dueAt), step.escalatedAt ? ` · ${t("workflow.actions.escalate")}` : ""] }), _jsx(TableCell, { children: _jsx(WorkflowStatus, { status: step.status }) })] }, step.stepNo))) })] })] }), _jsxs("section", { children: [_jsx("h3", { className: "mb-2 text-sm font-semibold", children: t("workflow.history") }), _jsx("ol", { className: "flex flex-col gap-1 text-sm", "data-testid": "request-history", children: detail.data.actions.map((action) => (_jsxs("li", { className: "flex flex-wrap items-baseline gap-2", children: [_jsx("span", { className: "text-fg-muted tabular", dir: "ltr", children: formatDateTime(action.actedAt) }), _jsx("span", { className: "font-medium", children: t(`workflow.actions.${action.action}`, { defaultValue: action.action }) }), _jsx("span", { children: action.actorName ?? "" }), action.onBehalfOf ? _jsx("span", { className: "text-fg-muted", children: t("workflow.onBehalfOf", { name: memberName(action.onBehalfOf) }) }) : null, action.comment ? _jsxs("span", { dir: "auto", children: ["\u2014 ", action.comment] }) : null] }, action.id))) })] }), _jsx(FormError, { message: problem?.message ?? null }), request.status === "pending" && (request.canAct || isRequester) ? (_jsxs("div", { className: "flex flex-col gap-3", children: [_jsx(Field, { label: t("workflow.comment"), children: _jsx(TextareaField, { value: comment, onChange: (e) => { setComment(e.target.value); }, placeholder: t("workflow.commentPlaceholder"), rows: 2, "data-testid": "decision-comment" }) }), _jsxs(DialogFooter, { children: [request.canAct ? (_jsxs(_Fragment, { children: [_jsx(Button, { onClick: () => { act.mutate("approve"); }, loading: act.isPending, "data-testid": "approve-request", children: t("workflow.approve") }), _jsx(Button, { variant: "danger", onClick: () => { act.mutate("reject"); }, loading: act.isPending, "data-testid": "reject-request", children: t("workflow.reject") }), _jsx(Button, { variant: "secondary", onClick: () => { act.mutate("request-changes"); }, loading: act.isPending, children: t("workflow.requestChanges") }), currentStep?.allowDelegate ? (_jsxs("span", { className: "flex items-center gap-2", children: [_jsxs(SelectField, { "aria-label": t("workflow.delegateTo"), value: delegateTo, onChange: (e) => { setDelegateTo(e.target.value); }, "data-testid": "delegate-to", children: [_jsx("option", { value: "", children: t("workflow.delegateTo") }), (members.data ?? []).filter((m) => m.status === "active" && m.membershipId !== me.data?.membershipId).map((m) => (_jsx("option", { value: m.membershipId, children: m.displayName }, m.membershipId)))] }), _jsx(Button, { variant: "secondary", onClick: () => { act.mutate("delegate"); }, loading: act.isPending, disabled: !delegateTo, "data-testid": "delegate-request", children: t("workflow.delegate") })] })) : null] })) : null, _jsx(Button, { variant: "ghost", onClick: () => { act.mutate("comment"); }, loading: act.isPending, disabled: !comment.trim(), children: t("workflow.comment") }), isRequester ? (_jsx(Button, { variant: "ghost", onClick: () => { act.mutate("cancel"); }, loading: act.isPending, "data-testid": "cancel-request", children: t("workflow.cancelRequest") })) : null] })] })) : null] })) : null] }) }), _jsx(Dialog, { open: Boolean(delegation), onOpenChange: (isOpen) => { if (!isOpen) {
                    setDelegation(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "max-w-lg", children: delegation ? (_jsxs("form", { onSubmit: submitDelegation, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("workflow.newDelegation") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(Field, { label: t("workflow.delegateTo"), required: true, children: _jsxs(SelectField, { value: delegation.toMembershipId, onChange: (e) => { setDelegation({ ...delegation, toMembershipId: e.target.value }); }, required: true, "data-testid": "delegation-to", children: [_jsx("option", { value: "", children: "\u2014" }), (members.data ?? []).filter((m) => m.status === "active" && m.membershipId !== me.data?.membershipId).map((m) => (_jsx("option", { value: m.membershipId, children: m.displayName }, m.membershipId)))] }) }), _jsxs("div", { className: "grid gap-4 sm:grid-cols-2", children: [_jsx(Field, { label: t("workflow.validFrom"), required: true, children: _jsx(TextField, { type: "date", value: delegation.validFrom, onChange: (e) => { setDelegation({ ...delegation, validFrom: e.target.value }); }, dir: "ltr", required: true }) }), _jsx(Field, { label: t("workflow.validTo"), required: true, children: _jsx(TextField, { type: "date", value: delegation.validTo, onChange: (e) => { setDelegation({ ...delegation, validTo: e.target.value }); }, dir: "ltr", required: true }) })] }), _jsx(Field, { label: t("workflow.reason"), children: _jsx(TextField, { value: delegation.reason, onChange: (e) => { setDelegation({ ...delegation, reason: e.target.value }); }, lang: i18n.language }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setDelegation(null); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: saveDelegation.isPending, "data-testid": "save-delegation", children: t("common.save") })] })] })) : null }) })] }));
}
