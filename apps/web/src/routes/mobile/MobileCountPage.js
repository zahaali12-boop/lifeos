import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, EmptyState, Field } from "@quicker/ui";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { cachedItem, resolveItem } from "./items";
import { enqueue, syncQueue, useQueue } from "./queue";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";
/**
 * Counting by scanning (roadmap 3.8): pick the frozen count, scan the bin (when the warehouse has bins), scan the
 * item, type the quantity. Each capture sets the counted quantity of that bin/item line and goes through the queue,
 * so it is the same online and offline.
 */
export function MobileCountPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const context = useScanContext();
    const online = useOnline();
    const queue = useQueue();
    const companyId = context.companyId ?? "";
    const warehouseId = context.warehouseId ?? "";
    const ready = context.companyId !== null && context.warehouseId !== null;
    const warehouses = useQuery({
        queryKey: ["mobile", "warehouses", context.companyId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
        enabled: ready,
    });
    const warehouse = warehouses.data?.find((w) => w.id === warehouseId);
    const needsBin = warehouse?.binsEnabled === true;
    const counts = useQuery({
        queryKey: ["mobile", "counts", companyId, warehouseId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: { companyId, warehouseId } } })),
        enabled: ready,
    });
    const open = (counts.data ?? []).filter((c) => c.status === "frozen" || c.status === "counting");
    const [chosenCount, setChosenCount] = useState(null);
    const count = open.find((c) => c.id === chosenCount) ?? open[0];
    const countId = count?.id ?? null;
    const bins = useQuery({
        queryKey: ["mobile", "bins", warehouseId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId } } })),
        enabled: needsBin,
    });
    const sheet = useQuery({
        queryKey: ["mobile", "sheet", countId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{countId}/sheet", { params: { path: { countId: countId ?? "" } } })),
        enabled: countId !== null && online,
    });
    const [bin, setBin] = useState(null);
    const [item, setItem] = useState(null);
    const [quantity, setQuantity] = useState("");
    const [lot, setLot] = useState("");
    const [serial, setSerial] = useState("");
    const [error, setError] = useState(null);
    const [notice, setNotice] = useState(null);
    const [captures, setCaptures] = useState([]);
    const step = needsBin && !bin ? "bin" : item ? "quantity" : "item";
    const tracking = item?.resolved?.tracking ?? "none";
    const wantsLot = tracking === "lot" || tracking === "lot_and_serial";
    const wantsSerial = tracking === "serial" || tracking === "lot_and_serial";
    const onCode = async (code) => {
        setError(null);
        setNotice(null);
        if (step === "bin") {
            const found = (bins.data ?? []).find((b) => b.code.localeCompare(code, undefined, { sensitivity: "accent" }) === 0);
            if (!found) {
                setError(t("mobile.count.binUnknown", { code }));
                return;
            }
            setBin(found);
            return;
        }
        let resolved = cachedItem(code);
        if (!resolved && online) {
            try {
                resolved = await resolveItem(code);
            }
            catch (caught) {
                if (!(caught instanceof TypeError)) {
                    throw caught;
                }
            }
            if (!resolved && navigator.onLine) {
                setError(t("mobile.count.itemUnknown", { code }));
                return;
            }
        }
        if (!resolved) {
            setNotice(t("mobile.count.itemOffline", { code }));
        }
        setItem({ code, resolved });
        const serialOnly = resolved?.tracking === "serial" || resolved?.tracking === "lot_and_serial";
        setQuantity(serialOnly ? "1" : "");
        setLot("");
        setSerial("");
    };
    const add = () => {
        if (!item || !count) {
            return;
        }
        const parsed = parseQuantity(quantity);
        if (parsed === null) {
            setError(t("mobile.count.quantityInvalid"));
            return;
        }
        const itemCode = item.resolved?.itemCode ?? item.code;
        const label = [bin?.code, itemCode, lot || null, serial || null, `× ${parsed}`].filter((part) => part !== null && part !== undefined && part !== "").join(" · ");
        const queued = enqueue({
            kind: "count",
            countId: count.id,
            countNumber: count.number,
            label,
            entry: {
                itemId: item.resolved?.itemId ?? null,
                itemCode,
                binId: bin?.id ?? null,
                lotNumber: lot || null,
                serialNumber: serial || null,
                variantId: item.resolved?.variantId ?? null,
                countedQty: parsed,
            },
        });
        setCaptures((previous) => [{ id: queued.id, label }, ...previous]);
        setItem(null);
        setQuantity("");
        setLot("");
        setSerial("");
        setError(null);
        setNotice(null);
        if (online) {
            void syncQueue().then(() => queryClient.invalidateQueries({ queryKey: ["mobile", "sheet"] }));
        }
    };
    const statusOf = (id) => {
        const pending = queue.find((q) => q.id === id);
        if (!pending) {
            return { key: "mobile.count.synced", tone: "success" };
        }
        return pending.error ? { key: "mobile.count.failed", tone: "danger" } : { key: "mobile.count.queued", tone: "warning" };
    };
    if (!ready) {
        return _jsx(EmptyState, { title: t("mobile.count.title"), description: t("mobile.count.noContext") });
    }
    return (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: t("mobile.count.title") }), counts.isSuccess && open.length === 0 ? (_jsx(EmptyState, { title: t("mobile.count.pick"), description: t("mobile.count.none") })) : (_jsx(Field, { label: t("mobile.count.pick"), children: _jsxs(SelectField, { className: "h-12 text-base", "data-testid": "scan-count", value: countId ?? "", onChange: (event) => { setChosenCount(event.target.value || null); setBin(null); setItem(null); }, children: [countId === null ? _jsx("option", { value: "", children: t("mobile.count.chooseCount") }) : null, open.map((c) => (_jsxs("option", { value: c.id, children: [c.number, " \u00B7 ", c.scope] }, c.id)))] }) })), count ? (_jsxs(_Fragment, { children: [sheet.data ? _jsx("p", { className: "text-sm text-fg-muted", children: t("mobile.count.lines", { count: Number(sheet.data.count.countedLines) }) }) : null, needsBin && bin ? (_jsxs("div", { className: "flex items-center justify-between rounded-md border border-border bg-surface px-3 py-2", "data-testid": "current-bin", children: [_jsxs("span", { children: [_jsx("span", { className: "text-xs text-fg-muted", children: t("mobile.count.bin") }), _jsx("span", { className: "ms-2 font-medium", children: bin.code })] }), _jsx(Button, { variant: "ghost", size: "sm", "data-testid": "change-bin", onClick: () => { setBin(null); setItem(null); }, children: t("mobile.count.changeBin") })] })) : null, _jsx(ScanBox, { label: step === "bin" ? t("mobile.count.scanBin") : t("mobile.count.scanItem"), error: error, onCode: onCode, disabled: step === "bin" && !bins.isSuccess }), notice ? _jsx("p", { className: "text-sm text-warning", children: notice }) : null, item ? (_jsxs("div", { className: "flex flex-col gap-3 rounded-md border border-border bg-surface p-3", children: [_jsxs("div", { "data-testid": "current-item", children: [_jsx("div", { className: "font-medium", children: item.resolved?.itemCode ?? item.code }), _jsxs("div", { className: "text-sm text-fg-muted", children: [localized(item.resolved?.name), item.resolved ? ` · ${item.resolved.uomCode ?? item.resolved.baseUom}` : null] })] }), _jsx(Field, { label: t("mobile.count.quantity"), children: _jsx(TextField, { className: "h-12 text-lg", inputMode: "decimal", autoComplete: "off", "data-testid": "count-quantity", value: quantity, onChange: (event) => { setQuantity(event.target.value); }, autoFocus: true }) }), wantsLot ? (_jsx(Field, { label: t("mobile.count.lot"), children: _jsx(TextField, { className: "h-12", autoComplete: "off", "data-testid": "count-lot", value: lot, onChange: (event) => { setLot(event.target.value); } }) })) : null, wantsSerial ? (_jsx(Field, { label: t("mobile.count.serial"), children: _jsx(TextField, { className: "h-12", autoComplete: "off", "data-testid": "count-serial", value: serial, onChange: (event) => { setSerial(event.target.value); } }) })) : null, _jsx(Button, { size: "lg", className: "h-12", "data-testid": "count-add", onClick: add, children: t("mobile.count.add") })] })) : null, _jsx(FormError, { message: counts.isError || bins.isError ? t("common.saveFailed") : null }), captures.length > 0 ? (_jsxs("section", { "aria-labelledby": "captures-heading", children: [_jsx("h2", { id: "captures-heading", className: "mb-2 text-sm font-semibold text-fg-muted", children: t("mobile.count.captured") }), _jsx("ul", { className: "flex flex-col gap-1", children: captures.map((capture) => {
                                    const status = statusOf(capture.id);
                                    return (_jsxs("li", { className: "flex items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-sm", "data-testid": "capture", children: [_jsx("span", { className: "truncate", children: capture.label }), _jsx(Badge, { tone: status.tone, children: t(status.key) })] }, capture.id));
                                }) })] })) : null] })) : null] }));
}
