import { Badge, Button, Dialog, DialogContent, DialogHeader, DialogTitle, EmptyState } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Printer, RefreshCw, Trash2, Undo2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDateTime, formatNumber, localized } from "../../lib/format";
import { useOnline } from "../mobile/context";
import { Receipt } from "./Receipt";
import { removeOrder, updateOrder, useTill, type LocalOrder } from "./store";
import { syncOrders, type SyncResult } from "./sync";

/** What this device sold: synced orders with their numbers, orders waiting for the network, orders the server refused with the reason; reprint and refund from here. */
export function PosOrdersPage() {
  const { t } = useTranslation();
  const till = useTill();
  const online = useOnline();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<SyncResult | null>(null);
  const [reprint, setReprint] = useState<LocalOrder | null>(null);
  const companies = useQuery({ queryKey: ["companies", ""], queryFn: async () => unwrap(await api.GET("/api/v1/organization/companies")), enabled: online, staleTime: 600_000 });
  const companyName = localized((companies.data ?? []).find((c) => c.id === till.companyId)?.legalName) || "";
  const minor = Number(till.catalogue?.minorUnits ?? 2);
  const money = (value: string | number): string => `${formatNumber(value, { minimumFractionDigits: minor, maximumFractionDigits: minor })} ${till.catalogue?.currency ?? ""}`;
  const pending = till.orders.filter((o) => o.status !== "synced").length;

  const sync = async (): Promise<void> => {
    setBusy(true);
    try {
      setResult(await syncOrders());
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="mx-auto flex max-w-4xl flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-xl font-semibold tracking-tight">{t("pos.till.tabs.orders")}</h1>
        <Button variant="secondary" className="gap-2" data-testid="till-sync-now" disabled={!online || busy || pending === 0} onClick={() => { void sync(); }}>
          <RefreshCw aria-hidden="true" />
          {busy ? t("mobile.queue.syncing") : t("mobile.queue.sync")}
        </Button>
      </div>
      {!online && pending > 0 ? <p className="text-sm text-warning">{t("mobile.queue.waiting")}</p> : null}
      {result ? <p className="text-sm text-fg-muted" data-testid="till-sync-result">{t("mobile.queue.result", { synced: result.synced, failed: result.failed })}</p> : null}
      {till.orders.length === 0 ? (
        <EmptyState title={t("pos.till.noOrders")} description={t("pos.till.noOrdersDescription")} />
      ) : (
        <ul className="flex flex-col gap-2">
          {till.orders.map((order) => (
            <li key={order.deviceOrderId} className="flex flex-col gap-1 rounded-md border border-border bg-surface px-3 py-2 text-sm" data-testid="till-order">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="flex items-center gap-2">
                  <span className="font-medium" dir="ltr" data-testid="till-order-number">{order.server?.number ?? t("pos.till.deviceReference", { reference: order.deviceOrderId.slice(0, 8).toUpperCase() })}</span>
                  {order.kind === "refund" ? <Badge tone="neutral">{t("pos.kinds.refund")}</Badge> : null}
                  <Badge tone={order.status === "synced" ? "success" : order.status === "failed" ? "danger" : "warning"} data-testid="till-order-status">{t(`pos.till.orderStatuses.${order.status}`)}</Badge>
                  {order.server?.einvoiceStatus ? <Badge tone="info">{t(`einvoicing.submissionStatuses.${order.server.einvoiceStatus}`, { defaultValue: order.server.einvoiceStatus })}</Badge> : null}
                </span>
                <span className="font-semibold tabular-nums" dir="ltr">{money(order.totals.due)}</span>
              </div>
              <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-fg-muted">
                <span dir="ltr">{formatDateTime(order.orderedAt)}{order.partnerName ? ` · ${order.partnerName}` : ""}</span>
                <span className="flex items-center gap-1">
                  <Button variant="ghost" size="sm" className="gap-1" onClick={() => { setReprint(order); }} data-testid="till-reprint">
                    <Printer className="size-4" aria-hidden="true" />
                    {t("pos.till.reprint")}
                  </Button>
                  {order.kind === "sale" && order.status === "synced" ? (
                    <Button variant="ghost" size="sm" className="gap-1" asChild>
                      <Link to="/pos/sell" search={{ refund: order.deviceOrderId }} data-testid="till-refund-link">
                        <Undo2 className="size-4" aria-hidden="true" />
                        {t("pos.till.refund")}
                      </Link>
                    </Button>
                  ) : null}
                  {order.status === "failed" ? (
                    <>
                      <Button variant="ghost" size="sm" onClick={() => { updateOrder(order.deviceOrderId, { status: "queued", error: null }); }} data-testid="till-retry">{t("pos.till.retry")}</Button>
                      <Button variant="ghost" size="sm" className="gap-1 text-danger" onClick={() => { removeOrder(order.deviceOrderId); }} data-testid="till-discard">
                        <Trash2 className="size-4" aria-hidden="true" />
                        {t("pos.till.discard")}
                      </Button>
                    </>
                  ) : null}
                </span>
              </div>
              {order.error ? <p role="alert" className="text-xs text-danger" data-testid="till-order-error">{order.error}</p> : null}
            </li>
          ))}
        </ul>
      )}
      <Dialog open={Boolean(reprint)} onOpenChange={(isOpen) => { if (!isOpen) { setReprint(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-md">
          <DialogHeader>
            <DialogTitle>{t("pos.till.receipt")}</DialogTitle>
          </DialogHeader>
          {reprint && till.register && till.catalogue ? <Receipt order={reprint} register={till.register} catalogue={till.catalogue} companyName={companyName} onPrint={() => { window.print(); }} /> : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}
