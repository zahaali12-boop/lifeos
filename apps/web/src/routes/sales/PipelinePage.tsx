import { Button } from "@quicker/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useState, type DragEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext } from "../inventory/shared";
import { NewOpportunityDialog, OpportunityDialog, useRefreshSales } from "./Opportunities";
import { Money, Totals, daysSince, useSalesReps, type Opportunity, type PipelineStage } from "./shared";

/**
 * The pipeline (roadmap 5.1): every active stage as a column with its deals and their weighted totals per currency;
 * a deal moves by dragging it or with its "Move to" menu; moving to the lost column asks why.
 */
export function PipelinePage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const can = useCan();
  const canManage = can("partners.customer.manage");
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const reps = useSalesReps();
  const [repId, setRepId] = useState("");
  const [mine, setMine] = useState(false);
  const [creating, setCreating] = useState(false);
  const [dragging, setDragging] = useState<string | null>(null);
  const [losing, setLosing] = useState<{ opportunity: Opportunity; stageId: string; reason: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const refreshSales = useRefreshSales();
  const openId = typeof search.open === "string" ? search.open : undefined;
  const open = (id: string | null): void => { void navigate({ to: "/sales/pipeline", search: id ? { open: id } : {} }); };

  const board = useQuery({
    queryKey: ["pipeline", companyId, repId, mine],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/pipeline", { params: { query: { companyId, ...(repId ? { salesRepId: repId } : {}), ...(mine ? { mine: true } : {}) } } })),
  });
  const move = useMutation({
    mutationFn: async (input: { opportunityId: string; stageId: string; lostReason?: string | undefined }) => unwrap(await api.POST("/api/v1/partners/opportunities/{opportunityId}/move", { params: { path: { opportunityId: input.opportunityId } }, body: { stageId: input.stageId, probabilityPct: null, lostReason: input.lostReason ?? null } })),
    onSuccess: async () => { setProblem(null); setLosing(null); await refreshSales(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const columns = board.data?.columns ?? [];
  const stages = columns.map((c) => c.stage);

  const moveTo = (opportunity: Opportunity, stage: PipelineStage): void => {
    if (stage.id === opportunity.stageId) {
      return;
    }
    if (stage.outcome === "lost") {
      setLosing({ opportunity, stageId: stage.id, reason: "" });
      return;
    }
    move.mutate({ opportunityId: opportunity.id, stageId: stage.id });
  };
  const drop = (stage: PipelineStage) => (event: DragEvent<HTMLElement>): void => {
    event.preventDefault();
    const id = event.dataTransfer.getData("text/plain");
    const opportunity = columns.flatMap((c) => c.opportunities).find((o) => o.id === id);
    setDragging(null);
    if (opportunity && canManage) {
      moveTo(opportunity, stage);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.pipeline")}
        description={t("sales.pipelineDescription")}
        actions={
          canManage ? (
            <Button onClick={() => { setCreating(true); }} disabled={!companyId} data-testid="new-opportunity">
              <Plus aria-hidden="true" />
              {t("sales.newOpportunity")}
            </Button>
          ) : null
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("sales.salesRep")}>
          <SelectField value={repId} onChange={(e) => { setRepId(e.target.value); }} data-testid="rep-filter">
            <option value="">{t("sales.allReps")}</option>
            {(reps.data ?? []).map((r) => (
              <option key={r.id} value={r.id}>
                {r.code} · {localized(r.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm">
          <input type="checkbox" checked={mine} onChange={(e) => { setMine(e.target.checked); }} data-testid="mine-filter" />
          {t("sales.myDeals")}
        </label>
        <div className="flex flex-col gap-1 text-sm" data-testid="open-totals">
          <span className="text-fg-muted">{t("sales.openPipelineWeighted")}</span>
          <Totals totals={board.data?.openTotals ?? []} />
        </div>
      </div>
      <FormError message={problem?.message ?? null} />
      {losing ? (
        <form
          className="mb-4 flex flex-wrap items-end gap-2 rounded-md border border-danger/40 bg-danger-soft p-3"
          onSubmit={(event) => { event.preventDefault(); move.mutate({ opportunityId: losing.opportunity.id, stageId: losing.stageId, lostReason: losing.reason }); }}
        >
          <Field label={t("sales.lostReasonFor", { title: losing.opportunity.title })} required className="min-w-72 flex-1">
            <TextField value={losing.reason} onChange={(e) => { setLosing({ ...losing, reason: e.target.value }); }} required lang={i18n.language} data-testid="lost-reason" />
          </Field>
          <Button type="submit" variant="danger" disabled={!losing.reason.trim()} loading={move.isPending} data-testid="confirm-lost">
            {t("sales.confirmLost")}
          </Button>
          <Button type="button" variant="ghost" onClick={() => { setLosing(null); }}>
            {t("sales.notYet")}
          </Button>
        </form>
      ) : null}
      {/* The board scrolls sideways; it takes focus so the keyboard can scroll it even when no deal is on screen. */}
      <div className="flex gap-3 overflow-x-auto pb-2" role="list" aria-label={t("nav.pipeline")} tabIndex={0} data-testid="pipeline-board">
        {columns.map((column) => (
          <section
            key={column.stage.id}
            role="listitem"
            aria-labelledby={`stage-${column.stage.id}`}
            className={`flex w-72 shrink-0 flex-col gap-2 rounded-lg border p-2 ${dragging ? "border-dashed border-accent" : "border-border"} bg-surface-sunken`}
            onDragOver={(event) => { if (canManage) { event.preventDefault(); } }}
            onDrop={drop(column.stage)}
            data-testid={`column-${column.stage.code}`}
          >
            <header className="flex flex-col gap-1 px-1">
              <h2 id={`stage-${column.stage.id}`} className="flex items-center justify-between gap-2 text-sm font-semibold">
                <span>{localized(column.stage.name)}</span>
                <span className="tabular text-fg-muted">{column.stage.outcome === "open" ? `${column.stage.defaultProbability}%` : ""}</span>
              </h2>
              <span className="text-xs text-fg-muted">
                <Totals totals={column.totals} weighted={column.stage.outcome === "open"} />
              </span>
            </header>
            <ul className="flex flex-col gap-2">
              {column.opportunities.map((o) => (
                <li
                  key={o.id}
                  draggable={canManage && o.status === "open"}
                  onDragStart={(event) => { event.dataTransfer.setData("text/plain", o.id); setDragging(o.id); }}
                  onDragEnd={() => { setDragging(null); }}
                  className="flex flex-col gap-1 rounded-md border border-border bg-surface p-2 text-sm shadow-sm"
                  data-testid="deal-card"
                >
                  <button type="button" className="min-h-6 text-start font-medium text-accent hover:underline" onClick={() => { open(o.id); }} dir="auto" data-testid="open-deal">
                    {o.title}
                  </button>
                  <Link to="/sales/customers/$partnerId" params={{ partnerId: o.partnerId }} className="flex min-h-6 items-center truncate text-xs text-fg-muted hover:underline" dir="auto">
                    {o.partnerCode} · {localized(o.partnerName)}
                  </Link>
                  <span className="flex flex-wrap items-center justify-between gap-1 text-xs">
                    <Money amount={o.expectedAmount} currency={o.currency} />
                    <span className="tabular text-fg-muted">{o.probabilityPct}%</span>
                  </span>
                  <span className="flex flex-wrap gap-x-2 text-xs text-fg-muted">
                    <span dir="ltr">{o.number}</span>
                    {o.salesRepCode ? <span>{o.salesRepCode}</span> : null}
                    {o.status === "open" ? <span>{t("sales.inStageFor", { count: daysSince(o.stageSince) })}</span> : null}
                    {o.expectedClose ? <span className={o.isOverdue ? "font-medium text-danger" : undefined}>{o.isOverdue ? t("sales.closeOverdue", { when: formatDate(o.expectedClose) }) : t("sales.closesOn", { when: formatDate(o.expectedClose) })}</span> : null}
                    {o.status !== "open" && o.closedOn ? <span>{t("sales.closedOn", { when: formatDate(o.closedOn) })}</span> : null}
                  </span>
                  {canManage ? (
                    <SelectField
                      aria-label={t("sales.moveDeal", { title: o.title })}
                      value=""
                      onChange={(e) => { const stage = stages.find((s) => s.id === e.target.value); if (stage) { moveTo(o, stage); } }}
                      className="h-8 text-xs"
                      data-testid="move-deal"
                    >
                      <option value="">{t("sales.moveTo")}</option>
                      {stages.filter((s) => s.id !== o.stageId).map((s) => (
                        <option key={s.id} value={s.id}>
                          {localized(s.name)}
                        </option>
                      ))}
                    </SelectField>
                  ) : null}
                </li>
              ))}
            </ul>
            {column.opportunities.length === 0 ? <p className="px-1 text-xs text-fg-muted">{t("sales.emptyStage")}</p> : null}
          </section>
        ))}
      </div>
      {openId ? <OpportunityDialog opportunityId={openId} onClose={() => { open(null); }} /> : null}
      {creating ? (
        <NewOpportunityDialog
          open
          companyId={companyId}
          onClose={() => { setCreating(false); }}
          onCreated={(o) => { setCreating(false); void refreshSales(); open(o.id); }}
        />
      ) : null}
    </>
  );
}
