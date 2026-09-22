import { Badge, Button, Select, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { RefreshCw } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime, formatNumber } from "../lib/format";
import { PageHeader } from "./common";

type Job = components["schemas"]["JobRecord"];

const tones: Record<string, "success" | "accent" | "danger" | "warning" | "neutral"> = { succeeded: "success", running: "accent", queued: "neutral", failed: "warning", dead: "danger" };

/** Background work: the job queue with retry/cancel, and the tenant's schedules. */
export function JobsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [state, setState] = useState("");
  const jobs = useQuery({ queryKey: ["jobs", state], queryFn: async () => unwrap(await api.GET("/api/v1/platform/jobs", { params: { query: { limit: 500, ...(state ? { state } : {}) } } })), refetchInterval: 15_000 });
  const schedules = useQuery({ queryKey: ["schedules"], queryFn: async () => unwrap(await api.GET("/api/v1/platform/schedules")) });
  const retry = useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/platform/jobs/{jobId}/retry", { params: { path: { jobId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["jobs"] }) });
  const cancel = useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/platform/jobs/{jobId}/cancel", { params: { path: { jobId: id } } })), onSuccess: () => queryClient.invalidateQueries({ queryKey: ["jobs"] }) });

  const columns = useMemo<ColumnDef<Job, unknown>[]>(
    () => [
      { id: "type", accessorKey: "type", header: t("jobs.type"), size: 220, cell: ({ row }) => <span dir="ltr">{row.original.type}</span> },
      { id: "state", accessorKey: "state", header: t("common.status"), size: 110, cell: ({ row }) => <Badge tone={tones[row.original.state ?? "queued"] ?? "neutral"}>{t(`jobs.states.${row.original.state ?? "queued"}`)}</Badge> },
      { id: "attempts", accessorFn: (row) => `${row.attempts ?? 0}/${row.maxAttempts ?? 0}`, header: t("jobs.attempts"), size: 100, cell: ({ row }) => <span className="tabular">{formatNumber(row.original.attempts ?? 0)}/{formatNumber(row.original.maxAttempts ?? 0)}</span> },
      { id: "createdAt", accessorKey: "createdAt", header: t("common.created"), size: 170, cell: ({ row }) => formatDateTime(row.original.createdAt) },
      { id: "finishedAt", accessorKey: "finishedAt", header: t("jobs.finished"), size: 170, cell: ({ row }) => formatDateTime(row.original.finishedAt) },
      { id: "error", accessorKey: "error", header: t("jobs.error"), size: 300, cell: ({ row }) => <span className="truncate text-danger">{row.original.error}</span> },
    ],
    [t],
  );

  return (
    <>
      <PageHeader
        title={t("nav.jobs")}
        description={t("jobs.description")}
        actions={
          <Button variant="secondary" onClick={() => { void jobs.refetch(); }} loading={jobs.isFetching}>
            <RefreshCw aria-hidden="true" />
            {t("common.refresh")}
          </Button>
        }
      />
      <DataGrid<Job>
        label="nav.jobs"
        columns={columns}
        data={jobs.data ?? []}
        rowKey={(row) => row.id ?? ""}
        selectable
        loading={jobs.isPending}
        emptyTitle={t("jobs.emptyTitle")}
        toolbar={
          <Select value={state} onChange={(e) => { setState(e.target.value); }} aria-label={t("common.status")} className="w-40">
            <option value="">{t("jobs.allStates")}</option>
            {["queued", "running", "succeeded", "failed", "dead"].map((s) => (
              <option key={s} value={s}>
                {t(`jobs.states.${s}`)}
              </option>
            ))}
          </Select>
        }
        bulkActions={(selected, clear) => (
          <>
            <Button size="sm" variant="secondary" onClick={() => { selected.forEach((id) => { retry.mutate(id); }); clear(); }}>
              {t("jobs.retry")}
            </Button>
            <Button size="sm" variant="secondary" onClick={() => { selected.forEach((id) => { cancel.mutate(id); }); clear(); }}>
              {t("jobs.cancel")}
            </Button>
          </>
        )}
      />
      <h2 className="mb-2 mt-8 text-sm font-semibold uppercase tracking-wide text-fg-muted">{t("jobs.schedules")}</h2>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("jobs.scheduleCode")}</TableHead>
            <TableHead>{t("jobs.type")}</TableHead>
            <TableHead>{t("jobs.cron")}</TableHead>
            <TableHead>{t("jobs.timeZone")}</TableHead>
            <TableHead>{t("jobs.nextRun")}</TableHead>
            <TableHead>{t("common.status")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(schedules.data ?? []).map((schedule) => (
            <TableRow key={schedule.id}>
              <TableCell className="font-medium" dir="ltr">{schedule.code}</TableCell>
              <TableCell dir="ltr">{schedule.jobType}</TableCell>
              <TableCell dir="ltr">{schedule.cron}</TableCell>
              <TableCell dir="ltr">{schedule.timeZone}</TableCell>
              <TableCell>{formatDateTime(schedule.nextRunAt)}</TableCell>
              <TableCell>
                <Badge tone={schedule.enabled ? "success" : "neutral"}>{schedule.enabled ? t("common.active") : t("common.inactive")}</Badge>
              </TableCell>
            </TableRow>
          ))}
          {(schedules.data ?? []).length === 0 ? (
            <TableRow>
              <TableCell colSpan={6} className="text-center text-fg-muted">
                {t("jobs.noSchedules")}
              </TableCell>
            </TableRow>
          ) : null}
        </TableBody>
      </Table>
    </>
  );
}
