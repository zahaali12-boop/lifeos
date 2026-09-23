import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, EmptyState, Field, Spinner, cn } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import { Bell, CheckCheck, Megaphone } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField, TextareaField } from "./common";
/** The member's inbox (paged by cursor), read/read-all, channel preferences, and announcements for those allowed. */
export function NotificationsPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const search = useSearch({ strict: false });
    const [announceOpen, setAnnounceOpen] = useState(false);
    const [announce, setAnnounce] = useState({ titleEn: "", titleAr: "", bodyEn: "", bodyAr: "", link: "" });
    const [problem, setProblem] = useState(null);
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
    const markRead = useMutation({ mutationFn: async (id) => unwrap(await api.POST("/api/v1/collaboration/notifications/{notificationId}/read", { params: { path: { notificationId: id } } })), onSuccess: invalidate });
    const readAll = useMutation({ mutationFn: async () => unwrap(await api.POST("/api/v1/collaboration/notifications/read-all")), onSuccess: invalidate });
    const savePreferences = useMutation({
        mutationFn: async (input) => unwrap(await api.PUT("/api/v1/collaboration/notifications/preferences", { body: input })),
        onSuccess: invalidate,
    });
    const sendAnnouncement = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/collaboration/notifications/announce", { body: { title: { en: announce.titleEn, ...(announce.titleAr ? { ar: announce.titleAr } : {}) }, body: { en: announce.bodyEn, ...(announce.bodyAr ? { ar: announce.bodyAr } : {}) }, link: announce.link || null } })),
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
    const togglePreference = (kind, channel, value) => {
        const current = (preferences.data ?? []).map((p) => ({ kind: p.kind, inApp: p.inApp, email: p.email }));
        const existing = current.find((p) => p.kind === kind);
        const next = existing ? current.map((p) => (p.kind === kind ? { ...p, [channel]: value } : p)) : [...current, { kind, inApp: channel === "inApp" ? value : true, email: channel === "email" ? value : true }];
        savePreferences.mutate(next);
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.notifications"), description: t("notifications.description"), actions: _jsxs(_Fragment, { children: [_jsxs(Button, { variant: "secondary", onClick: () => { readAll.mutate(); }, loading: readAll.isPending, children: [_jsx(CheckCheck, { "aria-hidden": "true" }), t("notifications.readAll")] }), canAnnounce ? (_jsxs(Button, { onClick: () => { setProblem(null); setAnnounceOpen(true); }, "data-testid": "announce", children: [_jsx(Megaphone, { "aria-hidden": "true" }), t("notifications.announce")] })) : null] }) }), _jsxs("div", { className: "grid gap-6 lg:grid-cols-[1fr_20rem]", children: [_jsxs("section", { "aria-labelledby": "inbox-title", className: "flex flex-col gap-2", children: [_jsxs("div", { className: "flex items-center justify-between", children: [_jsx("h2", { id: "inbox-title", className: "text-sm font-semibold uppercase tracking-wide text-fg-muted", children: t("notifications.inbox") }), _jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: unreadOnly, onChange: (e) => { setUnreadOnly(e.target.checked); } }), t("notifications.unreadOnly")] })] }), inbox.isPending ? (_jsx(Spinner, { label: t("common.loading") })) : items.length === 0 ? (_jsx(EmptyState, { icon: _jsx(Bell, {}), title: t("notifications.emptyTitle"), description: t("notifications.emptyDescription") })) : (_jsx("ul", { className: "flex flex-col gap-2", "data-testid": "inbox", children: items.map((item) => (_jsx("li", { className: cn("rounded-md border border-border bg-surface p-3", !item.readAt && "border-s-4 border-s-accent"), children: _jsxs("div", { className: "flex items-start justify-between gap-3", children: [_jsxs("div", { className: "min-w-0", children: [_jsx("div", { className: "font-medium", children: localized(item.title) }), localized(item.body) ? _jsx("p", { className: "mt-0.5 text-sm text-fg-muted", children: localized(item.body) }) : null, _jsxs("div", { className: "mt-1 flex flex-wrap gap-2 text-xs text-fg-subtle", children: [_jsx("span", { children: formatDateTime(item.createdAt) }), _jsx("span", { dir: "ltr", children: item.kind }), item.link ? (_jsx("a", { href: item.link, className: "text-accent underline-offset-4 hover:underline", target: "_blank", rel: "noreferrer", children: t("common.open") })) : null] })] }), !item.readAt ? (_jsx(Button, { size: "sm", variant: "ghost", onClick: () => { markRead.mutate(item.id); }, children: t("notifications.markRead") })) : null] }) }, item.id))) })), inbox.hasNextPage ? (_jsx(Button, { variant: "secondary", onClick: () => { void inbox.fetchNextPage(); }, loading: inbox.isFetchingNextPage, className: "self-center", children: t("common.loadMore") })) : null] }), _jsxs("aside", { "aria-labelledby": "preferences-title", className: "rounded-md border border-border bg-surface p-4", children: [_jsx("h2", { id: "preferences-title", className: "mb-3 text-sm font-semibold uppercase tracking-wide text-fg-muted", children: t("notifications.preferences") }), _jsxs("table", { className: "w-full text-sm", children: [_jsx("thead", { children: _jsxs("tr", { className: "text-start text-xs text-fg-muted", children: [_jsx("th", { scope: "col", className: "pb-2 text-start", children: t("notifications.kind") }), _jsx("th", { scope: "col", className: "pb-2", children: t("notifications.inApp") }), _jsx("th", { scope: "col", className: "pb-2", children: t("notifications.email") })] }) }), _jsx("tbody", { children: (preferences.data ?? []).map((preference) => (_jsxs("tr", { className: "border-t border-border", children: [_jsx("td", { className: "py-2", dir: "ltr", children: preference.kind === "*" ? t("notifications.allKinds") : preference.kind }), _jsx("td", { className: "py-2 text-center", children: _jsx("input", { type: "checkbox", "aria-label": `${preference.kind} ${t("notifications.inApp")}`, checked: preference.inApp, onChange: (e) => { togglePreference(preference.kind, "inApp", e.target.checked); } }) }), _jsx("td", { className: "py-2 text-center", children: _jsx("input", { type: "checkbox", "aria-label": `${preference.kind} ${t("notifications.email")}`, checked: preference.email, onChange: (e) => { togglePreference(preference.kind, "email", e.target.checked); } }) })] }, preference.kind))) })] })] })] }), _jsx(Dialog, { open: announceOpen, onOpenChange: (open) => { setAnnounceOpen(open); if (!open) {
                    void navigate({ to: "/notifications", search: {} });
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: _jsxs("form", { onSubmit: (event) => { event.preventDefault(); sendAnnouncement.mutate(); }, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("notifications.announce") }) }), _jsx(FormError, { message: problem?.message ?? null }), _jsx(Field, { label: t("notifications.titleEn"), required: true, children: _jsx(TextField, { value: announce.titleEn, onChange: (e) => { setAnnounce({ ...announce, titleEn: e.target.value }); }, required: true }) }), _jsx(Field, { label: t("notifications.titleAr"), children: _jsx(TextField, { value: announce.titleAr, onChange: (e) => { setAnnounce({ ...announce, titleAr: e.target.value }); }, dir: "rtl" }) }), _jsx(Field, { label: t("notifications.bodyEn"), children: _jsx(TextareaField, { value: announce.bodyEn, onChange: (e) => { setAnnounce({ ...announce, bodyEn: e.target.value }); } }) }), _jsx(Field, { label: t("notifications.bodyAr"), children: _jsx(TextareaField, { value: announce.bodyAr, onChange: (e) => { setAnnounce({ ...announce, bodyAr: e.target.value }); }, dir: "rtl" }) }), _jsx(Field, { label: t("notifications.link"), children: _jsx(TextField, { type: "url", value: announce.link, onChange: (e) => { setAnnounce({ ...announce, link: e.target.value }); }, dir: "ltr" }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setAnnounceOpen(false); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: sendAnnouncement.isPending, "data-testid": "send-announcement", children: t("notifications.send") })] })] }) }) })] }));
}
