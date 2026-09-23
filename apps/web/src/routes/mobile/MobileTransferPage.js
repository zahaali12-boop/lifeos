import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Button, EmptyState, Field } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { cachedItem, resolveItem } from "./items";
import { enqueue, syncQueue } from "./queue";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";
/** A one-step transfer built by scanning (roadmap 3.8): lines from the operator's warehouse to another, shipped on sync. */
export function MobileTransferPage() {
    const { t } = useTranslation();
    const context = useScanContext();
    const online = useOnline();
    const companyId = context.companyId ?? "";
    const fromId = context.warehouseId ?? "";
    const ready = context.companyId !== null && context.warehouseId !== null;
    const warehouses = useQuery({
        queryKey: ["mobile", "warehouses", context.companyId],
        queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId } } })),
        enabled: ready,
    });
    const [toId, setToId] = useState("");
    const [item, setItem] = useState(null);
    const [quantity, setQuantity] = useState("");
    const [lines, setLines] = useState([]);
    const [error, setError] = useState(null);
    const [notice, setNotice] = useState(null);
    const onCode = async (code) => {
        setError(null);
        setNotice(null);
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
        setQuantity("");
    };
    const addLine = () => {
        if (!item) {
            return;
        }
        const parsed = parseQuantity(quantity);
        if (parsed === null || parsed === "0") {
            setError(t("mobile.count.quantityInvalid"));
            return;
        }
        setLines((previous) => [...previous, { key: crypto.randomUUID(), code: item.code, resolved: item.resolved, quantity: parsed }]);
        setItem(null);
        setQuantity("");
        setError(null);
    };
    const ship = () => {
        const to = warehouses.data?.find((w) => w.id === toId);
        if (!to || lines.length === 0) {
            return;
        }
        if (to.id === fromId) {
            setError(t("mobile.transfer.sameWarehouse"));
            return;
        }
        enqueue({
            kind: "transfer",
            label: to.code,
            shipDate: null,
            request: {
                companyId,
                fromWarehouseId: fromId,
                toWarehouseId: to.id,
                kind: "one_step",
                lines: lines.map((line) => ({
                    itemId: line.resolved?.itemId ?? null,
                    itemCode: line.resolved?.itemCode ?? line.code,
                    quantity: line.quantity,
                    uom: line.resolved?.uomCode ?? null,
                    variantId: line.resolved?.variantId ?? null,
                })),
            },
        });
        setLines([]);
        setNotice(t("mobile.transfer.queued"));
        if (online) {
            void syncQueue();
        }
    };
    if (!ready) {
        return _jsx(EmptyState, { title: t("mobile.transfer.title"), description: t("mobile.count.noContext") });
    }
    return (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: t("mobile.transfer.title") }), _jsx(Field, { label: t("mobile.transfer.to"), children: _jsxs(SelectField, { className: "h-12 text-base", "data-testid": "transfer-to", value: toId, onChange: (event) => { setToId(event.target.value); }, children: [_jsx("option", { value: "", children: t("mobile.transfer.chooseTo") }), (warehouses.data ?? [])
                            .filter((w) => w.id !== fromId)
                            .map((w) => (_jsxs("option", { value: w.id, children: [w.code, " \u00B7 ", localized(w.name)] }, w.id)))] }) }), _jsx(ScanBox, { label: t("mobile.transfer.scanItem"), error: error, onCode: onCode, disabled: toId === "" }), notice ? _jsx("p", { className: "text-sm text-fg-muted", "data-testid": "transfer-notice", children: notice }) : null, item ? (_jsxs("div", { className: "flex flex-col gap-3 rounded-md border border-border bg-surface p-3", children: [_jsxs("div", { "data-testid": "current-item", children: [_jsx("div", { className: "font-medium", children: item.resolved?.itemCode ?? item.code }), _jsx("div", { className: "text-sm text-fg-muted", children: localized(item.resolved?.name) })] }), _jsx(Field, { label: t("mobile.transfer.quantity"), children: _jsx(TextField, { className: "h-12 text-lg", inputMode: "decimal", autoComplete: "off", "data-testid": "transfer-quantity", value: quantity, onChange: (event) => { setQuantity(event.target.value); }, autoFocus: true }) }), _jsx(Button, { size: "lg", className: "h-12", "data-testid": "transfer-add-line", onClick: addLine, children: t("mobile.transfer.addLine") })] })) : null, _jsxs("section", { "aria-labelledby": "transfer-lines-heading", children: [_jsx("h2", { id: "transfer-lines-heading", className: "mb-2 text-sm font-semibold text-fg-muted", children: t("mobile.transfer.lines") }), lines.length === 0 ? (_jsx("p", { className: "text-sm text-fg-subtle", children: t("mobile.transfer.noLines") })) : (_jsx("ul", { className: "flex flex-col gap-1", children: lines.map((line) => (_jsxs("li", { className: "flex items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-sm", "data-testid": "transfer-line", children: [_jsxs("span", { className: "truncate", children: [line.resolved?.itemCode ?? line.code, " \u00D7 ", line.quantity] }), _jsx(Button, { variant: "ghost", size: "icon", "aria-label": t("mobile.transfer.remove"), onClick: () => { setLines((previous) => previous.filter((l) => l.key !== line.key)); }, children: _jsx(Trash2, { "aria-hidden": "true" }) })] }, line.key))) }))] }), _jsx(FormError, { message: warehouses.isError ? t("common.saveFailed") : null }), _jsx(Button, { size: "lg", className: "h-12", "data-testid": "transfer-ship", disabled: lines.length === 0 || toId === "", onClick: ship, children: t("mobile.transfer.ship", { count: lines.length }) })] }));
}
