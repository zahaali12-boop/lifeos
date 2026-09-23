import { Badge, Button, Field } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { MessageSquare, X } from "lucide-react";
import { useMemo, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { currentLanguage } from "../i18n";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { useSession } from "../session/session";
import { AttachmentsPanel } from "./AttachmentsPanel";
import { AuditEventDialog } from "./AuditEventDialog";
import { auditActionLabel, auditActionTone, auditChanges, auditValue } from "./auditLabels";
import { FormError, SelectField, TextareaField } from "./common";
import { covers } from "./PermissionPicker";

type Comment = components["schemas"]["CommentView"];
type Person = components["schemas"]["MentionableMember"];

interface RecordRef {
  entityType: string;
  entityId: string;
}

/** What the signed-in member may do here, from GET /me (the server checks every call again). */
function useAbilities() {
  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")), staleTime: 60_000 });
  return useMemo(() => {
    const grants = me.data?.permissions ?? [];
    const can = (key: string) => grants.some((g) => covers(g, key));
    return {
      loaded: me.data !== undefined,
      readComments: can("collaboration.comment.read"),
      writeComments: can("collaboration.comment.write"),
      manageComments: can("collaboration.comment.manage"),
      readActivity: can("collaboration.activity.read"),
      readAudit: can("audit.event.read"),
      attachments: can("collaboration.attachment.read"),
    };
  }, [me.data]);
}

function initials(name: string): string {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((part) => part[0]?.toUpperCase() ?? "").join("");
}

/**
 * The text box of a comment or a reply. Mentions are picked from the workspace's members (a mention notifies the
 * member and writes "@Name" into the text); each picked member shows as a chip that can be taken off again.
 * Ctrl+Enter (⌘+Enter) sends.
 */
function Composer({ label, people, initialBody = "", initialMentions = [], submitLabel, busy, problem, onSubmit, onCancel, testId }: {
  label: string;
  people: Person[];
  initialBody?: string;
  initialMentions?: string[];
  submitLabel: string;
  busy: boolean;
  problem: string | null;
  onSubmit: (body: string, mentions: string[]) => void;
  onCancel?: () => void;
  testId: string;
}) {
  const { t } = useTranslation();
  const [body, setBody] = useState(initialBody);
  const [mentions, setMentions] = useState<string[]>(initialMentions);
  const nameOf = (id: string) => people.find((p) => p.membershipId === id)?.displayName ?? t("comments.formerMember");
  const send = () => {
    if (body.trim()) {
      onSubmit(body, mentions);
    }
  };
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      send();
    }
  };

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      <Field label={label}>
        <TextareaField value={body} onChange={(e) => { setBody(e.target.value); }} onKeyDown={onKeyDown} rows={3} dir="auto" maxLength={4000} data-testid={`${testId}-body`} />
      </Field>
      <div className="flex flex-wrap items-center gap-2">
        {people.length > 0 ? (
          <SelectField
            aria-label={t("comments.mention")}
            value=""
            onChange={(e) => {
              const id = e.target.value;
              if (id && !mentions.includes(id)) {
                setMentions([...mentions, id]);
                setBody((text) => `${text}${text && !text.endsWith(" ") && !text.endsWith("\n") ? " " : ""}@${nameOf(id)} `);
              }
            }}
            className="w-52"
            data-testid={`${testId}-mention`}
          >
            <option value="">{t("comments.mentionPlaceholder")}</option>
            {people.filter((p) => !mentions.includes(p.membershipId)).map((p) => (
              <option key={p.membershipId} value={p.membershipId}>{p.displayName}</option>
            ))}
          </SelectField>
        ) : null}
        {mentions.length > 0 ? (
          <ul className="flex flex-wrap items-center gap-1 text-xs" aria-label={t("comments.notifies")}>
            <li className="text-fg-muted">{t("comments.notifies")}</li>
            {mentions.map((id) => (
              <li key={id} className="inline-flex items-center gap-1 rounded-sm bg-accent-soft px-1.5 py-0.5 text-accent" data-testid="mention-chip">
                <span dir="auto">{nameOf(id)}</span>
                <button type="button" className="rounded-sm hover:bg-accent/10" aria-label={t("comments.unmention", { name: nameOf(id) })} onClick={() => { setMentions(mentions.filter((m) => m !== id)); }}>
                  <X className="size-3" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        ) : null}
        <div className="ms-auto flex gap-2">
          {onCancel ? <Button type="button" variant="secondary" size="sm" onClick={onCancel}>{t("common.cancel")}</Button> : null}
          <Button type="button" size="sm" onClick={send} loading={busy} disabled={!body.trim()} data-testid={`${testId}-send`}>{submitLabel}</Button>
        </div>
      </div>
      <FormError message={problem} />
    </div>
  );
}

/**
 * Comments on a record: threads with replies, mentions that notify members, and edits or removal by the author (or
 * a member with collaboration.comment.manage). A removed comment keeps its place so replies still read in context.
 */
export function CommentsPanel({ entityType, entityId }: RecordRef) {
  const { t } = useTranslation();
  const session = useSession();
  const queryClient = useQueryClient();
  const can = useAbilities();
  const key = ["comments", entityType, entityId];
  const comments = useQuery({
    queryKey: key,
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/comments", { params: { query: { entityType, entityId } } })),
    enabled: can.readComments,
  });
  const people = useQuery({
    queryKey: ["comments", "mentionable"],
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/comments/mentionable")),
    enabled: can.writeComments,
    staleTime: 60_000,
  });
  const [replyTo, setReplyTo] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [problem, setProblem] = useState<{ at: string; message: string } | null>(null);
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: key });
    await queryClient.invalidateQueries({ queryKey: ["record-history", entityType, entityId] });
  };
  const fail = (at: string) => (error: unknown) => { setProblem({ at, message: toFormProblem(error, t("common.saveFailed")).message }); };

  const add = useMutation({
    mutationFn: async (input: { body: string; mentions: string[]; parentId: string | null }) =>
      unwrap(await api.POST("/api/v1/collaboration/comments", { body: { entityType, entityId, body: input.body, mentions: input.mentions, parentId: input.parentId } })),
    onSuccess: async () => { setProblem(null); setReplyTo(null); await refresh(); },
    onError: (error, input) => { fail(input.parentId ?? "new")(error); },
  });
  const edit = useMutation({
    mutationFn: async (input: { id: string; body: string; mentions: string[] }) =>
      unwrap(await api.PUT("/api/v1/collaboration/comments/{commentId}", { params: { path: { commentId: input.id } }, body: { body: input.body, mentions: input.mentions } })),
    onSuccess: async () => { setProblem(null); setEditing(null); await refresh(); },
    onError: (error, input) => { fail(input.id)(error); },
  });
  const remove = useMutation({
    mutationFn: async (id: string) => { await api.DELETE("/api/v1/collaboration/comments/{commentId}", { params: { path: { commentId: id } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); setConfirming(null); await refresh(); },
    onError: (error, id) => { fail(id)(error); },
  });
  // The "new comment" box starts empty again after each post.
  const [composerKey, setComposerKey] = useState(0);

  const list = useMemo(() => comments.data ?? [], [comments.data]);
  const everyone = people.data ?? [];
  const nameOf = (id: string) => everyone.find((p) => p.membershipId === id)?.displayName ?? list.find((c) => c.authorMembershipId === id)?.authorName ?? t("comments.formerMember");
  const replies = useMemo(() => {
    const map = new Map<string, Comment[]>();
    for (const c of list) {
      const parent = c.parentId ?? "";
      map.set(parent, [...(map.get(parent) ?? []), c]);
    }
    return map;
  }, [list]);
  const visible = list.filter((c) => !c.deletedAt).length;

  if (can.loaded && !can.readComments) {
    return <p className="text-sm text-fg-muted">{t("comments.noAccess")}</p>;
  }

  const thread = (parentId: string, depth: number) => (
    <ul className={depth === 0 ? "flex flex-col gap-3" : "mt-3 flex flex-col gap-3 border-s-2 border-border ps-4"}>
      {(replies.get(parentId) ?? []).map((c) => {
        const mine = c.authorMembershipId === session?.membershipId;
        const mayChange = !c.deletedAt && (mine || can.manageComments);
        return (
          <li key={c.id} data-testid="comment">
            <article aria-label={t("comments.by", { name: c.authorName })} className="flex gap-3">
              <span aria-hidden="true" className="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent-soft text-xs font-semibold text-accent">{initials(c.authorName)}</span>
              <div className="min-w-0 flex-1">
                <p className="flex flex-wrap items-baseline gap-x-2 text-sm">
                  <span className="font-semibold" dir="auto">{c.authorName}</span>
                  <time dateTime={c.createdAt} className="text-xs text-fg-muted">{formatDateTime(c.createdAt)}</time>
                  {c.editedAt && !c.deletedAt ? <span className="text-xs text-fg-muted" data-testid="comment-edited">{t("comments.edited")}</span> : null}
                </p>
                {editing === c.id ? (
                  <Composer
                    label={t("comments.editLabel")}
                    people={everyone}
                    initialBody={c.body}
                    initialMentions={c.mentions}
                    submitLabel={t("common.save")}
                    busy={edit.isPending}
                    problem={problem?.at === c.id ? problem.message : null}
                    onSubmit={(body, mentions) => { edit.mutate({ id: c.id, body, mentions }); }}
                    onCancel={() => { setEditing(null); setProblem(null); }}
                    testId="comment-edit"
                  />
                ) : c.deletedAt ? (
                  <p className="text-sm italic text-fg-muted" data-testid="comment-removed">{t("comments.removed")}</p>
                ) : (
                  <>
                    <p className="whitespace-pre-wrap break-words text-sm" dir="auto" data-testid="comment-body">{c.body}</p>
                    {c.mentions.length > 0 ? (
                      <p className="mt-1 text-xs text-fg-muted" data-testid="comment-mentions">{t("comments.notified", { names: new Intl.ListFormat(currentLanguage(), { type: "conjunction" }).format(c.mentions.map(nameOf)) })}</p>
                    ) : null}
                  </>
                )}
                {editing !== c.id && !c.deletedAt ? (
                  <div className="mt-1 flex flex-wrap items-center gap-1">
                    {can.writeComments ? (
                      <Button variant="ghost" size="sm" onClick={() => { setReplyTo(replyTo === c.id ? null : c.id); setProblem(null); }} data-testid="comment-reply">{t("comments.reply")}</Button>
                    ) : null}
                    {mayChange ? (
                      <Button variant="ghost" size="sm" onClick={() => { setEditing(c.id); setReplyTo(null); setProblem(null); }} data-testid="comment-edit-open">{t("common.edit")}</Button>
                    ) : null}
                    {mayChange && confirming !== c.id ? (
                      <Button variant="ghost" size="sm" onClick={() => { setConfirming(c.id); }} data-testid="comment-remove">{t("comments.remove")}</Button>
                    ) : null}
                    {confirming === c.id ? (
                      <span role="group" aria-label={t("comments.confirmRemove")} className="inline-flex items-center gap-1 text-sm">
                        <span>{t("comments.confirmRemove")}</span>
                        <Button variant="danger" size="sm" onClick={() => { remove.mutate(c.id); }} loading={remove.isPending} data-testid="comment-remove-confirm">{t("comments.remove")}</Button>
                        <Button variant="ghost" size="sm" onClick={() => { setConfirming(null); }}>{t("comments.keep")}</Button>
                      </span>
                    ) : null}
                  </div>
                ) : null}
                {problem?.at === c.id && editing !== c.id && replyTo !== c.id ? <FormError message={problem.message} /> : null}
                {replyTo === c.id ? (
                  <div className="mt-2">
                    <Composer
                      label={t("comments.replyLabel", { name: c.authorName })}
                      people={everyone}
                      submitLabel={t("comments.reply")}
                      busy={add.isPending}
                      problem={problem?.at === c.id ? problem.message : null}
                      onSubmit={(body, mentions) => { add.mutate({ body, mentions, parentId: c.id }); }}
                      onCancel={() => { setReplyTo(null); setProblem(null); }}
                      testId="comment-reply-box"
                    />
                  </div>
                ) : null}
                {replies.has(c.id) ? thread(c.id, depth + 1) : null}
              </div>
            </article>
          </li>
        );
      })}
    </ul>
  );

  return (
    <section className="flex flex-col gap-3" aria-label={t("comments.title", { count: visible })} data-testid="comments">
      <h3 className="flex items-center gap-1.5 text-sm font-semibold">
        <MessageSquare className="size-4" aria-hidden="true" />
        {t("comments.title", { count: visible })}
      </h3>
      {comments.isPending && can.readComments ? <p className="text-sm text-fg-muted">{t("common.loading")}</p> : null}
      {!comments.isPending && list.length === 0 ? <p className="text-sm text-fg-muted">{t("comments.none")}</p> : null}
      {list.length > 0 ? thread("", 0) : null}
      {can.writeComments ? (
        <Composer
          key={composerKey}
          label={t("comments.newLabel")}
          people={everyone}
          submitLabel={t("comments.send")}
          busy={add.isPending && replyTo === null}
          problem={problem?.at === "new" ? problem.message : null}
          onSubmit={(body, mentions) => { add.mutate({ body, mentions, parentId: null }, { onSuccess: () => { setComposerKey((k) => k + 1); } }); }}
          testId="comment-new"
        />
      ) : null}
    </section>
  );
}

type HistoryEntry =
  | { kind: "audit"; at: string; id: string; event: components["schemas"]["AuditEventDetail"] }
  | { kind: "activity"; at: string; id: string; activity: components["schemas"]["ActivityView"] };

const shownChanges = 4;

/**
 * Everything that happened to a record, newest first: its audit trail (who changed which fields, from what to what;
 * each event opens in full) and its activity (comments, files, links). Each half needs its own permission.
 */
export function RecordHistory({ entityType, entityId }: RecordRef) {
  const { t } = useTranslation();
  const can = useAbilities();
  const [open, setOpen] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const audit = useQuery({
    queryKey: ["record-history", entityType, entityId, "audit"],
    queryFn: async () => unwrap(await api.GET("/api/v1/audit/records/{entityType}/{entityId}", { params: { path: { entityType, entityId } } })),
    enabled: can.readAudit,
  });
  const activity = useInfiniteQuery({
    queryKey: ["record-history", entityType, entityId, "activity"],
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET("/api/v1/collaboration/activities", { params: { query: { entityType, entityId, Limit: 50, ...(pageParam ? { Cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: can.readActivity,
  });

  const entries = useMemo<HistoryEntry[]>(() => {
    const fromAudit = (audit.data ?? []).map((event): HistoryEntry => ({ kind: "audit", at: event.occurredAt, id: event.id, event }));
    const fromActivity = (activity.data?.pages.flatMap((p) => p.items) ?? []).map((a): HistoryEntry => ({ kind: "activity", at: a.createdAt, id: a.id, activity: a }));
    return [...fromAudit, ...fromActivity].sort((a, b) => (a.at < b.at ? 1 : a.at > b.at ? -1 : 0));
  }, [audit.data, activity.data]);

  if (can.loaded && !can.readAudit && !can.readActivity) {
    return <p className="text-sm text-fg-muted">{t("history.noAccess")}</p>;
  }
  const loading = (can.readAudit && audit.isPending) || (can.readActivity && activity.isPending);

  return (
    <section className="flex flex-col gap-3" aria-label={t("history.title")} data-testid="record-history">
      {loading ? <p className="text-sm text-fg-muted">{t("common.loading")}</p> : null}
      {!loading && entries.length === 0 ? <p className="text-sm text-fg-muted">{t("history.none")}</p> : null}
      <ol className="flex flex-col gap-3">
        {entries.map((entry) => {
          if (entry.kind === "activity") {
            const group = entry.activity.kind.split(".")[0] ?? "";
            return (
              <li key={entry.id} className="flex flex-wrap items-baseline gap-2 text-sm" data-testid="history-activity">
                <Badge tone="neutral">{t(`history.kinds.${group}`, { defaultValue: group })}</Badge>
                <span dir="auto">{localized(entry.activity.summary)}</span>
                <time dateTime={entry.at} className="text-xs text-fg-muted">{formatDateTime(entry.at)}</time>
              </li>
            );
          }
          const e = entry.event;
          const changes = auditChanges(e.diff);
          const all = expanded.has(e.id);
          return (
            <li key={entry.id} className="flex flex-col gap-1 text-sm" data-testid="history-audit">
              <div className="flex flex-wrap items-baseline gap-2">
                <Badge tone={auditActionTone(e.action)}>{auditActionLabel(t, e.action)}</Badge>
                <span dir="auto">{e.actor.display}</span>
                <time dateTime={entry.at} className="text-xs text-fg-muted">{formatDateTime(entry.at)}</time>
                <Button variant="ghost" size="sm" className="ms-auto" onClick={() => { setOpen(e.id); }} aria-label={t("history.openEvent", { action: auditActionLabel(t, e.action), when: formatDateTime(entry.at) })} data-testid="history-open">
                  {t("history.details")}
                </Button>
              </div>
              {e.reason ? <p className="text-xs text-fg-muted" dir="auto">{t("history.reason", { reason: e.reason })}</p> : null}
              {changes.length > 0 ? (
                <ul className="flex flex-col gap-0.5 ps-2 text-xs">
                  {(all ? changes : changes.slice(0, shownChanges)).map((c) => (
                    <li key={c.field} className="flex flex-wrap gap-1" data-testid="history-change">
                      <code dir="ltr" className="text-fg-muted">{c.field}</code>
                      <span dir="auto" className="text-fg-muted line-through decoration-fg-muted/50">{auditValue(t, c.before)}</span>
                      <span aria-hidden="true">→</span>
                      <span className="sr-only">{t("history.became")}</span>
                      <span dir="auto">{auditValue(t, c.after)}</span>
                    </li>
                  ))}
                  {changes.length > shownChanges ? (
                    <li>
                      <button type="button" className="text-accent hover:underline" onClick={() => { const next = new Set(expanded); if (all) { next.delete(e.id); } else { next.add(e.id); } setExpanded(next); }}>
                        {all ? t("history.fewer") : t("history.more", { count: changes.length - shownChanges })}
                      </button>
                    </li>
                  ) : null}
                </ul>
              ) : null}
            </li>
          );
        })}
      </ol>
      {activity.hasNextPage ? (
        <Button variant="secondary" size="sm" className="self-start" onClick={() => { void activity.fetchNextPage(); }} loading={activity.isFetchingNextPage}>
          {t("common.loadMore")}
        </Button>
      ) : null}
      <AuditEventDialog eventId={open} onClose={() => { setOpen(null); }} />
    </section>
  );
}

/** A record's conversation: its comments, then the files attached to it (when the member may see them). */
export function RecordDiscussion({ entityType, entityId, files = true }: RecordRef & { files?: boolean }) {
  const can = useAbilities();
  return (
    <div className="flex flex-col gap-6">
      <CommentsPanel entityType={entityType} entityId={entityId} />
      {files && can.attachments ? <AttachmentsPanel entityType={entityType} entityId={entityId} /> : null}
    </div>
  );
}
