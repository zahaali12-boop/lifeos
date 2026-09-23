import { useSyncExternalStore } from "react";
const STORAGE_KEY = "quicker.scanContext";
const listeners = new Set();
let current = load();
function load() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (raw) {
            const parsed = JSON.parse(raw);
            return { companyId: typeof parsed.companyId === "string" ? parsed.companyId : null, warehouseId: typeof parsed.warehouseId === "string" ? parsed.warehouseId : null };
        }
    }
    catch {
        // storage unavailable: start empty
    }
    return { companyId: null, warehouseId: null };
}
export function getScanContext() {
    return current;
}
export function setScanContext(next) {
    current = { ...current, ...next };
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
    }
    catch {
        // ignore
    }
    listeners.forEach((listener) => {
        listener();
    });
}
export function useScanContext() {
    return useSyncExternalStore((listener) => {
        listeners.add(listener);
        return () => listeners.delete(listener);
    }, getScanContext, getScanContext);
}
/** navigator.onLine plus the online/offline events, as a hook. */
export function useOnline() {
    return useSyncExternalStore((listener) => {
        window.addEventListener("online", listener);
        window.addEventListener("offline", listener);
        return () => {
            window.removeEventListener("online", listener);
            window.removeEventListener("offline", listener);
        };
    }, () => navigator.onLine, () => true);
}
