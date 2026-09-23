import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDateTime } from "../lib/format";
import { auditActionLabel, auditActionTone, auditChanges, auditEntityLabel, auditValue } from "./auditLabels";

type AuditEvent = components["schemas"]["AuditEventDetail"];

/** A labelled value of the event; empty values are left out rather than shown as blanks. */
function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[10rem_1fr] gap-2 py-1 max-sm:grid-cols-1">
      <dt className="text-fg-muted">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </div>
  );
}

function Snapshot({ label, value }: { label: string; value: unknown }) {
  if (value === null || value === undefined) {
    return null;
  }
  return (
    <details className="rounded-md border border-border">
      <summary className="cursor-pointer px-3 py-2 text-sm font-medium">{label}</summary>
      <pre dir="ltr" className="max-h-64 overflow-auto border-t border-border bg-surface-sunken p-3 text-xs">{JSON.stringify(value, null, 2)}</pre>
    </details>
  );
}

/** The field-level changes of one event: what each field was and what it became. */
export function AuditChangesTable({ diff }: { diff: unknown }) {
  const { t } = useTranslation();
  const changes = auditChanges(diff);
  if (changes.length === 0) {
    return null;
  }
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("audit.detail.field")}</TableHead>
          <TableHead>{t("audit.detail.before")}</TableHead>
          <TableHead>{t("audit.detail.after")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {changes.map((c) => (
          <TableRow key={c.field} data-testid="audit-change-row">
            <TableCell><code dir="ltr" className="text-xs">{c.field}</code></TableCell>
            <TableCell className="text-fg-muted"><span dir="auto">{auditValue(t, c.before)}</span></TableCell>
            <TableCell><span dir="auto">{auditValue(t, c.after)}</span></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/**
 * One audit event in full: who did what to which record, from where (request, address, client), the fields it
 * changed, the before and after snapshots, and its place in the tamper-evident chain.
 */
export function AuditEventDialog({ eventId, onClose, onShowRecord }: { eventId: string | null; onClose: () => void; onShowRecord?: (event: AuditEvent) => void }) {
  const { t } = useTranslation();
  const event = useQuery({
    queryKey: ["audit", "event", eventId],
    queryFn: async () => unwrap(await api.GET("/api/v1/audit/events/{id}", { params: { path: { id: eventId ?? "" } } })),
    enabled: eventId !== null,
  });
  const e = event.data;

  return (
    <Dialog open={eventId !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <div className="flex flex-col gap-4" data-testid="audit-event-detail">
          <DialogHeader>
            <DialogTitle className="flex flex-wrap items-center gap-2 text-lg font-semibold">
              {e ? (
                <>
                  <Badge tone={auditActionTone(e.action)}>{auditActionLabel(t, e.action)}</Badge>{" "}
                  <span>{auditEntityLabel(t, e.entityType)}</span>{" "}
                  <span dir="auto" className="text-fg-muted">{e.entityDisplay}</span>
                </>
              ) : t("audit.detail.title")}
            </DialogTitle>
          </DialogHeader>
          {event.isPending ? <p className="text-sm text-fg-muted">{t("common.loading")}</p> : null}
          {e ? (
            <>
              <dl className="text-sm">
                <Row label={t("audit.when")}>{formatDateTime(e.occurredAt)}</Row>
                <Row label={t("audit.actor")}>
                  <span dir="auto">{e.actor.display}</span> <span className="text-fg-muted">({t(`audit.actorTypes.${e.actor.type}`, { defaultValue: e.actor.type })})</span>
                </Row>
                <Row label={t("audit.entityType")}>
                  {auditEntityLabel(t, e.entityType)} <code dir="ltr" className="text-xs text-fg-muted">{e.entityType}</code>
                </Row>
                <Row label={t("audit.record")}><span dir="auto">{e.entityDisplay}</span></Row>
                {e.reason ? <Row label={t("common.reason")}><span dir="auto">{e.reason}</span></Row> : null}
                {e.requestId ? <Row label={t("audit.detail.request")}><code dir="ltr" className="text-xs">{e.requestId}</code></Row> : null}
                {e.correlationId && e.correlationId !== e.requestId ? <Row label={t("audit.detail.correlation")}><code dir="ltr" className="text-xs">{e.correlationId}</code></Row> : null}
                {e.actorIp ? <Row label={t("audit.detail.address")}><span dir="ltr">{e.actorIp.replace(/\/(32|128)$/, "")}</span></Row> : null}
                {e.userAgent ? <Row label={t("audit.detail.client")}><span dir="ltr" className="text-xs">{e.userAgent}</span></Row> : null}
              </dl>
              {auditChanges(e.diff).length > 0 ? (
                <section className="flex flex-col gap-2" aria-label={t("audit.detail.changes")}>
                  <h3 className="text-sm font-semibold">{t("audit.detail.changes")}</h3>
                  <AuditChangesTable diff={e.diff} />
                </section>
              ) : null}
              <div className="flex flex-col gap-2">
                <Snapshot label={t("audit.detail.beforeSnapshot")} value={e.before} />
                <Snapshot label={t("audit.detail.afterSnapshot")} value={e.after} />
                <Snapshot label={t("audit.detail.details")} value={e.details} />
              </div>
              <section className="flex flex-col gap-1 rounded-md border border-border p-3 text-xs" aria-label={t("audit.detail.chain")}>
                <h3 className="text-sm font-semibold">{t("audit.detail.chain")}</h3>
                <p className="text-fg-muted">{t("audit.detail.chainHint", { seq: String(e.seq) })}</p>
                <p><span className="text-fg-muted">{t("audit.detail.prevHash")}</span> <code dir="ltr" className="break-all">{e.prevHash}</code></p>
                <p><span className="text-fg-muted">{t("audit.detail.hash")}</span> <code dir="ltr" className="break-all" data-testid="audit-hash">{e.hash}</code></p>
              </section>
            </>
          ) : null}
          <DialogFooter>
            {e && onShowRecord ? (
              <Button variant="secondary" onClick={() => { onShowRecord(e); }} data-testid="audit-show-record">
                {t("audit.detail.showRecord")}
              </Button>
            ) : null}
            <Button onClick={onClose}>{t("common.close")}</Button>
          </DialogFooter>
        </div>
      </DialogContent>
    </Dialog>
  );
}
