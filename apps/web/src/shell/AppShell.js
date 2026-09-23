import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger, TooltipProvider, cn } from "@quicker/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, Outlet, useNavigate, useRouterState } from "@tanstack/react-router";
import { Bell, Keyboard, Languages, LogOut, Menu, Moon, Search, Sun, User } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { currentLanguage, setLanguage } from "../i18n";
import { formatNumber, setDigitStyle, useDigitStyle } from "../lib/format";
import { clearSession, useSession } from "../session/session";
import { CommandPalette } from "./CommandPalette";
import { navigation } from "./navigation";
import { ShortcutsOverlay } from "./ShortcutsOverlay";
import { installShortcutListener, registerShortcut } from "./useShortcuts";
function readTheme() {
    try {
        const stored = localStorage.getItem("quicker.theme");
        if (stored === "dark" || stored === "light") {
            return stored;
        }
    }
    catch {
        // ignore
    }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}
export function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    try {
        localStorage.setItem("quicker.theme", theme);
    }
    catch {
        // ignore
    }
}
/** The signed-in frame: skip link, sidebar navigation, header with search, language/theme/digits, notifications and the user menu. */
export function AppShell() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const session = useSession();
    const location = useRouterState({ select: (s) => s.location.pathname });
    const [paletteOpen, setPaletteOpen] = useState(false);
    const [shortcutsOpen, setShortcutsOpen] = useState(false);
    const [sidebarOpen, setSidebarOpen] = useState(false);
    const [theme, setTheme] = useState(readTheme);
    const digits = useDigitStyle();
    const language = currentLanguage();
    const me = useQuery({
        queryKey: ["me"],
        queryFn: async () => unwrap(await api.GET("/api/v1/me")),
        staleTime: 60_000,
    });
    const permissions = useMemo(() => new Set(me.data?.permissions ?? []), [me.data]);
    const unread = useQuery({
        queryKey: ["notifications", "unread-count"],
        queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/notifications/unread-count")),
        refetchInterval: 30_000,
    });
    useEffect(() => {
        applyTheme(theme);
    }, [theme]);
    useEffect(() => installShortcutListener(), []);
    useEffect(() => {
        const unregister = [
            registerShortcut({ keys: "mod+k", description: "shortcuts.palette", group: "shortcuts.groups.global", handler: () => { setPaletteOpen(true); } }),
            registerShortcut({ keys: "?", description: "shortcuts.overlay", group: "shortcuts.groups.global", handler: () => { setShortcutsOpen(true); } }),
            ...navigation.map((item) => registerShortcut({ keys: item.shortcut, description: item.label, group: "shortcuts.groups.navigation", handler: () => { void navigate({ to: item.to }); } })),
        ];
        return () => {
            unregister.forEach((fn) => {
                fn();
            });
        };
    }, [navigate]);
    const switchLanguage = useCallback(async (next) => {
        await setLanguage(next);
        await queryClient.invalidateQueries();
    }, [queryClient]);
    const logout = useCallback(async () => {
        try {
            await api.POST("/api/v1/me/logout");
        }
        finally {
            clearSession();
            queryClient.clear();
            void navigate({ to: "/login" });
        }
    }, [navigate, queryClient]);
    const visibleNavigation = navigation.filter((item) => !item.permission || permissions.has("*") || permissions.has(item.permission));
    return (_jsxs(TooltipProvider, { delayDuration: 300, children: [_jsx("a", { href: "#main", className: "skip-link", children: t("shell.skipToContent") }), _jsxs("div", { className: "flex min-h-dvh bg-canvas text-fg", children: [_jsxs("aside", { className: cn("fixed inset-y-0 start-0 z-30 flex w-64 flex-col border-e border-border bg-surface transition-transform duration-base ease-standard lg:static lg:translate-x-0", sidebarOpen ? "translate-x-0" : "-translate-x-full rtl:translate-x-full lg:rtl:translate-x-0"), "aria-label": t("shell.primaryNavigation"), children: [_jsxs("div", { className: "flex h-14 items-center gap-2 border-b border-border px-4", children: [_jsx("span", { className: "text-lg font-bold tracking-tight text-accent", children: "Quicker" }), _jsx("span", { className: "truncate text-xs text-fg-muted", children: session?.tenant.name })] }), _jsx("nav", { className: "flex-1 overflow-y-auto p-2", children: _jsx("ul", { className: "flex flex-col gap-0.5", children: visibleNavigation.map((item) => {
                                        const active = item.to === "/" ? location === "/" : location === item.to || location.startsWith(`${item.to}/`);
                                        return (_jsx("li", { children: _jsxs(Link, { to: item.to, onClick: () => { setSidebarOpen(false); }, "aria-current": active ? "page" : undefined, className: cn("flex h-9 items-center gap-3 rounded-md px-3 text-sm transition-colors", active ? "bg-accent-soft font-medium text-accent" : "text-fg-muted hover:bg-surface-sunken hover:text-fg"), children: [_jsx(item.icon, { className: "size-4 shrink-0", "aria-hidden": "true" }), _jsx("span", { className: "truncate", children: t(item.label) })] }) }, item.to));
                                    }) }) }), _jsx("div", { className: "border-t border-border p-3 text-xs text-fg-subtle", children: _jsxs(Button, { variant: "ghost", size: "sm", className: "w-full justify-start gap-2", onClick: () => { setShortcutsOpen(true); }, children: [_jsx(Keyboard, { "aria-hidden": "true" }), t("shell.keyboardShortcuts")] }) })] }), sidebarOpen ? _jsx("button", { type: "button", className: "fixed inset-0 z-20 bg-black/30 lg:hidden", "aria-label": t("common.close"), onClick: () => { setSidebarOpen(false); } }) : null, _jsxs("div", { className: "flex min-w-0 flex-1 flex-col", children: [_jsxs("header", { className: "sticky top-0 z-10 flex h-14 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur sm:px-4", children: [_jsx(Button, { variant: "ghost", size: "icon", className: "lg:hidden", "aria-label": t("shell.openMenu"), onClick: () => { setSidebarOpen(true); }, children: _jsx(Menu, { "aria-hidden": "true" }) }), _jsxs(Button, { variant: "secondary", className: "hidden h-9 min-w-56 justify-start gap-2 text-fg-muted sm:inline-flex", onClick: () => { setPaletteOpen(true); }, children: [_jsx(Search, { "aria-hidden": "true" }), _jsx("span", { className: "flex-1 text-start", children: t("shell.search") }), _jsx("kbd", { className: "font-mono text-xs", dir: "ltr", children: "\u2318K" })] }), _jsx(Button, { variant: "ghost", size: "icon", className: "sm:hidden", "aria-label": t("shell.search"), onClick: () => { setPaletteOpen(true); }, children: _jsx(Search, { "aria-hidden": "true" }) }), _jsxs("div", { className: "ms-auto flex items-center gap-1", children: [_jsxs(DropdownMenu, { children: [_jsx(DropdownMenuTrigger, { asChild: true, children: _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("shell.language"), "data-testid": "language-menu", children: _jsx(Languages, { "aria-hidden": "true" }) }) }), _jsxs(DropdownMenuContent, { align: "end", children: [_jsx(DropdownMenuLabel, { children: t("shell.language") }), _jsxs(DropdownMenuRadioGroup, { value: language, onValueChange: (value) => { void switchLanguage(value); }, children: [_jsx(DropdownMenuRadioItem, { value: "en", "data-testid": "language-en", children: "English" }), _jsx(DropdownMenuRadioItem, { value: "ar", "data-testid": "language-ar", children: "\u0627\u0644\u0639\u0631\u0628\u064A\u0629" })] }), _jsx(DropdownMenuSeparator, {}), _jsx(DropdownMenuLabel, { children: t("shell.digits") }), _jsxs(DropdownMenuRadioGroup, { value: digits, onValueChange: (value) => { setDigitStyle(value === "arab" ? "arab" : "latn"); }, children: [_jsxs(DropdownMenuRadioItem, { value: "latn", children: [t("shell.digitsLatin"), " (0123)"] }), _jsxs(DropdownMenuRadioItem, { value: "arab", children: [t("shell.digitsArabic"), " (\u0660\u0661\u0662\u0663)"] })] })] })] }), _jsx(Button, { variant: "ghost", size: "icon", "aria-label": theme === "dark" ? t("shell.lightMode") : t("shell.darkMode"), onClick: () => { setTheme(theme === "dark" ? "light" : "dark"); }, children: theme === "dark" ? _jsx(Sun, { "aria-hidden": "true" }) : _jsx(Moon, { "aria-hidden": "true" }) }), _jsx(Button, { variant: "ghost", size: "icon", asChild: true, children: _jsxs(Link, { to: "/notifications", "aria-label": t("nav.notifications"), className: "relative", children: [_jsx(Bell, { "aria-hidden": "true" }), unread.data && Number(unread.data.count) > 0 ? (_jsx("span", { className: "absolute -end-0.5 -top-0.5 min-w-4 rounded-full bg-danger px-1 text-center text-[10px] font-semibold leading-4 text-danger-fg", "data-testid": "unread-count", children: formatNumber(unread.data.count) })) : null] }) }), _jsxs(DropdownMenu, { children: [_jsx(DropdownMenuTrigger, { asChild: true, children: _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("shell.account"), "data-testid": "account-menu", children: _jsx(User, { "aria-hidden": "true" }) }) }), _jsxs(DropdownMenuContent, { align: "end", children: [_jsxs(DropdownMenuLabel, { children: [_jsx("div", { className: "text-sm font-medium text-fg", children: session?.user.displayName }), _jsx("div", { className: "text-xs font-normal text-fg-muted", children: session?.user.email })] }), _jsx(DropdownMenuSeparator, {}), _jsxs(DropdownMenuItem, { onSelect: () => { void logout(); }, "data-testid": "logout", children: [_jsx(LogOut, { className: "size-4", "aria-hidden": "true" }), t("shell.signOut")] })] })] })] })] }), _jsx("main", { id: "main", className: "flex-1 p-4 sm:p-6", tabIndex: -1, children: _jsx(Outlet, {}) })] })] }), _jsx(CommandPalette, { open: paletteOpen, onOpenChange: setPaletteOpen, permissions: permissions }), _jsx(ShortcutsOverlay, { open: shortcutsOpen, onOpenChange: setShortcutsOpen })] }));
}
