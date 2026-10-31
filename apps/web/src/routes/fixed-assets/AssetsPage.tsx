import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useNewFromUrl, useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { useTaxCodeOptions } from "../expenses/shared";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { MoreFields } from "../MoreFields";
import { useBankAccounts } from "../payables/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { QuickCreateButton } from "../QuickCreate";
import { RecordActivity } from "../RecordDiscussion";
import { StatusBar } from "../StatusBar";
import { acquisitionSources, assetFlow, AssetStatus, assetStatuses, depreciationConventions, depreciationMethods, glBook, useAssetBooks, useAssetCategories, type Asset } from "./shared";

interface AssetForm {
  id: string | null;
  nameEn: string;
  nameAr: string;
  categoryId: string;
  acquisitionDate: string;
  inServiceDate: string;
  cost: string;
  quantity: string;
  unit: string;
  acquisitionSource: string;
  supplierPartnerId: string;
  supplierReference: string;
  acquisitionTaxCodeId: string;
  acquisitionDueDate: string;
  acquisitionBankAccountId: string;
  openingAccumulatedDepreciation: string;
  openingDepreciatedThrough: string;
  serialNumber: string;
  warrantyUntil: string;
  description: string;
  notes: string;
  method: string;
  lifeMonths: string;
  salvageValue: string;
  convention: string;
  customFields: CustomFieldValues;
}

type Action = "add" | "impair" | "revalue" | "transfer" | "dispose" | "verify";

interface ActionForm {
  kind: Action;
  date: string;
  amount: string;
  source: string;
  bankAccountId: string;
  supplierPartnerId: string;
  reason: string;
  newLifeMonths: string;
  disposalKind: string;
  proceeds: string;
  proceedsSource: string;
  customerPartnerId: string;
  quantity: string;
  custodian: string;
  condition: string;
  locationNote: string;
}

/**
 * The asset register (founder programme item 3, roadmap 6.6): assets drafted and activated into the books, then
 * depreciated by runs, added to, impaired, revalued, transferred, verified and disposed of, every action from the
 * asset's own status bar.
 */
