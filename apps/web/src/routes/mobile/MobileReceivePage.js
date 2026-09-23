import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Button, EmptyState, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { toFormProblem } from "../../lib/problem";
import { FormError, SelectField, TextField } from "../common";
import { useOnline, useScanContext } from "./context";
import { ScanBox } from "./ScanBox";
import { localized, parseQuantity } from "./text";
/** Receiving at the dock (roadmap 4.3): the open lines of one purchase order, quantities entered by scanning or typing, posted into the operator's warehouse at once. */
export function MobileReceivePage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const context = useScanContext();
    const online = useOnline();
    const companyId = context.companyId ?? "";
    const warehouseId = context.warehouseId ?? "";
    const ready = context.companyId !== null && context.warehouseId !== null;
    const receivable = useQuery({
        queryKey: ["mobile", "receivable", companyId],
        queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/receivable", { params: { query: { companyId } } })),
        enabled: ready && online,
    });
    const [orderId, setOrderId] = useState("");
    const [deliveryNote, setDeliveryNote] = useState("");
    const [entries, setEntries] = useState({});
    const [error, setError] = useState(null);
    const [done, setDone] = useState(null);
    const orders = new Map();
    for (const line of receivable.data ?? []) {
        orders.set(line.orderId, line.orderNumber);
    }
    const lines = (receivable.data ?? []).filter((l) => l.orderId === orderId);
    const entry = (line) => entries[line.orderLineId] ?? { quantity: "", lotNumber: "", expiresOn: "" };
    const patch = (line, change) => { setEntries((prev) => ({ ...prev, [line.orderLineId]: { ...entry(line), ...change } })); };
    const onCode = (code) => {
        setError(null);
        const line = lines.find((l) => l.itemCode.toUpperCase() === code.trim().toUpperCase());
        if (!line) {
            setError(t("mobile.receive.notOnOrder", { code }));
            return;
        }
        const current = parseQuantity(entry(line).quantity) ?? "0";
        patch(line, { quantity: String(Number(current) + 1) });
    };
    const receive = useMutation({
        mutationFn: async () => {
            const body = {
                orderId,
                warehouseId,
                supplierDeliveryNote: deliveryNote || null,
                lines: lines
                    .map((l) => ({ line: l, e: entry(l) }))
                    .filter(({ e }) => (parseQuantity(e.quantity) ?? "0") !== "0")
                    .map(({ line, e }) => ({ orderLineId: line.orderLineId, quantity: Number(parseQuantity(e.quantity) ?? "0"), lotNumber: e.lotNumber || null, expiresOn: e.expiresOn || null })),
            };
            if (body.lines.length === 0) {
                throw new Error(t("mobile.receive.nothingEntered"));
            }
            const draft = unwrap(await api.POST("/api/v1/purchasing/receipts", { body }));
            return unwrap(await api.POST("/api/v1/purchasing/receipts/{receiptId}/post", { params: { path: { receiptId: draft.id } } }));
        },
        onSuccess: async (posted) => {
            setError(null);
            setDone(posted.number);
            setEntries({});
            setDeliveryNote("");
            await queryClient.invalidateQueries({ queryKey: ["mobile", "receivable"] });
        },
        onError: (caught) => { setError(caught instanceof Error && !("status" in caught) ? caught.message : toFormProblem(caught, t("common.saveFailed")).message); },
    });
    if (!ready) {
        return _jsx(EmptyState, { title: t("mobile.count.noContext"), description: t("mobile.count.noContextDescription") });
    }
    return (_jsxs("div", { className: "flex flex-col gap-4", children: [_jsxs("div", { children: [_jsx("h1", { className: "text-xl font-semibold tracking-tight", children: t("mobile.receive.title") }), _jsx("p", { className: "mt-1 text-sm text-fg-muted", children: t("mobile.receive.description") })] }), !online ? _jsx("p", { className: "text-sm text-warning", "data-testid": "receive-offline", children: t("mobile.receive.offline") }) : null, _jsx(Field, { label: t("nav.purchaseOrders"), children: _jsxs(SelectField, { className: "h-12 text-base", value: orderId, onChange: (e) => { setOrderId(e.target.value); setEntries({}); setDone(null); }, "data-testid": "receive-order", children: [_jsx("option", { value: "", children: t("mobile.receive.chooseOrder") }), [...orders.entries()].map(([id, number]) => (_jsx("option", { value: id, children: number }, id)))] }) }), orderId ? (_jsxs(_Fragment, { children: [_jsx(ScanBox, { label: t("mobile.receive.scan"), hint: t("mobile.receive.scanHint"), error: null, disabled: false, onCode: onCode }), _jsx(Field, { label: t("purchasing.deliveryNote"), children: _jsx(TextField, { className: "h-12 text-base", value: deliveryNote, onChange: (e) => { setDeliveryNote(e.target.value); }, dir: "ltr", "data-testid": "receive-note" }) }), _jsx("ul", { className: "flex flex-col gap-3", "data-testid": "receive-lines", children: lines.map((line, index) => {
                            const e = entry(line);
                            const lotTracked = line.tracking === "lot" || line.tracking === "lot_and_serial";
                            return (_jsxs("li", { className: "rounded-md border border-border p-3", children: [_jsxs("div", { className: "flex items-baseline justify-between gap-2", children: [_jsxs("span", { className: "font-medium", dir: "auto", children: [line.itemCode, " \u00B7 ", localized(line.itemName)] }), _jsx("span", { className: "text-xs text-fg-muted tabular", dir: "ltr", children: t("mobile.receive.remaining", { remaining: String(line.remaining), uom: line.uomCode }) })] }), _jsxs("div", { className: "mt-2 grid grid-cols-2 gap-2", children: [_jsx(Field, { label: t("purchasing.receiveNow"), children: _jsx(TextField, { className: "h-12 text-base", inputMode: "decimal", value: e.quantity, onChange: (ev) => { patch(line, { quantity: ev.target.value }); }, dir: "ltr", "data-testid": `mobile-receive-qty-${String(index)}` }) }), lotTracked ? (_jsx(Field, { label: t("purchasing.lot"), children: _jsx(TextField, { className: "h-12 text-base", value: e.lotNumber, onChange: (ev) => { patch(line, { lotNumber: ev.target.value }); }, dir: "ltr", "data-testid": `mobile-receive-lot-${String(index)}` }) })) : null, lotTracked ? (_jsx(Field, { label: t("purchasing.expiresOn"), children: _jsx(TextField, { className: "h-12 text-base", type: "date", value: e.expiresOn, onChange: (ev) => { patch(line, { expiresOn: ev.target.value }); }, dir: "ltr" }) })) : null] })] }, line.orderLineId));
                        }) }), _jsx(FormError, { message: error }), done ? _jsx("p", { className: "text-sm text-success", "data-testid": "receive-done", children: t("mobile.receive.done", { number: done }) }) : null, _jsx(Button, { size: "lg", className: "h-14 text-base", onClick: () => { receive.mutate(); }, loading: receive.isPending, disabled: !online || lines.length === 0, "data-testid": "receive-post", children: t("mobile.receive.post") })] })) : null] }));
}
