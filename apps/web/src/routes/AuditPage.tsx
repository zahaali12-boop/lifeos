import { Badge, Button, Input } from "@quicker/ui";
import { useInfiniteQuery } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { X } from "lucide-react";
import { useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { DataGrid } from "../grid/DataGrid";
import { formatDateTime } from "../lib/format";
import { AuditEventDialog } from "./AuditEventDialog";
import { auditActionLabel, auditActionTone, auditEntityLabel, auditVocabulary } from "./auditLabels";
import { PageHeader } from "./common";

type AuditEvent = components["schemas"]["AuditEventSummary"];

interface RecordFilter {
  entityType: string;
  entityId: string;
  display: string;
}

/**
 * The audit explorer: newest first, cursor-paged, filtered by text, record type and action (typed or picked from the
 * known ones). A row opens the event in full, and from there the explorer narrows to every event of that record.
 */
export function AuditPage() {
  const { t } = useTranslation();
  const listId = useId();
  const [q, setQ] = useState("");
  const [entityType, setEntityType] = useState("");
  const [action, setAction] = useState("");
  const [record, setRecord] = useState<RecordFilter | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const vocabulary = useMemo(() => auditVocabulary(t), [t]);
  // The filters take a code; a label picked from the suggestions is turned back into its code.
  const typeCode = vocabulary.entityTypes.find((v) => v.label === entityType)?.code ?? entityType.trim();
  const actionCode = vocabulary.actions.find((v) => v.label === action)?.code ?? action.trim();

  const events = useInfiniteQuery({
    queryKey: ["audit", q, typeCode, actionCode, record?.entityId ?? ""],
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET("/api/v1/audit/events", {
        params: {
          query: {
            Limit: 100,
            ...(q ? { Q: q } : {}),
            ...(record ? { EntityType: record.entityType, EntityId: record.entityId } : typeCode ? { EntityType: typeCode } : {}),
            ...(actionCode ? { Action: actionCode } : {}),
            ...(pageParam ? { Cursor: pageParam } : {}),
          },
        },
      })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const columns = useMemo<ColumnDef<AuditEvent, unknown>[]>(
    () => [
      { id: "seq", accessorKey: "seq", header: "#", size: 70, cell: ({ row }) => <span className="tabular">{String(row.original.seq)}</span> },
      { id: "occurredAt", accessorKey: "occurredAt", header: t("audit.when"), size: 170, cell: ({ row }) => formatDateTime(row.original.occurredAt) },
      { id: "actor", accessorFn: (row) => row.actor.display, header: t("audit.actor"), size: 180 },
      {
        id: "action",
        accessorKey: "action",
        header: t("audit.action"),
        size: 150,
        cell: ({ row }) => <Badge tone={auditActionTone(row.original.action)}>{auditActionLabel(t, row.original.action)}</Badge>,
      },
      { id: "entityType", accessorFn: (row) => auditEntityLabel(t, row.entityType), header: t("audit.entityType"), size: 170 },
      { id: "entityDisplay", accessorKey: "entityDisplay", header: t("audit.record"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.entityDisplay}</span> },
      { id: "reason", accessorKey: "reason", header: t("common.reason"), size: 200 },
    ],
    [t],
  );

  const rows = events.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <>
      <PageHeader title={t("nav.audit")} description={t("audit.description")} />
      <datalist id={`${listId}-types`}>
        {vocabulary.entityTypes.map((v) => <option key={v.code} value={v.label} />)}
      </datalist>
      <datalist id={`${listId}-actions`}>
        {vocabulary.actions.map((v) => <option key={v.code} value={v.label} />)}
      </datalist>
      <DataGrid<AuditEvent>
        label="nav.audit"
        columns={columns}
        data={rows}
        rowKey={(row) => row.id}
        loading={events.isPending}
        emptyTitle={t("audit.emptyTitle")}
        onOpen={(row) => { setOpen(row.id); }}
        toolbar={
          <>
            <Input type="search" placeholder={t("audit.searchPlaceholder")} value={q} onChange={(e) => { setQ(e.target.value); }} className="w-56" aria-label={t("common.search")} />
            {record ? (
              <span className="inline-flex items-center gap-1 rounded-md border border-border bg-accent-soft px-2 py-1 text-sm text-accent" data-testid="audit-record-filter">
                <span dir="auto">{t("audit.detail.recordFilter", { name: `${auditEntityLabel(t, record.entityType)} ${record.display}` })}</span>
                <button type="button" className="rounded-sm hover:bg-accent/10" aria-label={t("audit.detail.clearRecord")} onClick={() => { setRecord(null); }} data-testid="audit-clear-record">
                  <X className="size-3.5" aria-hidden="true" />
                </button>
              </span>
            ) : (
              <Input
                placeholder={t("audit.allRecords")}
                value={entityType}
                onChange={(e) => { setEntityType(e.target.value); }}
                list={`${listId}-types`}
                className="w-48"
                aria-label={t("audit.entityType")}
                data-testid="audit-entity-type"
              />
            )}
            <Input
              placeholder={t("audit.allActions")}
              value={action}
              onChange={(e) => { setAction(e.target.value); }}
              list={`${listId}-actions`}
              className="w-44"
              aria-label={t("audit.action")}
              data-testid="audit-action"
            />
          </>
        }
      />
      {events.hasNextPage ? (
        <Button variant="secondary" className="mt-3" onClick={() => { void events.fetchNextPage(); }} loading={events.isFetchingNextPage}>
          {t("common.loadMore")}
        </Button>
      ) : null}
      <AuditEventDialog
        openRecord
        eventId={open}
        onClose={() => { setOpen(null); }}
        onShowRecord={(e) => { setRecord({ entityType: e.entityType, entityId: e.entityId, display: e.entityDisplay }); setOpen(null); }}
      />
    </>
  );
}
