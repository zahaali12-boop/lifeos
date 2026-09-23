import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Dialog, DialogContent, DialogTitle } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { Command } from "cmdk";
import { Building2, Megaphone, Plus, Search } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { localized } from "../lib/format";
import { navigation } from "./navigation";
import { describeKeys } from "./useShortcuts";
const RECENT_KEY = "quicker.recent";
export function rememberRecent(record) {
    try {
        const list = JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]").filter((r) => r.to !== record.to);
        list.unshift({ ...record, at: Date.now() });
        localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, 8)));
    }
    catch {
        // ignore
    }
}
function readRecent() {
    try {
        return JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]");
    }
    catch {
        return [];
    }
}
function allowed(permissions, permission) {
    return !permission || permissions.has("*") || permissions.has(permission);
}
/** Ctrl/⌘+K: navigation, actions, recent records and a global search over companies (more entities join as they land). */
export function CommandPalette({ open, onOpenChange, permissions }) {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const [query, setQuery] = useState("");
    useEffect(() => {
        if (!open) {
            setQuery("");
        }
    }, [open]);
    const search = useQuery({
        queryKey: ["palette", "companies", query],
        enabled: open && query.trim().length >= 2 && allowed(permissions, "organization.company.read"),
        queryFn: async () => {
            const escaped = query.trim().replace(/'/g, "''");
            return unwrap(await api.GET("/api/v1/organization/companies", { params: { query: { filter: `code like '${escaped}'` } } }));
        },
    });
    const go = (to) => {
        onOpenChange(false);
        void navigate({ to });
    };
    return (_jsx(Dialog, { open: open, onOpenChange: onOpenChange, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "top-24 translate-y-0 p-0 sm:max-w-xl", children: [_jsx(DialogTitle, { className: "sr-only", children: t("palette.title") }), _jsxs(Command, { label: t("palette.title"), shouldFilter: true, className: "flex flex-col", children: [_jsxs("div", { className: "flex items-center gap-2 border-b border-border px-3", children: [_jsx(Search, { className: "size-4 text-fg-subtle", "aria-hidden": "true" }), _jsx(Command.Input, { value: query, onValueChange: setQuery, placeholder: t("palette.placeholder"), className: "h-11 w-full bg-transparent text-sm outline-none placeholder:text-fg-subtle" })] }), _jsxs(Command.List, { className: "max-h-80 overflow-y-auto p-2", children: [_jsx(Command.Empty, { className: "px-2 py-6 text-center text-sm text-fg-muted", children: t("palette.empty") }), search.data && search.data.length > 0 ? (_jsx(Command.Group, { heading: t("palette.groups.companies"), className: "[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted", children: search.data.map((company) => (_jsxs(Command.Item, { value: `company ${company.code} ${localized(company.legalName)}`, onSelect: () => { go(`/companies?open=${company.id}`); }, className: "flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken", children: [_jsx(Building2, { className: "size-4 text-fg-subtle", "aria-hidden": "true" }), _jsx("span", { className: "font-medium", children: company.code }), _jsx("span", { className: "text-fg-muted", children: localized(company.legalName) })] }, company.id))) })) : null, _jsx(Command.Group, { heading: t("palette.groups.navigation"), className: "[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted", children: navigation.filter((item) => allowed(permissions, item.permission)).map((item) => (_jsxs(Command.Item, { value: `${t(item.label)} ${item.to}`, onSelect: () => { go(item.to); }, className: "flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken", children: [_jsx(item.icon, { className: "size-4 text-fg-subtle", "aria-hidden": "true" }), _jsx("span", { children: t(item.label) }), _jsx("kbd", { className: "ms-auto font-mono text-xs text-fg-subtle", dir: "ltr", children: describeKeys(item.shortcut, t("shortcuts.then")) })] }, item.to))) }), _jsxs(Command.Group, { heading: t("palette.groups.actions"), className: "[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted", children: [allowed(permissions, "organization.company.manage") ? (_jsxs(Command.Item, { value: t("companies.new"), onSelect: () => { go("/companies?new=1"); }, className: "flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken", children: [_jsx(Plus, { className: "size-4 text-fg-subtle", "aria-hidden": "true" }), t("companies.new")] })) : null, allowed(permissions, "collaboration.notification.announce") ? (_jsxs(Command.Item, { value: t("notifications.announce"), onSelect: () => { go("/notifications?announce=1"); }, className: "flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken", children: [_jsx(Megaphone, { className: "size-4 text-fg-subtle", "aria-hidden": "true" }), t("notifications.announce")] })) : null] }), readRecent().length > 0 ? (_jsx(Command.Group, { heading: t("palette.groups.recent"), className: "[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:text-fg-muted", children: readRecent().map((record) => (_jsx(Command.Item, { value: `recent ${record.label}`, onSelect: () => { go(record.to); }, className: "flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm data-[selected=true]:bg-surface-sunken", children: record.label }, record.to))) })) : null] })] })] }) }));
}
