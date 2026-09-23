import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2, X } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Amount, today, useCompanies } from "../accounting/shared";
import { Field, FormError, SelectField, TextField } from "../common";
import { ItemCostPanel } from "./ItemCostDialog";
import { Qty, type Item } from "./shared";

type ItemUom = components["schemas"]["ItemUomSummary"];
type ItemSupplier = components["schemas"]["ItemSupplierSummary"];
type WarehouseSettings = components["schemas"]["WarehouseSettingsSummary"];

const symbologies = ["EAN13", "EAN8", "UPCA", "CODE128", "CODE39", "QR", "DATAMATRIX", "OTHER"];
const cycleClasses = ["", "A", "B", "C"];

function useItemRefresh(itemId: string) {
  const queryClient = useQueryClient();
  return async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["item", itemId] });
    await queryClient.invalidateQueries({ queryKey: ["items"] });
  };
}

function num(value: string): string | null {
  return value.trim() ? value.trim() : null;
}

/** An item's units (with their factor to the base unit, weight and purchase/sales defaults) and the barcodes on each (roadmap 3.1). */
export function ItemUnitsEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const refresh = useItemRefresh(item.id);
  const [unit, setUnit] = useState<{ uom: string; numerator: string; denominator: string; weightKg: string; isPurchaseDefault: boolean; isSalesDefault: boolean; editing: boolean } | null>(null);
  const [barcode, setBarcode] = useState<{ barcode: string; uom: string; symbology: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const uoms = useQuery({ queryKey: ["uoms"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/uoms")) });
  const units = item.uoms ?? [];
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const saveUnit = useMutation({
    mutationFn: async () => {
      if (!unit) {
        return;
      }
      await api.PUT("/api/v1/items/{itemId}/uoms", { params: { path: { itemId: item.id } }, body: { uom: unit.uom, numerator: unit.numerator || "1", denominator: unit.denominator || "1", weightKg: num(unit.weightKg), isPurchaseDefault: unit.isPurchaseDefault, isSalesDefault: unit.isSalesDefault } }).then(unwrap);
    },
    onSuccess: async () => { setUnit(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const removeUnit = useMutation({
    mutationFn: async (u: ItemUom) => { await api.DELETE("/api/v1/items/{itemId}/uoms/{itemUomId}", { params: { path: { itemId: item.id, itemUomId: u.id } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const addBarcode = useMutation({
    mutationFn: async () => {
      if (!barcode) {
        return;
      }
      await api.POST("/api/v1/items/{itemId}/barcodes", { params: { path: { itemId: item.id } }, body: { barcode: barcode.barcode, uom: barcode.uom || null, symbology: barcode.symbology } }).then(unwrap);
    },
    onSuccess: async () => { setBarcode(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const removeBarcode = useMutation({
    mutationFn: async (barcodeId: string) => { await api.DELETE("/api/v1/items/{itemId}/barcodes/{barcodeId}", { params: { path: { itemId: item.id, barcodeId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });

  const submitUnit = (event: FormEvent): void => { event.preventDefault(); saveUnit.mutate(); };
  const submitBarcode = (event: FormEvent): void => { event.preventDefault(); addBarcode.mutate(); };
  const base = units.find((u) => u.isBase);

  return (
    <div className="flex flex-col gap-4" data-testid="item-units">
      <FormError message={problem?.message ?? null} />
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("inventory.items.unit")}</TableHead>
            <TableHead>{t("inventory.items.factor")}</TableHead>
            <TableHead className="text-end">{t("itemEditor.weightKg")}</TableHead>
            <TableHead>{t("itemEditor.defaults")}</TableHead>
            <TableHead>{t("inventory.items.barcodes")}</TableHead>
            <TableHead />
          </TableRow>
        </TableHeader>
        <TableBody>
          {units.map((u) => (
            <TableRow key={u.id} data-testid="unit-row">
              <TableCell>
                <span dir="ltr">{u.uomCode}</span> {u.isBase ? <Badge tone="accent">{t("inventory.items.base")}</Badge> : null}
              </TableCell>
              <TableCell>
                {u.isBase ? "—" : (
                  <span>
                    1 <span dir="ltr">{u.uomCode}</span> = <Qty value={Number(u.numerator) / Number(u.denominator)} /> <span dir="ltr">{base?.uomCode}</span>
                  </span>
                )}
              </TableCell>
              <TableNumberCell>{u.weightKg === null ? "" : <Qty value={u.weightKg} />}</TableNumberCell>
              <TableCell>
                {u.isPurchaseDefault ? <Badge className="me-1">{t("itemEditor.purchase")}</Badge> : null}
                {u.isSalesDefault ? <Badge>{t("itemEditor.sales")}</Badge> : null}
              </TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {u.barcodes.map((b) => (
                    <span key={b.id} className="inline-flex items-center gap-1 rounded-sm border border-border px-1.5 py-0.5 text-xs" dir="ltr" data-testid="barcode-chip">
                      {b.barcode}
                      <span className="text-fg-subtle">{b.symbology}</span>
                      <button type="button" className="text-fg-subtle hover:text-danger" aria-label={t("itemEditor.removeBarcode", { barcode: b.barcode })} onClick={() => { removeBarcode.mutate(b.id); }}>
                        <X className="size-3" aria-hidden="true" />
                      </button>
                    </span>
                  ))}
                </div>
              </TableCell>
              <TableCell>
                <div className="flex justify-end gap-1">
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setUnit({ uom: u.uomCode, numerator: String(u.numerator), denominator: String(u.denominator), weightKg: u.weightKg === null ? "" : String(u.weightKg), isPurchaseDefault: u.isPurchaseDefault, isSalesDefault: u.isSalesDefault, editing: true }); }}>
                    {t("common.edit")}
                  </Button>
                  {u.isBase ? null : (
                    <Button type="button" variant="ghost" size="icon" aria-label={t("itemEditor.removeUnit", { unit: u.uomCode })} onClick={() => { removeUnit.mutate(u); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  )}
                </div>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" size="sm" onClick={() => { setProblem(null); setBarcode(null); setUnit({ uom: "", numerator: "", denominator: "1", weightKg: "", isPurchaseDefault: false, isSalesDefault: false, editing: false }); }} data-testid="add-unit">
          <Plus aria-hidden="true" />
          {t("itemEditor.addUnit")}
        </Button>
        <Button type="button" variant="secondary" size="sm" onClick={() => { setProblem(null); setUnit(null); setBarcode({ barcode: "", uom: base?.uomCode ?? "", symbology: "EAN13" }); }} data-testid="add-barcode">
          <Plus aria-hidden="true" />
          {t("itemEditor.addBarcode")}
        </Button>
      </div>
      {unit ? (
        <form onSubmit={submitUnit} className="flex flex-col gap-3 rounded-md border border-border p-3">
          <h4 className="text-sm font-semibold">{unit.editing ? t("itemEditor.editUnit") : t("itemEditor.addUnit")}</h4>
          <p className="text-sm text-fg-muted">{t("itemEditor.factorHint", { base: base?.uomCode ?? "" })}</p>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("inventory.items.unit")} required error={problem?.fields.uom}>
              <SelectField value={unit.uom} onChange={(e) => { setUnit({ ...unit, uom: e.target.value }); }} disabled={unit.editing} required data-testid="unit-uom">
                <option value="">{t("itemEditor.chooseUnit")}</option>
                {(uoms.data ?? []).filter((u) => u.isActive).map((u) => (
                  <option key={u.id} value={u.code}>
                    {u.code} · {localized(u.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("itemEditor.numerator")} required error={problem?.fields.numerator}>
              <TextField inputMode="decimal" value={unit.numerator} onChange={(e) => { setUnit({ ...unit, numerator: e.target.value }); }} required dir="ltr" data-testid="unit-numerator" />
            </Field>
            <Field label={t("itemEditor.denominator")} error={problem?.fields.denominator}>
              <TextField inputMode="decimal" value={unit.denominator} onChange={(e) => { setUnit({ ...unit, denominator: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("itemEditor.weightKg")}>
              <TextField inputMode="decimal" value={unit.weightKg} onChange={(e) => { setUnit({ ...unit, weightKg: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
          <div className="flex flex-wrap gap-4 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={unit.isPurchaseDefault} onChange={(e) => { setUnit({ ...unit, isPurchaseDefault: e.target.checked }); }} />
              {t("itemEditor.purchaseDefault")}
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={unit.isSalesDefault} onChange={(e) => { setUnit({ ...unit, isSalesDefault: e.target.checked }); }} />
              {t("itemEditor.salesDefault")}
            </label>
          </div>
          <div className="flex gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => { setUnit(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" size="sm" loading={saveUnit.isPending} data-testid="save-unit">
              {t("itemEditor.saveUnit")}
            </Button>
          </div>
        </form>
      ) : null}
      {barcode ? (
        <form onSubmit={submitBarcode} className="flex flex-col gap-3 rounded-md border border-border p-3">
          <h4 className="text-sm font-semibold">{t("itemEditor.addBarcode")}</h4>
          <p className="text-sm text-fg-muted">{t("itemEditor.barcodeHint")}</p>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("itemEditor.barcode")} required error={problem?.fields.barcode}>
              <TextField value={barcode.barcode} onChange={(e) => { setBarcode({ ...barcode, barcode: e.target.value }); }} required dir="ltr" data-testid="barcode-value" />
            </Field>
            <Field label={t("inventory.items.unit")}>
              <SelectField value={barcode.uom} onChange={(e) => { setBarcode({ ...barcode, uom: e.target.value }); }} data-testid="barcode-uom">
                {units.map((u) => (
                  <option key={u.id} value={u.uomCode}>
                    {u.uomCode}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("itemEditor.symbology")} error={problem?.fields.symbology}>
              <SelectField value={barcode.symbology} onChange={(e) => { setBarcode({ ...barcode, symbology: e.target.value }); }} data-testid="barcode-symbology">
                {symbologies.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          <div className="flex gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => { setBarcode(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" size="sm" loading={addBarcode.isPending} data-testid="save-barcode">
              {t("itemEditor.saveBarcode")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/** The suppliers an item is bought from: their item code, unit, lead time, last price and the preferred one (roadmap 3.1, 4.1). */
export function ItemSuppliersEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const refresh = useItemRefresh(item.id);
  const [form, setForm] = useState<{ id: string | null; partnerId: string; supplierItemCode: string; uom: string; leadTimeDays: string; lastPrice: string; lastPriceCurrency: string; isPreferred: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const companies = useCompanies();
  const defaultCurrency = item.listPriceCurrency ?? companies.data?.[0]?.functionalCurrency ?? "";
  const partners = useQuery({ queryKey: ["partners", "suppliers", "all"], queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "supplier", limit: 500 } } })) });
  const partnerLabel = (id: string) => {
    const partner = partners.data?.items.find((p) => p.id === id);
    return partner ? `${partner.code} · ${localized(partner.legalName)}` : id;
  };
  const rows = item.suppliers ?? [];
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async () => {
      if (!form) {
        return;
      }
      const body = { partnerId: form.partnerId, supplierItemCode: form.supplierItemCode || null, uom: form.uom || null, leadTimeDays: form.leadTimeDays ? Number(form.leadTimeDays) : null, lastPrice: num(form.lastPrice), lastPriceCurrency: form.lastPriceCurrency || null, isPreferred: form.isPreferred };
      if (form.id) {
        await api.PUT("/api/v1/items/{itemId}/suppliers/{supplierId}", { params: { path: { itemId: item.id, supplierId: form.id } }, body }).then(unwrap);
      } else {
        await api.POST("/api/v1/items/{itemId}/suppliers", { params: { path: { itemId: item.id } }, body }).then(unwrap);
      }
    },
    onSuccess: async () => { setForm(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: async (s: ItemSupplier) => { await api.DELETE("/api/v1/items/{itemId}/suppliers/{supplierId}", { params: { path: { itemId: item.id, supplierId: s.id } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };

  return (
    <div className="flex flex-col gap-4" data-testid="item-suppliers">
      <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
      {rows.length === 0 ? <p className="text-sm text-fg-muted">{t("itemEditor.noSuppliers")}</p> : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("itemEditor.supplier")}</TableHead>
              <TableHead>{t("itemEditor.supplierItemCode")}</TableHead>
              <TableHead>{t("inventory.items.unit")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.leadTimeDays")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.lastPrice")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((s) => (
              <TableRow key={s.id} data-testid="supplier-row">
                <TableCell>
                  {partnerLabel(s.partnerId)} {s.isPreferred ? <Badge tone="accent">{t("inventory.items.preferred")}</Badge> : null}
                </TableCell>
                <TableCell dir="ltr">{s.supplierItemCode ?? ""}</TableCell>
                <TableCell dir="ltr">{s.uomCode ?? ""}</TableCell>
                <TableNumberCell>{s.leadTimeDays === null ? "" : String(s.leadTimeDays)}</TableNumberCell>
                <TableNumberCell>{s.lastPrice === null ? "" : <><Amount value={s.lastPrice} /> {s.lastPriceCurrency}</>}</TableNumberCell>
                <TableCell>
                  <div className="flex justify-end gap-1">
                    <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setForm({ id: s.id, partnerId: s.partnerId, supplierItemCode: s.supplierItemCode ?? "", uom: s.uomCode ?? "", leadTimeDays: s.leadTimeDays === null ? "" : String(s.leadTimeDays), lastPrice: s.lastPrice === null ? "" : String(s.lastPrice), lastPriceCurrency: s.lastPriceCurrency ?? "", isPreferred: s.isPreferred }); }}>
                      {t("common.edit")}
                    </Button>
                    <Button type="button" variant="ghost" size="icon" aria-label={t("itemEditor.removeSupplier")} onClick={() => { remove.mutate(s); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <div>
        <Button type="button" variant="secondary" size="sm" onClick={() => { setProblem(null); setForm({ id: null, partnerId: "", supplierItemCode: "", uom: "", leadTimeDays: "", lastPrice: "", lastPriceCurrency: defaultCurrency, isPreferred: rows.length === 0 }); }} data-testid="add-supplier">
          <Plus aria-hidden="true" />
          {t("itemEditor.addSupplier")}
        </Button>
      </div>
      {form ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3">
          <h4 className="text-sm font-semibold">{form.id ? t("itemEditor.editSupplier") : t("itemEditor.addSupplier")}</h4>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("itemEditor.supplier")} required error={problem?.fields.partnerId}>
              <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required disabled={Boolean(form.id)} data-testid="supplier-partner">
                <option value="">{t("itemEditor.chooseSupplier")}</option>
                {(partners.data?.items ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.code} · {localized(p.legalName)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("itemEditor.supplierItemCode")}>
              <TextField value={form.supplierItemCode} onChange={(e) => { setForm({ ...form, supplierItemCode: e.target.value }); }} dir="ltr" data-testid="supplier-item-code" />
            </Field>
            <Field label={t("inventory.items.unit")}>
              <SelectField value={form.uom} onChange={(e) => { setForm({ ...form, uom: e.target.value }); }}>
                <option value="">{t("itemEditor.baseUnit")}</option>
                {(item.uoms ?? []).map((u) => (
                  <option key={u.id} value={u.uomCode}>
                    {u.uomCode}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("itemEditor.leadTimeDays")} error={problem?.fields.leadTimeDays}>
              <TextField inputMode="numeric" value={form.leadTimeDays} onChange={(e) => { setForm({ ...form, leadTimeDays: e.target.value }); }} dir="ltr" data-testid="supplier-lead-time" />
            </Field>
            <Field label={t("itemEditor.lastPrice")} error={problem?.fields.lastPrice}>
              <TextField inputMode="decimal" value={form.lastPrice} onChange={(e) => { setForm({ ...form, lastPrice: e.target.value }); }} dir="ltr" data-testid="supplier-price" />
            </Field>
            <Field label={t("itemEditor.currency")} error={problem?.fields.lastPriceCurrency}>
              <TextField value={form.lastPriceCurrency} onChange={(e) => { setForm({ ...form, lastPriceCurrency: e.target.value.toUpperCase() }); }} maxLength={3} dir="ltr" />
            </Field>
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={form.isPreferred} onChange={(e) => { setForm({ ...form, isPreferred: e.target.checked }); }} />
            {t("itemEditor.preferredHint")}
          </label>
          <div className="flex gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => { setForm(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" size="sm" loading={save.isPending} data-testid="save-supplier-link">
              {t("itemEditor.saveSupplier")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/** Per-warehouse planning parameters the replenishment planner reads (roadmap 3.7): reorder point, min/max, safety stock, lead time, cycle-count class. */
export function ItemPlanningEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useState("");
  const [form, setForm] = useState<{ warehouseId: string; reorderPoint: string; minQty: string; maxQty: string; safetyStock: string; leadTimeDays: string; cycleCountClass: string; editing: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const activeCompany = companyId || (companies.data?.[0]?.id ?? "");
  const settings = useQuery({
    queryKey: ["item-warehouse-settings", item.id],
    queryFn: async () => unwrap(await api.GET("/api/v1/items/{itemId}/warehouse-settings", { params: { path: { itemId: item.id } } })),
  });
  const warehouses = useQuery({
    queryKey: ["warehouses", activeCompany],
    enabled: Boolean(activeCompany),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId: activeCompany } } })),
  });
  const inCompany = (warehouses.data ?? []).filter((w) => w.kind !== "in_transit");
  const rows = (settings.data ?? []).filter((s) => inCompany.some((w) => w.id === s.warehouseId));
  const code = (warehouseId: string) => inCompany.find((w) => w.id === warehouseId)?.code ?? "";
  const refresh = async (): Promise<void> => { await queryClient.invalidateQueries({ queryKey: ["item-warehouse-settings", item.id] }); };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async () => {
      if (!form) {
        return;
      }
      await api.PUT("/api/v1/items/{itemId}/warehouse-settings/{warehouseId}", { params: { path: { itemId: item.id, warehouseId: form.warehouseId } }, body: { reorderPoint: num(form.reorderPoint), minQty: num(form.minQty), maxQty: num(form.maxQty), safetyStock: num(form.safetyStock), leadTimeDays: form.leadTimeDays ? Number(form.leadTimeDays) : null, cycleCountClass: form.cycleCountClass || null } }).then(unwrap);
    },
    onSuccess: async () => { setForm(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: async (s: WarehouseSettings) => { await api.DELETE("/api/v1/items/{itemId}/warehouse-settings/{warehouseId}", { params: { path: { itemId: item.id, warehouseId: s.warehouseId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const edit = (s: WarehouseSettings): void => {
    const text = (v: number | string | null) => (v === null ? "" : String(v));
    setProblem(null);
    setForm({ warehouseId: s.warehouseId, reorderPoint: text(s.reorderPoint), minQty: text(s.minQty), maxQty: text(s.maxQty), safetyStock: text(s.safetyStock), leadTimeDays: text(s.leadTimeDays), cycleCountClass: s.cycleCountClass ?? "", editing: true });
  };

  return (
    <div className="flex flex-col gap-4" data-testid="item-planning">
      <p className="text-sm text-fg-muted">{t("itemEditor.planningHint")}</p>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t("itemEditor.company")}>
          <SelectField value={activeCompany} onChange={(e) => { setCompanyId(e.target.value); setForm(null); }} data-testid="planning-company">
            {(companies.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.code} · {localized(c.legalName)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
      {rows.length === 0 ? <p className="text-sm text-fg-muted">{t("itemEditor.noPlanning")}</p> : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("inventory.warehouse")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.reorderPoint")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.minQty")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.maxQty")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.safetyStock")}</TableHead>
              <TableHead className="text-end">{t("itemEditor.leadTimeDays")}</TableHead>
              <TableHead>{t("itemEditor.cycleClass")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((s) => (
              <TableRow key={s.warehouseId} data-testid="planning-row">
                <TableCell dir="ltr">{code(s.warehouseId)}</TableCell>
                <TableNumberCell>{s.reorderPoint === null ? "" : <Qty value={s.reorderPoint} />}</TableNumberCell>
                <TableNumberCell>{s.minQty === null ? "" : <Qty value={s.minQty} />}</TableNumberCell>
                <TableNumberCell>{s.maxQty === null ? "" : <Qty value={s.maxQty} />}</TableNumberCell>
                <TableNumberCell>{s.safetyStock === null ? "" : <Qty value={s.safetyStock} />}</TableNumberCell>
                <TableNumberCell>{s.leadTimeDays === null ? "" : String(s.leadTimeDays)}</TableNumberCell>
                <TableCell>{s.cycleCountClass ?? ""}</TableCell>
                <TableCell>
                  <div className="flex justify-end gap-1">
                    <Button type="button" variant="ghost" size="sm" onClick={() => { edit(s); }}>
                      {t("common.edit")}
                    </Button>
                    <Button type="button" variant="ghost" size="icon" aria-label={t("itemEditor.removePlanning", { warehouse: code(s.warehouseId) })} onClick={() => { remove.mutate(s); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <div>
        <Button type="button" variant="secondary" size="sm" disabled={inCompany.length === 0} onClick={() => { setProblem(null); setForm({ warehouseId: inCompany.find((w) => !rows.some((r) => r.warehouseId === w.id))?.id ?? "", reorderPoint: "", minQty: "", maxQty: "", safetyStock: "", leadTimeDays: "", cycleCountClass: "", editing: false }); }} data-testid="add-planning">
          <Plus aria-hidden="true" />
          {t("itemEditor.addPlanning")}
        </Button>
      </div>
      {form ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3">
          <h4 className="text-sm font-semibold">{form.editing ? t("itemEditor.editPlanning") : t("itemEditor.addPlanning")}</h4>
          <div className="grid gap-3 sm:grid-cols-4">
            <Field label={t("inventory.warehouse")} required>
              <SelectField value={form.warehouseId} onChange={(e) => { setForm({ ...form, warehouseId: e.target.value }); }} disabled={form.editing} required data-testid="planning-warehouse">
                {inCompany.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.code} · {localized(w.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("itemEditor.reorderPoint")} error={problem?.fields.reorderPoint}>
              <TextField inputMode="decimal" value={form.reorderPoint} onChange={(e) => { setForm({ ...form, reorderPoint: e.target.value }); }} dir="ltr" data-testid="planning-reorder" />
            </Field>
            <Field label={t("itemEditor.minQty")} error={problem?.fields.minQty}>
              <TextField inputMode="decimal" value={form.minQty} onChange={(e) => { setForm({ ...form, minQty: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("itemEditor.maxQty")} error={problem?.fields.maxQty}>
              <TextField inputMode="decimal" value={form.maxQty} onChange={(e) => { setForm({ ...form, maxQty: e.target.value }); }} dir="ltr" data-testid="planning-max" />
            </Field>
            <Field label={t("itemEditor.safetyStock")} error={problem?.fields.safetyStock}>
              <TextField inputMode="decimal" value={form.safetyStock} onChange={(e) => { setForm({ ...form, safetyStock: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("itemEditor.leadTimeDays")} error={problem?.fields.leadTimeDays}>
              <TextField inputMode="numeric" value={form.leadTimeDays} onChange={(e) => { setForm({ ...form, leadTimeDays: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("itemEditor.cycleClass")}>
              <SelectField value={form.cycleCountClass} onChange={(e) => { setForm({ ...form, cycleCountClass: e.target.value }); }}>
                {cycleClasses.map((c) => (
                  <option key={c} value={c}>
                    {c || t("itemEditor.noClass")}
                  </option>
                ))}
              </SelectField>
            </Field>
          </div>
          <div className="flex gap-2">
            <Button type="button" variant="secondary" size="sm" onClick={() => { setForm(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" size="sm" loading={save.isPending} data-testid="save-planning">
              {t("itemEditor.savePlanning")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/**
 * How the item is costed in each company (roadmap 3.3): an override of the company's costing method (only while the
 * item has no stock there, since a change would re-value history), a default warehouse, the negative-stock exception,
 * and the cost panel with the standard-cost versions and a new standard.
 */
export function ItemCostingEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const companies = useCompanies();
  const [companyId, setCompanyId] = useState("");
  const [form, setForm] = useState<{ costingMethodOverride: string; defaultWarehouseId: string; allowNegativeStock: string } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const activeCompany = companyId || (companies.data?.[0]?.id ?? "");
  const settings = useQuery({
    queryKey: ["item-company-settings", item.id],
    queryFn: async () => unwrap(await api.GET("/api/v1/items/{itemId}/company-settings", { params: { path: { itemId: item.id } } })),
  });
  const warehouses = useQuery({
    queryKey: ["warehouses", activeCompany],
    enabled: Boolean(activeCompany),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses", { params: { query: { companyId: activeCompany } } })),
  });
  const cost = useQuery({
    queryKey: ["item-cost", activeCompany, item.id, null, today()],
    enabled: Boolean(activeCompany),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/item-cost", { params: { query: { companyId: activeCompany, itemId: item.id, asOf: today() } } })),
  });
  const current = settings.data?.find((s) => s.companyId === activeCompany);
  const hasStock = Number(cost.data?.quantity ?? 0) !== 0 || Number(cost.data?.value ?? 0) !== 0;
  const values = form ?? { costingMethodOverride: current?.costingMethodOverride ?? "", defaultWarehouseId: current?.defaultWarehouseId ?? "", allowNegativeStock: current?.allowNegativeStock === null || current?.allowNegativeStock === undefined ? "" : String(current.allowNegativeStock) };
  const save = useMutation({
    mutationFn: async () => {
      await api.PUT("/api/v1/items/{itemId}/company-settings/{companyId}", { params: { path: { itemId: item.id, companyId: activeCompany } }, body: { costingMethodOverride: values.costingMethodOverride || null, standardCost: current?.standardCost ?? null, itemPostingGroupOverride: current?.itemPostingGroupOverride ?? null, defaultWarehouseId: values.defaultWarehouseId || null, allowNegativeStock: values.allowNegativeStock === "" ? null : values.allowNegativeStock === "true" } }).then(unwrap);
    },
    onSuccess: async () => {
      setForm(null);
      setProblem(null);
      await queryClient.invalidateQueries({ queryKey: ["item-company-settings", item.id] });
      await queryClient.invalidateQueries({ queryKey: ["item-cost"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };

  return (
    <div className="flex flex-col gap-4" data-testid="item-costing">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t("itemEditor.company")}>
          <SelectField value={activeCompany} onChange={(e) => { setCompanyId(e.target.value); setForm(null); }} data-testid="costing-company">
            {(companies.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.code} · {localized(c.legalName)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3">
        <h4 className="text-sm font-semibold">{t("itemEditor.companySettings")}</h4>
        <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label={t("itemEditor.costingOverride")} description={hasStock ? t("itemEditor.costingLocked") : t("itemEditor.costingOverrideHint")} error={problem?.fields.costingMethodOverride}>
            <SelectField value={values.costingMethodOverride} onChange={(e) => { setForm({ ...values, costingMethodOverride: e.target.value }); }} disabled={hasStock} data-testid="costing-override">
              <option value="">{t("itemEditor.companyMethod")}</option>
              {["average", "fifo", "standard"].map((m) => (
                <option key={m} value={m}>
                  {t(`itemCost.methods.${m}`)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("itemEditor.defaultWarehouse")}>
            <SelectField value={values.defaultWarehouseId} onChange={(e) => { setForm({ ...values, defaultWarehouseId: e.target.value }); }}>
              <option value="">—</option>
              {(warehouses.data ?? []).filter((w) => w.kind !== "in_transit").map((w) => (
                <option key={w.id} value={w.id}>
                  {w.code} · {localized(w.name)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("itemEditor.negativeStock")}>
            <SelectField value={values.allowNegativeStock} onChange={(e) => { setForm({ ...values, allowNegativeStock: e.target.value }); }}>
              <option value="">{t("itemEditor.companyPolicy")}</option>
              <option value="true">{t("itemEditor.allowNegative")}</option>
              <option value="false">{t("itemEditor.refuseNegative")}</option>
            </SelectField>
          </Field>
        </div>
        <div>
          <Button type="submit" size="sm" loading={save.isPending} data-testid="save-costing-settings">
            {t("itemEditor.saveSettings")}
          </Button>
        </div>
      </form>
      {activeCompany ? <ItemCostPanel companyId={activeCompany} itemId={item.id} warehouseId={null} asOf={today()} /> : null}
    </div>
  );
}
