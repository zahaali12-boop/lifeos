import { Badge, Button } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, SelectField, TextareaField, TextField } from "../common";
import { ActivityIcon, useSalesReps, type CrmActivity } from "./shared";

const kinds = ["call", "meeting", "email", "task", "note"] as const;

/** Who an activity can be given to: the signed-in member and every sales rep who is a member of the workspace. */
export function useAssignees() {
  const { t } = useTranslation();
  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  const reps = useSalesReps();
  const options = new Map<string, string>();
  if (me.data) {
    options.set(me.data.membershipId, t("sales.me", { name: me.data.user.displayName }));
  }
  for (const rep of reps.data ?? []) {
    if (rep.membershipId && rep.isActive && !options.has(rep.membershipId)) {
      options.set(rep.membershipId, `${rep.code} · ${localized(rep.name)}`);
    }
  }
  return { options: [...options.entries()], me: me.data?.membershipId ?? "" };
}

/** Plans a call, meeting, email or task, or logs a note, with a partner (and one of its opportunities). */
export function ActivityForm({ partnerId, opportunityId, onSaved }: { partnerId: string; opportunityId?: string; onSaved: () => Promise<void> }) {
  const { t, i18n } = useTranslation();
  const { options, me } = useAssignees();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const empty = { kind: "call", subject: "", dueAt: "", assignee: "", body: "" };
  const [form, setForm] = useState(empty);
  const save = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/partners/crm-activities", { body: {
      partnerId, kind: form.kind, subject: form.subject, body: form.body || null,
      dueAt: form.kind === "note" || !form.dueAt ? null : new Date(form.dueAt).toISOString(),
      assignedMembershipId: form.kind === "note" ? null : (form.assignee || me || null),
      opportunityId: opportunityId ?? null, contactId: null, companyId: null,
    } })),
    onSuccess: async () => { setProblem(null); setForm(empty); await onSaved(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const isNote = form.kind === "note";
  return (
    <form onSubmit={submit} className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-4" data-testid="activity-form">
      <div className="sm:col-span-4">
        <FormError message={problem?.message ?? null} />
      </div>
      <Field label={t("sales.activityKind")}>
        <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value }); }} data-testid="activity-kind">
          {kinds.map((k) => (
            <option key={k} value={k}>
              {t(`sales.activityKinds.${k}`)}
            </option>
          ))}
        </SelectField>
      </Field>
      <Field label={t("sales.subject")} required className="sm:col-span-3">
        <TextField value={form.subject} onChange={(e) => { setForm({ ...form, subject: e.target.value }); }} required maxLength={200} lang={i18n.language} data-testid="activity-subject" />
      </Field>
      {!isNote ? (
        <>
          <Field label={t("sales.dueAt")}>
            <TextField type="datetime-local" value={form.dueAt} onChange={(e) => { setForm({ ...form, dueAt: e.target.value }); }} dir="ltr" data-testid="activity-due" />
          </Field>
          <Field label={t("sales.assignedTo")}>
            <SelectField value={form.assignee || me} onChange={(e) => { setForm({ ...form, assignee: e.target.value }); }} data-testid="activity-assignee">
              {options.map(([id, name]) => (
                <option key={id} value={id}>
                  {name}
                </option>
              ))}
            </SelectField>
          </Field>
        </>
      ) : null}
      <Field label={t("sales.details")} className={isNote ? "sm:col-span-4" : "sm:col-span-2"}>
        <TextareaField value={form.body} onChange={(e) => { setForm({ ...form, body: e.target.value }); }} rows={2} lang={i18n.language} />
      </Field>
      <div className="flex items-end sm:col-span-4">
        <Button type="submit" variant="secondary" loading={save.isPending} data-testid="save-activity">
          {isNote ? t("sales.logNote") : t("sales.planActivity")}
        </Button>
      </div>
    </form>
  );
}

