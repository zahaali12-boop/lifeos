import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@quicker/ui";
import { useTranslation } from "react-i18next";
import { describeKeys, useShortcutList } from "./useShortcuts";
/** The "?" overlay: every registered shortcut, grouped, in the current language. */
export function ShortcutsOverlay({ open, onOpenChange }) {
    const { t } = useTranslation();
    const shortcuts = useShortcutList();
    const groups = new Map();
    for (const shortcut of shortcuts) {
        const list = groups.get(shortcut.group) ?? [];
        list.push(shortcut);
        groups.set(shortcut.group, list);
    }
    return (_jsx(Dialog, { open: open, onOpenChange: onOpenChange, children: _jsxs(DialogContent, { closeLabel: t("common.close"), className: "max-w-2xl", children: [_jsxs(DialogHeader, { children: [_jsx(DialogTitle, { className: "text-lg font-semibold", children: t("shortcuts.title") }), _jsx(DialogDescription, { className: "text-sm text-fg-muted", children: t("shortcuts.description") })] }), _jsx("div", { className: "grid gap-6 sm:grid-cols-2", children: [...groups.entries()].map(([group, items]) => (_jsxs("section", { "aria-labelledby": `shortcuts-${group}`, children: [_jsx("h3", { id: `shortcuts-${group}`, className: "mb-2 text-xs font-semibold uppercase tracking-wide text-fg-muted", children: t(group) }), _jsx("dl", { className: "flex flex-col gap-1.5", children: items.map((item) => (_jsxs("div", { className: "flex items-center justify-between gap-4 text-sm", children: [_jsx("dt", { className: "text-fg", children: t(item.description) }), _jsx("dd", { children: _jsx("kbd", { className: "rounded-sm border border-border bg-surface-sunken px-1.5 py-0.5 font-mono text-xs text-fg-muted", dir: "ltr", children: describeKeys(item.keys, t("shortcuts.then")) }) })] }, item.keys))) })] }, group))) })] }) }));
}
