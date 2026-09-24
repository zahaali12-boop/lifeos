import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, PurchaseStatus, useChargeTypes, useSuppliers } from "./shared";
import { DocumentFlowBar } from "./DocumentFlow";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

type LandedCost = components["schemas"]["LandedCostSummary"];

interface ChargeForm {
  chargeTypeId: string;
  partnerId: string;
  amount: string;
  allocationBasis: string;
  description: string;
}

interface LandedCostForm {
  id: string | null;
  customFields: CustomFieldValues;
  postingDate: string;
  currency: string;
  reference: string;
  receiptLineIds: string[];
  charges: ChargeForm[];
}

const bases = ["value", "weight", "volume", "quantity"];

/** Landed costs (roadmap 4.5): charges allocated to posted receipt lines by value, weight, volume or quantity, posted so what is on hand takes its share and what was sold goes to cost of sales; charge invoices settle the estimates. */
export function LandedCostsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<LandedCostForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/landed-costs");
  const [tab, setTab] = useState("allocations");
  const [reversal, setReversal] = useState<string | null>(null);
  const suppliers = useSuppliers(companyId);
  const chargeTypes = useChargeTypes();
  const [view, setView] = useState("documents");
  const [chargeType, setChargeType] = useState<{ id: string | null; code: string; name: string; nameAr: string; basis: string; isActive: boolean } | null>(null);

  const list = useQuery({
    queryKey: ["landed-costs", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const allocatable = useQuery({
    queryKey: ["allocatable", companyId],
    enabled: Boolean(companyId) && Boolean(form),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs/allocatable", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["landed-cost", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/landed-costs/{landedCostId}", { params: { path: { landedCostId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["landed-costs"], ["landed-cost"], ["allocatable"], ["invoicable"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: LandedCostForm) => {
      const body = {
        customFields: f.customFields,
        companyId,
        postingDate: f.postingDate || null,
        currency: f.currency || null,
        reference: f.reference || null,
        receiptLineIds: f.receiptLineIds,
        charges: f.charges.map((c) => ({ chargeTypeId: c.chargeTypeId, amount: num(c.amount), partnerId: c.partnerId || null, description: c.description || null, allocationBasis: c.allocationBasis || null })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/purchasing/landed-costs/{landedCostId}", { params: { path: { landedCostId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/landed-costs", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const saveChargeType = useMutation({
    mutationFn: async (f: { id: string | null; code: string; name: string; nameAr: string; basis: string; isActive: boolean }) => {
      const body = { code: f.code, name: { en: f.name, ar: f.nameAr || f.name }, defaultAllocationBasis: f.basis, isActive: f.isActive };
      return f.id ? unwrap(await api.PUT("/api/v1/purchasing/charge-types/{chargeTypeId}", { params: { path: { chargeTypeId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/charge-types", { body }));
    },
    onSuccess: async () => { setProblem(null); setChargeType(null); await queryClient.invalidateQueries({ queryKey: ["charge-types"] }); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { landedCostId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/purchasing/landed-costs/{landedCostId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/purchasing/landed-costs/{landedCostId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/purchasing/landed-costs/{landedCostId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<LandedCost, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
      { id: "reference", accessorKey: "reference", header: t("purchasing.reference"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.reference ?? ""}</span> },
      { id: "charges", accessorFn: (row) => row.charges.length, header: t("purchasing.charges"), size: 90, cell: ({ row }) => String(row.original.charges.length) },
      { id: "total", accessorKey: "totalAmountFc", header: t("purchasing.total"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalAmountFc, row.original.functionalCurrency)}</span> },
      { id: "onHand", accessorKey: "onHandPortionFc", header: t("purchasing.toStock"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.onHandPortionFc, row.original.functionalCurrency)}</span> },
      { id: "sold", accessorKey: "soldPortionFc", header: t("purchasing.toCogs"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.soldPortionFc, row.original.functionalCurrency)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ id: null, customFields: {}, postingDate: today(), currency: "", reference: "", receiptLineIds: [], charges: [{ chargeTypeId: "", partnerId: "", amount: "", allocationBasis: "", description: "" }] }); };
  const openEdit = (d: LandedCost): void => {
    setProblem(null);
    setForm({ id: d.id, customFields: asCustomFieldValues(d.customFields), postingDate: d.postingDate, currency: d.currency, reference: d.reference ?? "", receiptLineIds: [...new Set(d.allocations.map((a) => a.receiptLineId))], charges: d.charges.map((c) => ({ chargeTypeId: c.chargeTypeId, partnerId: c.partnerId ?? "", amount: String(c.amount), allocationBasis: c.allocationBasis, description: c.description ?? "" })) });
  };
  const patchCharge = (index: number, change: Partial<ChargeForm>): void => { if (form) { setForm({ ...form, charges: form.charges.map((c, i) => (i === index ? { ...c, ...change } : c)) }); } };
  const toggleLine = (id: string): void => { if (form) { setForm({ ...form, receiptLineIds: form.receiptLineIds.includes(id) ? form.receiptLineIds.filter((x) => x !== id) : [...form.receiptLineIds, id] }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const d = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.landedCosts")}
        description={t("purchasing.landedCostsDescription")}
        actions={
          view === "documents" ? (
            <Button onClick={openNew} disabled={!companyId} data-testid="new-landed-cost">
              <Plus aria-hidden="true" />
              {t("purchasing.newLandedCost")}
            </Button>
          ) : (
            <Button onClick={() => { setProblem(null); setChargeType({ id: null, code: "", name: "", nameAr: "", basis: "value", isActive: true }); }} data-testid="new-charge-type">
              <Plus aria-hidden="true" />
              {t("purchasing.newChargeType")}
            </Button>
          )
        }
      />
      <Tabs tabs={[{ id: "documents", label: t("nav.landedCosts"), testId: "view-documents" }, { id: "types", label: t("purchasing.chargeTypes"), testId: "view-charge-types" }]} value={view} onChange={setView} />
      {view === "documents" ? (
        <>
          <div className="mb-3 flex flex-wrap items-end gap-3">
            <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
            <Field label={t("common.status")}>
              <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
                <option value="">{t("common.all")}</option>
                {["draft", "posted", "reversed"].map((s) => (
                  <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
                ))}
              </SelectField>
            </Field>
          </div>
          <DataGrid<LandedCost> label="nav.landedCosts" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyLandedCosts")} emptyDescription={t("purchasing.emptyLandedCostsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setTab("allocations"); setOpenId(row.id); }} />
        </>
      ) : (
        <Table data-testid="charge-types">
          <TableHeader>
            <TableRow>
              <TableHead>{t("partners.code")}</TableHead>
              <TableHead>{t("partners.name")}</TableHead>
              <TableHead>{t("purchasing.defaultBasis")}</TableHead>
              <TableHead>{t("common.status")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {(chargeTypes.data ?? []).map((c) => (
              <TableRow key={c.id} data-testid="charge-type-row">
                <TableCell dir="ltr">{c.code}</TableCell>
                <TableCell dir="auto">{localized(c.name)}</TableCell>
                <TableCell>{t(`purchasing.bases.${c.defaultAllocationBasis}`)}</TableCell>
                <TableCell>{c.isActive ? t("common.active") : t("common.inactive")}</TableCell>
                <TableCell><Button variant="ghost" size="sm" onClick={() => { setProblem(null); setChargeType({ id: c.id, code: c.code, name: c.name.en ?? "", nameAr: c.name.ar ?? "", basis: c.defaultAllocationBasis, isActive: c.isActive }); }}>{t("common.edit")}</Button></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      <Dialog open={Boolean(chargeType)} onOpenChange={(isOpen) => { if (!isOpen) { setChargeType(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-xl">
          {chargeType ? (
            <form onSubmit={(e) => { e.preventDefault(); saveChargeType.mutate(chargeType); }} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{chargeType.id ? chargeType.code : t("purchasing.newChargeType")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("partners.code")} required>
                  <TextField value={chargeType.code} onChange={(e) => { setChargeType({ ...chargeType, code: e.target.value.toUpperCase() }); }} dir="ltr" required data-testid="charge-type-code" />
                </Field>
                <Field label={t("purchasing.defaultBasis")}>
                  <SelectField value={chargeType.basis} onChange={(e) => { setChargeType({ ...chargeType, basis: e.target.value }); }} data-testid="charge-type-basis">
                    {bases.map((b) => (
                      <option key={b} value={b}>{t(`purchasing.bases.${b}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("partners.name")} required>
                  <TextField value={chargeType.name} onChange={(e) => { setChargeType({ ...chargeType, name: e.target.value }); }} required data-testid="charge-type-name" />
                </Field>
                <Field label={t("partners.nameAr")}>
                  <TextField value={chargeType.nameAr} onChange={(e) => { setChargeType({ ...chargeType, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={chargeType.isActive} onChange={(e) => { setChargeType({ ...chargeType, isActive: e.target.checked }); }} />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setChargeType(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={saveChargeType.isPending} data-testid="save-charge-type">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("purchasing.editLandedCost") : t("purchasing.newLandedCost")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("purchasing.postingDate")}>
                  <TextField type="date" value={form.postingDate} onChange={(e) => { setForm({ ...form, postingDate: e.target.value }); }} dir="ltr" data-testid="landed-cost-date" />
                </Field>
                <Field label={t("partners.currency")} description={t("purchasing.companyCurrencyHelp")}>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                </Field>
                <Field label={t("purchasing.reference")}>
                  <TextField value={form.reference} onChange={(e) => { setForm({ ...form, reference: e.target.value }); }} dir="ltr" data-testid="landed-cost-reference" />
                </Field>
              </div>
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold">{t("purchasing.charges")}</h3>
                <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, charges: [...form.charges, { chargeTypeId: "", partnerId: "", amount: "", allocationBasis: "", description: "" }] }); }} data-testid="add-charge">{t("purchasing.addCharge")}</Button>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("purchasing.chargeType")}</TableHead>
                    <TableHead>{t("purchasing.amount")}</TableHead>
                    <TableHead>{t("purchasing.basis")}</TableHead>
                    <TableHead>{t("purchasing.expectedFrom")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {form.charges.map((c, index) => (
                    <TableRow key={index} data-testid="charge-row">
                      <TableCell>
                        <SelectField aria-label={t("purchasing.chargeType")} value={c.chargeTypeId} onChange={(e) => { patchCharge(index, { chargeTypeId: e.target.value }); }} required data-testid={`charge-type-${String(index)}`}>
                          <option value="">—</option>
                          {(chargeTypes.data ?? []).filter((x) => x.isActive).map((x) => (
                            <option key={x.id} value={x.id}>{x.code} · {localized(x.name)}</option>
                          ))}
                        </SelectField>
                      </TableCell>
                      <TableCell><TextField aria-label={t("purchasing.amount")} inputMode="decimal" value={c.amount} onChange={(e) => { patchCharge(index, { amount: e.target.value }); }} dir="ltr" className="w-28" data-testid={`charge-amount-${String(index)}`} /></TableCell>
                      <TableCell>
                        <SelectField aria-label={t("purchasing.basis")} value={c.allocationBasis} onChange={(e) => { patchCharge(index, { allocationBasis: e.target.value }); }} data-testid={`charge-basis-${String(index)}`}>
                          <option value="">{t("purchasing.typeDefault")}</option>
                          {bases.map((b) => (
                            <option key={b} value={b}>{t(`purchasing.bases.${b}`)}</option>
                          ))}
                        </SelectField>
                      </TableCell>
                      <TableCell>
                        <SelectField aria-label={t("purchasing.expectedFrom")} value={c.partnerId} onChange={(e) => { patchCharge(index, { partnerId: e.target.value }); }} data-testid={`charge-supplier-${String(index)}`}>
                          <option value="">—</option>
                          {(suppliers.data ?? []).map((s) => (
                            <option key={s.partnerId} value={s.partnerId}>{s.partnerCode}</option>
                          ))}
                        </SelectField>
                      </TableCell>
                      <TableCell><Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, charges: form.charges.filter((_, i) => i !== index) }); }}>{t("workflow.remove")}</Button></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <h3 className="text-sm font-semibold">{t("purchasing.landOn")}</h3>
              {(allocatable.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.nothingAllocatable")}</p> : (
                <ul className="grid gap-1 text-sm sm:grid-cols-2" data-testid="allocatable-lines">
                  {(allocatable.data ?? []).map((l) => (
                    <li key={l.receiptLineId}>
                      <label className="flex items-center gap-2">
                        <input type="checkbox" checked={form.receiptLineIds.includes(l.receiptLineId)} onChange={() => { toggleLine(l.receiptLineId); }} data-testid={`allocate-${l.receiptNumber}-${String(l.lineNo)}`} />
                        <span dir="ltr">{l.receiptNumber}</span>
                        <span dir="auto">{l.itemCode} · {localized(l.itemName)}</span>
                        <span className="tabular text-fg-muted" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode} · {formatNumber(l.valueFc)}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
              <CustomFieldsFieldset entityType="landed_cost_document" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={form.receiptLineIds.length === 0 || form.charges.length === 0} data-testid="save-landed-cost">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {d ? (
            <div className="flex flex-col gap-4" data-testid="landed-cost-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{d.number}</span>
                  <PurchaseStatus status={d.status} />
                </DialogTitle>
              </DialogHeader>
              <DocumentFlowBar documentType="landed_cost_document" documentId={d.id} />
              <CustomFieldValuesList entityType="landed_cost_document" values={d.customFields} />
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("purchasing.postingDate"), formatDate(d.postingDate)],
                [t("purchasing.reference"), d.reference ?? "—"],
                [t("purchasing.total"), <span key="total" data-testid="landed-cost-total">{formatMoney(d.totalAmountFc, d.functionalCurrency)}</span>],
                [t("purchasing.toStock"), <span key="stock" data-testid="landed-cost-on-hand">{formatMoney(d.onHandPortionFc, d.functionalCurrency)}</span>],
                [t("purchasing.toCogs"), <span key="cogs" data-testid="landed-cost-sold">{formatMoney(d.soldPortionFc, d.functionalCurrency)}</span>],
                ...(d.reversalReason ? [[t("purchasing.reversalReason"), d.reversalReason] as [string, string]] : []),
              ]} />
              <Tabs tabs={[{ id: "allocations", label: t("purchasing.allocationReport"), testId: "tab-allocations" }, { id: "charges", label: t("purchasing.charges"), testId: "tab-charges" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="landed_cost_document" entityId={d.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="landed_cost_document" entityId={d.id} /> : null}
              {tab === "allocations" ? (
                <Table data-testid="allocation-rows">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.chargeType")}</TableHead>
                      <TableHead>{t("nav.receipts")}</TableHead>
                      <TableHead>{t("purchasing.item")}</TableHead>
                      <TableHead>{t("purchasing.basis")}</TableHead>
                      <TableHead>{t("purchasing.allocated")}</TableHead>
                      <TableHead>{t("purchasing.toStock")}</TableHead>
                      <TableHead>{t("purchasing.toCogs")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {d.allocations.map((a) => (
                      <TableRow key={a.id} data-testid="allocation-row">
                        <TableCell>{a.chargeTypeCode}</TableCell>
                        <TableCell dir="ltr">{a.receiptNumber}</TableCell>
                        <TableCell dir="auto">{a.itemCode} · {localized(a.itemName)} ({formatNumber(a.receivedQuantity, { maximumFractionDigits: 3 })} {a.uomCode})</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(a.basisValue, { maximumFractionDigits: 3 })}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(a.allocatedAmountFc, d.functionalCurrency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(a.onHandPortionFc, d.functionalCurrency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(a.soldPortionFc, d.functionalCurrency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {tab === "charges" ? (
                <Table data-testid="charge-rows">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("purchasing.chargeType")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("purchasing.basis")}</TableHead>
                      <TableHead>{t("purchasing.expectedFrom")}</TableHead>
                      <TableHead>{t("purchasing.settlement")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {d.charges.map((c) => (
                      <TableRow key={c.id}>
                        <TableCell>{String(c.lineNo)}</TableCell>
                        <TableCell>{c.chargeTypeCode}{c.description ? ` · ${c.description}` : ""}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(c.amount, d.currency)}</TableCell>
                        <TableCell>{t(`purchasing.bases.${c.allocationBasis}`)}</TableCell>
                        <TableCell dir="ltr">{c.partnerCode ?? "—"}</TableCell>
                        <TableCell className="tabular" dir="ltr">{c.isEstimate ? t("purchasing.estimated") : t("purchasing.invoicedAt", { amount: formatMoney(c.invoicedAmountFc, d.functionalCurrency) })}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {reversal !== null ? (
                <Field label={t("purchasing.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {d.status === "draft" ? <Button variant="secondary" onClick={() => { openEdit(d); }} data-testid="edit-landed-cost">{t("common.edit")}</Button> : null}
                {d.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: d.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-landed-cost">{t("purchasing.deleteDraft")}</Button> : null}
                {d.status === "draft" ? <Button onClick={() => { act.mutate({ id: d.id, action: "post" }); }} loading={act.isPending} data-testid="post-landed-cost">{t("purchasing.postLandedCost")}</Button> : null}
                {d.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-landed-cost">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: d.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