/** Activities in order: open ones by due date (late ones flagged), then what was done. */
export function ActivityList({ activities, onChanged, showPartner = false, testId = "activity-list" }: { activities: CrmActivity[]; onChanged: () => Promise<void>; showPartner?: boolean; testId?: string }) {
  const { t, i18n } = useTranslation();
  const can = useCan();
  const canManage = can("partners.customer.manage");
  const [closing, setClosing] = useState<{ id: string; outcome: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const close = useMutation({
    mutationFn: async (input: { id: string; done: boolean; outcome: string }) => input.done
      ? unwrap(await api.POST("/api/v1/partners/crm-activities/{activityId}/complete", { params: { path: { activityId: input.id } }, body: { outcome: input.outcome || null } }))
      : unwrap(await api.POST("/api/v1/partners/crm-activities/{activityId}/cancel", { params: { path: { activityId: input.id } } })),
    onSuccess: async () => { setClosing(null); setProblem(null); await onChanged(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  if (activities.length === 0) {
    return <p className="text-sm text-fg-muted" data-testid={testId}>{t("sales.noActivities")}</p>;
  }
  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <FormError message={problem?.message ?? null} />
      <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
        {activities.map((a) => (
          <li key={a.id} className="flex flex-col gap-2 p-3" data-testid="activity-row">
            <div className="flex flex-wrap items-start gap-3">
              <ActivityIcon kind={a.kind} />
              <div className="flex min-w-0 flex-1 flex-col gap-1">
                <span className="font-medium" dir="auto">
                  <span className="sr-only">{t(`sales.activityKinds.${a.kind}`)}: </span>
                  {a.subject}
                </span>
                <span className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-fg-muted">
                  {showPartner ? (
                    <Link to="/sales/customers/$partnerId" params={{ partnerId: a.partnerId }} className="inline-flex min-h-6 items-center text-accent hover:underline" dir="auto">
                      {a.partnerCode} · {localized(a.partnerName)}
                    </Link>
                  ) : null}
                  {a.opportunityNumber ? <span dir="ltr">{a.opportunityNumber}</span> : null}
                  {a.dueAt ? <span>{t("sales.due", { when: formatDateTime(a.dueAt) })}</span> : null}
                  {a.assignedName ? <span>{t("sales.assignedToName", { name: a.assignedName })}</span> : null}
                  {a.completedAt && a.status !== "open" ? <span>{t(a.status === "done" ? "sales.doneOn" : "sales.cancelledOn", { when: formatDateTime(a.completedAt) })}</span> : null}
                </span>
                {a.body ? <p className="whitespace-pre-line text-sm" dir="auto">{a.body}</p> : null}
                {a.outcome ? <p className="text-sm" dir="auto"><span className="text-fg-muted">{t("sales.outcome")}: </span>{a.outcome}</p> : null}
              </div>
              <span className="flex items-center gap-2">
                {a.isOverdue ? <Badge tone="danger" data-testid="overdue-badge">{t("sales.overdue")}</Badge> : null}
                {a.status !== "open" && a.kind !== "note" ? <Badge tone={a.status === "done" ? "success" : "neutral"}>{t(`sales.activityStatuses.${a.status}`)}</Badge> : null}
                {a.status === "open" && canManage && closing?.id !== a.id ? (
                  <>
                    <Button size="sm" variant="secondary" onClick={() => { setClosing({ id: a.id, outcome: "" }); }} data-testid="complete-activity">
                      {t("sales.markDone")}
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => { close.mutate({ id: a.id, done: false, outcome: "" }); }} data-testid="cancel-activity">
                      {t("common.cancel")}
                    </Button>
                  </>
                ) : null}
              </span>
            </div>
            {closing?.id === a.id ? (
              <form
                className="flex flex-wrap items-end gap-2 ps-7"
                onSubmit={(event) => { event.preventDefault(); close.mutate({ id: a.id, done: true, outcome: closing.outcome }); }}
              >
                <Field label={t("sales.outcome")} className="min-w-64 flex-1">
                  <TextField value={closing.outcome} onChange={(e) => { setClosing({ ...closing, outcome: e.target.value }); }} lang={i18n.language} data-testid="activity-outcome" />
                </Field>
                <Button type="submit" size="sm" loading={close.isPending} data-testid="confirm-done">
                  {t("sales.markDone")}
                </Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => { setClosing(null); }}>
                  {t("sales.notYet")}
                </Button>
              </form>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  );
}
