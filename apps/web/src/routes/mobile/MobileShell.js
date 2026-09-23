import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Badge, Button, cn } from "@quicker/ui";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { ArrowRightLeft, ClipboardCheck, Home, Languages, Monitor, RefreshCw, Wifi, WifiOff } from "lucide-react";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { currentLanguage, setLanguage } from "../../i18n";
import { useSession } from "../../session/session";
import { useOnline } from "./context";
import { getQueue, syncQueue, useQueue } from "./queue";
const tabs = [
    { to: "/m", label: "mobile.tabs.home", icon: Home },
    { to: "/m/count", label: "mobile.tabs.count", icon: ClipboardCheck },
    { to: "/m/transfer", label: "mobile.tabs.transfer", icon: ArrowRightLeft },
    { to: "/m/queue", label: "mobile.tabs.queue", icon: RefreshCw },
];
/** The scanner's frame (roadmap 3.8): a thin header with the network state, the screen, and a thumb-reach tab bar. */
export function MobileShell() {
    const { t } = useTranslation();
    const session = useSession();
    const online = useOnline();
    const queue = useQueue();
    const location = useRouterState({ select: (s) => s.location.pathname });
    const language = currentLanguage();
    const pending = queue.length;
    // Back online: replay what was captured meanwhile.
    useEffect(() => {
        if (online && getQueue().length > 0) {
            void syncQueue();
        }
    }, [online]);
    return (_jsxs("div", { className: "flex min-h-dvh flex-col bg-canvas text-fg", children: [_jsx("a", { href: "#main", className: "skip-link", children: t("shell.skipToContent") }), _jsxs("header", { className: "sticky top-0 z-10 flex h-14 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur", children: [_jsx("span", { className: "text-lg font-bold tracking-tight text-accent", children: "Quicker" }), _jsx("span", { className: "truncate text-xs text-fg-muted", children: session?.tenant.name }), _jsxs("div", { className: "ms-auto flex items-center gap-1", children: [_jsxs(Badge, { tone: online ? "success" : "warning", className: "gap-1", "data-testid": "online-status", children: [online ? _jsx(Wifi, { className: "size-3", "aria-hidden": "true" }) : _jsx(WifiOff, { className: "size-3", "aria-hidden": "true" }), online ? t("mobile.online") : t("mobile.offline")] }), _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("mobile.switchLanguage"), "data-testid": "mobile-language", onClick: () => { void setLanguage(language === "ar" ? "en" : "ar"); }, children: _jsx(Languages, { "aria-hidden": "true" }) }), _jsx(Button, { variant: "ghost", size: "icon", asChild: true, children: _jsx(Link, { to: "/", "aria-label": t("mobile.backToDesktop"), children: _jsx(Monitor, { "aria-hidden": "true" }) }) })] })] }), _jsx("main", { id: "main", tabIndex: -1, className: "flex-1 p-4 pb-24", children: _jsx(Outlet, {}) }), _jsx("nav", { "aria-label": t("mobile.tabs.label"), className: "fixed inset-x-0 bottom-0 z-10 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)]", children: _jsx("ul", { className: "grid grid-cols-4", children: tabs.map((tab) => {
                        const active = tab.to === "/m" ? location === "/m" || location === "/m/" : location.startsWith(tab.to);
                        return (_jsx("li", { children: _jsxs(Link, { to: tab.to, "aria-current": active ? "page" : undefined, className: cn("relative flex h-16 flex-col items-center justify-center gap-1 text-xs", active ? "font-medium text-accent" : "text-fg-muted"), children: [_jsx(tab.icon, { className: "size-5", "aria-hidden": "true" }), t(tab.label), tab.to === "/m/queue" && pending > 0 ? (_jsx("span", { "data-testid": "queue-badge", className: "absolute end-1/4 top-2 min-w-4 rounded-full bg-danger px-1 text-center text-[10px] font-semibold leading-4 text-danger-fg", children: pending })) : null] }) }, tab.to));
                    }) }) })] }));
}
