import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Field, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import type { components } from "../api/schema";
import { formatDateTime, formatNumber } from "../lib/format";
import { toFormProblem } from "../lib/problem";
import { FormError, SelectField, TextField } from "./common";

type Message = components["schemas"]["OutboxMessage"];

const states = ["dead", "discarded", "pending"] as const;

/**
 * Operators only: integration events the worker could not deliver. A dead letter holds back its aggregate's later
 * events; the operator retries it (the fault is fixed) or discards it with a reason (its effect is not wanted, or was
 * applied by hand), after which the later events flow.
 */
export function OutboxPanel() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [state, setState] = useState<(typeof states)[number]>("dead");
  const [discarding, setDiscarding] = useState<{ id: string; eventType: string; reason: string } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const messages = useQuery({
    queryKey: ["outbox", state],
    queryFn: async () => unwrap(await api.GET("/api/v1/platform/ops/outbox", { params: { query: { state, limit: 200 } } })),
    refetchInterval: 30_000,
  });
  const refresh = async (): Promise<void> => { await queryClient.invalidateQueries({ queryKey: ["outbox"] }); };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed")).message); };
  const retry = useMutation({
    mutationFn: async (messageId: string) => { await api.POST("/api/v1/platform/ops/outbox/{messageId}/retry", { params: { path: { messageId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const discard = useMutation({
    mutationFn: async () => {
      if (!discarding) {
        return;
      }
      await api.POST("/api/v1/platform/ops/outbox/{messageId}/discard", { params: { path: { messageId: discarding.id } }, body: { reason: discarding.reason } }).then(unwrap);
    },
    onSuccess: async () => { setDiscarding(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); discard.mutate(); };
  const rows = messages.data ?? [];

  return (
    <section className="mt-8 flex flex-col gap-2" data-testid="outbox">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-wide text-fg-muted">{t("outbox.title")}</h2>
          <p className="text-sm text-fg-muted">{t("outbox.hint")}</p>
        </div>
        <Field label={t("common.status")} className="w-44">
          <SelectField value={state} onChange={(e) => { setState(e.target.value as (typeof states)[number]); }} data-testid="outbox-state">
            {states.map((s) => (
              <option key={s} value={s}>{t(`outbox.states.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <FormError message={discarding ? null : problem} />
      {rows.length === 0 ? <p className="text-sm text-fg-muted" data-testid="outbox-empty">{t(`outbox.empty.${state}`)}</p> : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("outbox.event")}</TableHead>
              <TableHead>{t("outbox.aggregate")}</TableHead>
              <TableHead>{t("outbox.workspace")}</TableHead>
              <TableHead>{t("jobs.attempts")}</TableHead>
              <TableHead>{t("jobs.error")}</TableHead>
              <TableHead>{state === "discarded" ? t("outbox.discarded") : t("outbox.occurred")}</TableHead>
              {state === "dead" ? <TableHead /> : null}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((m: Message) => (
              <TableRow key={m.id} data-testid="outbox-row">
                <TableCell>
                  <span dir="ltr" className="font-mono text-xs">{m.eventType}</span>
                  <span className="block text-xs text-fg-muted" dir="ltr">{m.id}</span>
                </TableCell>
                <TableCell>
                  <span dir="ltr" className="text-xs">{m.aggregateType}</span>
                  <span className="block font-mono text-xs text-fg-muted" dir="ltr">{m.aggregateId}</span>
                </TableCell>
                <TableCell><span dir="ltr" className="font-mono text-xs">{m.tenantId ?? "—"}</span></TableCell>
                <TableCell className="tabular">{formatNumber(m.attempts ?? 0)}</TableCell>
                <TableCell><span className="line-clamp-3 text-xs text-danger" dir="auto">{m.lastError}</span></TableCell>
                <TableCell>
                  {state === "discarded" ? (
                    <>
                      {formatDateTime(m.discardedAt)}
                      <span className="block text-xs text-fg-muted" dir="auto" data-testid="outbox-discard-reason">{m.discardReason}</span>
                      <span className="block text-xs text-fg-muted" dir="ltr">{m.discardedBy}</span>
                    </>
                  ) : formatDateTime(m.deadAt ?? m.occurredAt)}
                </TableCell>
                {state === "dead" ? (
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      <Button size="sm" variant="secondary" onClick={() => { if (m.id) { retry.mutate(m.id); } }} loading={retry.isPending && retry.variables === m.id} data-testid="outbox-retry">
                        {t("jobs.retry")}
                      </Button>
                      <Button size="sm" variant="ghost" className="text-danger" onClick={() => { setProblem(null); setDiscarding({ id: m.id ?? "", eventType: m.eventType ?? "", reason: "" }); }} data-testid="outbox-discard">
                        {t("outbox.discard")}
                      </Button>
                    </div>
                  </TableCell>
                ) : null}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Dialog open={Boolean(discarding)} onOpenChange={(isOpen) => { if (!isOpen) { setDiscarding(null); } }}>
        <DialogContent closeLabel={t("common.close")}>
          {discarding ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("outbox.discardTitle")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm"><span dir="ltr" className="font-mono text-xs">{discarding.eventType}</span></p>
              <p className="text-sm text-fg-muted">{t("outbox.discardHint")}</p>
              <FormError message={problem} />
              <Field label={t("common.reason")} required>
                <TextField value={discarding.reason} onChange={(e) => { setDiscarding({ ...discarding, reason: e.target.value }); }} required maxLength={500} data-testid="outbox-discard-reason-input" />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setDiscarding(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" variant="danger" loading={discard.isPending} data-testid="outbox-discard-confirm">
                  {t("outbox.discard")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </section>
  );
}

