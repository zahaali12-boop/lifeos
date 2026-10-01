import { Badge, Button, EmptyState } from "@quicker/ui";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCw, Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useOnline } from "./context";
import { removeFromQueue, syncQueue, useQueue, type SyncResult } from "./queue";

/** What is still on the device: replay it, or drop a capture the API refused. */
export function MobileQueuePage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const queue = useQueue();
  const online = useOnline();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<SyncResult | null>(null);

  const sync = async (): Promise<void> => {
    setBusy(true);
    try {
      setResult(await syncQueue());
      await queryClient.invalidateQueries({ queryKey: ["mobile", "sheet"] });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between gap-2">
        <h1 className="text-xl font-semibold tracking-tight">{t("mobile.queue.title")}</h1>
        <Button variant="secondary" className="gap-2" data-testid="sync-now" disabled={!online || busy || queue.length === 0} onClick={() => { void sync(); }}>
          <RefreshCw aria-hidden="true" />
          {busy ? t("mobile.queue.syncing") : t("mobile.queue.sync")}
        </Button>
      </div>
      {!online ? <p className="text-sm text-warning">{t("mobile.queue.waiting")}</p> : null}
      {result ? <p className="text-sm text-fg-muted" data-testid="sync-result">{t("mobile.queue.result", { synced: result.synced, failed: result.failed })}</p> : null}
      {queue.length === 0 ? (
        <EmptyState title={t("mobile.queue.title")} description={t("mobile.queue.empty")} />
      ) : (
        <ul className="flex flex-col gap-2">
          {queue.map((item) => (
            <li key={item.id} className="flex flex-col gap-1 rounded-md border border-border bg-surface px-3 py-2 text-sm" data-testid="queue-item">
              <div className="flex items-center justify-between gap-2">
                <span className="truncate font-medium">{item.kind === "count" ? t("mobile.queue.countLabel", { number: item.countNumber, label: item.label }) : t("mobile.queue.transferLabel", { label: item.label })}</span>
                <Button variant="ghost" size="icon" aria-label={t("mobile.queue.remove")} data-testid="queue-remove" onClick={() => { removeFromQueue(item.id); }}>
                  <Trash2 aria-hidden="true" />
                </Button>
              </div>
              <div className="flex items-center justify-between gap-2 text-xs text-fg-muted">
                <span>{new Date(item.createdAt).toLocaleString(i18n.language)}</span>
                {item.error ? <Badge tone="danger">{t("mobile.queue.error", { message: item.error })}</Badge> : <Badge tone="warning">{t("mobile.count.queued")}</Badge>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
