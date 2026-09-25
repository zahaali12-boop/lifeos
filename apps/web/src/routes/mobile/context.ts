import { useSyncExternalStore } from "react";

/** Where the operator is working: the company and warehouse every mobile capture is made in, kept between launches. */
export interface ScanContext {
  companyId: string | null;
  warehouseId: string | null;
}

const STORAGE_KEY = "quicker.scanContext";
const listeners = new Set<() => void>();
let current: ScanContext = load();

function load(): ScanContext {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<ScanContext>;
      return { companyId: typeof parsed.companyId === "string" ? parsed.companyId : null, warehouseId: typeof parsed.warehouseId === "string" ? parsed.warehouseId : null };
    }
  } catch {
    // storage unavailable: start empty
  }
  return { companyId: null, warehouseId: null };
}

export function getScanContext(): ScanContext {
  return current;
}

export function setScanContext(next: Partial<ScanContext>): void {
  current = { ...current, ...next };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(current));
  } catch {
    // ignore
  }
  listeners.forEach((listener) => {
    listener();
  });
}

export function useScanContext(): ScanContext {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getScanContext,
    getScanContext,
  );
}

/** navigator.onLine plus the online/offline events, as a hook. */
export function useOnline(): boolean {
  return useSyncExternalStore(
    (listener) => {
      window.addEventListener("online", listener);
      window.addEventListener("offline", listener);
      return () => {
        window.removeEventListener("online", listener);
        window.removeEventListener("offline", listener);
      };
    },
    () => navigator.onLine,
    () => true,
  );
}
