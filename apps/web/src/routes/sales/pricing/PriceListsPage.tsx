import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, X } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../../api";
import { DataGrid } from "../../../grid/DataGrid";
import { localized } from "../../../lib/format";
import { useCan } from "../../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField, TextareaField } from "../../common";
import { CompanyFilter, useCompanyContext } from "../../inventory/shared";
import { useCustomerGroups } from "../shared";
import { Validity, refLabel, useCompanyCustomers, usePriceLists, type PriceList } from "./shared";

export interface PriceListForm {
  code: string;
  nameEn: string;
  nameAr: string;
  currency: string;
  pricesIncludeTax: boolean;
  parentListId: string;
  parentAdjustmentPct: string;
  roundingIncrement: string;
  roundingMode: string;
  priceSurcharge: string;
  validFrom: string;
  validTo: string;
  priority: string;
  isDefault: boolean;
  isActive: boolean;
  notes: string;
  partnerIds: string[];
  customerGroupIds: string[];
}

export function priceListForm(list: PriceList | null, currency: string): PriceListForm {
  return list
    ? {
        code: list.code,
        nameEn: list.name.en ?? "",
        nameAr: list.name.ar ?? "",
        currency: list.currency,
        pricesIncludeTax: list.pricesIncludeTax,
        parentListId: list.parentListId ?? "",
        parentAdjustmentPct: list.parentAdjustmentPct === null ? "" : String(list.parentAdjustmentPct),
        roundingIncrement: list.roundingIncrement === null ? "" : String(list.roundingIncrement),
        roundingMode: list.roundingMode,
        priceSurcharge: list.priceSurcharge ? String(list.priceSurcharge) : "",
        validFrom: list.validFrom ?? "",
        validTo: list.validTo ?? "",
        priority: String(list.priority),
        isDefault: list.isDefault,
        isActive: list.isActive,
        notes: list.notes ?? "",
        partnerIds: list.customers.map((c) => c.id),
        customerGroupIds: list.customerGroups.map((g) => g.id),
      }
    : { code: "", nameEn: "", nameAr: "", currency, pricesIncludeTax: false, parentListId: "", parentAdjustmentPct: "", roundingIncrement: "", roundingMode: "nearest", priceSurcharge: "", validFrom: "", validTo: "", priority: "100", isDefault: false, isActive: true, notes: "", partnerIds: [], customerGroupIds: [] };
}

function body(form: PriceListForm, companyId: string) {
  const derived = Boolean(form.parentListId);
  return {
    companyId,
    code: form.code,
    name: { en: form.nameEn, ar: form.nameAr || form.nameEn },
    currency: form.currency,
    pricesIncludeTax: form.pricesIncludeTax,
    parentListId: form.parentListId || null,
    parentAdjustmentPct: derived && form.parentAdjustmentPct ? Number(form.parentAdjustmentPct) : null,
    roundingIncrement: derived && form.roundingIncrement ? Number(form.roundingIncrement) : null,
    roundingMode: form.roundingMode,
    priceSurcharge: derived && form.priceSurcharge ? Number(form.priceSurcharge) : 0,
    validFrom: form.validFrom || null,
    validTo: form.validTo || null,
    priority: Number(form.priority || "100"),
    isDefault: form.isDefault,
    isActive: form.isActive,
    notes: form.notes || null,
    partnerIds: form.partnerIds,
    customerGroupIds: form.customerGroupIds,
  };
}

