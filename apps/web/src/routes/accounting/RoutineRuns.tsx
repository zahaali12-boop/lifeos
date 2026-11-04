import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatDateTime, formatNumber } from "../../lib/format";
import { SourceDocument } from "./shared";

type Item = components["schemas"]["RoutineRunItem"];

/** What an item produced opens on its screen: a generated recurring journal, or the posted entry of a reversal or deferral. */
function Produced({ item }: { item: Item }) {
  if (!item.producedId) {
    return null;
  }
  return <SourceDocument type={item.kind === "recurring" ? "manual_journal" : "journal_entry"} id={item.producedId} number={item.producedNumber} />;
}

/**
 * The company's routine runs, newest first: when, whether the schedule or a person ran them, for which date, and how
 * many items went out or waited; a run opens to show each item, what it produced and why it waited.
 */
export function RoutineRuns({ companyId }: { companyId: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<string | null>(null);
  const runs = useQuery({
    queryKey: ["routine-runs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/accounting/companies/{companyId}/routine-runs", { params: { path: { companyId }, query: { limit: 100 } } })),
  });
  const rows = runs.data ?? [];
  if (rows.length === 0) {
    return <p className="text-sm text-fg-muted" data-testid="routine-runs-empty">{runs.isPending && companyId ? t("common.loading") : t("routines.noRuns")}</p>;
  }

  const target = (item: Item): string => {
    if (item.kind === "deferral") {
      return item.targetRef ? t(`routines.kinds.${item.targetRef}`, { defaultValue: item.targetRef }) : "—";
    }
    return item.targetRef ?? "—";
  };

  return (
    <Table data-testid="routine-runs">
      <TableHeader>
        <TableRow>
          <TableHead />
          <TableHead>{t("routines.ranAt")}</TableHead>
          <TableHead>{t("routines.runFor")}</TableHead>
          <TableHead>{t("routines.startedBy")}</TableHead>
          <TableHead className="text-end">{t("routines.done")}</TableHead>
          <TableHead className="text-end">{t("routines.waiting")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((run) => {
          const expanded = open === run.id;
          return (
            <Fragment key={run.id}>
              <TableRow data-testid="routine-run">
                <TableCell>
                  <Button variant="ghost" size="icon" aria-expanded={expanded} aria-label={t(expanded ? "routines.hideItems" : "routines.showItems", { date: formatDateTime(run.ranAt) })} onClick={() => { setOpen(expanded ? null : run.id); }} disabled={run.items.length === 0} data-testid="routine-run-toggle">
                    {expanded ? <ChevronDown aria-hidden="true" /> : <ChevronRight aria-hidden="true" className="rtl:-scale-x-100" />}
                  </Button>
                </TableCell>
                <TableCell>{formatDateTime(run.ranAt)}</TableCell>
                <TableCell>{formatDate(run.asOf)}</TableCell>
                <TableCell>{run.trigger === "manual" ? t("routines.startedManually", { name: run.runByName ?? "—" }) : t("routines.startedBySchedule")}</TableCell>
                <TableCell className="tabular text-end">{formatNumber(Number(run.posted))}</TableCell>
                <TableCell className="tabular text-end">{Number(run.waiting) > 0 ? <Badge tone="warning">{formatNumber(Number(run.waiting))}</Badge> : formatNumber(0)}</TableCell>
              </TableRow>
              {expanded ? (
                <TableRow>
                  <TableCell colSpan={6}>
                    <ul className="flex flex-col gap-1 text-sm" data-testid="routine-run-items">
                      {run.items.map((item, index) => (
                        <li key={`${item.targetId}-${String(index)}`} className="flex flex-wrap items-center gap-2" data-testid="routine-run-item">
                          <Badge tone={item.outcome === "waiting" ? "warning" : "success"}>{t(`routines.outcomes.${item.outcome}`, { defaultValue: item.outcome })}</Badge>
                          <span>{t(`routines.itemKinds.${item.kind}`, { defaultValue: item.kind })}</span>
                          <span dir="auto" className="font-medium">{target(item)}</span>
                          {item.producedId ? <span className="text-fg-muted">→ <Produced item={item} /></span> : null}
                          {item.problem ? <span className="font-mono text-xs text-fg-muted" dir="ltr">{item.problem}</span> : null}
                        </li>
                      ))}
                    </ul>
                  </TableCell>
                </TableRow>
              ) : null}
            </Fragment>
          );
        })}
      </TableBody>
    </Table>
  );
}
