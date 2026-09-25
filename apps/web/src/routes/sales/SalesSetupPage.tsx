import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { useCompanies } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Tabs } from "../inventory/shared";
import { useDeliveryTerms, usePaymentTerms } from "../purchasing/shared";
import { Money, useCommissionPlans, useCustomerGroups, useCustomerPostingGroups, usePipelineStages, useSalesReps, type CommissionPlan, type CustomerGroup, type PipelineStage, type SalesRep } from "./shared";

/** Sales set-up (roadmap 5.1): customer groups, the sales team and how it is paid, and the pipeline's stages. */
export function SalesSetupPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState("groups");
  return (
    <>
      <PageHeader title={t("nav.salesSetup")} description={t("sales.setupDescription")} />
      <Tabs
        tabs={[
          { id: "groups", label: t("sales.customerGroups"), testId: "tab-groups" },
          { id: "reps", label: t("sales.salesReps"), testId: "tab-reps" },
          { id: "plans", label: t("sales.commissionPlans"), testId: "tab-plans" },
          { id: "stages", label: t("sales.pipelineStages"), testId: "tab-stages" },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === "groups" ? <GroupsTab /> : null}
      {tab === "reps" ? <RepsTab /> : null}
      {tab === "plans" ? <PlansTab /> : null}
      {tab === "stages" ? <StagesTab /> : null}
    </>
  );
}

function ActiveBadge({ active }: { active: boolean }) {
  const { t } = useTranslation();
  return <Badge tone={active ? "success" : "neutral"}>{active ? t("common.active") : t("common.inactive")}</Badge>;
}

