import { api } from "../../api";

/** What a scanned code resolved to: the item (and unit) behind a barcode or an item code. */
export interface ResolvedItem {
  itemId: string;
  itemCode: string;
  name: Record<string, string>;
  tracking: string;
  baseUom: string;
  uomCode: string | null;
  variantId: string | null;
}

const STORAGE_KEY = "quicker.scanItems";
const MAX_CACHED = 500;
const cache = new Map<string, ResolvedItem>(load());

function load(): [string, ResolvedItem][] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as [string, ResolvedItem][]) : [];
  } catch {
    return [];
  }
}

function remember(key: string, item: ResolvedItem): void {
  cache.delete(key);
  cache.set(key, item);
  while (cache.size > MAX_CACHED) {
    const oldest = cache.keys().next().value;
    if (oldest === undefined) {
      break;
    }
    cache.delete(oldest);
  }
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...cache.entries()]));
  } catch {
    // ignore
  }
}

/** The cached resolution of a code, for offline capture; codes are matched as typed, trimmed. */
export function cachedItem(code: string): ResolvedItem | null {
  return cache.get(code.trim()) ?? null;
}

/**
 * Resolves a code online: a barcode first (the unit it carries), then an item code. Null when nothing matches; the
 * resolution is cached under both the code scanned and the item code so the same labels resolve offline later.
 */
export async function resolveItem(code: string): Promise<ResolvedItem | null> {
  const key = code.trim();
  if (key.length === 0) {
    return null;
  }
  const byBarcode = await api.GET("/api/v1/items/by-barcode/{barcode}", { params: { path: { barcode: key } } });
  let itemCode = key;
  let uomCode: string | null = null;
  let variantId: string | null = null;
  if (byBarcode.data) {
    itemCode = byBarcode.data.itemCode;
    uomCode = byBarcode.data.uomCode;
    variantId = byBarcode.data.variantId;
  } else if (byBarcode.response.status !== 404) {
    throw byBarcode.error;
  }
  const byCode = await api.GET("/api/v1/items/by-code/{code}", { params: { path: { code: itemCode } } });
  if (!byCode.data) {
    if (byCode.response.status === 404) {
      return null;
    }
    throw byCode.error;
  }
  const item: ResolvedItem = { itemId: byCode.data.id, itemCode: byCode.data.code, name: byCode.data.name, tracking: byCode.data.tracking, baseUom: byCode.data.baseUom, uomCode, variantId };
  remember(key, item);
  remember(item.itemCode, { ...item, uomCode: null, variantId: null });
  return item;
}
