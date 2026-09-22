import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, EmptyState, Field, Spinner, cn } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Bell, CheckCheck, Megaphone } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField, TextareaField } from "./common";

/** The member's inbox (paged by cursor), read/read-all, channel preferences, and announcements for those allowed. */
export function NotificationsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const [announceOpen, setAnnounceOpen] = useState(false);
  const [announce, setAnnounce] = useState({ titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", link: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [unreadOnly, setUnreadOnly] = useState(false);

  useEffect(() => {
    if (search.announce) {
      setAnnounceOpen(true);
    }
  }, [search.announce]);

  const inbox = useInfiniteQuery({
    queryKey: ["notifications", "inbox", unreadOnly],
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/collaboration/notifications", { params: { query: { unreadOnly, Limit: 50, ...(pageParam ? { Cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const preferences = useQuery({ queryKey: ["notifications", "preferences"], queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/notifications/preferences")) });
  const me = useQuery({ queryKey: ["me"], queryFn: async () => unwrap(await api.GET("/api/v1/me")) });
  const canAnnounce = (me.data?.permissions.includes("*") ?? false) || (me.data?.permissions.includes("collaboration.notification.announce") ?? false);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["notifications"] });
  const markRead = useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/collaboration/notifications/{notificationId}/read", { params: { path: { notificationId: id } } })), onSuccess: invalidate });
  const readAll = useMutation({ mutationFn: async () => unwrap(await api.POST("/api/v1/collaboration/notifications/read-all")), onSuccess: invalidate });
  const savePreferences = useMutation({
    mutationFn: async (input: { kind: string; inApp: boolean; email: boolean }[]) => unwrap(await api.PUT("/api/v1/collaboration/notifications/preferences", { body: input })),
    onSuccess: invalidate,
  });
  const sendAnnouncement = useMutation({
    mutationFn: async () =>
      unwrap(await api.POST("/api/v1/collaboration/notifications/announce", { body: { title: { en: announce.titleEn, ...(announce.titleAr ? { ar: announce.titleAr } : {}) }, body: { en: announce.bodyEn, ...(announce.bodyAr ? { ar: announce.bodyAr } : {}) }, link: announce.link || null } })),
    onSuccess: async () => {
      setAnnounceOpen(false);
      setProblem(null);
      setAnnounce({ titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", link: "" });
      void navigate({ to: "/notifications", search: {} });
      await invalidate();
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const items = inbox.data?.pages.flatMap((page) => page.items) ?? [];
  const togglePreference = (kind: string, channel: "inApp" | "email", value: boolean): void => {
    const current = (preferences.data ?? []).map((p) => ({ kind: p.kind, inApp: p.inApp, email: p.email }));
    const existing = current.find((p) => p.kind === kind);
    const next = existing ? current.map((p) => (p.kind === kind ? { ...p, [channel]: value } : p)) : [...current, { kind, inApp: channel === "inApp" ? value : true, email: channel === "email" ? value : true }];
    savePreferences.mutate(next);
  };

  return (
    <>
      <PageHeader
        title={t("nav.notifications")}
        description={t("notifications.description")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { readAll.mutate(); }} loading={readAll.isPending}>
              <CheckCheck aria-hidden="true" />
              {t("notifications.readAll")}
            </Button>
            {canAnnounce ? (
              <Button onClick={() => { setProblem(null); setAnnounceOpen(true); }} data-testid="announce">
                <Megaphone aria-hidden="true" />
                {t("notifications.announce")}
              </Button>
            ) : null}
          </>
        }
      />
      <div className="grid gap-6 lg:grid-cols-[1fr_20rem]">
        <section aria-labelledby="inbox-title" className="flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <h2 id="inbox-title" className="text-sm font-semibold uppercase tracking-wide text-fg-muted">
              {t("notifications.inbox")}
            </h2>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={unreadOnly} onChange={(e) => { setUnreadOnly(e.target.checked); }} />
              {t("notifications.unreadOnly")}
            </label>
          </div>
          {inbox.isPending ? (
            <Spinner label={t("common.loading")} />
          ) : items.length === 0 ? (
            <EmptyState icon={<Bell />} title={t("notifications.emptyTitle")} description={t("notifications.emptyDescription")} />
          ) : (
            <ul className="flex flex-col gap-2" data-testid="inbox">
              {items.map((item) => (
                <li key={item.id} className={cn("rounded-md border border-border bg-surface p-3", !item.readAt && "border-s-4 border-s-accent")}>
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="font-medium">{localized(item.title)}</div>
                      {localized(item.body) ? <p className="mt-0.5 text-sm text-fg-muted">{localized(item.body)}</p> : null}
                      <div className="mt-1 flex flex-wrap gap-2 text-xs text-fg-subtle">
                        <span>{formatDateTime(item.createdAt)}</span>
                        <span dir="ltr">{item.kind}</span>
                        {item.link ? (
                          <a href={item.link} className="text-accent underline-offset-4 hover:underline" target="_blank" rel="noreferrer">
                            {t("common.open")}
                          </a>
                        ) : null}
                      </div>
                    </div>
                    {!item.readAt ? (
                      <Button size="sm" variant="ghost" onClick={() => { markRead.mutate(item.id); }}>
                        {t("notifications.markRead")}
                      </Button>
                    ) : null}
                  </div>
                </li>
              ))}
            </ul>
          )}
          {inbox.hasNextPage ? (
            <Button variant="secondary" onClick={() => { void inbox.fetchNextPage(); }} loading={inbox.isFetchingNextPage} className="self-center">
              {t("common.loadMore")}
            </Button>
          ) : null}
        </section>
        <aside aria-labelledby="preferences-title" className="rounded-md border border-border bg-surface p-4">
          <h2 id="preferences-title" className="mb-3 text-sm font-semibold uppercase tracking-wide text-fg-muted">
            {t("notifications.preferences")}
          </h2>
          <table className="w-full text-sm">
            <thead>
              <tr className="text-start text-xs text-fg-muted">
                <th scope="col" className="pb-2 text-start">{t("notifications.kind")}</th>
                <th scope="col" className="pb-2">{t("notifications.inApp")}</th>
                <th scope="col" className="pb-2">{t("notifications.email")}</th>
              </tr>
            </thead>
            <tbody>
              {(preferences.data ?? []).map((preference) => (
                <tr key={preference.kind} className="border-t border-border">
                  <td className="py-2" dir="ltr">
                    {preference.kind === "*" ? t("notifications.allKinds") : preference.kind}
                  </td>
                  <td className="py-2 text-center">
                    <input type="checkbox" aria-label={`${preference.kind} ${t("notifications.inApp")}`} checked={preference.inApp} onChange={(e) => { togglePreference(preference.kind, "inApp", e.target.checked); }} />
                  </td>
                  <td className="py-2 text-center">
                    <input type="checkbox" aria-label={`${preference.kind} ${t("notifications.email")}`} checked={preference.email} onChange={(e) => { togglePreference(preference.kind, "email", e.target.checked); }} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </aside>
      </div>
      <Dialog open={announceOpen} onOpenChange={(open) => { setAnnounceOpen(open); if (!open) { void navigate({ to: "/notifications", search: {} }); } }}>
        <DialogContent closeLabel={t("common.close")}>
          <form onSubmit={(event: FormEvent) => { event.preventDefault(); sendAnnouncement.mutate(); }} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("notifications.announce")}</DialogTitle>
            </DialogHeader>
            <FormError message={problem?.message ?? null} />
            <Field label={t("notifications.titleEn")} required>
              <TextField value={announce.titleEn} onChange={(e) => { setAnnounce({ ...announce, titleEn: e.target.value }); }} required />
            </Field>
            <Field label={t("notifications.titleAr")}>
              <TextField value={announce.titleAr} onChange={(e) => { setAnnounce({ ...announce, titleAr: e.target.value }); }} dir="rtl" />
            </Field>
            <Field label={t("notifications.bodyEn")}>
              <TextareaField value={announce.bodyEn} onChange={(e) => { setAnnounce({ ...announce, bodyEn: e.target.value }); }} />
            </Field>
            <Field label={t("notifications.bodyAr")}>
              <TextareaField value={announce.bodyAr} onChange={(e) => { setAnnounce({ ...announce, bodyAr: e.target.value }); }} dir="rtl" />
            </Field>
            <Field label={t("notifications.link")}>
              <TextField type="url" value={announce.link} onChange={(e) => { setAnnounce({ ...announce, link: e.target.value }); }} dir="ltr" />
            </Field>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setAnnounceOpen(false); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={sendAnnouncement.isPending} data-testid="send-announcement">
                {t("notifications.send")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
