import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useCallback, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField, TextareaField } from "../common";
import { KeyValues, Tabs, plain } from "../inventory/shared";

type Request = components["schemas"]["RequestSummary"];
type Delegation = components["schemas"]["DelegationSummary"];

interface Evaluation {
  rules?: { rule: number; name: Record<string, string>; condition: string; matched: boolean }[];
  matched?: number | null;
}

const tones: Record<string, "success" | "accent" | "danger" | "neutral" | "warning" | "info"> = {
  approved: "success", auto_approved: "success", cleared: "success",
  pending: "accent", overridden: "accent",
  rejected: "danger", expired: "danger",
  open: "warning", waiting: "neutral", skipped: "neutral", cancelled: "neutral",
};

export function WorkflowStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="request-status">
      {t(`workflow.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
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
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [delegation, setDelegation] = useState<{ toMembershipId: string; validFrom: string; validTo: string; reason: string } | null>(null);

  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  const permissions = useMemo(() => new Set<string>(me.data?.permissions ?? []), [me.data]);
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

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["approvals"] });
    await queryClient.invalidateQueries({ queryKey: ["approval", openId] });
  };
  const open = (id: string | null): void => { setComment(""); setDelegateTo(""); setProblem(null); void navigate({ to: "/approvals", search: id ? { open: id } : {} }); };

  const act = useMutation({
    mutationFn: async (action: "approve" | "reject" | "request-changes" | "delegate" | "comment" | "cancel") => {
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
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/workflow/delegations/{delegationId}", { params: { path: { delegationId: id } } })),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["delegations"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const memberName = useCallback((membershipId: string): string => members.data?.find((m) => m.membershipId === membershipId)?.displayName ?? membershipId.slice(0, 8), [members.data]);

  const columns = useMemo<ColumnDef<Request, unknown>[]>(
    () => [
      { id: "display", accessorKey: "display", header: t("workflow.document"), size: 240, cell: ({ row }) => <span dir="auto">{row.original.display}</span> },
      { id: "entityType", accessorKey: "entityType", header: t("workflow.entityType"), size: 140, cell: ({ row }) => t(`workflow.entityTypes.${row.original.entityType}`, { defaultValue: row.original.entityType }) },
      { id: "rule", accessorFn: (row) => localized(row.ruleName), header: t("workflow.rule"), size: 180, cell: ({ row }) => localized(row.original.ruleName) || t("workflow.noRuleMatched") },
      { id: "requestedBy", accessorKey: "requestedByName", header: t("workflow.requestedBy"), size: 150 },
      { id: "step", accessorKey: "currentStepNo", header: t("workflow.step"), size: 70, cell: ({ row }) => (row.original.currentStepNo === null ? "" : String(row.original.currentStepNo)) },
      { id: "due", accessorKey: "dueAt", header: t("workflow.due"), size: 150, cell: ({ row }) => formatDateTime(row.original.dueAt) },
      { id: "created", accessorKey: "createdAt", header: t("workflow.created"), size: 150, cell: ({ row }) => formatDateTime(row.original.createdAt) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <WorkflowStatus status={row.original.status} /> },
    ],
    [t],
  );
  const delegationColumns = useMemo<ColumnDef<Delegation, unknown>[]>(
    () => [
      { id: "from", accessorKey: "fromMembershipId", header: t("workflow.from"), size: 180, cell: ({ row }) => memberName(row.original.fromMembershipId) },
      { id: "to", accessorKey: "toMembershipId", header: t("workflow.to"), size: 180, cell: ({ row }) => memberName(row.original.toMembershipId) },
      { id: "validFrom", accessorKey: "validFrom", header: t("workflow.validFrom"), size: 120, cell: ({ row }) => formatDate(row.original.validFrom) },
      { id: "validTo", accessorKey: "validTo", header: t("workflow.validTo"), size: 120, cell: ({ row }) => formatDate(row.original.validTo) },
      { id: "reason", accessorKey: "reason", header: t("workflow.reason"), size: 200 },
      { id: "remove", header: "", size: 100, cell: ({ row }) => (
        <Button type="button" variant="ghost" size="sm" onClick={() => { removeDelegation.mutate(row.original.id); }}>
          {t("workflow.remove")}
        </Button>
      ) },
    ],
    [t, memberName, removeDelegation],
  );

  const request = detail.data?.request;
  const evaluation = detail.data?.evaluation as Evaluation | undefined;
  const subject = (detail.data?.subject ?? {}) as Record<string, unknown>;
  const myId = me.data?.membershipId;
  const isRequester = myId !== undefined && request?.requestedBy === myId;
  const currentStep = detail.data?.steps.find((s) => String(s.stepNo) === String(request?.currentStepNo ?? ""));
  const submitDelegation = (event: FormEvent): void => {
    event.preventDefault();
    saveDelegation.mutate();
  };

  return (
    <>
      <PageHeader
        title={t("nav.approvals")}
        description={t("workflow.inboxDescription")}
        actions={
          tab === "delegations" ? (
            <Button onClick={() => { setProblem(null); setDelegation({ toMembershipId: "", validFrom: today(), validTo: today(), reason: "" }); }} data-testid="new-delegation">
              <Plus aria-hidden="true" />
              {t("workflow.newDelegation")}
            </Button>
          ) : null
        }
      />
      <Tabs
        tabs={[
          { id: "inbox", label: t("workflow.inbox"), testId: "tab-inbox" },
          ...(canReadAll ? [{ id: "all", label: t("workflow.all"), testId: "tab-all" }] : []),
          { id: "delegations", label: t("workflow.delegations"), testId: "tab-delegations" },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === "inbox" ? (
        <DataGrid<Request> label="workflow.inbox" columns={columns} data={inbox.data ?? []} rowKey={(row) => row.id} loading={inbox.isPending} emptyTitle={t("workflow.emptyInbox")} emptyDescription={t("workflow.emptyInboxDescription")} onOpen={(row) => { open(row.id); }} />
      ) : null}
      {tab === "all" ? (
        <>
          <div className="mb-4 grid gap-3 sm:grid-cols-3">
            <Field label={t("common.status")}>
              <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
                {allStatuses.map((s) => (
                  <option key={s} value={s}>
                    {s ? t(`workflow.statuses.${s}`) : t("accounting.anyStatus")}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          <DataGrid<Request> label="workflow.all" columns={columns} data={all.data ?? []} rowKey={(row) => row.id} loading={all.isPending} emptyTitle={t("workflow.emptyAll")} emptyDescription={t("workflow.emptyAllDescription")} onOpen={(row) => { open(row.id); }} />
        </>
      ) : null}
      {tab === "delegations" ? (
        <>
          <FormError message={problem?.message ?? null} />
          <DataGrid<Delegation> label="workflow.delegations" columns={delegationColumns} data={delegations.data ?? []} rowKey={(row) => row.id} loading={delegations.isPending} emptyTitle={t("workflow.emptyDelegations")} emptyDescription={t("workflow.emptyDelegationsDescription")} />
        </>
      ) : null}

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {request ? request.display : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail.data && request ? (
            <div className="flex flex-col gap-4" data-testid="request-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <WorkflowStatus status={request.status} />
                <span>{t(`workflow.entityTypes.${request.entityType}`, { defaultValue: request.entityType })}</span>
                {request.requestedByName ? <span className="text-fg-muted">{t("workflow.requestedBy")}: {request.requestedByName}</span> : null}
                {request.dueAt ? <span className="text-fg-muted">{t("workflow.due")}: {formatDateTime(request.dueAt)}</span> : null}
                {request.decisionAction ? <span className="text-fg-muted">{t("workflow.decision")}: {t(`workflow.actions.${request.decisionAction}`, { defaultValue: request.decisionAction })}</span> : null}
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <section className="rounded-md border border-border p-3" data-testid="why-panel">
                  <h3 className="mb-2 text-sm font-semibold">{t("workflow.why")}</h3>
                  {evaluation?.rules && evaluation.rules.length > 0 ? (
                    <ul className="flex flex-col gap-1 text-sm">
                      {evaluation.rules.map((rule) => (
                        <li key={rule.rule} className="flex flex-wrap items-baseline gap-2">
                          <Badge tone={rule.matched ? "accent" : "neutral"}>{rule.matched ? t("workflow.matched") : t("workflow.notMatched")}</Badge>
                          <span dir="auto">{localized(rule.name)}</span>
                          <code className="text-xs text-fg-muted" dir="ltr">{rule.condition}</code>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p className="text-sm text-fg-muted">{t("workflow.noRuleMatched")}</p>
                  )}
                  {evaluation?.matched === null ? <p className="mt-2 text-sm text-fg-muted">{t("workflow.noRuleMatched")}</p> : null}
                </section>
                <section className="rounded-md border border-border p-3">
                  <h3 className="mb-2 text-sm font-semibold">{t("workflow.values")}</h3>
                  <KeyValues entries={Object.entries(subject).map(([key, value]) => [key, plain(value)])} />
                </section>
              </div>
              {detail.data.block ? (
                <section className="rounded-md border border-border p-3" data-testid="block-panel">
                  <h3 className="mb-2 text-sm font-semibold">{t("workflow.block")}</h3>
                  <div className="mb-2 flex items-center gap-2 text-sm">
                    <WorkflowStatus status={detail.data.block.status} />
                    <span>{detail.data.block.kind}</span>
                  </div>
                  <KeyValues entries={Object.entries((detail.data.block.why ?? {}) as Record<string, unknown>).map(([key, value]) => [key, plain(value)])} />
                  {detail.data.override ? (
                    <p className="mt-2 text-sm">
                      {t("workflow.override")}: {memberName(detail.data.override.approvedBy ?? "")} · {detail.data.override.reason} · {t("workflow.overrideExpires")} {formatDateTime(detail.data.override.expiresAt)}
                      {detail.data.override.consumed ? ` · ${t("workflow.overrideConsumed")}` : ""}
                    </p>
                  ) : null}
                </section>
              ) : null}
              <section>
                <h3 className="mb-2 text-sm font-semibold">{t("workflow.steps")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("workflow.step")}</TableHead>
                      <TableHead>{t("workflow.mode")}</TableHead>
                      <TableHead>{t("workflow.approvers")}</TableHead>
                      <TableHead>{t("workflow.approvedBy")}</TableHead>
                      <TableHead>{t("workflow.due")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {detail.data.steps.map((step) => (
                      <TableRow key={step.stepNo} data-testid="request-step">
                        <TableCell>{String(step.stepNo)}</TableCell>
                        <TableCell dir="auto">{localized(step.name)}</TableCell>
                        <TableCell>{t(`workflow.modes.${step.mode}`, { defaultValue: step.mode })}{step.mode === "quorum" && step.quorum !== null ? ` (${String(step.quorum)})` : ""}</TableCell>
                        <TableCell>{step.approverNames.join(", ")}</TableCell>
                        <TableCell>{step.approvedBy.map((id) => memberName(id)).join(", ")}</TableCell>
                        <TableCell>{formatDateTime(step.dueAt)}{step.escalatedAt ? ` · ${t("workflow.actions.escalate")}` : ""}</TableCell>
                        <TableCell><WorkflowStatus status={step.status} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
              <section>
                <h3 className="mb-2 text-sm font-semibold">{t("workflow.history")}</h3>
                <ol className="flex flex-col gap-1 text-sm" data-testid="request-history">
                  {detail.data.actions.map((action) => (
                    <li key={action.id} className="flex flex-wrap items-baseline gap-2">
                      <span className="text-fg-muted tabular" dir="ltr">{formatDateTime(action.actedAt)}</span>
                      <span className="font-medium">{t(`workflow.actions.${action.action}`, { defaultValue: action.action })}</span>
                      <span>{action.actorName ?? ""}</span>
                      {action.onBehalfOf ? <span className="text-fg-muted">{t("workflow.onBehalfOf", { name: memberName(action.onBehalfOf) })}</span> : null}
                      {action.comment ? <span dir="auto">— {action.comment}</span> : null}
                    </li>
                  ))}
                </ol>
              </section>
              <FormError message={problem?.message ?? null} />
              {request.status === "pending" && (request.canAct || isRequester) ? (
                <div className="flex flex-col gap-3">
                  <Field label={t("workflow.comment")}>
                    <TextareaField value={comment} onChange={(e) => { setComment(e.target.value); }} placeholder={t("workflow.commentPlaceholder")} rows={2} data-testid="decision-comment" />
                  </Field>
                  <DialogFooter>
                    {request.canAct ? (
                      <>
                        <Button onClick={() => { act.mutate("approve"); }} loading={act.isPending} data-testid="approve-request">
                          {t("workflow.approve")}
                        </Button>
                        <Button variant="danger" onClick={() => { act.mutate("reject"); }} loading={act.isPending} data-testid="reject-request">
                          {t("workflow.reject")}
                        </Button>
                        <Button variant="secondary" onClick={() => { act.mutate("request-changes"); }} loading={act.isPending}>
                          {t("workflow.requestChanges")}
                        </Button>
                        {currentStep?.allowDelegate ? (
                          <span className="flex items-center gap-2">
                            <SelectField aria-label={t("workflow.delegateTo")} value={delegateTo} onChange={(e) => { setDelegateTo(e.target.value); }} data-testid="delegate-to">
                              <option value="">{t("workflow.delegateTo")}</option>
                              {(members.data ?? []).filter((m) => m.status === "active" && m.membershipId !== me.data?.membershipId).map((m) => (
                                <option key={m.membershipId} value={m.membershipId}>
                                  {m.displayName}
                                </option>
                              ))}
                            </SelectField>
                            <Button variant="secondary" onClick={() => { act.mutate("delegate"); }} loading={act.isPending} disabled={!delegateTo} data-testid="delegate-request">
                              {t("workflow.delegate")}
                            </Button>
                          </span>
                        ) : null}
                      </>
                    ) : null}
                    <Button variant="ghost" onClick={() => { act.mutate("comment"); }} loading={act.isPending} disabled={!comment.trim()}>
                      {t("workflow.comment")}
                    </Button>
                    {isRequester ? (
                      <Button variant="ghost" onClick={() => { act.mutate("cancel"); }} loading={act.isPending} data-testid="cancel-request">
                        {t("workflow.cancelRequest")}
                      </Button>
                    ) : null}
                  </DialogFooter>
                </div>
              ) : null}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(delegation)} onOpenChange={(isOpen) => { if (!isOpen) { setDelegation(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-lg">
          {delegation ? (
            <form onSubmit={submitDelegation} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("workflow.newDelegation")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <Field label={t("workflow.delegateTo")} required>
                <SelectField value={delegation.toMembershipId} onChange={(e) => { setDelegation({ ...delegation, toMembershipId: e.target.value }); }} required data-testid="delegation-to">
                  <option value="">—</option>
                  {(members.data ?? []).filter((m) => m.status === "active" && m.membershipId !== me.data?.membershipId).map((m) => (
                    <option key={m.membershipId} value={m.membershipId}>
                      {m.displayName}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("workflow.validFrom")} required>
                  <TextField type="date" value={delegation.validFrom} onChange={(e) => { setDelegation({ ...delegation, validFrom: e.target.value }); }} dir="ltr" required />
                </Field>
                <Field label={t("workflow.validTo")} required>
                  <TextField type="date" value={delegation.validTo} onChange={(e) => { setDelegation({ ...delegation, validTo: e.target.value }); }} dir="ltr" required />
                </Field>
              </div>
              <Field label={t("workflow.reason")}>
                <TextField value={delegation.reason} onChange={(e) => { setDelegation({ ...delegation, reason: e.target.value }); }} lang={i18n.language} />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setDelegation(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveDelegation.isPending} data-testid="save-delegation">
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
