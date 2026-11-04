import { api, isApiProblem, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { getTill, type LocalOrder, updateOrder } from "./store";

type SubmitOrderRequest = components["schemas"]["SubmitOrderRequest"];

export interface SyncResult {
  synced: number;
  failed: number;
  /** True when a network error stopped the sync before the queue was drained. */
  interrupted: boolean;
}

let syncing: Promise<SyncResult> | null = null;

function describe(error: unknown): string {
  if (isApiProblem(error)) {
    return error.code ? `${error.code}: ${error.detail ?? error.title ?? ""}`.trim() : (error.detail ?? error.title ?? "error");
  }
  return error instanceof Error ? error.message : String(error);
}

/** A thrown TypeError from fetch means the request never reached the API: keep the order untouched and stop. */
function isNetworkError(error: unknown): boolean {
  return error instanceof TypeError;
}

/** The order as the API takes it: the lines with the price charged and the code the catalogue gave, the tenders, what the receipt showed as due. */
export function requestOf(order: LocalOrder, registerId: string, sessionId: string): SubmitOrderRequest {
  return {
    registerId,
    sessionId,
    deviceOrderId: order.deviceOrderId,
    orderedAt: order.orderedAt,
    kind: order.kind,
    refundsDeviceOrderId: order.refundsDeviceOrderId,
    partnerId: order.partnerId,
    lines: order.lines.map((l) => ({ itemId: l.itemId, variantId: l.variantId, uomId: l.uomId, quantity: l.quantity, unitPrice: l.unitPrice, discountPct: l.discountPct, taxCodeId: l.taxCodeId })),
    payments: order.payments.map((p) => ({ method: p.method, amount: p.amount, reference: p.reference })),
    totalDue: order.totals.due,
    changeGiven: order.changeGiven,
  };
}

/**
 * Sends the orders waiting on this device in the order they were made (a refund after the sale it gives back). The
 * server answers a repeated order with the one already posted, so a sync cut off halfway is safe to run again. A
 * refusal (a stale catalogue, a closed session) marks the order failed with the reason; a network failure stops the
 * run and the queue is retried when the device is online again.
 */
export function syncOrders(): Promise<SyncResult> {
  if (syncing) {
    return syncing;
  }
  syncing = (async () => {
    const result: SyncResult = { synced: 0, failed: 0, interrupted: false };
    const till = getTill();
    if (!till.register || !till.session) {
      return result;
    }
    const pending = [...till.orders].filter((o) => o.status !== "synced").sort((a, b) => a.orderedAt.localeCompare(b.orderedAt));
    for (const order of pending) {
      if (!navigator.onLine) {
        result.interrupted = true;
        break;
      }
      try {
        const posted = unwrap(await api.POST("/api/v1/pos/orders", { body: requestOf(order, till.register.id, till.session.id) }));
        updateOrder(order.deviceOrderId, { status: "synced", error: null, server: { id: posted.id, number: posted.number, einvoiceStatus: posted.einvoiceStatus ?? null, einvoiceQr: posted.einvoiceQr ?? null } });
        result.synced += 1;
      } catch (error) {
        if (isNetworkError(error)) {
          result.interrupted = true;
          break;
        }
        updateOrder(order.deviceOrderId, { status: "failed", error: describe(error) });
        result.failed += 1;
      }
    }
    return result;
  })().finally(() => {
    syncing = null;
  });
  return syncing;
}

/** Re-reads a synced order from the server (the e-invoice outcome arrives after the sync, from the worker). */
export async function refreshOrder(order: LocalOrder): Promise<void> {
  if (!order.server) {
    return;
  }
  try {
    const fresh = unwrap(await api.GET("/api/v1/pos/orders/{orderId}", { params: { path: { orderId: order.server.id } } }));
    updateOrder(order.deviceOrderId, { server: { id: fresh.id, number: fresh.number, einvoiceStatus: fresh.einvoiceStatus ?? null, einvoiceQr: fresh.einvoiceQr ?? null } });
  } catch {
    // offline or refused: the device keeps what it has
  }
}
