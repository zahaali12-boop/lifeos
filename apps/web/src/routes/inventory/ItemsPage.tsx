import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input } from "@quicker/ui";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { rememberRecent } from "../../shell/CommandPalette";
import { Amount } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { ItemCostingEditor, ItemPlanningEditor, ItemSuppliersEditor, ItemUnitsEditor } from "./ItemEditors";
import { ItemAttributesEditor, ItemBomEditor, ItemImage, ItemSubstitutesEditor, ItemVariantsEditor } from "./ItemStructure";
import { DocStatus, Tabs, type Item } from "./shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

interface ItemForm {
  code: string;
  nameEn: string;
  nameAr: string;
  type: string;
  baseUom: string;
  categoryCode: string;
  brandCode: string;
  tracking: string;
  expiryRequired: boolean;
  shelfLifeDays: string;
  fefo: boolean;
  listPrice: string;
  listPriceCurrency: string;
  isActive: boolean;
}

const types = ["stock", "non_stock", "service", "kit", "assembly"];
const trackings = ["none", "lot", "serial", "lot_and_serial"];
const empty: ItemForm = { code: "", nameEn: "", nameAr: "", type: "stock", baseUom: "PCS", categoryCode: "", brandCode: "", tracking: "none", expiryRequired: false, shelfLifeDays: "", fefo: false, listPrice: "", listPriceCurrency: "", isActive: true };

function toForm(item: Item): ItemForm {
  return {
    code: item.code,
    nameEn: item.name.en ?? "",
    nameAr: item.name.ar ?? "",
    type: item.type,
    baseUom: item.baseUom,
    categoryCode: item.categoryCode ?? "",
    brandCode: item.brandCode ?? "",
    tracking: item.tracking,
    expiryRequired: item.expiryRequired,
    shelfLifeDays: item.shelfLifeDays === null ? "" : String(item.shelfLifeDays),
    fefo: item.fefo,
    listPrice: item.listPrice === null ? "" : String(item.listPrice),
    listPriceCurrency: item.listPriceCurrency ?? "",
    isActive: item.isActive,
  };
}

