import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { UserPlus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { currentLanguage } from "../i18n";
import { formatDateTime, localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField } from "./common";
import { MemberDialog } from "./MemberAssignments";

type Member = components["schemas"]["MemberSummary"];

export function MembersPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState({ email: "", displayName: "", roleIds: [] as string[] });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

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
    mutationFn: async (input: { ids: string[]; enable: boolean }) => {
      for (const id of input.ids) {
        unwrap(await (input.enable ? api.POST("/api/v1/users/{membershipId}/enable", { params: { path: { membershipId: id } } }) : api.POST("/api/v1/users/{membershipId}/disable", { params: { path: { membershipId: id } } })));
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["members"] }),
  });

  const columns = useMemo<ColumnDef<Member, unknown>[]>(
    () => [
      { id: "displayName", accessorKey: "displayName", header: t("members.name"), size: 200 },
      { id: "email", accessorKey: "email", header: t("auth.email"), size: 240, cell: ({ row }) => <span dir="ltr">{row.original.email}</span> },
      { id: "roles", accessorFn: (row) => row.assignments.map((a) => a.roleCode).join(", "), header: t("nav.roles"), size: 200, cell: ({ row }) => (row.original.isOwner ? <Badge tone="accent">{t("members.owner")}</Badge> : row.original.assignments.map((a) => a.roleCode).join(", ")) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <Badge tone={row.original.status === "active" ? "success" : row.original.status === "invited" ? "info" : "neutral"}>{t(`members.status.${row.original.status}`)}</Badge> },
      { id: "hasMfa", accessorKey: "hasMfa", header: t("members.mfa"), size: 90, cell: ({ row }) => (row.original.hasMfa ? t("common.yes") : t("common.no")) },
      { id: "lastLoginAt", accessorKey: "lastLoginAt", header: t("members.lastLogin"), size: 160, cell: ({ row }) => formatDateTime(row.original.lastLoginAt) },
    ],
    [t],
  );

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    invite.mutate();
  };

  return (
    <>
      <PageHeader
        title={t("nav.members")}
        description={t("members.description")}
        actions={
          <Button onClick={() => { setProblem(null); setOpen(true); }} data-testid="invite-member">
            <UserPlus aria-hidden="true" />
            {t("members.invite")}
          </Button>
        }
      />
      <DataGrid<Member>
        label="nav.members"
        columns={columns}
        data={members.data ?? []}
        rowKey={(row) => row.membershipId}
        selectable
        onOpen={(row) => { setOpenId(row.membershipId); }}
        loading={members.isPending}
        emptyTitle={t("members.emptyTitle")}
        bulkActions={(selected, clear) => (
          <>
            <Button size="sm" variant="secondary" onClick={() => { setStatus.mutate({ ids: selected, enable: false }); clear(); }}>
              {t("members.disable")}
            </Button>
            <Button size="sm" variant="secondary" onClick={() => { setStatus.mutate({ ids: selected, enable: true }); clear(); }}>
              {t("members.enable")}
            </Button>
          </>
        )}
      />
      <MemberDialog member={members.data?.find((m) => m.membershipId === openId) ?? null} roles={roles.data ?? []} onClose={() => { setOpenId(null); }} />
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent closeLabel={t("common.close")}>
          <form onSubmit={submit} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("members.invite")}</DialogTitle>
            </DialogHeader>
            <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
            <Field label={t("auth.email")} required error={problem?.fields.email}>
              <TextField type="email" value={form.email} onChange={(e) => { setForm({ ...form, email: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("members.name")}>
              <TextField value={form.displayName} onChange={(e) => { setForm({ ...form, displayName: e.target.value }); }} />
            </Field>
            <fieldset className="flex flex-col gap-2">
              <legend className="text-sm font-medium">{t("nav.roles")}</legend>
              {(roles.data ?? []).filter((role) => role.isActive).map((role) => (
                <label key={role.id} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={form.roleIds.includes(role.id)}
                    onChange={(e) => { setForm({ ...form, roleIds: e.target.checked ? [...form.roleIds, role.id] : form.roleIds.filter((id) => id !== role.id) }); }}
                  />
                  <span className="font-medium">{role.code}</span>
                  <span className="text-fg-muted">{localized(role.name)}</span>
                </label>
              ))}
            </fieldset>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setOpen(false); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={invite.isPending}>
                {t("members.sendInvitation")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