export function AssetsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<AssetForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/fixed-assets");
  const [action, setAction] = useState<ActionForm | null>(null);
  const categories = useAssetCategories(true);
  const books = useAssetBooks(companyId);
  const suppliers = useSuppliers(companyId);
  const customers = useQuery({
    queryKey: ["partners", "customers"],
    queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "customer", limit: 200 } } })),
  });
  const banks = useBankAccounts(companyId);
  const taxCodes = useTaxCodeOptions();
  const list = useQuery({
    queryKey: ["fixed-assets", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/assets", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["fixed-asset", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/fixed-assets/assets/{assetId}", { params: { path: { assetId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["fixed-assets"], ["fixed-asset"], ["asset-books"], ["depreciation-runs"], ["fa-forecast"], ["fa-reconciliation"], ["open-items"], ["bank-accounts"], ["bank-account"], ["bank-transactions"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const currency = companies.find((c) => c.id === companyId)?.functionalCurrency ?? "";
  const glBookId = (books.data ?? []).find((b) => b.postsToGl)?.id ?? null;
  const save = useMutation({
    mutationFn: async (f: AssetForm) => {
      const overrides = glBookId && (f.method || f.lifeMonths || f.salvageValue || f.convention)
        ? [{ bookId: glBookId, method: f.method || null, lifeMonths: f.lifeMonths.trim() ? Math.trunc(num(f.lifeMonths)) : null, salvageValue: f.salvageValue.trim() ? num(f.salvageValue) : null, convention: f.convention || null, ratePct: null, totalUnits: null }]
        : null;
      const body = {
        companyId,
        name: { en: f.nameEn, ar: f.nameAr },
        categoryId: f.categoryId,
        acquisitionDate: f.acquisitionDate,
        inServiceDate: f.inServiceDate || null,
        cost: num(f.cost),
        quantity: f.quantity.trim() ? num(f.quantity) : 1,
        unit: f.unit || null,
        acquisitionSource: f.acquisitionSource,
        supplierPartnerId: f.acquisitionSource === "supplier" || f.supplierPartnerId ? f.supplierPartnerId || null : null,
        supplierReference: f.supplierReference || null,
        acquisitionTaxCodeId: f.acquisitionSource === "supplier" ? f.acquisitionTaxCodeId || null : null,
        acquisitionDueDate: f.acquisitionSource === "supplier" ? f.acquisitionDueDate || null : null,
        acquisitionBankAccountId: f.acquisitionSource === "bank" ? f.acquisitionBankAccountId || null : null,
        openingAccumulatedDepreciation: f.acquisitionSource === "opening" ? num(f.openingAccumulatedDepreciation) : 0,
        openingDepreciatedThrough: f.acquisitionSource === "opening" ? f.openingDepreciatedThrough || null : null,
        serialNumber: f.serialNumber || null,
        warrantyUntil: f.warrantyUntil || null,
        description: f.description || null,
        notes: f.notes || null,
        books: overrides,
        customFields: f.customFields,
      };
      return f.id ? unwrap(await api.PUT("/api/v1/fixed-assets/assets/{assetId}", { params: { path: { assetId: f.id } }, body })) : unwrap(await api.POST("/api/v1/fixed-assets/assets", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "activate" } | { id: string; action: "delete" } | { id: string; action: "do"; form: ActionForm }) => {
      const params = { path: { assetId: input.id } };
      if (input.action === "activate") {
        return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/activate", { params }));
      }
      if (input.action === "delete") {
        unwrap(await api.DELETE("/api/v1/fixed-assets/assets/{assetId}", { params }));
        return null;
      }
      const f = input.form;
      switch (f.kind) {
        case "add": return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/additions", { params, body: { amount: num(f.amount), date: f.date, source: f.source, bankAccountId: f.source === "bank" ? f.bankAccountId || null : null, supplierPartnerId: f.source === "supplier" ? f.supplierPartnerId || null : null, dueDate: null, taxCodeId: null, reference: null, description: f.reason || null } }));
        case "impair": return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/impair", { params, body: { amount: num(f.amount), date: f.date, reason: f.reason } }));
        case "revalue": return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/revalue", { params, body: { fairValue: num(f.amount), date: f.date, reason: f.reason, newLifeMonths: f.newLifeMonths.trim() ? Math.trunc(num(f.newLifeMonths)) : null } }));
        case "transfer": return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/transfer", { params, body: { date: f.date, branchId: null, dimensions: null, custodianMembershipId: f.custodian || null, reason: f.reason || null } }));
        case "dispose": return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/dispose", { params, body: { date: f.date, kind: f.disposalKind, proceeds: num(f.proceeds), proceedsSource: num(f.proceeds) > 0 ? f.proceedsSource : "none", bankAccountId: f.proceedsSource === "bank" ? f.bankAccountId || null : null, customerPartnerId: f.proceedsSource === "customer" ? f.customerPartnerId || null : null, dueDate: null, quantity: f.quantity.trim() ? num(f.quantity) : null, reason: f.reason || null } }));
        default: return unwrap(await api.POST("/api/v1/fixed-assets/assets/{assetId}/verify", { params, body: { verifiedOn: f.date, condition: f.condition, locationNote: f.locationNote || null, note: f.reason || null } }));
      }
    },
    onSuccess: async (result) => { setProblem(null); setAction(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Asset, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("banking.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "category", accessorKey: "categoryCode", header: t("fixedAssets.category"), size: 110 },
      { id: "status", accessorFn: (r) => t(`fixedAssets.statuses.${r.status}`), header: t("common.status"), size: 130, cell: ({ row }) => <AssetStatus status={row.original.status} /> },
      { id: "acquired", accessorKey: "acquisitionDate", header: t("fixedAssets.acquisitionDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.acquisitionDate)}</span> },
      { id: "cost", accessorKey: "cost", header: t("fixedAssets.cost"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.cost, row.original.currency)}</span> },
      { id: "accDep", accessorKey: "accumulatedDepreciation", header: t("fixedAssets.accumulatedDepreciation"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.accumulatedDepreciation, row.original.currency)}</span> },
      { id: "nbv", accessorKey: "netBookValue", header: t("fixedAssets.netBookValue"), size: 140, meta: { aggregate: "sum" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.netBookValue, row.original.currency)}</span> },
      { id: "method", accessorFn: (r) => (r.method ? t(`fixedAssets.methods.${r.method}`) : ""), header: t("fixedAssets.method"), size: 150 },
      { id: "through", accessorKey: "depreciatedThrough", header: t("fixedAssets.depreciatedThrough"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.depreciatedThrough ? formatDate(row.original.depreciatedThrough) : ""}</span> },
      { id: "serial", accessorKey: "serialNumber", header: t("fixedAssets.serialNumber"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.serialNumber ?? ""}</span> },
      { id: "source", accessorFn: (r) => t(`fixedAssets.sources.${r.acquisitionSource}`), header: t("fixedAssets.source"), size: 130 },
    ],
    [t],
  );

  const blank = (): AssetForm => ({ id: null, nameEn: "", nameAr: "", categoryId: "", acquisitionDate: today(), inServiceDate: "", cost: "", quantity: "1", unit: "", acquisitionSource: "supplier", supplierPartnerId: "", supplierReference: "", acquisitionTaxCodeId: "", acquisitionDueDate: "", acquisitionBankAccountId: "", openingAccumulatedDepreciation: "", openingDepreciatedThrough: "", serialNumber: "", warrantyUntil: "", description: "", notes: "", method: "", lifeMonths: "", salvageValue: "", convention: "", customFields: {} });
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const a = detail.data;
  const book = a ? glBook(a) : undefined;
  const live = a?.status === "active" || a?.status === "fully_depreciated";
  const openForm = (x: Asset): void => {
    const b = glBook(x);
    setProblem(null);
    setForm({ id: x.id, nameEn: x.name.en ?? "", nameAr: x.name.ar ?? "", categoryId: x.categoryId, acquisitionDate: x.acquisitionDate, inServiceDate: x.inServiceDate, cost: String(x.cost), quantity: String(x.quantity), unit: x.unit ?? "", acquisitionSource: x.acquisitionSource, supplierPartnerId: x.supplierPartnerId ?? "", supplierReference: x.supplierReference ?? "", acquisitionTaxCodeId: x.acquisitionTaxCodeId ?? "", acquisitionDueDate: x.acquisitionDueDate ?? "", acquisitionBankAccountId: x.acquisitionBankAccountId ?? "", openingAccumulatedDepreciation: String(x.openingAccumulatedDepreciation), openingDepreciatedThrough: "", serialNumber: x.serialNumber ?? "", warrantyUntil: x.warrantyUntil ?? "", description: x.description ?? "", notes: x.notes ?? "", method: b?.method ?? "", lifeMonths: b ? String(b.lifeMonths) : "", salvageValue: b ? String(b.salvageValue) : "", convention: b?.convention ?? "", customFields: asCustomFieldValues(x.customFields) });
  };
  const startAction = (kind: Action): void => {
    setProblem(null);
    setAction({ kind, date: today(), amount: kind === "revalue" && book ? String(book.netBookValue) : "", source: "clearing", bankAccountId: "", supplierPartnerId: a?.supplierPartnerId ?? "", reason: "", newLifeMonths: "", disposalKind: "sale", proceeds: "", proceedsSource: "bank", customerPartnerId: "", quantity: "", custodian: a?.custodianMembershipId ?? "", condition: "good", locationNote: "" });
  };
  const actionTitle: Record<Action, string> = { add: t("fixedAssets.addition"), impair: t("fixedAssets.impairment"), revalue: t("fixedAssets.revaluation"), transfer: t("fixedAssets.transfer"), dispose: t("fixedAssets.disposal"), verify: t("fixedAssets.verification") };
  const money = (value: number | string): ReactNode => <span className="tabular" dir="ltr">{formatMoney(value, a?.currency ?? currency)}</span>;

  useNewFromUrl("/fixed-assets", () => { setProblem(null); setForm(blank()); }, Boolean(companyId));
  return (
    <>
      <PageHeader
        title={t("nav.fixedAssets")}
        description={t("fixedAssets.description")}
        actions={
          <Button onClick={() => { setProblem(null); setForm(blank()); }} disabled={!companyId} data-testid="new-asset">
            <Plus aria-hidden="true" />
            {t("fixedAssets.newAsset")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(ev) => { setStatus(ev.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {assetStatuses.map((s) => (
              <option key={s} value={s}>{t(`fixedAssets.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Asset> label="nav.fixedAssets" entityType="fixed_asset" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("fixedAssets.emptyAssets")} emptyDescription={t("fixedAssets.emptyAssetsDescription")} onOpen={(row) => { setProblem(null); setAction(null); setOpenId(row.id); }} groupable searchable presets={[{ id: "category", label: t("fixedAssets.byCategory"), grouping: ["category"] }, { id: "status", label: t("fixedAssets.byStatus"), grouping: ["status"] }]} viewExtras={() => ({ status })} onApplyView={(extras) => { setStatus(typeof extras?.status === "string" ? extras.status : ""); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("fixedAssets.editAsset") : t("fixedAssets.newAsset")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("banking.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(ev) => { setForm({ ...form, nameEn: ev.target.value }); }} dir="ltr" required data-testid="asset-name-en" />
                </Field>
                <Field label={t("banking.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(ev) => { setForm({ ...form, nameAr: ev.target.value }); }} dir="rtl" required data-testid="asset-name-ar" />
                </Field>
                <Field label={t("fixedAssets.category")} required>
                  <SelectField value={form.categoryId} onChange={(ev) => { setForm({ ...form, categoryId: ev.target.value }); }} required disabled={Boolean(form.id)} data-testid="asset-category">
                    <option value="">—</option>
                    {(categories.data ?? []).map((c) => (
                      <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("fixedAssets.acquisitionDate")} required>
                  <TextField type="date" value={form.acquisitionDate} onChange={(ev) => { setForm({ ...form, acquisitionDate: ev.target.value }); }} dir="ltr" required disabled={Boolean(form.id)} data-testid="asset-acquisition-date" />
                </Field>
                <Field label={t("fixedAssets.inServiceDate")} description={t("fixedAssets.inServiceHelp")}>
                  <TextField type="date" value={form.inServiceDate} onChange={(ev) => { setForm({ ...form, inServiceDate: ev.target.value }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="asset-in-service-date" />
                </Field>
                <Field label={`${t("fixedAssets.cost")} (${currency})`} required>
                  <TextField inputMode="decimal" value={form.cost} onChange={(ev) => { setForm({ ...form, cost: ev.target.value }); }} dir="ltr" required disabled={Boolean(form.id)} data-testid="asset-cost" />
                </Field>
                <Field label={t("fixedAssets.source")} required>
                  <SelectField value={form.acquisitionSource} onChange={(ev) => { setForm({ ...form, acquisitionSource: ev.target.value }); }} disabled={Boolean(form.id)} data-testid="asset-source">
                    {acquisitionSources.map((s) => (
                      <option key={s} value={s}>{t(`fixedAssets.sources.${s}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                {form.acquisitionSource === "supplier" ? (
                  <>
                    <Field label={t("partners.supplier")} required>
                      <div className="flex items-center gap-1">
                        <SelectField value={form.supplierPartnerId} onChange={(ev) => { setForm({ ...form, supplierPartnerId: ev.target.value }); }} required disabled={Boolean(form.id)} data-testid="asset-supplier">
                          <option value="">—</option>
                          {(suppliers.data ?? []).map((sup) => (
                            <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
                          ))}
                        </SelectField>
                        {form.id ? null : <QuickCreateButton kind="supplier" companyId={companyId} currency={currency} onCreated={(created) => { setForm({ ...form, supplierPartnerId: created.id }); }} />}
                      </div>
                    </Field>
                    <Field label={t("fixedAssets.dueDate")}>
                      <TextField type="date" value={form.acquisitionDueDate} onChange={(ev) => { setForm({ ...form, acquisitionDueDate: ev.target.value }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="asset-due-date" />
                    </Field>
                    <Field label={t("expenses.taxCode")} description={t("fixedAssets.taxHelp")}>
                      <SelectField value={form.acquisitionTaxCodeId} onChange={(ev) => { setForm({ ...form, acquisitionTaxCodeId: ev.target.value }); }} disabled={Boolean(form.id)} data-testid="asset-tax-code">
                        <option value="">{t("fixedAssets.noTax")}</option>
                        {taxCodes.map((c) => (
                          <option key={c.id} value={c.id}>{c.label}</option>
                        ))}
                      </SelectField>
                    </Field>
                  </>
                ) : null}
                {form.acquisitionSource === "bank" ? (
                  <Field label={t("nav.bankAccounts")} required>
                    <SelectField value={form.acquisitionBankAccountId} onChange={(ev) => { setForm({ ...form, acquisitionBankAccountId: ev.target.value }); }} required disabled={Boolean(form.id)} data-testid="asset-bank">
                      <option value="">—</option>
                      {(banks.data ?? []).map((b) => (
                        <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                {form.acquisitionSource === "opening" ? (
                  <>
                    <Field label={t("fixedAssets.openingAccumulatedDepreciation")} required>
                      <TextField inputMode="decimal" value={form.openingAccumulatedDepreciation} onChange={(ev) => { setForm({ ...form, openingAccumulatedDepreciation: ev.target.value }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="asset-opening-depreciation" />
                    </Field>
                    <Field label={t("fixedAssets.openingThrough")} description={t("fixedAssets.openingThroughHelp")} required>
                      <TextField type="date" value={form.openingDepreciatedThrough} onChange={(ev) => { setForm({ ...form, openingDepreciatedThrough: ev.target.value }); }} dir="ltr" required disabled={Boolean(form.id)} data-testid="asset-opening-through" />
                    </Field>
                  </>
                ) : null}
                <Field label={t("fixedAssets.serialNumber")}>
                  <TextField value={form.serialNumber} onChange={(ev) => { setForm({ ...form, serialNumber: ev.target.value }); }} dir="ltr" data-testid="asset-serial" />
                </Field>
                <Field label={t("expenses.descriptionLabel")} className="sm:col-span-2">
                  <TextareaField rows={1} value={form.description} onChange={(ev) => { setForm({ ...form, description: ev.target.value }); }} data-testid="asset-description" />
                </Field>
              </div>
              <MoreFields filled={Boolean(form.method || form.lifeMonths || form.salvageValue || form.quantity !== "1" || form.supplierReference || form.warrantyUntil || form.notes)}>
                <div className="grid gap-4 sm:grid-cols-4">
                  <Field label={t("fixedAssets.quantity")} description={t("fixedAssets.quantityHelp")}>
                    <TextField inputMode="decimal" value={form.quantity} onChange={(ev) => { setForm({ ...form, quantity: ev.target.value }); }} dir="ltr" disabled={Boolean(form.id)} data-testid="asset-quantity" />
                  </Field>
                  <Field label={t("fixedAssets.unit")}>
                    <TextField value={form.unit} onChange={(ev) => { setForm({ ...form, unit: ev.target.value }); }} dir="ltr" data-testid="asset-unit" />
                  </Field>
                  <Field label={t("fixedAssets.supplierReference")}>
                    <TextField value={form.supplierReference} onChange={(ev) => { setForm({ ...form, supplierReference: ev.target.value }); }} dir="ltr" data-testid="asset-supplier-reference" />
                  </Field>
                  <Field label={t("fixedAssets.warrantyUntil")}>
                    <TextField type="date" value={form.warrantyUntil} onChange={(ev) => { setForm({ ...form, warrantyUntil: ev.target.value }); }} dir="ltr" data-testid="asset-warranty" />
                  </Field>
                  {form.id ? null : (
                    <>
                      <Field label={t("fixedAssets.method")} description={t("fixedAssets.overrideHelp")}>
                        <SelectField value={form.method} onChange={(ev) => { setForm({ ...form, method: ev.target.value }); }} data-testid="asset-method">
                          <option value="">{t("fixedAssets.categoryDefault")}</option>
                          {depreciationMethods.map((m) => (
                            <option key={m} value={m}>{t(`fixedAssets.methods.${m}`)}</option>
                          ))}
                        </SelectField>
                      </Field>
                      <Field label={t("fixedAssets.lifeMonths")}>
                        <TextField inputMode="numeric" value={form.lifeMonths} onChange={(ev) => { setForm({ ...form, lifeMonths: ev.target.value }); }} dir="ltr" placeholder={t("fixedAssets.categoryDefault")} data-testid="asset-life" />
                      </Field>
                      <Field label={t("fixedAssets.salvageValue")}>
                        <TextField inputMode="decimal" value={form.salvageValue} onChange={(ev) => { setForm({ ...form, salvageValue: ev.target.value }); }} dir="ltr" placeholder={t("fixedAssets.categoryDefault")} data-testid="asset-salvage" />
                      </Field>
                      <Field label={t("fixedAssets.convention")}>
                        <SelectField value={form.convention} onChange={(ev) => { setForm({ ...form, convention: ev.target.value }); }} data-testid="asset-convention">
                          <option value="">{t("fixedAssets.categoryDefault")}</option>
                          {depreciationConventions.map((c) => (
                            <option key={c} value={c}>{t(`fixedAssets.conventions.${c}`)}</option>
                          ))}
                        </SelectField>
                      </Field>
                    </>
                  )}
                  <Field label={t("fixedAssets.notes")} className="sm:col-span-4">
                    <TextareaField rows={2} value={form.notes} onChange={(ev) => { setForm({ ...form, notes: ev.target.value }); }} data-testid="asset-notes" />
                  </Field>
                </div>
                <CustomFieldsFieldset entityType="fa_asset" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
              </MoreFields>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.categoryId || !form.nameEn || num(form.cost) < 0} data-testid="save-asset">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setAction(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {a ? (
            <div className="flex flex-col gap-4" data-testid="asset-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{a.number}</span>
                  <span dir="auto">{localized(a.name)}</span>
                  <AssetStatus status={a.status} />
                </DialogTitle>
              </DialogHeader>
              <StatusBar {...assetFlow(a.status, t)} actions={(
                <>
                  {a.status === "draft" ? <Button onClick={() => { act.mutate({ id: a.id, action: "activate" }); }} loading={act.isPending} data-testid="activate-asset">{t("fixedAssets.activate")}</Button> : null}
                  {a.status === "draft" ? <Button variant="secondary" onClick={() => { openForm(a); }} data-testid="edit-asset">{t("common.edit")}</Button> : null}
                  {a.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: a.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-asset">{t("purchasing.deleteDraft")}</Button> : null}
                  {live ? <Button variant="secondary" onClick={() => { startAction("add"); }} data-testid="add-to-asset">{t("fixedAssets.addition")}…</Button> : null}
                  {live ? <Button variant="secondary" onClick={() => { startAction("impair"); }} data-testid="impair-asset">{t("fixedAssets.impairment")}…</Button> : null}
                  {live ? <Button variant="secondary" onClick={() => { startAction("revalue"); }} data-testid="revalue-asset">{t("fixedAssets.revaluation")}…</Button> : null}
                  {live ? <Button variant="secondary" onClick={() => { startAction("transfer"); }} data-testid="transfer-asset">{t("fixedAssets.transfer")}…</Button> : null}
                  {live ? <Button variant="secondary" onClick={() => { startAction("dispose"); }} data-testid="dispose-asset">{t("fixedAssets.disposal")}…</Button> : null}
                  {a.status !== "draft" ? <Button variant="secondary" onClick={() => { startAction("verify"); }} data-testid="verify-asset">{t("fixedAssets.verification")}…</Button> : null}
                  {a.status !== "draft" ? <Button variant="ghost" onClick={() => { openForm(a); }} data-testid="edit-asset-details">{t("common.edit")}</Button> : null}
                </>
              )} />
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("fixedAssets.category"), `${a.categoryCode} · ${localized(a.categoryName)}`],
                [t("fixedAssets.acquisitionDate"), formatDate(a.acquisitionDate)],
                [t("fixedAssets.inServiceDate"), formatDate(a.inServiceDate)],
                [t("fixedAssets.source"), t(`fixedAssets.sources.${a.acquisitionSource}`)],
                [t("fixedAssets.cost"), <span key="cost" data-testid="asset-cost-value">{money(a.cost)}</span>],
                [t("fixedAssets.accumulatedDepreciation"), <span key="dep" data-testid="asset-accdep-value">{money(a.accumulatedDepreciation)}</span>],
                [t("fixedAssets.accumulatedImpairment"), money(a.accumulatedImpairment)],
                [t("fixedAssets.netBookValue"), <span key="nbv" data-testid="asset-nbv-value">{money(a.netBookValue)}</span>],
                ...(book ? [[t("fixedAssets.method"), `${t(`fixedAssets.methods.${book.method}`)} · ${String(book.lifeMonths)} ${t("fixedAssets.months")} · ${t(`fixedAssets.conventions.${book.convention}`)}`] as [string, ReactNode]] : []),
                ...(a.depreciatedThrough ? [[t("fixedAssets.depreciatedThrough"), formatDate(a.depreciatedThrough)] as [string, ReactNode]] : []),
                ...(a.quantity !== 1 ? [[t("fixedAssets.quantity"), `${formatNumber(a.quantity)} ${a.unit ?? ""}`] as [string, ReactNode]] : []),
                ...(a.serialNumber ? [[t("fixedAssets.serialNumber"), a.serialNumber] as [string, ReactNode]] : []),
                ...(a.supplierCode ? [[t("partners.supplier"), `${a.supplierCode}${a.supplierReference ? ` · ${a.supplierReference}` : ""}`] as [string, ReactNode]] : []),
                ...(a.disposedOn ? [[t("fixedAssets.disposal"), `${t(`fixedAssets.disposalKinds.${a.disposalKind ?? "sale"}`)} · ${formatDate(a.disposedOn)}`] as [string, ReactNode]] : []),
              ]} />
              {action ? (
                <form onSubmit={(event) => { event.preventDefault(); act.mutate({ id: a.id, action: "do", form: action }); }} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="asset-action-form">
                  <h3 className="text-sm font-semibold">{actionTitle[action.kind]}</h3>
                  <div className="grid gap-4 sm:grid-cols-3">
                    <Field label={t("fixedAssets.date")} required>
                      <TextField type="date" value={action.date} onChange={(ev) => { setAction({ ...action, date: ev.target.value }); }} dir="ltr" required data-testid="action-date" />
                    </Field>
                    {action.kind === "add" || action.kind === "impair" ? (
                      <Field label={t("purchasing.amount")} required>
                        <TextField inputMode="decimal" value={action.amount} onChange={(ev) => { setAction({ ...action, amount: ev.target.value }); }} dir="ltr" required data-testid="action-amount" />
                      </Field>
                    ) : null}
                    {action.kind === "revalue" ? (
                      <>
                        <Field label={t("fixedAssets.fairValue")} required>
                          <TextField inputMode="decimal" value={action.amount} onChange={(ev) => { setAction({ ...action, amount: ev.target.value }); }} dir="ltr" required data-testid="action-fair-value" />
                        </Field>
                        <Field label={t("fixedAssets.newLifeMonths")} description={t("fixedAssets.newLifeHelp")}>
                          <TextField inputMode="numeric" value={action.newLifeMonths} onChange={(ev) => { setAction({ ...action, newLifeMonths: ev.target.value }); }} dir="ltr" data-testid="action-new-life" />
                        </Field>
                      </>
                    ) : null}
                    {action.kind === "add" ? (
                      <Field label={t("fixedAssets.source")} required>
                        <SelectField value={action.source} onChange={(ev) => { setAction({ ...action, source: ev.target.value }); }} data-testid="action-source">
                          {acquisitionSources.filter((s) => s !== "opening").map((s) => (
                            <option key={s} value={s}>{t(`fixedAssets.sources.${s}`)}</option>
                          ))}
                        </SelectField>
                      </Field>
                    ) : null}
                    {(action.kind === "add" && action.source === "bank") || (action.kind === "dispose" && action.proceedsSource === "bank" && num(action.proceeds) > 0) ? (
                      <Field label={t("nav.bankAccounts")} required>
                        <SelectField value={action.bankAccountId} onChange={(ev) => { setAction({ ...action, bankAccountId: ev.target.value }); }} required data-testid="action-bank">
                          <option value="">—</option>
                          {(banks.data ?? []).map((b) => (
                            <option key={b.id} value={b.id}>{b.code} · {b.currency}</option>
                          ))}
                        </SelectField>
                      </Field>
                    ) : null}
                    {action.kind === "add" && action.source === "supplier" ? (
                      <Field label={t("partners.supplier")} required>
                        <SelectField value={action.supplierPartnerId} onChange={(ev) => { setAction({ ...action, supplierPartnerId: ev.target.value }); }} required data-testid="action-supplier">
                          <option value="">—</option>
                          {(suppliers.data ?? []).map((sup) => (
                            <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
                          ))}
                        </SelectField>
                      </Field>
                    ) : null}
                    {action.kind === "dispose" ? (
                      <>
                        <Field label={t("fixedAssets.disposalKind")} required>
                          <SelectField value={action.disposalKind} onChange={(ev) => { setAction({ ...action, disposalKind: ev.target.value }); }} data-testid="action-disposal-kind">
                            <option value="sale">{t("fixedAssets.disposalKinds.sale")}</option>
                            <option value="scrap">{t("fixedAssets.disposalKinds.scrap")}</option>
                          </SelectField>
                        </Field>
                        <Field label={t("fixedAssets.proceeds")}>
                          <TextField inputMode="decimal" value={action.proceeds} onChange={(ev) => { setAction({ ...action, proceeds: ev.target.value }); }} dir="ltr" data-testid="action-proceeds" />
                        </Field>
                        {num(action.proceeds) > 0 ? (
                          <Field label={t("fixedAssets.proceedsTo")} required>
                            <SelectField value={action.proceedsSource} onChange={(ev) => { setAction({ ...action, proceedsSource: ev.target.value }); }} data-testid="action-proceeds-source">
                              <option value="bank">{t("fixedAssets.proceedsSources.bank")}</option>
                              <option value="customer">{t("fixedAssets.proceedsSources.customer")}</option>
                            </SelectField>
                          </Field>
                        ) : null}
                        {num(action.proceeds) > 0 && action.proceedsSource === "customer" ? (
                          <Field label={t("partners.customer")} required>
                            <SelectField value={action.customerPartnerId} onChange={(ev) => { setAction({ ...action, customerPartnerId: ev.target.value }); }} required data-testid="action-customer">
                              <option value="">—</option>
                              {(customers.data?.items ?? []).map((p) => (
                                <option key={p.id} value={p.id}>{p.code} · {localized(p.legalName)}</option>
                              ))}
                            </SelectField>
                          </Field>
                        ) : null}
                        {a.quantity !== 1 ? (
                          <Field label={t("fixedAssets.quantityDisposed")} description={t("fixedAssets.quantityDisposedHelp", { quantity: formatNumber(a.quantity) })}>
                            <TextField inputMode="decimal" value={action.quantity} onChange={(ev) => { setAction({ ...action, quantity: ev.target.value }); }} dir="ltr" data-testid="action-quantity" />
                          </Field>
                        ) : null}
                      </>
                    ) : null}
                    {action.kind === "verify" ? (
                      <>
                        <Field label={t("fixedAssets.condition")}>
                          <SelectField value={action.condition} onChange={(ev) => { setAction({ ...action, condition: ev.target.value }); }} data-testid="action-condition">
                            {["good", "damaged", "missing"].map((c) => (
                              <option key={c} value={c}>{t(`fixedAssets.conditions.${c}`)}</option>
                            ))}
                          </SelectField>
                        </Field>
                        <Field label={t("fixedAssets.locationNote")}>
                          <TextField value={action.locationNote} onChange={(ev) => { setAction({ ...action, locationNote: ev.target.value }); }} data-testid="action-location" />
                        </Field>
                      </>
                    ) : null}
                    {action.kind === "transfer" ? (
                      <Field label={t("fixedAssets.custodian")} description={t("fixedAssets.custodianHelp")}>
                        <TextField value={action.custodian} onChange={(ev) => { setAction({ ...action, custodian: ev.target.value }); }} dir="ltr" data-testid="action-custodian" />
                      </Field>
                    ) : null}
                    <Field label={action.kind === "impair" || action.kind === "revalue" ? t("purchasing.reversalReason") : t("fixedAssets.note")} required={action.kind === "impair" || action.kind === "revalue"} className="sm:col-span-3">
                      <TextField value={action.reason} onChange={(ev) => { setAction({ ...action, reason: ev.target.value }); }} data-testid="action-reason" />
                    </Field>
                  </div>
                  <div className="flex gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setAction(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={act.isPending} data-testid="confirm-action">{actionTitle[action.kind]}</Button>
                  </div>
                </form>
              ) : null}
              {(a.books ?? []).length > 0 ? (
                <div className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("fixedAssets.books")}</h3>
                  <Table data-testid="asset-books">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("fixedAssets.book")}</TableHead>
                        <TableHead>{t("fixedAssets.method")}</TableHead>
                        <TableHead>{t("fixedAssets.lifeMonths")}</TableHead>
                        <TableHead>{t("fixedAssets.salvageValue")}</TableHead>
                        <TableHead>{t("fixedAssets.cost")}</TableHead>
                        <TableHead>{t("fixedAssets.accumulatedDepreciation")}</TableHead>
                        <TableHead>{t("fixedAssets.netBookValue")}</TableHead>
                        <TableHead>{t("fixedAssets.depreciatedThrough")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(a.books ?? []).map((b) => (
                        <TableRow key={b.id} data-testid="asset-book-row">
                          <TableCell dir="ltr">{b.bookCode}{b.postsToGl ? " ★" : ""}</TableCell>
                          <TableCell>{t(`fixedAssets.methods.${b.method}`)} · {t(`fixedAssets.conventions.${b.convention}`)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{String(b.lifeMonths)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(b.salvageValue, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(b.cost, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(b.accumulatedDepreciation, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(b.netBookValue, a.currency)}</TableCell>
                          <TableCell dir="ltr">{b.depreciatedThrough ? formatDate(b.depreciatedThrough) : "—"}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </div>
              ) : null}
              {book && (book.schedule ?? []).length > 0 ? (
                <details className="rounded-md border border-border p-3" data-testid="asset-schedule">
                  <summary className="cursor-pointer text-sm font-semibold">{t("fixedAssets.schedule", { count: (book.schedule ?? []).length })}</summary>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("fixedAssets.month")}</TableHead>
                        <TableHead>{t("fixedAssets.planned")}</TableHead>
                        <TableHead>{t("fixedAssets.posted")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(book.schedule ?? []).map((s) => (
                        <TableRow key={s.id} data-testid="schedule-row">
                          <TableCell dir="ltr">{formatDate(s.periodStart)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(s.plannedAmount, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(s.postedAmount, a.currency)}</TableCell>
                          <TableCell>{t(`fixedAssets.scheduleStatuses.${s.status}`)}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </details>
              ) : null}
              {(a.transactions ?? []).length > 0 ? (
                <div className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("fixedAssets.transactions")}</h3>
                  <Table data-testid="asset-transactions">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("fixedAssets.date")}</TableHead>
                        <TableHead>{t("fixedAssets.book")}</TableHead>
                        <TableHead>{t("purchasing.kind")}</TableHead>
                        <TableHead>{t("fixedAssets.cost")}</TableHead>
                        <TableHead>{t("fixedAssets.accumulatedDepreciation")}</TableHead>
                        <TableHead>{t("fixedAssets.accumulatedImpairment")}</TableHead>
                        <TableHead>{t("fixedAssets.proceeds")}</TableHead>
                        <TableHead>{t("fixedAssets.gainLoss")}</TableHead>
                        <TableHead>{t("banking.reference")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(a.transactions ?? []).map((x) => (
                        <TableRow key={x.id} data-testid="asset-transaction-row">
                          <TableCell dir="ltr">{formatDate(x.postingDate)}</TableCell>
                          <TableCell dir="ltr">{x.bookCode}</TableCell>
                          <TableCell>{t(`fixedAssets.transactionKinds.${x.kind}`)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(x.costDelta, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(x.accumulatedDepreciationDelta, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(x.impairmentDelta, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(x.proceeds, a.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(x.gainLoss, a.currency)}</TableCell>
                          <TableCell dir="ltr">{x.reference ?? ""}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </div>
              ) : null}
              {(a.verifications ?? []).length > 0 ? (
                <div className="flex flex-col gap-1 text-sm" data-testid="asset-verifications">
                  <h3 className="text-sm font-semibold">{t("fixedAssets.verifications")}</h3>
                  {(a.verifications ?? []).map((v) => (
                    <p key={v.id}><span dir="ltr">{formatDate(v.verifiedOn)}</span> · {t(`fixedAssets.conditions.${v.condition}`)}{v.locationNote ? ` · ${v.locationNote}` : ""}{v.note ? ` · ${v.note}` : ""}</p>
                  ))}
                </div>
              ) : null}
              <CustomFieldValuesList entityType="fa_asset" values={a.customFields} />
              <RecordActivity entityType="fixed_asset" entityId={a.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
