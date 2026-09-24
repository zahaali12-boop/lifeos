import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Check, Trophy, X } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { useCompanies } from "../accounting/shared";
import { Field, FormError, SelectField, TextareaField, TextField } from "../common";
import { CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { Tabs } from "../inventory/shared";
import { RecordDiscussion } from "../RecordDiscussion";
import { ActivityForm, ActivityList } from "./Activities";
import { Money, OutcomeBadge, daysSince, money, usePipelineStages, useSalesReps, type Opportunity } from "./shared";

/** Invalidates what shows opportunities: the board, lists, the 360 views and the open deal itself. */
export function useRefreshSales() {
  const queryClient = useQueryClient();
  return async (): Promise<void> => {
    await Promise.all(["pipeline", "opportunities", "opportunity", "customer-360", "crm-activities"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
  };
}

/** A new deal with a customer or a prospect (a new one can be created here), in the company's number series. */
export function NewOpportunityDialog({ open, onClose, companyId: initialCompany, partner, onCreated }: { open: boolean; onClose: () => void; companyId: string; partner?: { id: string; code: string; name: Record<string, string> } | undefined; onCreated: (opportunity: Opportunity) => void }) {
  const { t, i18n } = useTranslation();
  const companies = useCompanies();
  const stages = usePipelineStages();
  const reps = useSalesReps();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [search, setSearch] = useState("");
  const [form, setForm] = useState({ companyId: initialCompany, partnerId: partner?.id ?? "", newProspect: false, prospectCode: "", prospectName: "", prospectNameAr: "", title: "", amount: "", currency: "", stageId: "", expectedClose: "", source: "", salesRepId: "", notes: "", customFields: {} });
  const companyId = form.companyId || initialCompany;
  const company = companies.data?.find((c) => c.id === companyId);
  const found = useQuery({
    queryKey: ["partners", "pick", search],
    enabled: open && !partner && search.trim().length >= 2,
    queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { q: search.trim(), isActive: true, limit: 20 } } })),
  });
  const openStages = (stages.data ?? []).filter((s) => s.isActive && s.outcome === "open");
  const create = useMutation({
    mutationFn: async () => {
      let partnerId = form.partnerId;
      if (!partner && form.newProspect) {
        const created = unwrap(await api.POST("/api/v1/partners", { body: { code: form.prospectCode, legalName: { en: form.prospectName, ar: form.prospectNameAr || form.prospectName }, tradeName: null, kind: "organization", isSupplier: false, isCustomer: false, isEmployee: false, defaultLanguage: "en", isActive: true, email: null, phone: null, website: null, notes: null, customFields: {} } }));
        partnerId = created.id;
      }
      return unwrap(await api.POST("/api/v1/partners/opportunities", { body: {
        companyId, partnerId, title: form.title, expectedAmount: form.amount.trim() === "" ? 0 : form.amount.trim(), currency: form.currency || null,
        stageId: form.stageId || null, probabilityPct: null, contactId: null, salesRepId: form.salesRepId || null,
        expectedClose: form.expectedClose || null, source: form.source || null, notes: form.notes || null, customFields: form.customFields,
      } }));
    },
    onSuccess: (opportunity) => { setProblem(null); onCreated(opportunity); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); create.mutate(); };
  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("sales.newOpportunity")}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("accounting.company")} required>
              <SelectField value={companyId} onChange={(e) => { setForm({ ...form, companyId: e.target.value }); }} data-testid="opportunity-company">
                {(companies.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.code} · {localized(c.legalName)}
                  </option>
                ))}
              </SelectField>
            </Field>
            {partner ? (
              <Field label={t("sales.customerOrProspect")}>
                <TextField value={`${partner.code} · ${localized(partner.name)}`} readOnly dir="auto" />
              </Field>
            ) : (
              <div className="flex flex-col gap-2">
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={form.newProspect} onChange={(e) => { setForm({ ...form, newProspect: e.target.checked }); }} data-testid="new-prospect" />
                  {t("sales.newProspect")}
                </label>
                {!form.newProspect ? (
                  <>
                    <Field label={t("sales.findPartner")}>
                      <TextField value={search} onChange={(e) => { setSearch(e.target.value); }} placeholder={t("partners.search")} data-testid="partner-search" />
                    </Field>
                    <Field label={t("sales.customerOrProspect")} required>
                      <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required data-testid="opportunity-partner">
                        <option value="">{search.trim().length >= 2 ? t("sales.choosePartner") : t("sales.typeToFind")}</option>
                        {(found.data?.items ?? []).map((p) => (
                          <option key={p.id} value={p.id}>
                            {p.code} · {localized(p.legalName)}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                  </>
                ) : null}
              </div>
            )}
            {!partner && form.newProspect ? (
              <>
                <Field label={t("partners.code")} required>
                  <TextField value={form.prospectCode} onChange={(e) => { setForm({ ...form, prospectCode: e.target.value }); }} dir="ltr" required data-testid="prospect-code" />
                </Field>
                <Field label={t("partners.legalName")} required>
                  <TextField value={form.prospectName} onChange={(e) => { setForm({ ...form, prospectName: e.target.value }); }} required data-testid="prospect-name" />
                </Field>
                <Field label={t("partners.legalNameAr")}>
                  <TextField value={form.prospectNameAr} onChange={(e) => { setForm({ ...form, prospectNameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
              </>
            ) : null}
            <Field label={t("sales.title")} required className="sm:col-span-2">
              <TextField value={form.title} onChange={(e) => { setForm({ ...form, title: e.target.value }); }} required maxLength={200} lang={i18n.language} data-testid="opportunity-title" />
            </Field>
            <Field label={t("sales.expectedAmount")}>
              <TextField inputMode="decimal" value={form.amount} onChange={(e) => { setForm({ ...form, amount: e.target.value }); }} dir="ltr" data-testid="opportunity-amount" />
            </Field>
            <Field label={t("partners.currency")} description={t("sales.currencyDefault", { currency: company?.functionalCurrency ?? "" })}>
              <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} data-testid="opportunity-currency" />
            </Field>
            <Field label={t("sales.stage")}>
              <SelectField value={form.stageId} onChange={(e) => { setForm({ ...form, stageId: e.target.value }); }} data-testid="opportunity-stage">
                <option value="">{t("sales.firstStage")}</option>
                {openStages.map((s) => (
                  <option key={s.id} value={s.id}>
                    {localized(s.name)} · {s.defaultProbability}%
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("sales.expectedClose")}>
              <TextField type="date" value={form.expectedClose} onChange={(e) => { setForm({ ...form, expectedClose: e.target.value }); }} dir="ltr" data-testid="opportunity-close" />
            </Field>
            <Field label={t("sales.salesRep")} description={t("sales.repDefault")}>
              <SelectField value={form.salesRepId} onChange={(e) => { setForm({ ...form, salesRepId: e.target.value }); }}>
                <option value="">—</option>
                {(reps.data ?? []).filter((r) => r.isActive && (!r.companyId || r.companyId === companyId)).map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.code} · {localized(r.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("sales.source")}>
              <TextField value={form.source} onChange={(e) => { setForm({ ...form, source: e.target.value }); }} lang={i18n.language} />
            </Field>
          </div>
          <CustomFieldsFieldset entityType="opportunity" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={create.isPending} data-testid="save-opportunity">
              {t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** The stages as steps: where the deal is, a click to move it, won and lost at the end (lost asks why). */
function StageStepper({ opportunity, onMove, busy, canManage }: { opportunity: Opportunity; onMove: (stageId: string, lostReason?: string) => void; busy: boolean; canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const stages = usePipelineStages();
  const [losing, setLosing] = useState<{ stageId: string; reason: string } | null>(null);
  const active = (stages.data ?? []).filter((s) => s.isActive);
  const open = active.filter((s) => s.outcome === "open");
  const won = active.find((s) => s.outcome === "won");
  const lost = active.find((s) => s.outcome === "lost");
  return (
    <div className="flex flex-col gap-2">
      <ol className="flex flex-wrap items-center gap-1" aria-label={t("sales.stages")}>
        {open.map((s) => {
          const current = s.id === opportunity.stageId;
          return (
            <li key={s.id}>
              <button
                type="button"
                disabled={!canManage || busy || current}
                aria-current={current ? "step" : undefined}
                onClick={() => { onMove(s.id); }}
                className={current ? "rounded-md bg-accent px-3 py-1 text-sm font-medium text-accent-fg" : "rounded-md border border-border px-3 py-1 text-sm text-fg-muted hover:bg-surface-sunken disabled:cursor-default"}
                data-testid={`move-${s.code}`}
              >
                {localized(s.name)}
              </button>
            </li>
          );
        })}
        {won ? (
          <li>
            <Button size="sm" variant={opportunity.status === "won" ? "primary" : "secondary"} disabled={!canManage || busy || opportunity.status === "won"} onClick={() => { onMove(won.id); }} data-testid="mark-won">
              <Trophy aria-hidden="true" />
              {localized(won.name)}
            </Button>
          </li>
        ) : null}
        {lost ? (
          <li>
            <Button size="sm" variant={opportunity.status === "lost" ? "danger" : "secondary"} disabled={!canManage || busy || opportunity.status === "lost"} onClick={() => { setLosing({ stageId: lost.id, reason: "" }); }} data-testid="mark-lost">
              <X aria-hidden="true" />
              {localized(lost.name)}
            </Button>
          </li>
        ) : null}
      </ol>
      {losing ? (
        <form className="flex flex-wrap items-end gap-2" onSubmit={(event) => { event.preventDefault(); onMove(losing.stageId, losing.reason); setLosing(null); }}>
          <Field label={t("sales.lostReason")} required className="min-w-64 flex-1">
            <TextField value={losing.reason} onChange={(e) => { setLosing({ ...losing, reason: e.target.value }); }} required lang={i18n.language} data-testid="lost-reason" />
          </Field>
          <Button type="submit" size="sm" variant="danger" disabled={!losing.reason.trim()} data-testid="confirm-lost">
            <Check aria-hidden="true" />
            {t("sales.confirmLost")}
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={() => { setLosing(null); }}>
            {t("sales.notYet")}
          </Button>
        </form>
      ) : null}
    </div>
  );
}

interface DealForm {
  title: string;
  amount: string;
  currency: string;
  probability: string;
  expectedClose: string;
  contactId: string;
  salesRepId: string;
  source: string;
  notes: string;
  customFields: CustomFieldValues;
}

function dealForm(o: Opportunity): DealForm {
  return { title: o.title, amount: String(o.expectedAmount), currency: o.currency, probability: String(o.probabilityPct), expectedClose: o.expectedClose ?? "", contactId: o.contactId ?? "", salesRepId: o.salesRepId ?? "", source: o.source ?? "", notes: o.notes ?? "", customFields: (o.customFields ?? {}) as CustomFieldValues };
}

/** One deal: where it stands, its details, what was done about it, how it moved, and its discussion. */
export function OpportunityDialog({ opportunityId, onClose }: { opportunityId: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const can = useCan();
  const canManage = can("partners.customer.manage");
  const refreshSales = useRefreshSales();
  const reps = useSalesReps();
  const [tab, setTab] = useState("details");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [edit, setEdit] = useState<DealForm | null>(null);
  const detail = useQuery({ queryKey: ["opportunity", opportunityId], queryFn: async () => unwrap(await api.GET("/api/v1/partners/opportunities/{opportunityId}", { params: { path: { opportunityId } } })) });
  const o = detail.data?.opportunity;
  const partner = useQuery({ queryKey: ["partner", o?.partnerId], enabled: Boolean(o), queryFn: async () => unwrap(await api.GET("/api/v1/partners/{partnerId}", { params: { path: { partnerId: o?.partnerId ?? "" } } })) });
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const move = useMutation({
    mutationFn: async (input: { stageId: string; lostReason?: string | undefined }) => unwrap(await api.POST("/api/v1/partners/opportunities/{opportunityId}/move", { params: { path: { opportunityId } }, body: { stageId: input.stageId, probabilityPct: null, lostReason: input.lostReason ?? null } })),
    onSuccess: async () => { setProblem(null); setEdit(null); await refreshSales(); },
    onError: fail,
  });
  const save = useMutation({
    mutationFn: async (f: DealForm) => unwrap(await api.PUT("/api/v1/partners/opportunities/{opportunityId}", { params: { path: { opportunityId } }, body: {
      title: f.title, expectedAmount: f.amount.trim() === "" ? 0 : f.amount.trim(), currency: f.currency, probabilityPct: Number(f.probability),
      contactId: f.contactId || null, salesRepId: f.salesRepId || null, expectedClose: f.expectedClose || null, source: f.source || null, notes: f.notes || null, customFields: f.customFields,
    } })),
    onSuccess: async () => { setProblem(null); setEdit(null); await refreshSales(); },
    onError: fail,
  });
  const form = edit ?? (o ? dealForm(o) : null);
  const editable = canManage && o?.status === "open";
  const patch = (change: Partial<DealForm>): void => { if (form) { setEdit({ ...form, ...change }); } };
  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold" dir="auto">
            {o ? (
              <>
                <span dir="ltr">{o.number}</span> · {o.title}
              </>
            ) : t("common.loading")}
          </DialogTitle>
        </DialogHeader>
        {o && form ? (
          <div className="flex flex-col gap-4" data-testid="opportunity-detail">
            <div className="flex flex-wrap items-center gap-3 text-sm">
              <OutcomeBadge status={o.status} />
              {o.status === "open" ? <Badge tone="accent" data-testid="opportunity-stage-badge">{localized(o.stageName)}</Badge> : null}
              <Link to="/sales/customers/$partnerId" params={{ partnerId: o.partnerId }} className="text-accent hover:underline" dir="auto">
                {o.partnerCode} · {localized(o.partnerName)}
              </Link>
              <span>
                <Money amount={o.expectedAmount} currency={o.currency} /> · {o.probabilityPct}% · {t("sales.weighted")} <Money amount={o.weightedAmount} currency={o.currency} testId="opportunity-weighted" />
              </span>
              {o.status === "open" ? <span className="text-fg-muted">{t("sales.inStageFor", { count: daysSince(o.stageSince) })}</span> : null}
              {o.lostReason ? <span className="text-danger" dir="auto">{t("sales.lostBecause", { reason: o.lostReason })}</span> : null}
            </div>
            <StageStepper opportunity={o} onMove={(stageId, lostReason) => { move.mutate({ stageId, lostReason }); }} busy={move.isPending} canManage={canManage} />
            <FormError message={problem?.message ?? null} />
            <Tabs
              tabs={[
                { id: "details", label: t("sales.details"), testId: "tab-details" },
                { id: "activities", label: t("sales.activities"), testId: "tab-activities" },
                { id: "history", label: t("sales.stageHistory"), testId: "tab-stage-history" },
                { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" },
              ]}
              value={tab}
              onChange={setTab}
            />
            {tab === "details" ? (
              <form onSubmit={(event) => { event.preventDefault(); save.mutate(form); }} className="flex flex-col gap-4">
                <fieldset className="grid gap-4 sm:grid-cols-3" disabled={!editable}>
                  <legend className="sr-only">{t("sales.details")}</legend>
                  <Field label={t("sales.title")} required className="sm:col-span-3">
                    <TextField value={form.title} onChange={(e) => { patch({ title: e.target.value }); }} required maxLength={200} lang={i18n.language} data-testid="edit-title" />
                  </Field>
                  <Field label={t("sales.expectedAmount")}>
                    <TextField inputMode="decimal" value={form.amount} onChange={(e) => { patch({ amount: e.target.value }); }} dir="ltr" data-testid="edit-amount" />
                  </Field>
                  <Field label={t("partners.currency")}>
                    <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                  </Field>
                  <Field label={t("sales.probability")}>
                    <TextField type="number" min={0} max={100} value={form.probability} onChange={(e) => { patch({ probability: e.target.value }); }} dir="ltr" data-testid="edit-probability" />
                  </Field>
                  <Field label={t("sales.expectedClose")}>
                    <TextField type="date" value={form.expectedClose} onChange={(e) => { patch({ expectedClose: e.target.value }); }} dir="ltr" />
                  </Field>
                  <Field label={t("sales.contact")}>
                    <SelectField value={form.contactId} onChange={(e) => { patch({ contactId: e.target.value }); }}>
                      <option value="">—</option>
                      {(partner.data?.contacts ?? []).map((c) => (
                        <option key={c.id} value={c.id}>
                          {localized(c.name)}{c.role ? ` · ${c.role}` : ""}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("sales.salesRep")}>
                    <SelectField value={form.salesRepId} onChange={(e) => { patch({ salesRepId: e.target.value }); }}>
                      <option value="">—</option>
                      {(reps.data ?? []).filter((r) => (r.isActive && (!r.companyId || r.companyId === o.companyId)) || r.id === form.salesRepId).map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.code} · {localized(r.name)}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("sales.source")}>
                    <TextField value={form.source} onChange={(e) => { patch({ source: e.target.value }); }} lang={i18n.language} />
                  </Field>
                  <Field label={t("partners.notes")} className="sm:col-span-2">
                    <TextareaField value={form.notes} onChange={(e) => { patch({ notes: e.target.value }); }} rows={2} lang={i18n.language} />
                  </Field>
                </fieldset>
                {editable ? (
                  <CustomFieldsFieldset entityType="opportunity" values={form.customFields} onChange={(customFields) => { patch({ customFields }); }} errors={problem?.fields} />
                ) : (
                  <CustomFieldValuesList entityType="opportunity" values={o.customFields} />
                )}
                <p className="text-xs text-fg-muted">
                  {t("sales.numberInCompany", { company: o.companyCode })} · {o.closedOn ? t("sales.closedOn", { when: formatDate(o.closedOn) }) : o.expectedClose ? t("sales.closesOn", { when: formatDate(o.expectedClose) }) : t("sales.noCloseDate")}
                </p>
                {editable ? (
                  <DialogFooter>
                    <Button type="submit" loading={save.isPending} disabled={!edit} data-testid="save-deal">
                      {t("common.save")}
                    </Button>
                  </DialogFooter>
                ) : null}
              </form>
            ) : null}
            {tab === "activities" ? (
              <div className="flex flex-col gap-4">
                <ActivityList activities={detail.data?.activities ?? []} onChanged={refreshSales} />
                {canManage ? <ActivityForm partnerId={o.partnerId} opportunityId={o.id} onSaved={refreshSales} /> : null}
              </div>
            ) : null}
            {tab === "history" ? (
              <ol className="flex flex-col gap-2" data-testid="stage-history">
                {(detail.data?.stageHistory ?? []).map((h) => (
                  <li key={h.id} className="flex flex-wrap items-center gap-2 text-sm" data-testid="stage-change">
                    <span className="tabular text-fg-muted">{formatDateTime(h.changedAt)}</span>
                    <span>{h.fromStageCode ? t("sales.movedFromTo", { from: h.fromStageCode, to: h.toStageCode }) : t("sales.startedIn", { stage: h.toStageCode })}</span>
                    <span className="text-fg-muted">
                      {h.probabilityPct}% · {money(h.expectedAmount, o.currency)}
                    </span>
                  </li>
                ))}
              </ol>
            ) : null}
            {tab === "discussion" ? <RecordDiscussion entityType="opportunity" entityId={o.id} /> : null}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