function EditDialog({ title, open, onClose, onSubmit, busy, problem, children, testId }: { title: string; open: boolean; onClose: () => void; onSubmit: () => void; busy: boolean; problem: FormProblem | null; children: React.ReactNode; testId: string }) {
  const { t } = useTranslation();
  const submit = (event: FormEvent): void => { event.preventDefault(); onSubmit(); };
  return (
    <Dialog open={open} onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{title}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          {children}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={busy} data-testid={testId}>
              {t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ customer groups

function GroupsTab() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const groups = useCustomerGroups();
  const paymentTerms = usePaymentTerms();
  const deliveryTerms = useDeliveryTerms();
  const postingGroups = useCustomerPostingGroups();
  const [editing, setEditing] = useState<{ id: string | null; code: string; name: string; nameAr: string; postingGroupId: string; paymentTermsId: string; deliveryTermsId: string; isActive: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const save = useMutation({
    mutationFn: async () => {
      if (!editing) {
        return;
      }
      const body = { code: editing.code, name: { en: editing.name, ar: editing.nameAr || editing.name }, postingGroupId: editing.postingGroupId || null, paymentTermsId: editing.paymentTermsId || null, deliveryTermsId: editing.deliveryTermsId || null, isActive: editing.isActive };
      if (editing.id) {
        unwrap(await api.PUT("/api/v1/partners/customer-groups/{groupId}", { params: { path: { groupId: editing.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/partners/customer-groups", { body }));
      }
    },
    onSuccess: async () => { setEditing(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["customer-groups"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const edit = (g?: CustomerGroup): void => {
    setProblem(null);
    setEditing(g ? { id: g.id, code: g.code, name: g.name.en ?? "", nameAr: g.name.ar ?? "", postingGroupId: g.postingGroupId ?? "", paymentTermsId: g.paymentTermsId ?? "", deliveryTermsId: g.deliveryTermsId ?? "", isActive: g.isActive } : { id: null, code: "", name: "", nameAr: "", postingGroupId: "", paymentTermsId: "", deliveryTermsId: "", isActive: true });
  };
  const code = (rows: { id: string; code: string }[] | undefined, id: string | null): string => rows?.find((r) => r.id === id)?.code ?? "—";
  const canEdit = can("partners.terms.manage");
  return (
    <div className="flex flex-col gap-3">
      {canEdit ? (
        <div>
          <Button onClick={() => { edit(); }} data-testid="new-customer-group">
            <Plus aria-hidden="true" />
            {t("sales.newCustomerGroup")}
          </Button>
        </div>
      ) : null}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("partners.code")}</TableHead>
            <TableHead>{t("partners.name")}</TableHead>
            <TableHead>{t("partners.postingGroup")}</TableHead>
            <TableHead>{t("partners.paymentTerms")}</TableHead>
            <TableHead>{t("partners.deliveryTerms")}</TableHead>
            <TableHead>{t("sales.customers")}</TableHead>
            <TableHead>{t("common.status")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(groups.data ?? []).map((g) => (
            <TableRow key={g.id} data-testid="group-row">
              <TableCell dir="ltr">{canEdit ? <button type="button" className="text-accent hover:underline" onClick={() => { edit(g); }}>{g.code}</button> : g.code}</TableCell>
              <TableCell dir="auto">{localized(g.name)}</TableCell>
              <TableCell>{code(postingGroups.data, g.postingGroupId)}</TableCell>
              <TableCell>{code(paymentTerms.data, g.paymentTermsId)}</TableCell>
              <TableCell>{code(deliveryTerms.data, g.deliveryTermsId)}</TableCell>
              <TableCell className="tabular">{g.customers}</TableCell>
              <TableCell><ActiveBadge active={g.isActive} /></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <EditDialog title={editing?.id ? t("sales.editCustomerGroup") : t("sales.newCustomerGroup")} open={Boolean(editing)} onClose={() => { setEditing(null); }} onSubmit={() => { save.mutate(); }} busy={save.isPending} problem={problem} testId="save-customer-group">
        {editing ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("partners.code")} required>
              <TextField value={editing.code} onChange={(e) => { setEditing({ ...editing, code: e.target.value }); }} dir="ltr" required data-testid="group-code" />
            </Field>
            <label className="flex items-center gap-2 self-end text-sm">
              <input type="checkbox" checked={editing.isActive} onChange={(e) => { setEditing({ ...editing, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
            <Field label={t("partners.name")} required>
              <TextField value={editing.name} onChange={(e) => { setEditing({ ...editing, name: e.target.value }); }} required data-testid="group-name" />
            </Field>
            <Field label={t("partners.nameAr")}>
              <TextField value={editing.nameAr} onChange={(e) => { setEditing({ ...editing, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("partners.postingGroup")}>
              <SelectField value={editing.postingGroupId} onChange={(e) => { setEditing({ ...editing, postingGroupId: e.target.value }); }}>
                <option value="">—</option>
                {(postingGroups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.code} · {localized(g.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("partners.paymentTerms")}>
              <SelectField value={editing.paymentTermsId} onChange={(e) => { setEditing({ ...editing, paymentTermsId: e.target.value }); }} data-testid="group-payment-terms">
                <option value="">—</option>
                {(paymentTerms.data ?? []).filter((p) => p.isActive).map((p) => <option key={p.id} value={p.id}>{p.code} · {localized(p.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("partners.deliveryTerms")}>
              <SelectField value={editing.deliveryTermsId} onChange={(e) => { setEditing({ ...editing, deliveryTermsId: e.target.value }); }}>
                <option value="">—</option>
                {(deliveryTerms.data ?? []).filter((d) => d.isActive).map((d) => <option key={d.id} value={d.id}>{d.code} · {localized(d.name)}</option>)}
              </SelectField>
            </Field>
          </div>
        ) : null}
      </EditDialog>
    </div>
  );
}

// ------------------------------------------------------------------ sales reps

function RepsTab() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const reps = useSalesReps();
  const plans = useCommissionPlans();
  const companies = useCompanies();
  const canEdit = can("partners.sales_setup.manage");
  const members = useQuery({ queryKey: ["members"], enabled: canEdit && can("identity.user.read"), queryFn: async () => unwrap(await api.GET("/api/v1/users")) });
  const [editing, setEditing] = useState<{ id: string | null; code: string; name: string; nameAr: string; membershipId: string; companyId: string; commissionPlanId: string; email: string; phone: string; isActive: boolean; memberName: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const save = useMutation({
    mutationFn: async () => {
      if (!editing) {
        return;
      }
      const body = { code: editing.code, name: { en: editing.name, ar: editing.nameAr || editing.name }, membershipId: editing.membershipId || null, partnerId: null, companyId: editing.companyId || null, commissionPlanId: editing.commissionPlanId || null, email: editing.email || null, phone: editing.phone || null, isActive: editing.isActive };
      if (editing.id) {
        unwrap(await api.PUT("/api/v1/partners/sales-reps/{repId}", { params: { path: { repId: editing.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/partners/sales-reps", { body }));
      }
    },
    onSuccess: async () => { setEditing(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["sales-reps"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const edit = (r?: SalesRep): void => {
    setProblem(null);
    setEditing(r
      ? { id: r.id, code: r.code, name: r.name.en ?? "", nameAr: r.name.ar ?? "", membershipId: r.membershipId ?? "", companyId: r.companyId ?? "", commissionPlanId: r.commissionPlanId ?? "", email: r.email ?? "", phone: r.phone ?? "", isActive: r.isActive, memberName: r.memberName ?? "" }
      : { id: null, code: "", name: "", nameAr: "", membershipId: "", companyId: "", commissionPlanId: "", email: "", phone: "", isActive: true, memberName: "" });
  };
  return (
    <div className="flex flex-col gap-3">
      {canEdit ? (
        <div>
          <Button onClick={() => { edit(); }} data-testid="new-sales-rep">
            <Plus aria-hidden="true" />
            {t("sales.newSalesRep")}
          </Button>
        </div>
      ) : null}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("partners.code")}</TableHead>
            <TableHead>{t("partners.name")}</TableHead>
            <TableHead>{t("sales.member")}</TableHead>
            <TableHead>{t("accounting.company")}</TableHead>
            <TableHead>{t("sales.commissionPlan")}</TableHead>
            <TableHead>{t("sales.customers")}</TableHead>
            <TableHead>{t("sales.openDeals")}</TableHead>
            <TableHead>{t("common.status")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(reps.data ?? []).map((r) => (
            <TableRow key={r.id} data-testid="rep-row">
              <TableCell dir="ltr">{canEdit ? <button type="button" className="text-accent hover:underline" onClick={() => { edit(r); }}>{r.code}</button> : r.code}</TableCell>
              <TableCell dir="auto">{localized(r.name)}</TableCell>
              <TableCell dir="auto">{r.memberName ?? "—"}</TableCell>
              <TableCell>{r.companyCode ?? t("sales.allCompanies")}</TableCell>
              <TableCell>{r.commissionPlanCode ?? "—"}</TableCell>
              <TableCell className="tabular">{r.customers}</TableCell>
              <TableCell className="tabular">{r.openOpportunities}</TableCell>
              <TableCell><ActiveBadge active={r.isActive} /></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <EditDialog title={editing?.id ? t("sales.editSalesRep") : t("sales.newSalesRep")} open={Boolean(editing)} onClose={() => { setEditing(null); }} onSubmit={() => { save.mutate(); }} busy={save.isPending} problem={problem} testId="save-sales-rep">
        {editing ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("partners.code")} required>
              <TextField value={editing.code} onChange={(e) => { setEditing({ ...editing, code: e.target.value }); }} dir="ltr" required data-testid="rep-code" />
            </Field>
            <label className="flex items-center gap-2 self-end text-sm">
              <input type="checkbox" checked={editing.isActive} onChange={(e) => { setEditing({ ...editing, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
            <Field label={t("partners.name")} required>
              <TextField value={editing.name} onChange={(e) => { setEditing({ ...editing, name: e.target.value }); }} required data-testid="rep-name" />
            </Field>
            <Field label={t("partners.nameAr")}>
              <TextField value={editing.nameAr} onChange={(e) => { setEditing({ ...editing, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("sales.member")} description={t("sales.memberHint")}>
              {members.data ? (
                <SelectField value={editing.membershipId} onChange={(e) => { setEditing({ ...editing, membershipId: e.target.value }); }} data-testid="rep-member">
                  <option value="">—</option>
                  {members.data.filter((m) => m.status === "active").map((m) => <option key={m.membershipId} value={m.membershipId}>{m.displayName} · {m.email}</option>)}
                </SelectField>
              ) : (
                <TextField value={editing.memberName || "—"} readOnly />
              )}
            </Field>
            <Field label={t("accounting.company")}>
              <SelectField value={editing.companyId} onChange={(e) => { setEditing({ ...editing, companyId: e.target.value }); }} data-testid="rep-company">
                <option value="">{t("sales.allCompanies")}</option>
                {(companies.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.legalName)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("sales.commissionPlan")}>
              <SelectField value={editing.commissionPlanId} onChange={(e) => { setEditing({ ...editing, commissionPlanId: e.target.value }); }} data-testid="rep-plan">
                <option value="">—</option>
                {(plans.data ?? []).filter((p) => p.isActive || p.id === editing.commissionPlanId).map((p) => <option key={p.id} value={p.id}>{p.code} · {localized(p.name)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("partners.email")}>
              <TextField type="email" value={editing.email} onChange={(e) => { setEditing({ ...editing, email: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("partners.phone")}>
              <TextField value={editing.phone} onChange={(e) => { setEditing({ ...editing, phone: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
        ) : null}
      </EditDialog>
    </div>
  );
}

// ------------------------------------------------------------------ commission plans

interface RuleForm {
  itemCategoryId: string;
  customerGroupId: string;
  fromAmount: string;
  ratePct: string;
}

interface PlanForm {
  id: string | null;
  code: string;
  name: string;
  nameAr: string;
  currency: string;
  basis: string;
  accrualPoint: string;
  tierPeriod: string;
  isActive: boolean;
  rules: RuleForm[];
}

function planForm(p?: CommissionPlan): PlanForm {
  return p
    ? { id: p.id, code: p.code, name: p.name.en ?? "", nameAr: p.name.ar ?? "", currency: p.currency, basis: p.basis, accrualPoint: p.accrualPoint, tierPeriod: p.tierPeriod, isActive: p.isActive, rules: p.rules.map((r) => ({ itemCategoryId: r.itemCategoryId ?? "", customerGroupId: r.customerGroupId ?? "", fromAmount: String(r.fromAmount), ratePct: String(r.ratePct) })) }
    : { id: null, code: "", name: "", nameAr: "", currency: "", basis: "revenue", accrualPoint: "invoice", tierPeriod: "month", isActive: true, rules: [{ itemCategoryId: "", customerGroupId: "", fromAmount: "0", ratePct: "" }] };
}

function PlansTab() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const plans = useCommissionPlans();
  const groups = useCustomerGroups();
  const categories = useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
  const canEdit = can("partners.sales_setup.manage");
  const [editing, setEditing] = useState<PlanForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [trying, setTrying] = useState<{ plan: CommissionPlan; amount: string; periodToDate: string; itemCategoryId: string; customerGroupId: string } | null>(null);
  const save = useMutation({
    mutationFn: async (f: PlanForm) => {
      const body = { code: f.code, name: { en: f.name, ar: f.nameAr || f.name }, currency: f.currency, basis: f.basis, accrualPoint: f.accrualPoint, tierPeriod: f.tierPeriod, isActive: f.isActive, rules: f.rules.map((r) => ({ itemCategoryId: r.itemCategoryId || null, customerGroupId: r.customerGroupId || null, fromAmount: r.fromAmount.trim() || "0", ratePct: r.ratePct.trim() || "0" })) };
      if (f.id) {
        unwrap(await api.PUT("/api/v1/partners/commission-plans/{planId}", { params: { path: { planId: f.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/partners/commission-plans", { body }));
      }
    },
    onSuccess: async () => { setEditing(null); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["commission-plans"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const quote = useMutation({
    mutationFn: async () => {
      if (!trying) {
        throw new Error("nothing to quote");
      }
      return unwrap(await api.POST("/api/v1/partners/commission-plans/{planId}/quote", { params: { path: { planId: trying.plan.id } }, body: { amount: trying.amount.trim() || "0", periodToDate: trying.periodToDate.trim() || "0", itemCategoryId: trying.itemCategoryId || null, customerGroupId: trying.customerGroupId || null } }));
    },
  });
  const rule = (index: number, patch: Partial<RuleForm>): void => { if (editing) { setEditing({ ...editing, rules: editing.rules.map((r, i) => (i === index ? { ...r, ...patch } : r)) }); } };
  const categoryName = (id: string | null): string => (id ? (categories.data?.find((c) => c.id === id)?.code ?? "") : t("sales.anyCategory"));
  return (
    <div className="flex flex-col gap-3">
      {canEdit ? (
        <div>
          <Button onClick={() => { setProblem(null); setEditing(planForm()); }} data-testid="new-commission-plan">
            <Plus aria-hidden="true" />
            {t("sales.newCommissionPlan")}
          </Button>
        </div>
      ) : null}
      <p className="text-sm text-fg-muted">{t("sales.commissionExplained")}</p>
      {(plans.data ?? []).map((p) => (
        <section key={p.id} className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="plan-card">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-sm font-semibold" dir="auto">
              <span dir="ltr">{p.code}</span> · {localized(p.name)}
            </h2>
            <ActiveBadge active={p.isActive} />
            <span className="text-xs text-fg-muted">
              {t(`sales.bases.${p.basis}`)} · {t(`sales.accrualPoints.${p.accrualPoint}`)} · {t(`sales.tierPeriods.${p.tierPeriod}`)} · {p.currency} · {t("sales.repsCount", { count: p.salesReps })}
            </span>
            <span className="ms-auto flex gap-2">
              <Button size="sm" variant="secondary" onClick={() => { quote.reset(); setTrying({ plan: p, amount: "", periodToDate: "0", itemCategoryId: "", customerGroupId: "" }); }} data-testid="try-plan">
                {t("sales.tryPlan")}
              </Button>
              {canEdit ? (
                <Button size="sm" variant="ghost" onClick={() => { setProblem(null); setEditing(planForm(p)); }} data-testid="edit-plan">
                  {t("common.edit")}
                </Button>
              ) : null}
            </span>
          </div>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("sales.itemCategory")}</TableHead>
                <TableHead>{t("sales.customerGroup")}</TableHead>
                <TableHead>{t("sales.fromAmount")}</TableHead>
                <TableHead>{t("sales.rate")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {p.rules.map((r) => (
                <TableRow key={r.sequence}>
                  <TableCell>{r.itemCategoryCode ?? t("sales.anyCategory")}</TableCell>
                  <TableCell>{r.customerGroupCode ?? t("sales.anyGroup")}</TableCell>
                  <TableCell><Money amount={r.fromAmount} currency={p.currency} /></TableCell>
                  <TableCell className="tabular" dir="ltr">{String(r.ratePct)}%</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </section>
      ))}

      <EditDialog title={editing?.id ? t("sales.editCommissionPlan") : t("sales.newCommissionPlan")} open={Boolean(editing)} onClose={() => { setEditing(null); }} onSubmit={() => { if (editing) { save.mutate(editing); } }} busy={save.isPending} problem={problem} testId="save-commission-plan">
        {editing ? (
          <div className="flex flex-col gap-4">
            <div className="grid gap-4 sm:grid-cols-3">
              <Field label={t("partners.code")} required>
                <TextField value={editing.code} onChange={(e) => { setEditing({ ...editing, code: e.target.value }); }} dir="ltr" required data-testid="plan-code" />
              </Field>
              <Field label={t("partners.name")} required>
                <TextField value={editing.name} onChange={(e) => { setEditing({ ...editing, name: e.target.value }); }} required data-testid="plan-name" />
              </Field>
              <Field label={t("partners.nameAr")}>
                <TextField value={editing.nameAr} onChange={(e) => { setEditing({ ...editing, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
              </Field>
              <Field label={t("partners.currency")} required>
                <TextField value={editing.currency} onChange={(e) => { setEditing({ ...editing, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="plan-currency" />
              </Field>
              <Field label={t("sales.basis")}>
                <SelectField value={editing.basis} onChange={(e) => { const basis = e.target.value; setEditing({ ...editing, basis, accrualPoint: basis === "collected" ? "payment" : editing.accrualPoint }); }}>
                  {["revenue", "margin", "collected"].map((b) => <option key={b} value={b}>{t(`sales.bases.${b}`)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("sales.accrualPoint")}>
                <SelectField value={editing.accrualPoint} onChange={(e) => { setEditing({ ...editing, accrualPoint: e.target.value }); }} disabled={editing.basis === "collected"}>
                  {["invoice", "payment"].map((a) => <option key={a} value={a}>{t(`sales.accrualPoints.${a}`)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("sales.tierPeriod")}>
                <SelectField value={editing.tierPeriod} onChange={(e) => { setEditing({ ...editing, tierPeriod: e.target.value }); }}>
                  {["month", "quarter", "year"].map((p) => <option key={p} value={p}>{t(`sales.tierPeriods.${p}`)}</option>)}
                </SelectField>
              </Field>
              <label className="flex items-center gap-2 self-end text-sm">
                <input type="checkbox" checked={editing.isActive} onChange={(e) => { setEditing({ ...editing, isActive: e.target.checked }); }} />
                {t("common.active")}
              </label>
            </div>
            <div className="flex flex-col gap-2">
              <h3 className="text-sm font-semibold">{t("sales.rates")}</h3>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("sales.itemCategory")}</TableHead>
                    <TableHead>{t("sales.customerGroup")}</TableHead>
                    <TableHead>{t("sales.fromAmount")}</TableHead>
                    <TableHead>{t("sales.rate")}</TableHead>
                    <TableHead><span className="sr-only">{t("common.delete")}</span></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.rules.map((r, index) => (
                    <TableRow key={index} data-testid="rule-row">
                      <TableCell>
                        <SelectField aria-label={t("sales.itemCategory")} value={r.itemCategoryId} onChange={(e) => { rule(index, { itemCategoryId: e.target.value }); }}>
                          <option value="">{t("sales.anyCategory")}</option>
                          {(categories.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>)}
                        </SelectField>
                      </TableCell>
                      <TableCell>
                        <SelectField aria-label={t("sales.customerGroup")} value={r.customerGroupId} onChange={(e) => { rule(index, { customerGroupId: e.target.value }); }}>
                          <option value="">{t("sales.anyGroup")}</option>
                          {(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.code}</option>)}
                        </SelectField>
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("sales.fromAmount")} inputMode="decimal" value={r.fromAmount} onChange={(e) => { rule(index, { fromAmount: e.target.value }); }} dir="ltr" data-testid={`rule-from-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("sales.rate")} inputMode="decimal" value={r.ratePct} onChange={(e) => { rule(index, { ratePct: e.target.value }); }} dir="ltr" data-testid={`rule-rate-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <Button type="button" size="sm" variant="ghost" aria-label={t("common.delete")} onClick={() => { setEditing({ ...editing, rules: editing.rules.filter((_, i) => i !== index) }); }}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div>
                <Button type="button" size="sm" variant="secondary" onClick={() => { setEditing({ ...editing, rules: [...editing.rules, { itemCategoryId: "", customerGroupId: "", fromAmount: "0", ratePct: "" }] }); }} data-testid="add-rule">
                  <Plus aria-hidden="true" />
                  {t("sales.addRate")}
                </Button>
              </div>
            </div>
          </div>
        ) : null}
      </EditDialog>

      <Dialog open={Boolean(trying)} onOpenChange={(isOpen) => { if (!isOpen) { setTrying(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {trying ? (
            <form onSubmit={(event) => { event.preventDefault(); quote.mutate(); }} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("sales.tryPlanTitle", { plan: trying.plan.code })}</DialogTitle>
              </DialogHeader>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("sales.saleAmount", { currency: trying.plan.currency })} required>
                  <TextField inputMode="decimal" value={trying.amount} onChange={(e) => { setTrying({ ...trying, amount: e.target.value }); }} dir="ltr" required data-testid="quote-amount" />
                </Field>
                <Field label={t("sales.periodToDate")} description={t("sales.periodToDateHint")}>
                  <TextField inputMode="decimal" value={trying.periodToDate} onChange={(e) => { setTrying({ ...trying, periodToDate: e.target.value }); }} dir="ltr" data-testid="quote-period" />
                </Field>
                <Field label={t("sales.itemCategory")}>
                  <SelectField value={trying.itemCategoryId} onChange={(e) => { setTrying({ ...trying, itemCategoryId: e.target.value }); }}>
                    <option value="">{t("sales.anyCategory")}</option>
                    {(categories.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("sales.customerGroup")}>
                  <SelectField value={trying.customerGroupId} onChange={(e) => { setTrying({ ...trying, customerGroupId: e.target.value }); }}>
                    <option value="">{t("sales.anyGroup")}</option>
                    {(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.code}</option>)}
                  </SelectField>
                </Field>
              </div>
              {quote.data ? (
                <div className="flex flex-col gap-2" data-testid="quote-result">
                  <p className="text-sm">
                    {t("sales.quoteScope", { category: categoryName(quote.data.matchedCategoryId), group: groups.data?.find((g) => g.id === quote.data.matchedCustomerGroupId)?.code ?? t("sales.anyGroup") })}
                  </p>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("sales.band")}</TableHead>
                        <TableHead>{t("sales.rate")}</TableHead>
                        <TableHead>{t("sales.basisInBand")}</TableHead>
                        <TableHead>{t("sales.commission")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {quote.data.bands.map((b) => (
                        <TableRow key={String(b.fromAmount)}>
                          <TableCell className="tabular" dir="ltr">{b.toAmount === null ? `≥ ${String(b.fromAmount)}` : `${String(b.fromAmount)} – ${String(b.toAmount)}`}</TableCell>
                          <TableCell className="tabular" dir="ltr">{String(b.ratePct)}%</TableCell>
                          <TableCell><Money amount={b.basis} currency={quote.data.currency} /></TableCell>
                          <TableCell><Money amount={b.commission} currency={quote.data.currency} /></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  <p className="text-sm font-semibold">
                    {t("sales.commissionTotal")} <Money amount={quote.data.commission} currency={quote.data.currency} testId="quote-total" />
                  </p>
                </div>
              ) : null}
              {quote.error ? <FormError message={toFormProblem(quote.error, t("common.saveFailed")).message} /> : null}
              <DialogFooter>
                <Button type="submit" loading={quote.isPending} data-testid="run-quote">
                  {t("sales.calculate")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ------------------------------------------------------------------ pipeline stages

function StagesTab() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const stages = usePipelineStages();
  const canEdit = can("partners.sales_setup.manage");
  const [editing, setEditing] = useState<{ id: string | null; code: string; name: string; nameAr: string; defaultProbability: string; outcome: string; isActive: boolean; isSystem: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const refresh = async (): Promise<void> => { await queryClient.invalidateQueries({ queryKey: ["pipeline-stages"] }); await queryClient.invalidateQueries({ queryKey: ["pipeline"] }); };
  const save = useMutation({
    mutationFn: async () => {
      if (!editing) {
        return;
      }
      const body = { code: editing.code, name: { en: editing.name, ar: editing.nameAr || editing.name }, defaultProbability: Number(editing.defaultProbability), outcome: editing.outcome, sortOrder: null, isActive: editing.isActive };
      if (editing.id) {
        unwrap(await api.PUT("/api/v1/partners/pipeline-stages/{stageId}", { params: { path: { stageId: editing.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/partners/pipeline-stages", { body }));
      }
    },
    onSuccess: async () => { setEditing(null); setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const reorder = useMutation({
    mutationFn: async (ids: string[]) => unwrap(await api.PUT("/api/v1/partners/pipeline-stages/order", { body: { stageIds: ids } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const list = stages.data ?? [];
  const shift = (index: number, by: number): void => {
    const ids = list.map((s) => s.id);
    const target = index + by;
    if (target < 0 || target >= ids.length) {
      return;
    }
    [ids[index], ids[target]] = [ids[target] ?? "", ids[index] ?? ""];
    reorder.mutate(ids);
  };
  const edit = (s?: PipelineStage): void => {
    setProblem(null);
    setEditing(s
      ? { id: s.id, code: s.code, name: s.name.en ?? "", nameAr: s.name.ar ?? "", defaultProbability: String(s.defaultProbability), outcome: s.outcome, isActive: s.isActive, isSystem: s.isSystem }
      : { id: null, code: "", name: "", nameAr: "", defaultProbability: "50", outcome: "open", isActive: true, isSystem: false });
  };
  return (
    <div className="flex flex-col gap-3">
      {canEdit ? (
        <div>
          <Button onClick={() => { edit(); }} data-testid="new-stage">
            <Plus aria-hidden="true" />
            {t("sales.newStage")}
          </Button>
        </div>
      ) : null}
      <FormError message={editing ? null : (problem?.message ?? null)} />
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("sales.order")}</TableHead>
            <TableHead>{t("partners.code")}</TableHead>
            <TableHead>{t("partners.name")}</TableHead>
            <TableHead>{t("sales.probability")}</TableHead>
            <TableHead>{t("sales.outcome")}</TableHead>
            <TableHead>{t("sales.openDeals")}</TableHead>
            <TableHead>{t("common.status")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {list.map((s, index) => (
            <TableRow key={s.id} data-testid="stage-row">
              <TableCell>
                {canEdit ? (
                  <span className="flex gap-1">
                    <Button size="sm" variant="ghost" aria-label={t("sales.moveUp", { stage: localized(s.name) })} disabled={index === 0 || reorder.isPending} onClick={() => { shift(index, -1); }} data-testid="stage-up">
                      <ArrowUp aria-hidden="true" />
                    </Button>
                    <Button size="sm" variant="ghost" aria-label={t("sales.moveDown", { stage: localized(s.name) })} disabled={index === list.length - 1 || reorder.isPending} onClick={() => { shift(index, 1); }} data-testid="stage-down">
                      <ArrowDown aria-hidden="true" />
                    </Button>
                  </span>
                ) : index + 1}
              </TableCell>
              <TableCell dir="ltr">{canEdit ? <button type="button" className="text-accent hover:underline" onClick={() => { edit(s); }}>{s.code}</button> : s.code}</TableCell>
              <TableCell dir="auto">{localized(s.name)}</TableCell>
              <TableCell className="tabular">{s.defaultProbability}%</TableCell>
              <TableCell>{t(`sales.outcomes.${s.outcome}`)}</TableCell>
              <TableCell className="tabular">{s.openOpportunities}</TableCell>
              <TableCell><ActiveBadge active={s.isActive} /></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <EditDialog title={editing?.id ? t("sales.editStage") : t("sales.newStage")} open={Boolean(editing)} onClose={() => { setEditing(null); }} onSubmit={() => { save.mutate(); }} busy={save.isPending} problem={problem} testId="save-stage">
        {editing ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("partners.code")} required description={editing.isSystem ? t("sales.systemStage") : undefined}>
              <TextField value={editing.code} onChange={(e) => { setEditing({ ...editing, code: e.target.value }); }} dir="ltr" required readOnly={editing.isSystem} data-testid="stage-code" />
            </Field>
            <Field label={t("sales.outcome")}>
              <SelectField value={editing.outcome} onChange={(e) => { const outcome = e.target.value; setEditing({ ...editing, outcome, defaultProbability: outcome === "won" ? "100" : outcome === "lost" ? "0" : editing.defaultProbability }); }} disabled={editing.isSystem}>
                {["open", "won", "lost"].map((o) => <option key={o} value={o}>{t(`sales.outcomes.${o}`)}</option>)}
              </SelectField>
            </Field>
            <Field label={t("partners.name")} required>
              <TextField value={editing.name} onChange={(e) => { setEditing({ ...editing, name: e.target.value }); }} required data-testid="stage-name" />
            </Field>
            <Field label={t("partners.nameAr")}>
              <TextField value={editing.nameAr} onChange={(e) => { setEditing({ ...editing, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("sales.probability")}>
              <TextField type="number" min={0} max={100} value={editing.defaultProbability} onChange={(e) => { setEditing({ ...editing, defaultProbability: e.target.value }); }} dir="ltr" disabled={editing.outcome !== "open"} data-testid="stage-probability" />
            </Field>
            <label className="flex items-center gap-2 self-end text-sm">
              <input type="checkbox" checked={editing.isActive} onChange={(e) => { setEditing({ ...editing, isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
          </div>
        ) : null}
      </EditDialog>
    </div>
  );
}
