import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Badge, Button, EmptyState } from "@quicker/ui";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCw, Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useOnline } from "./context";
import { removeFromQueue, syncQueue, useQueue } from "./queue";
/** What is still on the device: replay it, or drop a capture the API refused. */
export function MobileQueuePage() {
    const { t, i18n } = useTranslation();
    const queryClient = useQueryClient();
    const queue = useQueue();
    const online = useOnline();
    const [busy, setBusy] = useState(false);
    const [result, setResult] = useState(null);
    const sync = async () => {
        setBusy(true);
        try {
            setResult(await syncQueue());
            await queryClient.invalidateQueries({ queryKey: ["mobile", "sheet"] });
        }
        finally {
            setBusy(false);
        }
    };
    return (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs("div", { className: "flex items-center justify-between gap-2", children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: t("mobile.queue.title") }), _jsxs(Button, { variant: "secondary", className: "gap-2", "data-testid": "sync-now", disabled: !online || busy || queue.length === 0, onClick: () => { void sync(); }, children: [_jsx(RefreshCw, { "aria-hidden": "true" }), busy ? t("mobile.queue.syncing") : t("mobile.queue.sync")] })] }), !online ? _jsx("p", { className: "text-sm text-warning", children: t("mobile.queue.waiting") }) : null, result ? _jsx("p", { className: "text-sm text-fg-muted", "data-testid": "sync-result", children: t("mobile.queue.result", { synced: result.synced, failed: result.failed }) }) : null, queue.length === 0 ? (_jsx(EmptyState, { title: t("mobile.queue.title"), description: t("mobile.queue.empty") })) : (_jsx("ul", { className: "flex flex-col gap-2", children: queue.map((item) => (_jsxs("li", { className: "flex flex-col gap-1 rounded-md border border-border bg-surface px-3 py-2 text-sm", "data-testid": "queue-item", children: [_jsxs("div", { className: "flex items-center justify-between gap-2", children: [_jsx("span", { className: "truncate font-medium", children: item.kind === "count" ? t("mobile.queue.countLabel", { number: item.countNumber, label: item.label }) : t("mobile.queue.transferLabel", { label: item.label }) }), _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("mobile.queue.remove"), "data-testid": "queue-remove", onClick: () => { removeFromQueue(item.id); }, children: _jsx(Trash2, { "aria-hidden": "true" }) })] }), _jsxs("div", { className: "flex items-center justify-between gap-2 text-xs text-fg-muted", children: [_jsx("span", { children: new Date(item.createdAt).toLocaleString(i18n.language) }), item.error ? _jsx(Badge, { tone: "danger", children: t("mobile.queue.error", { message: item.error }) }) : _jsx(Badge, { tone: "warning", children: t("mobile.count.queued") })] })] }, item.id))) }))] }));
}