/** The dialog a price list is created and changed in: its currency, tax basis, validity, derivation and who it is for. */
export function PriceListDialog({ list, companyId, currency, onClose, onSaved }: { list: PriceList | null; companyId: string; currency: string; onClose: () => void; onSaved: (list: PriceList) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<PriceListForm>(() => priceListForm(list, currency));
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const lists = usePriceLists(companyId);
  const customers = useCompanyCustomers(companyId);
  const groups = useCustomerGroups();
  const patch = (p: Partial<PriceListForm>): void => { setForm((prev) => ({ ...prev, ...p })); };
  const save = useMutation({
    mutationFn: async () => (list
      ? unwrap(await api.PUT("/api/v1/pricing/price-lists/{listId}", { params: { path: { listId: list.id } }, body: body(form, companyId) }))
      : unwrap(await api.POST("/api/v1/pricing/price-lists", { body: body(form, companyId) }))),
    onSuccess: async (saved) => { await queryClient.invalidateQueries({ queryKey: ["price-lists"] }); onSaved(saved); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const derived = Boolean(form.parentListId);
  const customerById = new Map((customers.data ?? []).map((c) => [c.partnerId, `${c.partnerCode} · ${localized(c.partnerName)}`]));
  const groupById = new Map((groups.data ?? []).map((g) => [g.id, `${g.code} · ${localized(g.name)}`]));

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{list ? t("pricing.editPriceList") : t("pricing.newPriceList")}</DialogTitle>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("pricing.code")} required>
              <TextField value={form.code} onChange={(e) => { patch({ code: e.target.value }); }} required dir="ltr" data-testid="list-code" />
            </Field>
            <Field label={t("pricing.nameEn")} required>
              <TextField value={form.nameEn} onChange={(e) => { patch({ nameEn: e.target.value }); }} required data-testid="list-name" />
            </Field>
            <Field label={t("pricing.nameAr")}>
              <TextField value={form.nameAr} onChange={(e) => { patch({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("pricing.currency")} required>
              <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} maxLength={3} required dir="ltr" data-testid="list-currency" />
            </Field>
            <Field label={t("pricing.priority")} description={t("pricing.priorityHint")}>
              <TextField type="number" min={0} value={form.priority} onChange={(e) => { patch({ priority: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validFrom")}>
              <TextField type="date" value={form.validFrom} onChange={(e) => { patch({ validFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validTo")}>
              <TextField type="date" value={form.validTo} onChange={(e) => { patch({ validTo: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
          <div className="flex flex-wrap gap-4 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={form.isDefault} onChange={(e) => { patch({ isDefault: e.target.checked }); }} data-testid="list-default" />
              {t("pricing.isDefault")}
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={form.pricesIncludeTax} onChange={(e) => { patch({ pricesIncludeTax: e.target.checked }); }} />
              {t("pricing.pricesIncludeTax")}
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={form.isActive} onChange={(e) => { patch({ isActive: e.target.checked }); }} />
              {t("common.active")}
            </label>
          </div>

          <fieldset className="flex flex-col gap-3 rounded-md border border-border p-3">
            <legend className="px-1 text-sm font-medium">{t("pricing.derivation")}</legend>
            <div className="grid gap-3 sm:grid-cols-5">
              <Field label={t("pricing.parentList")} className="sm:col-span-2">
                <SelectField value={form.parentListId} onChange={(e) => { patch({ parentListId: e.target.value }); }} data-testid="list-parent">
                  <option value="">{t("pricing.ownPrices")}</option>
                  {(lists.data ?? []).filter((l) => l.id !== list?.id).map((l) => (
                    <option key={l.id} value={l.id}>
                      {l.code} · {localized(l.name)}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("pricing.adjustmentPct")} description={t("pricing.adjustmentHint")}>
                <TextField type="number" step="any" value={form.parentAdjustmentPct} onChange={(e) => { patch({ parentAdjustmentPct: e.target.value }); }} disabled={!derived} required={derived} dir="ltr" data-testid="list-adjustment" />
              </Field>
              <Field label={t("pricing.roundingIncrement")}>
                <TextField type="number" min={0} step="any" value={form.roundingIncrement} onChange={(e) => { patch({ roundingIncrement: e.target.value }); }} disabled={!derived} dir="ltr" data-testid="list-rounding" />
              </Field>
              <Field label={t("pricing.roundingMode")}>
                <SelectField value={form.roundingMode} onChange={(e) => { patch({ roundingMode: e.target.value }); }} disabled={!derived}>
                  {["nearest", "up", "down"].map((m) => (
                    <option key={m} value={m}>
                      {t(`pricing.value.${m}`)}
                    </option>
                  ))}
                </SelectField>
              </Field>
              <Field label={t("pricing.surcharge")} description={t("pricing.surchargeHint")}>
                <TextField type="number" step="any" value={form.priceSurcharge} onChange={(e) => { patch({ priceSurcharge: e.target.value }); }} disabled={!derived} dir="ltr" />
              </Field>
            </div>
          </fieldset>

          <fieldset className="flex flex-col gap-3 rounded-md border border-border p-3">
            <legend className="px-1 text-sm font-medium">{t("pricing.forWhom")}</legend>
            <p className="text-xs text-fg-muted">{t("pricing.forWhomHint")}</p>
            <div className="grid gap-3 sm:grid-cols-2">
              <Chooser
                label={t("pricing.customers")}
                options={(customers.data ?? []).map((c) => ({ id: c.partnerId, label: `${c.partnerCode} · ${localized(c.partnerName)}` }))}
                chosen={form.partnerIds}
                labelOf={(id) => customerById.get(id) ?? id}
                onChange={(partnerIds) => { patch({ partnerIds }); }}
                testId="list-customer"
              />
              <Chooser
                label={t("pricing.customerGroups")}
                options={(groups.data ?? []).map((g) => ({ id: g.id, label: `${g.code} · ${localized(g.name)}` }))}
                chosen={form.customerGroupIds}
                labelOf={(id) => groupById.get(id) ?? id}
                onChange={(customerGroupIds) => { patch({ customerGroupIds }); }}
                testId="list-group"
              />
            </div>
          </fieldset>
          <Field label={t("pricing.notes")}>
            <TextareaField value={form.notes} onChange={(e) => { patch({ notes: e.target.value }); }} rows={2} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={save.isPending} data-testid="save-price-list">
              {t("common.save")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Picks several records from a list: a select to add one, the chosen ones as removable chips. */
export function Chooser({ label, options, chosen, labelOf, onChange, testId }: { label: string; options: { id: string; label: string }[]; chosen: string[]; labelOf: (id: string) => string; onChange: (ids: string[]) => void; testId: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2">
      <Field label={label}>
        <SelectField value="" onChange={(e) => { if (e.target.value && !chosen.includes(e.target.value)) { onChange([...chosen, e.target.value]); } }} data-testid={testId}>
          <option value="">{t("pricing.add")}</option>
          {options.filter((o) => !chosen.includes(o.id)).map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </SelectField>
      </Field>
      <ul className="flex flex-wrap gap-1">
        {chosen.map((id) => (
          <li key={id}>
            <Badge tone="accent" className="gap-1">
              <span dir="auto">{labelOf(id)}</span>
              <button type="button" className="inline-flex min-h-6 min-w-6 items-center justify-center" aria-label={t("pricing.removeChosen", { name: labelOf(id) })} onClick={() => { onChange(chosen.filter((c) => c !== id)); }}>
                <X aria-hidden="true" className="size-3" />
              </button>
            </Badge>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Price lists (roadmap 5.2): the company's lists with their currency, basis, derivation, validity and who they are for; a row opens the list's prices. */
export function PriceListsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const can = useCan();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const lists = usePriceLists(companyId);
  const [creating, setCreating] = useState(false);

  const columns = useMemo<ColumnDef<PriceList, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("pricing.code"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("pricing.name"), size: 220 },
      { id: "currency", accessorKey: "currency", header: t("pricing.currency"), size: 90 },
      { id: "basis", accessorFn: (row) => (row.pricesIncludeTax ? t("pricing.inclusive") : t("pricing.exclusive")), header: t("pricing.taxBasis"), size: 120 },
      { id: "derived", accessorFn: (row) => (row.parentCode ? `${row.parentCode} ${row.parentAdjustmentPct !== null && Number(row.parentAdjustmentPct) >= 0 ? "+" : ""}${String(row.parentAdjustmentPct ?? "")}%` : ""), header: t("pricing.derivedFrom"), size: 150 },
      { id: "for", accessorFn: (row) => [...row.customers, ...row.customerGroups].map(refLabel).join(", "), header: t("pricing.forWhom"), size: 220 },
      { id: "validity", accessorFn: (row) => row.validFrom ?? "", header: t("pricing.validity"), size: 190, cell: ({ row }) => <Validity from={row.original.validFrom} to={row.original.validTo} /> },
      { id: "items", accessorKey: "items", header: t("pricing.prices"), size: 90, meta: { exportType: "number" as const } },
      {
        id: "status",
        accessorFn: (row) => (row.isDefault ? "default" : row.isActive ? "active" : "inactive"),
        header: t("common.status"),
        size: 130,
        cell: ({ row }) => (row.original.isDefault ? <Badge tone="accent">{t("pricing.default")}</Badge> : <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge>),
      },
    ],
    [t],
  );

  return (
    <>
      <PageHeader
        title={t("nav.priceLists")}
        description={t("pricing.priceListsDescription")}
        actions={
          can("pricing.price_list.manage") ? (
            <Button onClick={() => { setCreating(true); }} disabled={!companyId} data-testid="new-price-list">
              <Plus aria-hidden="true" />
              {t("pricing.newPriceList")}
            </Button>
          ) : null
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<PriceList>
        label="nav.priceLists"
        columns={columns}
        data={lists.data ?? []}
        rowKey={(row) => row.id}
        loading={lists.isPending && Boolean(companyId)}
        emptyTitle={t("pricing.emptyLists")}
        emptyDescription={t("pricing.emptyListsDescription")}
        onOpen={(row) => { void navigate({ to: "/sales/price-lists/$listId", params: { listId: row.id } }); }}
      />
      {creating ? (
        <PriceListDialog
          list={null}
          companyId={companyId}
          currency={company?.functionalCurrency ?? ""}
          onClose={() => { setCreating(false); }}
          onSaved={(saved) => { setCreating(false); void navigate({ to: "/sales/price-lists/$listId", params: { listId: saved.id } }); }}
        />
      ) : null}
    </>
  );
}
