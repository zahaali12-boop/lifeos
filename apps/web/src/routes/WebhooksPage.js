import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField } from "./common";
const tones = { delivered: "success", pending: "warning", failed: "danger", dead: "danger" };
export function WebhooksPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [open, setOpen] = useState(false);
    const [form, setForm] = useState({ name: "", url: "", eventTypes: "*" });
    const [problem, setProblem] = useState(null);
    const [secret, setSecret] = useState(null);
    const [inspecting, setInspecting] = useState(null);
    const webhooks = useQuery({ queryKey: ["webhooks"], queryFn: async () => unwrap(await api.GET("/api/v1/integration/webhooks")) });
    const deliveries = useQuery({
        queryKey: ["webhooks", inspecting?.id, "deliveries"],
        enabled: inspecting !== null,
        queryFn: async () => unwrap(await api.GET("/api/v1/integration/webhooks/{webhookId}/deliveries", { params: { path: { webhookId: inspecting?.id ?? "" }, query: { Limit: 50 } } })),
    });
    const create = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/integration/webhooks", { body: { name: form.name, url: form.url, eventTypes: form.eventTypes.split(",").map((s) => s.trim()).filter(Boolean), filters: null, active: true } })),
        onSuccess: async (created) => {
            setOpen(false);
            setProblem(null);
            setSecret(created.secret);
            setForm({ name: "", url: "", eventTypes: "*" });
            await queryClient.invalidateQueries({ queryKey: ["webhooks"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const test = useMutation({ mutationFn: async (id) => unwrap(await api.POST("/api/v1/integration/webhooks/{webhookId}/test", { params: { path: { webhookId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });
    const remove = useMutation({ mutationFn: async (id) => unwrap(await api.DELETE("/api/v1/integration/webhooks/{webhookId}", { params: { path: { webhookId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });
    const replay = useMutation({ mutationFn: async (id) => unwrap(await api.POST("/api/v1/integration/webhooks/deliveries/{deliveryId}/replay", { params: { path: { deliveryId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });
    const columns = useMemo(() => [
        { id: "name", accessorKey: "name", header: t("webhooks.name"), size: 180 },
        { id: "url", accessorKey: "url", header: "URL", size: 300, cell: ({ row }) => _jsx("span", { dir: "ltr", className: "truncate", children: row.original.url }) },
        { id: "eventTypes", accessorFn: (row) => row.eventTypes.join(", "), header: t("webhooks.eventTypes"), size: 200, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.eventTypes.join(", ") }) },
        { id: "active", accessorKey: "active", header: t("common.status"), size: 100, cell: ({ row }) => _jsx(Badge, { tone: row.original.active ? "success" : "neutral", children: row.original.active ? t("common.active") : t("common.inactive") }) },
        { id: "createdAt", accessorKey: "createdAt", header: t("common.created"), size: 170, cell: ({ row }) => formatDateTime(row.original.createdAt) },
    ], [t]);
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.webhooks"), description: t("webhooks.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setOpen(true); }, children: [_jsx(Plus, { "aria-hidden": "true" }), t("webhooks.new")] }) }), secret ? (_jsxs("div", { role: "status", className: "mb-4 rounded-md border border-warning/40 bg-warning-soft p-3 text-sm", children: [_jsx("div", { className: "font-medium", children: t("webhooks.secretOnce") }), _jsx("code", { className: "mt-1 block break-all font-mono", dir: "ltr", children: secret }), _jsx(Button, { size: "sm", variant: "ghost", className: "mt-2", onClick: () => { setSecret(null); }, children: t("common.dismiss") })] })) : null, _jsx(DataGrid, { label: "nav.webhooks", columns: columns, data: webhooks.data ?? [], rowKey: (row) => row.id, selectable: true, loading: webhooks.isPending, onOpen: setInspecting, emptyTitle: t("webhooks.emptyTitle"), emptyDescription: t("webhooks.emptyDescription"), bulkActions: (selected, clear) => (_jsxs(_Fragment, { children: [_jsx(Button, { size: "sm", variant: "secondary", onClick: () => { selected.forEach((id) => { test.mutate(id); }); clear(); }, children: t("webhooks.test") }), _jsx(Button, { size: "sm", variant: "danger", onClick: () => { selected.forEach((id) => { remove.mutate(id); }); clear(); }, children: t("common.delete") })] })) }), _jsx(Dialog, { open: open, onOpenChange: setOpen, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: _jsxs("form", { onSubmit: (event) => { event.preventDefault(); create.mutate(); }, className: "flex flex-col gap-4", children: [_jsxs(DialogHeader, { children: [_jsx(DialogTitle, { className: "text-lg font-semibold", children: t("webhooks.new") }), _jsx(DialogDescription, { className: "text-sm text-fg-muted", children: t("webhooks.newDescription") })] }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsx(Field, { label: t("webhooks.name"), required: true, error: problem?.fields.name, children: _jsx(TextField, { value: form.name, onChange: (e) => { setForm({ ...form, name: e.target.value }); }, required: true }) }), _jsx(Field, { label: "URL", required: true, error: problem?.fields.url, children: _jsx(TextField, { type: "url", value: form.url, onChange: (e) => { setForm({ ...form, url: e.target.value }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("webhooks.eventTypes"), required: true, description: t("webhooks.eventTypesHint"), error: problem?.fields.eventTypes, children: _jsx(TextField, { value: form.eventTypes, onChange: (e) => { setForm({ ...form, eventTypes: e.target.value }); }, required: true, dir: "ltr" }) }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setOpen(false); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: create.isPending, children: t("common.save") })] })] }) }) }), _jsx(Dialog, { open: inspecting !== null, onOpenChange: (value) => { if (!value) {
                    setInspecting(null);
                } }, children: _jsx(DialogContent, { closeLabel: t("common.close"), className: "sm:max-w-3xl", children: inspecting ? (_jsxs(_Fragment, { children: [_jsxs(DialogHeader, { children: [_jsx(DialogTitle, { className: "text-lg font-semibold", children: inspecting.name }), _jsx(DialogDescription, { className: "text-sm text-fg-muted", dir: "ltr", children: inspecting.url })] }), _jsxs(Table, { children: [_jsx(TableHeader, { children: _jsxs(TableRow, { children: [_jsx(TableHead, { children: t("webhooks.event") }), _jsx(TableHead, { children: t("common.status") }), _jsx(TableHead, { children: t("webhooks.attempt") }), _jsx(TableHead, { children: t("webhooks.response") }), _jsx(TableHead, { children: t("common.created") }), _jsx(TableHead, {})] }) }), _jsxs(TableBody, { children: [(deliveries.data?.items ?? []).map((delivery) => (_jsxs(TableRow, { children: [_jsx(TableCell, { dir: "ltr", children: delivery.eventType }), _jsx(TableCell, { children: _jsx(Badge, { tone: tones[delivery.status] ?? "neutral", children: delivery.status }) }), _jsx(TableCell, { className: "tabular", children: delivery.attempt }), _jsx(TableCell, { children: delivery.responseStatus ?? delivery.lastError ?? "" }), _jsx(TableCell, { children: formatDateTime(delivery.createdAt) }), _jsx(TableCell, { children: _jsx(Button, { size: "sm", variant: "ghost", onClick: () => { replay.mutate(delivery.id); }, children: t("webhooks.replay") }) })] }, delivery.id))), (deliveries.data?.items ?? []).length === 0 ? (_jsx(TableRow, { children: _jsx(TableCell, { colSpan: 6, className: "text-center text-fg-muted", children: t("webhooks.noDeliveries") }) })) : null] })] })] })) : null }) })] }));
}