/** The item master (roadmap 3.1): search, create and edit items, and see each item's units, barcodes, variants and suppliers. */
export function ItemsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search = useSearch({ strict: false });
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<{ id: string | null; form: ItemForm } | null>(null);
  const [masterOpen, setMasterOpen] = useState(false);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;
  const [detailTab, setDetailTab] = useState("units");

  const items = useInfiniteQuery({
    queryKey: ["items", query],
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/items", { params: { query: { limit: 100, ...(query.trim() ? { q: query.trim() } : {}), ...(pageParam ? { cursor: pageParam } : {}) } } })),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const item = useQuery({
    queryKey: ["item", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/items/{itemId}", { params: { path: { itemId: openId ?? "" }, query: { expand: "uoms,variants,suppliers,substitutes" } } })),
  });
  const uoms = useQuery({ queryKey: ["uoms"], queryFn: async () => unwrap(await api.GET("/api/v1/organization/uoms")) });
  const categories = useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
  const brands = useQuery({ queryKey: ["item-brands"], queryFn: async () => unwrap(await api.GET("/api/v1/items/brands")) });

  const open = (id: string | null): void => { void navigate({ to: "/inventory/items", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: ItemForm }) => {
      const f = input.form;
      const body = {
        code: f.code,
        name: { en: f.nameEn, ...(f.nameAr ? { ar: f.nameAr } : {}) },
        type: f.type,
        baseUom: f.baseUom,
        categoryCode: f.categoryCode || null,
        brandCode: f.brandCode || null,
        tracking: f.tracking,
        expiryRequired: f.expiryRequired,
        shelfLifeDays: f.shelfLifeDays ? Number(f.shelfLifeDays) : null,
        fefo: f.fefo,
        listPrice: f.listPrice || null,
        listPriceCurrency: f.listPriceCurrency || null,
        isActive: f.isActive,
      };
      return input.id
        ? unwrap(await api.PUT("/api/v1/items/{itemId}", { params: { path: { itemId: input.id } }, body }))
        : unwrap(await api.POST("/api/v1/items", { body }));
    },
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      rememberRecent({ to: `/inventory/items?open=${saved.id}`, label: `${saved.code} · ${localized(saved.name)}` });
      await queryClient.invalidateQueries({ queryKey: ["items"] });
      await queryClient.invalidateQueries({ queryKey: ["item", saved.id] });
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Item, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("inventory.items.code"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("inventory.items.name"), size: 300 },
      { id: "type", accessorKey: "type", header: t("inventory.items.type"), size: 100, cell: ({ row }) => t(`inventory.items.types.${row.original.type}`, { defaultValue: row.original.type }) },
      { id: "baseUom", accessorKey: "baseUom", header: t("inventory.items.baseUom"), size: 90 },
      { id: "tracking", accessorKey: "tracking", header: t("inventory.items.tracking"), size: 130, cell: ({ row }) => t(`inventory.items.trackings.${row.original.tracking}`, { defaultValue: row.original.tracking }) },
      { id: "category", accessorKey: "categoryCode", header: t("inventory.items.category"), size: 110 },
      { id: "brand", accessorKey: "brandCode", header: t("inventory.items.brand"), size: 110 },
      { id: "isActive", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
      { id: "updatedAt", accessorKey: "updatedAt", header: t("common.updated"), size: 130, cell: ({ row }) => formatDate(row.original.updatedAt) },
    ],
    [t],
  );

  const rows = items.data?.pages.flatMap((page) => page.items) ?? [];
  const detail = item.data;
  const form = editing?.form;
  const isEdit = editing?.id != null;
  const setForm = (patch: Partial<ItemForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.items")}
        description={t("inventory.items.description")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { setMasterOpen(true); }} data-testid="master-data">
              {t("inventory.items.masterData")}
            </Button>
            <Button onClick={() => { setProblem(null); setEditing({ id: null, form: empty }); }} data-testid="new-item">
              <Plus aria-hidden="true" />
              {t("inventory.items.new")}
            </Button>
          </>
        }
      />
      <DataGrid<Item>
        label="nav.items"
        columns={columns}
        data={rows}
        rowKey={(row) => row.id}
        entityType="item"
        loading={items.isPending}
        onOpen={(row) => { open(row.id); }}
        emptyTitle={t("inventory.items.emptyTitle")}
        emptyDescription={t("inventory.items.emptyDescription")}
        toolbar={<Input type="search" placeholder={t("inventory.items.searchPlaceholder")} value={query} onChange={(e) => { setQuery(e.target.value); }} className="w-64" aria-label={t("common.search")} data-testid="item-search" />}
      />
      {items.hasNextPage ? (
        <Button variant="secondary" className="mt-3" onClick={() => { void items.fetchNextPage(); }} loading={items.isFetchingNextPage}>
          {t("common.loadMore")}
        </Button>
      ) : null}

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-h-[90vh] max-w-5xl overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.code} · ${localized(detail.name)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="item-detail">
              <ItemImage item={detail} />
              <div className="flex flex-wrap gap-2 text-sm">
                <Badge tone={detail.isActive ? "success" : "neutral"}>{detail.isActive ? t("common.active") : t("common.inactive")}</Badge>
                <Badge>{t(`inventory.items.types.${detail.type}`, { defaultValue: detail.type })}</Badge>
                <Badge>{t(`inventory.items.trackings.${detail.tracking}`, { defaultValue: detail.tracking })}</Badge>
                {detail.fefo ? <Badge tone="info">FEFO</Badge> : null}
                {detail.categoryCode ? <span className="text-fg-muted">{t("inventory.items.category")}: {detail.categoryCode}</span> : null}
                {detail.brandCode ? <span className="text-fg-muted">{t("inventory.items.brand")}: {detail.brandCode}</span> : null}
                {detail.listPrice !== null ? (
                  <span className="text-fg-muted">
                    {t("inventory.items.listPrice")}: <Amount value={detail.listPrice} /> {detail.listPriceCurrency}
                  </span>
                ) : null}
              </div>
              <Tabs
                value={detailTab}
                onChange={setDetailTab}
                tabs={[
                  { id: "units", label: t("itemEditor.tabs.units"), testId: "item-tab-units" },
                  { id: "suppliers", label: t("itemEditor.tabs.suppliers"), testId: "item-tab-suppliers" },
                  { id: "planning", label: t("itemEditor.tabs.planning"), testId: "item-tab-planning" },
                  { id: "costing", label: t("itemEditor.tabs.costing"), testId: "item-tab-costing" },
                  ...(detail.type === "kit" || detail.type === "assembly" ? [{ id: "bom", label: t("itemStructure.tabs.bom"), testId: "item-tab-bom" }] : []),
                  { id: "variants", label: t("inventory.items.variants"), testId: "item-tab-variants" },
                  { id: "substitutes", label: t("itemStructure.tabs.substitutes"), testId: "item-tab-substitutes" },
                  { id: "discussion", label: t("comments.tab"), testId: "item-tab-discussion" },
                  { id: "history", label: t("history.tab"), testId: "item-tab-history" },
                ]}
              />
              {detailTab === "discussion" ? <RecordDiscussion entityType="item" entityId={detail.id} /> : null}
              {detailTab === "history" ? <RecordHistory entityType="item" entityId={detail.id} /> : null}
              {detailTab === "units" ? <ItemUnitsEditor item={detail} /> : null}
              {detailTab === "suppliers" ? <ItemSuppliersEditor item={detail} /> : null}
              {detailTab === "planning" ? <ItemPlanningEditor item={detail} /> : null}
              {detailTab === "costing" ? <ItemCostingEditor item={detail} /> : null}
              {detailTab === "bom" ? <ItemBomEditor item={detail} /> : null}
              {detailTab === "variants" ? <ItemVariantsEditor item={detail} /> : null}
              {detailTab === "substitutes" ? <ItemSubstitutesEditor item={detail} /> : null}
              <DialogFooter>
                <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }} data-testid="edit-item">
                  {t("inventory.items.edit")}
                </Button>
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{isEdit ? t("inventory.items.edit") : t("inventory.items.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("inventory.items.code")} required error={problem?.fields.code}>
                  <TextField value={form.code} onChange={(e) => { setForm({ code: e.target.value.toUpperCase() }); }} required dir="ltr" disabled={isEdit} data-testid="item-code" />
                </Field>
                <Field label={t("inventory.items.nameEn")} required error={problem?.fields.name}>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ nameEn: e.target.value }); }} required data-testid="item-name-en" />
                </Field>
                <Field label={t("inventory.items.nameAr")}>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ nameAr: e.target.value }); }} dir="rtl" data-testid="item-name-ar" />
                </Field>
                <Field label={t("inventory.items.type")}>
                  <SelectField value={form.type} onChange={(e) => { setForm({ type: e.target.value }); }}>
                    {types.map((k) => (
                      <option key={k} value={k}>
                        {t(`inventory.items.types.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.baseUom")} required error={problem?.fields.baseUom}>
                  <SelectField value={form.baseUom} onChange={(e) => { setForm({ baseUom: e.target.value }); }} disabled={isEdit} data-testid="item-base-uom">
                    {(uoms.data ?? []).map((u) => (
                      <option key={u.id} value={u.code}>
                        {u.code} · {localized(u.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.tracking")}>
                  <SelectField value={form.tracking} onChange={(e) => { setForm({ tracking: e.target.value }); }} data-testid="item-tracking">
                    {trackings.map((k) => (
                      <option key={k} value={k}>
                        {t(`inventory.items.trackings.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.category")} error={problem?.fields.category}>
                  <SelectField value={form.categoryCode} onChange={(e) => { setForm({ categoryCode: e.target.value }); }}>
                    <option value="">—</option>
                    {(categories.data ?? []).map((c) => (
                      <option key={c.id} value={c.code}>
                        {c.code} · {localized(c.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.brand")} error={problem?.fields.brand}>
                  <SelectField value={form.brandCode} onChange={(e) => { setForm({ brandCode: e.target.value }); }}>
                    <option value="">—</option>
                    {(brands.data ?? []).map((b) => (
                      <option key={b.id} value={b.code}>
                        {b.code} · {localized(b.name)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.items.shelfLifeDays")} error={problem?.fields.shelfLifeDays}>
                  <TextField inputMode="numeric" value={form.shelfLifeDays} onChange={(e) => { setForm({ shelfLifeDays: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("inventory.items.listPrice")} error={problem?.fields.listPrice}>
                  <TextField inputMode="decimal" value={form.listPrice} onChange={(e) => { setForm({ listPrice: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("inventory.items.listPriceCurrency")}>
                  <TextField value={form.listPriceCurrency} onChange={(e) => { setForm({ listPriceCurrency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                </Field>
                <div className="flex flex-col gap-2 self-end pb-2 text-sm">
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={form.expiryRequired} onChange={(e) => { setForm({ expiryRequired: e.target.checked }); }} />
                    {t("inventory.items.expiryRequired")}
                  </label>
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={form.fefo} onChange={(e) => { setForm({ fefo: e.target.checked }); }} />
                    {t("inventory.items.fefo")}
                  </label>
                  <label className="flex items-center gap-2">
                    <input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ isActive: e.target.checked }); }} />
                    {t("common.active")}
                  </label>
                </div>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-item">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <MasterDataDialog open={masterOpen} onOpenChange={setMasterOpen} />
    </>
  );
}

/** Categories and brands: the two lists an item picks from, created here so the master stays configuration, not code. */
function MasterDataDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [category, setCategory] = useState({ code: "", en: "", ar: "", parentCode: "" });
  const [brand, setBrand] = useState({ code: "", en: "", ar: "" });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const categories = useQuery({ queryKey: ["item-categories"], queryFn: async () => unwrap(await api.GET("/api/v1/items/categories")) });
  const brands = useQuery({ queryKey: ["item-brands"], queryFn: async () => unwrap(await api.GET("/api/v1/items/brands")) });
  const addCategory = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/items/categories", { body: { code: category.code, name: { en: category.en, ...(category.ar ? { ar: category.ar } : {}) }, parentCode: category.parentCode || null, isActive: true } })),
    onSuccess: async () => { setCategory({ code: "", en: "", ar: "", parentCode: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-categories"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const addBrand = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/items/brands", { body: { code: brand.code, name: { en: brand.en, ...(brand.ar ? { ar: brand.ar } : {}) }, isActive: true } })),
    onSuccess: async () => { setBrand({ code: "", en: "", ar: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-brands"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold">{t("inventory.items.masterData")}</DialogTitle>
        </DialogHeader>
        <FormError message={problem?.message ?? null} />
        <div className="grid gap-6 sm:grid-cols-2">
          <form className="flex flex-col gap-3" onSubmit={(e) => { e.preventDefault(); addCategory.mutate(); }}>
            <h3 className="text-sm font-semibold">{t("inventory.items.categories")}</h3>
            <ul className="max-h-40 overflow-y-auto text-sm">
              {(categories.data ?? []).map((c) => (
                <li key={c.id}>
                  <span dir="ltr">{c.code}</span> · {localized(c.name)} {c.parentCode ? <span className="text-fg-subtle">({c.parentCode})</span> : null}
                </li>
              ))}
            </ul>
            <Field label={t("inventory.items.code")} required>
              <TextField value={category.code} onChange={(e) => { setCategory({ ...category, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="category-code" />
            </Field>
            <Field label={t("inventory.items.nameEn")} required>
              <TextField value={category.en} onChange={(e) => { setCategory({ ...category, en: e.target.value }); }} required data-testid="category-name-en" />
            </Field>
            <Field label={t("inventory.items.nameAr")}>
              <TextField value={category.ar} onChange={(e) => { setCategory({ ...category, ar: e.target.value }); }} dir="rtl" />
            </Field>
            <Field label={t("inventory.items.parentCategory")}>
              <SelectField value={category.parentCode} onChange={(e) => { setCategory({ ...category, parentCode: e.target.value }); }}>
                <option value="">—</option>
                {(categories.data ?? []).map((c) => (
                  <option key={c.id} value={c.code}>
                    {c.code}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Button type="submit" variant="secondary" loading={addCategory.isPending} data-testid="save-category">
              {t("inventory.items.addCategory")}
            </Button>
          </form>
          <form className="flex flex-col gap-3" onSubmit={(e) => { e.preventDefault(); addBrand.mutate(); }}>
            <h3 className="text-sm font-semibold">{t("inventory.items.brands")}</h3>
            <ul className="max-h-40 overflow-y-auto text-sm">
              {(brands.data ?? []).map((b) => (
                <li key={b.id}>
                  <span dir="ltr">{b.code}</span> · {localized(b.name)}
                </li>
              ))}
            </ul>
            <Field label={t("inventory.items.code")} required>
              <TextField value={brand.code} onChange={(e) => { setBrand({ ...brand, code: e.target.value.toUpperCase() }); }} required dir="ltr" />
            </Field>
            <Field label={t("inventory.items.nameEn")} required>
              <TextField value={brand.en} onChange={(e) => { setBrand({ ...brand, en: e.target.value }); }} required />
            </Field>
            <Field label={t("inventory.items.nameAr")}>
              <TextField value={brand.ar} onChange={(e) => { setBrand({ ...brand, ar: e.target.value }); }} dir="rtl" />
            </Field>
            <Button type="submit" variant="secondary" loading={addBrand.isPending}>
              {t("inventory.items.addBrand")}
            </Button>
          </form>
        </div>
        <div className="mt-6 border-t border-border pt-4">
          <ItemAttributesEditor />
        </div>
      </DialogContent>
    </Dialog>
  );
}

export { DocStatus as ItemStatus };
