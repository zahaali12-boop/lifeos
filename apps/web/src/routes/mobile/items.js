import { api } from "../../api";
const STORAGE_KEY = "quicker.scanItems";
const MAX_CACHED = 500;
const cache = new Map(load());
function load() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : [];
    }
    catch {
        return [];
    }
}
function remember(key, item) {
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
    }
    catch {
        // ignore
    }
}
/** The cached resolution of a code, for offline capture; codes are matched as typed, trimmed. */
export function cachedItem(code) {
    return cache.get(code.trim()) ?? null;
}
/**
 * Resolves a code online: a barcode first (the unit it carries), then an item code. Null when nothing matches; the
 * resolution is cached under both the code scanned and the item code so the same labels resolve offline later.
 */
export async function resolveItem(code) {
    const key = code.trim();
    if (key.length === 0) {
        return null;
    }
    const byBarcode = await api.GET("/api/v1/items/by-barcode/{barcode}", { params: { path: { barcode: key } } });
    let itemCode = key;
    let uomCode = null;
    let variantId = null;
    if (byBarcode.data) {
        itemCode = byBarcode.data.itemCode;
        uomCode = byBarcode.data.uomCode;
        variantId = byBarcode.data.variantId;
    }
    else if (byBarcode.response.status !== 404) {
        throw byBarcode.error;
    }
    const byCode = await api.GET("/api/v1/items/by-code/{code}", { params: { path: { code: itemCode } } });
    if (!byCode.data) {
        if (byCode.response.status === 404) {
            return null;
        }
        throw byCode.error;
    }
    const item = { itemId: byCode.data.id, itemCode: byCode.data.code, name: byCode.data.name, tracking: byCode.data.tracking, baseUom: byCode.data.baseUom, uomCode, variantId };
    remember(key, item);
    remember(item.itemCode, { ...item, uomCode: null, variantId: null });
    return item;
}
