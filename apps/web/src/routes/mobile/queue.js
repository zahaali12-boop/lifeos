import { useSyncExternalStore } from "react";
import { api, isApiProblem, unwrap } from "../../api";
const STORAGE_KEY = "quicker.scanQueue";
const listeners = new Set();
let items = load();
let syncing = null;
function load() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : [];
    }
    catch {
        return [];
    }
}
function commit(next) {
    items = next;
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
    }
    catch {
        // storage unavailable: the queue lives in memory for this session
    }
    listeners.forEach((listener) => {
        listener();
    });
}
export function getQueue() {
    return items;
}
export function useQueue() {
    return useSyncExternalStore((listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
    }, getQueue, getQueue);
}
export function enqueue(capture) {
    const item = { ...capture, id: crypto.randomUUID(), createdAt: new Date().toISOString() };
    commit([...items, item]);
    return item;
}
export function removeFromQueue(id) {
    commit(items.filter((item) => item.id !== id));
}
function describe(error) {
    if (isApiProblem(error)) {
        return error.code ? `${error.code}: ${error.detail ?? error.title ?? ""}`.trim() : (error.detail ?? error.title ?? "error");
    }
    return error instanceof Error ? error.message : String(error);
}
/** A thrown TypeError from fetch means the request never reached the API: keep the capture untouched and stop. */
function isNetworkError(error) {
    return error instanceof TypeError;
}
async function send(item) {
    if (item.kind === "count") {
        unwrap(await api.POST("/api/v1/inventory/counts/{countId}/entries", { params: { path: { countId: item.countId } }, body: { entries: [item.entry] } }));
        return;
    }
    const transfer = unwrap(await api.POST("/api/v1/inventory/transfers", { body: item.request }));
    unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/ship", { params: { path: { transferId: transfer.id } }, body: { shipDate: item.shipDate } }));
}
/** Sends the queue in order. Concurrent calls share one run. */
export function syncQueue() {
    if (syncing) {
        return syncing;
    }
    syncing = (async () => {
        const result = { synced: 0, failed: 0, interrupted: false };
        for (const item of [...items]) {
            if (!navigator.onLine) {
                result.interrupted = true;
                break;
            }
            try {
                await send(item);
                commit(items.filter((queued) => queued.id !== item.id));
                result.synced += 1;
            }
            catch (error) {
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
