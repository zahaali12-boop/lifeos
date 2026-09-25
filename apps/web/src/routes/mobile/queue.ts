import { useSyncExternalStore } from "react";
import { api, isApiProblem, unwrap } from "../../api";
import type { components } from "../../api/schema";

type CountEntry = components["schemas"]["CountEntryRequest"];
type TransferRequest = components["schemas"]["SaveTransferRequest"];

/**
 * The offline queue (roadmap 3.8): every capture is appended here first and sent in order when the network allows.
 * A capture the API rejects (a business rule, an unknown code) stays in the queue with the problem so the operator
 * can fix or drop it; a network failure stops the sync and the queue is retried when the device is online again.
 */
export type QueuedCapture =
  | { id: string; kind: "count"; createdAt: string; countId: string; countNumber: string; label: string; entry: CountEntry; error?: string }
  | { id: string; kind: "transfer"; createdAt: string; label: string; request: TransferRequest; shipDate: string | null; error?: string };

const STORAGE_KEY = "quicker.scanQueue";
const listeners = new Set<() => void>();
let items: QueuedCapture[] = load();
let syncing: Promise<SyncResult> | null = null;

function load(): QueuedCapture[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as QueuedCapture[]) : [];
  } catch {
    return [];
  }
}

function commit(next: QueuedCapture[]): void {
  items = next;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
  } catch {
    // storage unavailable: the queue lives in memory for this session
  }
  listeners.forEach((listener) => {
    listener();
  });
}

export function getQueue(): QueuedCapture[] {
  return items;
}

export function useQueue(): QueuedCapture[] {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getQueue,
    getQueue,
  );
}

/** A capture before it is queued: each kind without the fields the queue assigns. */
export type NewCapture = QueuedCapture extends infer C ? (C extends QueuedCapture ? Omit<C, "id" | "createdAt"> : never) : never;

export function enqueue(capture: NewCapture): QueuedCapture {
  const item: QueuedCapture = { ...capture, id: crypto.randomUUID(), createdAt: new Date().toISOString() };
  commit([...items, item]);
  return item;
}

export function removeFromQueue(id: string): void {
  commit(items.filter((item) => item.id !== id));
}

export interface SyncResult {
  synced: number;
  failed: number;
  /** True when a network error stopped the sync before the queue was drained. */
  interrupted: boolean;
}

function describe(error: unknown): string {
  if (isApiProblem(error)) {
    return error.code ? `${error.code}: ${error.detail ?? error.title ?? ""}`.trim() : (error.detail ?? error.title ?? "error");
  }
  return error instanceof Error ? error.message : String(error);
}

/** A thrown TypeError from fetch means the request never reached the API: keep the capture untouched and stop. */
function isNetworkError(error: unknown): boolean {
  return error instanceof TypeError;
}

async function send(item: QueuedCapture): Promise<void> {
  if (item.kind === "count") {
    unwrap(await api.POST("/api/v1/inventory/counts/{countId}/entries", { params: { path: { countId: item.countId } }, body: { entries: [item.entry] } }));
    return;
  }
  const transfer = unwrap(await api.POST("/api/v1/inventory/transfers", { body: item.request }));
  unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/ship", { params: { path: { transferId: transfer.id } }, body: { shipDate: item.shipDate } }));
}

/** Sends the queue in order. Concurrent calls share one run. */
export function syncQueue(): Promise<SyncResult> {
  if (syncing) {
    return syncing;
  }
  syncing = (async () => {
    const result: SyncResult = { synced: 0, failed: 0, interrupted: false };
    for (const item of [...items]) {
      if (!navigator.onLine) {
        result.interrupted = true;
        break;
      }
      try {
        await send(item);
        commit(items.filter((queued) => queued.id !== item.id));
        result.synced += 1;
      } catch (error) {
        if (isNetworkError(error)) {
          result.interrupted = true;
          break;
        }
        result.failed += 1;
        commit(items.map((queued) => (queued.id === item.id ? { ...queued, error: describe(error) } : queued)));
      }
    }
    return result;
  })().finally(() => {
    syncing = null;
  });
  return syncing;
}
