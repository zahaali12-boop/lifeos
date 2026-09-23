import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { UserPlus } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { DataGrid } from "../grid/DataGrid";
import { currentLanguage } from "../i18n";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField } from "./common";
export function MembersPage() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [open, setOpen] = useState(false);
    const [form, setForm] = useState({ email: "", displayName: "", roleIds: [] });
    const [problem, setProblem] = useState(null);
    const members = useQuery({ queryKey: ["members"], queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
    const roles = useQuery({ queryKey: ["roles"], queryFn: async () => unwrap(await api.GET("/api/v1/roles")) });
    const invite = useMutation({
        mutationFn: async () => unwrap(await api.POST("/api/v1/users/invite", { body: { email: form.email, displayName: form.displayName || null, roleIds: form.roleIds, language: currentLanguage() } })),
        onSuccess: async () => {
            setOpen(false);
            setProblem(null);
            setForm({ email: "", displayName: "", roleIds: [] });
            await queryClient.invalidateQueries({ queryKey: ["members"] });
        },
        onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
    });
    const setStatus = useMutation({
        mutationFn: async (input) => {
            for (const id of input.ids) {
                unwrap(await (input.enable ? api.POST("/api/v1/users/{membershipId}/enable", { params: { path: { membershipId: id } } }) : api.POST("/api/v1/users/{membershipId}/disable", { params: { path: { membershipId: id } } })));
            }
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["members"] }),
    });
    const columns = useMemo(() => [
        { id: "displayName", accessorKey: "displayName", header: t("members.name"), size: 200 },
        { id: "email", accessorKey: "email", header: t("auth.email"), size: 240, cell: ({ row }) => _jsx("span", { dir: "ltr", children: row.original.email }) },
        { id: "roles", accessorFn: (row) => row.assignments.map((a) => a.roleCode).join(", "), header: t("nav.roles"), size: 200, cell: ({ row }) => (row.original.isOwner ? _jsx(Badge, { tone: "accent", children: t("members.owner") }) : row.original.assignments.map((a) => a.roleCode).join(", ")) },
        { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => _jsx(Badge, { tone: row.original.status === "active" ? "success" : row.original.status === "invited" ? "info" : "neutral", children: t(`members.status.${row.original.status}`) }) },
        { id: "hasMfa", accessorKey: "hasMfa", header: t("members.mfa"), size: 90, cell: ({ row }) => (row.original.hasMfa ? t("common.yes") : t("common.no")) },
        { id: "lastLoginAt", accessorKey: "lastLoginAt", header: t("members.lastLogin"), size: 160, cell: ({ row }) => formatDateTime(row.original.lastLoginAt) },
    ], [t]);
    const submit = (event) => {
        event.preventDefault();
        invite.mutate();
    };
    return (_jsxs(_Fragment, { children: [_jsx(PageHeader, { title: t("nav.members"), description: t("members.description"), actions: _jsxs(Button, { onClick: () => { setProblem(null); setOpen(true); }, "data-testid": "invite-member", children: [_jsx(UserPlus, { "aria-hidden": "true" }), t("members.invite")] }) }), _jsx(DataGrid, { label: "nav.members", columns: columns, data: members.data ?? [], rowKey: (row) => row.membershipId, selectable: true, loading: members.isPending, emptyTitle: t("members.emptyTitle"), bulkActions: (selected, clear) => (_jsxs(_Fragment, { children: [_jsx(Button, { size: "sm", variant: "secondary", onClick: () => { setStatus.mutate({ ids: selected, enable: false }); clear(); }, children: t("members.disable") }), _jsx(Button, { size: "sm", variant: "secondary", onClick: () => { setStatus.mutate({ ids: selected, enable: true }); clear(); }, children: t("members.enable") })] })) }), _jsx(Dialog, { open: open, onOpenChange: setOpen, children: _jsx(DialogContent, { closeLabel: t("common.close"), children: _jsxs("form", { onSubmit: submit, className: "flex flex-col gap-4", children: [_jsx(DialogHeader, { children: _jsx(DialogTitle, { className: "text-lg font-semibold", children: t("members.invite") }) }), _jsx(FormError, { message: problem && Object.keys(problem.fields).length === 0 ? problem.message : null }), _jsx(Field, { label: t("auth.email"), required: true, error: problem?.fields.email, children: _jsx(TextField, { type: "email", value: form.email, onChange: (e) => { setForm({ ...form, email: e.target.value }); }, required: true, dir: "ltr" }) }), _jsx(Field, { label: t("members.name"), children: _jsx(TextField, { value: form.displayName, onChange: (e) => { setForm({ ...form, displayName: e.target.value }); } }) }), _jsxs("fieldset", { className: "flex flex-col gap-2", children: [_jsx("legend", { className: "text-sm font-medium", children: t("nav.roles") }), (roles.data ?? []).filter((role) => role.isActive).map((role) => (_jsxs("label", { className: "flex items-center gap-2 text-sm", children: [_jsx("input", { type: "checkbox", checked: form.roleIds.includes(role.id), onChange: (e) => { setForm({ ...form, roleIds: e.target.checked ? [...form.roleIds, role.id] : form.roleIds.filter((id) => id !== role.id) }); } }), _jsx("span", { className: "font-medium", children: role.code }), _jsx("span", { className: "text-fg-muted", children: localized(role.name) })] }, role.id)))] }), _jsxs(DialogFooter, { children: [_jsx(Button, { type: "button", variant: "secondary", onClick: () => { setOpen(false); }, children: t("common.cancel") }), _jsx(Button, { type: "submit", loading: invite.isPending, children: t("members.sendInvitation") })] })] }) }) })] }));
}
