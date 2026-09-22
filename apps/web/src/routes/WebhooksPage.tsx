import { Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { FormError, PageHeader, TextField } from "./common";

type Webhook = components["schemas"]["WebhookSummary"];

const tones: Record<string, "success" | "warning" | "danger" | "neutral"> = { delivered: "success", pending: "warning", failed: "danger", dead: "danger" };

export function WebhooksPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState({ name: "", url: "", eventTypes: "*" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [secret, setSecret] = useState<string | null>(null);
  const [inspecting, setInspecting] = useState<Webhook | null>(null);

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
  const test = useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integration/webhooks/{webhookId}/test", { params: { path: { webhookId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });
  const remove = useMutation({ mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/integration/webhooks/{webhookId}", { params: { path: { webhookId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });
  const replay = useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integration/webhooks/deliveries/{deliveryId}/replay", { params: { path: { deliveryId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["webhooks"] }) });

  const columns = useMemo<ColumnDef<Webhook, unknown>[]>(
    () => [
      { id: "name", accessorKey: "name", header: t("webhooks.name"), size: 180 },
      { id: "url", accessorKey: "url", header: "URL", size: 300, cell: ({ row }) => <span dir="ltr" className="truncate">{row.original.url}</span> },
      { id: "eventTypes", accessorFn: (row) => row.eventTypes.join(", "), header: t("webhooks.eventTypes"), size: 200, cell: ({ row }) => <span dir="ltr">{row.original.eventTypes.join(", ")}</span> },
      { id: "active", accessorKey: "active", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.active ? "success" : "neutral"}>{row.original.active ? t("common.active") : t("common.inactive")}</Badge> },
      { id: "createdAt", accessorKey: "createdAt", header: t("common.created"), size: 170, cell: ({ row }) => formatDateTime(row.original.createdAt) },
    ],
    [t],
  );

  return (
    <>
      <PageHeader
        title={t("nav.webhooks")}
        description={t("webhooks.description")}
        actions={
          <Button onClick={() => { setProblem(null); setOpen(true); }}>
            <Plus aria-hidden="true" />
            {t("webhooks.new")}
          </Button>
        }
      />
      {secret ? (
        <div role="status" className="mb-4 rounded-md border border-warning/40 bg-warning-soft p-3 text-sm">
          <div className="font-medium">{t("webhooks.secretOnce")}</div>
          <code className="mt-1 block break-all font-mono" dir="ltr">
            {secret}
          </code>
          <Button size="sm" variant="ghost" className="mt-2" onClick={() => { setSecret(null); }}>
            {t("common.dismiss")}
          </Button>
        </div>
      ) : null}
      <DataGrid<Webhook>
        label="nav.webhooks"
        columns={columns}
        data={webhooks.data ?? []}
        rowKey={(row) => row.id}
        selectable
        loading={webhooks.isPending}
        onOpen={setInspecting}
        emptyTitle={t("webhooks.emptyTitle")}
        emptyDescription={t("webhooks.emptyDescription")}
        bulkActions={(selected, clear) => (
          <>
            <Button size="sm" variant="secondary" onClick={() => { selected.forEach((id) => { test.mutate(id); }); clear(); }}>
              {t("webhooks.test")}
            </Button>
            <Button size="sm" variant="danger" onClick={() => { selected.forEach((id) => { remove.mutate(id); }); clear(); }}>
              {t("common.delete")}
            </Button>
          </>
        )}
      />
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent closeLabel={t("common.close")}>
          <form onSubmit={(event: FormEvent) => { event.preventDefault(); create.mutate(); }} className="flex flex-col gap-4">
            <DialogHeader>
              <DialogTitle className="text-lg font-semibold">{t("webhooks.new")}</DialogTitle>
              <DialogDescription className="text-sm text-fg-muted">{t("webhooks.newDescription")}</DialogDescription>
            </DialogHeader>
            <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
            <Field label={t("webhooks.name")} required error={problem?.fields.name}>
              <TextField value={form.name} onChange={(e) => { setForm({ ...form, name: e.target.value }); }} required />
            </Field>
            <Field label="URL" required error={problem?.fields.url}>
              <TextField type="url" value={form.url} onChange={(e) => { setForm({ ...form, url: e.target.value }); }} required dir="ltr" />
            </Field>
            <Field label={t("webhooks.eventTypes")} required description={t("webhooks.eventTypesHint")} error={problem?.fields.eventTypes}>
              <TextField value={form.eventTypes} onChange={(e) => { setForm({ ...form, eventTypes: e.target.value }); }} required dir="ltr" />
            </Field>
            <DialogFooter>
              <Button type="button" variant="secondary" onClick={() => { setOpen(false); }}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={create.isPending}>
                {t("common.save")}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog open={inspecting !== null} onOpenChange={(value) => { if (!value) { setInspecting(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="sm:max-w-3xl">
          {inspecting ? (
            <>
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{inspecting.name}</DialogTitle>
                <DialogDescription className="text-sm text-fg-muted" dir="ltr">
                  {inspecting.url}
                </DialogDescription>
              </DialogHeader>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("webhooks.event")}</TableHead>
                    <TableHead>{t("common.status")}</TableHead>
                    <TableHead>{t("webhooks.attempt")}</TableHead>
                    <TableHead>{t("webhooks.response")}</TableHead>
                    <TableHead>{t("common.created")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(deliveries.data?.items ?? []).map((delivery) => (
                    <TableRow key={delivery.id}>
                      <TableCell dir="ltr">{delivery.eventType}</TableCell>
                      <TableCell>
                        <Badge tone={tones[delivery.status] ?? "neutral"}>{delivery.status}</Badge>
                      </TableCell>
                      <TableCell className="tabular">{delivery.attempt}</TableCell>
                      <TableCell>{delivery.responseStatus ?? delivery.lastError ?? ""}</TableCell>
                      <TableCell>{formatDateTime(delivery.createdAt)}</TableCell>
                      <TableCell>
                        <Button size="sm" variant="ghost" onClick={() => { replay.mutate(delivery.id); }}>
                          {t("webhooks.replay")}
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                  {(deliveries.data?.items ?? []).length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={6} className="text-center text-fg-muted">
                        {t("webhooks.noDeliveries")}
                      </TableCell>
                    </TableRow>
                  ) : null}
                </TableBody>
              </Table>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
