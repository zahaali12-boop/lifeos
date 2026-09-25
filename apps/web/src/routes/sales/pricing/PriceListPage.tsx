import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { Pencil, Percent, Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../../api";
import { formatNumber, localized } from "../../../lib/format";
import { useCan } from "../../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../../common";
import { ItemCodeField } from "../../inventory/ItemCodeField";
import { KeyValues, findItemByCode, type Item } from "../../inventory/shared";
import { PriceListDialog } from "./PriceListsPage";
import { Validity, price, refLabel, type PriceListItem } from "./shared";

interface EntryForm {
  id: string | null;
  itemCode: string;
  item: Item | null;
  variantId: string;
  uomId: string;
  minQuantity: string;
  price: string;
  validFrom: string;
  validTo: string;
}

/** One price list: what it is, who it is for, and its prices by item, unit, quantity break and date. */
export function PriceListPage() {
  const { t } = useTranslation();
  const { listId } = useParams({ strict: false });
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const can = useCan();
  const mayManage = can("pricing.price_list.manage");
  const [editing, setEditing] = useState(false);
  const [entry, setEntry] = useState<EntryForm | null>(null);
  const [adjusting, setAdjusting] = useState<{ pct: string; increment: string; mode: string } | null>(null);
  const [q, setQ] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const id = listId ?? "";

  const list = useQuery({ queryKey: ["price-list", id], enabled: Boolean(id), queryFn: async () => unwrap(await api.GET("/api/v1/pricing/price-lists/{listId}", { params: { path: { listId: id } } })) });
  const entries = useQuery({ queryKey: ["price-list-items", id, q], enabled: Boolean(id), queryFn: async () => unwrap(await api.GET("/api/v1/pricing/price-lists/{listId}/items", { params: { path: { listId: id }, query: q ? { q } : {} } })) });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["price-list", id] });
    await queryClient.invalidateQueries({ queryKey: ["price-list-items", id] });
    await queryClient.invalidateQueries({ queryKey: ["price-lists"] });
  };

  const saveEntry = useMutation({
    mutationFn: async (form: EntryForm) => {
      const item = form.item?.code === form.itemCode ? form.item : await findItemByCode(form.itemCode);
      if (!item) {
        throw new Error(t("pricing.itemUnknown", { code: form.itemCode }));
      }
      const body = { itemId: item.id, variantId: form.variantId || null, uomId: form.uomId || null, minQuantity: Number(form.minQuantity || "0"), price: Number(form.price), validFrom: form.validFrom || null, validTo: form.validTo || null };
      return form.id
        ? unwrap(await api.PUT("/api/v1/pricing/price-lists/{listId}/items/{entryId}", { params: { path: { listId: id, entryId: form.id } }, body }))
        : unwrap(await api.POST("/api/v1/pricing/price-lists/{listId}/items", { params: { path: { listId: id } }, body }));
    },
    onSuccess: async () => { setEntry(null); setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const removeEntry = useMutation({
    mutationFn: async (entryId: string) => { unwrap(await api.DELETE("/api/v1/pricing/price-lists/{listId}/items/{entryId}", { params: { path: { listId: id, entryId } } })); },
    onSuccess: refresh,
  });
  const adjust = useMutation({
    mutationFn: async (form: { pct: string; increment: string; mode: string }) => unwrap(await api.POST("/api/v1/pricing/price-lists/{listId}/adjust", { params: { path: { listId: id } }, body: { pct: Number(form.pct), roundingIncrement: form.increment ? Number(form.increment) : null, roundingMode: form.mode } })),
    onSuccess: async () => { setAdjusting(null); setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const removeList = useMutation({
    mutationFn: async () => { unwrap(await api.DELETE("/api/v1/pricing/price-lists/{listId}", { params: { path: { listId: id } } })); },
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ["price-lists"] }); void navigate({ to: "/sales/price-lists" }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const l = list.data;
  if (!l) {
    return <PageHeader title={t("nav.priceLists")} />;
  }

  const openEntry = async (e: PriceListItem | null): Promise<void> => {
    setProblem(null);
    if (!e) {
      setEntry({ id: null, itemCode: "", item: null, variantId: "", uomId: "", minQuantity: "0", price: "", validFrom: "", validTo: "" });
      return;
    }
    const item = await findItemByCode(e.itemCode);
    setEntry({ id: e.id, itemCode: e.itemCode, item, variantId: e.variantId ?? "", uomId: e.uomId, minQuantity: String(e.minQuantity), price: String(e.price), validFrom: e.validFrom ?? "", validTo: e.validTo ?? "" });
  };
  const submitEntry = (event: FormEvent): void => { event.preventDefault(); if (entry) { saveEntry.mutate(entry); } };
  const derivation = l.parentCode ? t("pricing.derivationSummary", { parent: l.parentCode, pct: l.parentAdjustmentPct ?? 0, increment: l.roundingIncrement ?? "—", mode: t(`pricing.value.${l.roundingMode}`) }) : t("pricing.ownPrices");

  return (
    <>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-2">
            <span dir="ltr">{l.code}</span> · <span dir="auto">{localized(l.name)}</span>
            {l.isDefault ? <Badge tone="accent">{t("pricing.default")}</Badge> : null}
            {!l.isActive ? <Badge tone="neutral">{t("common.inactive")}</Badge> : null}
          </span>
        }
        description={<Link to="/sales/price-lists" className="text-accent hover:underline">{t("pricing.backToLists")}</Link>}
        actions={
          mayManage ? (
            <>
              <Button variant="secondary" onClick={() => { setEditing(true); }} data-testid="edit-price-list">
                <Pencil aria-hidden="true" />
                {t("common.edit")}
              </Button>
              <Button variant="secondary" onClick={() => { setProblem(null); setAdjusting({ pct: "", increment: "", mode: "nearest" }); }} data-testid="adjust-price-list">
                <Percent aria-hidden="true" />
                {t("pricing.adjustPrices")}
              </Button>
              <Button variant="ghost" onClick={() => { removeList.mutate(); }} loading={removeList.isPending} data-testid="delete-price-list">
                <Trash2 aria-hidden="true" />
                {t("common.delete")}
              </Button>
            </>
          ) : null
        }
      />
      <FormError message={!entry && !adjusting ? (problem?.message ?? null) : null} />
      <div className="mb-4 rounded-lg border border-border bg-surface p-4" data-testid="price-list-summary">
        <KeyValues
          entries={[
            [t("pricing.currency"), <span key="c" dir="ltr">{l.currency}</span>],
            [t("pricing.taxBasis"), l.pricesIncludeTax ? t("pricing.inclusive") : t("pricing.exclusive")],
            [t("pricing.priority"), <span key="p" className="tabular">{l.priority}</span>],
            [t("pricing.validity"), <Validity key="v" from={l.validFrom} to={l.validTo} />],
            [t("pricing.pricesFrom"), derivation],
            [t("pricing.forWhom"), [...l.customers, ...l.customerGroups].map(refLabel).join(", ") || t("pricing.forDocumentsOrDefault")],
          ]}
        />
      </div>

      <div className="mb-2 flex flex-wrap items-end justify-between gap-3">
        <Field label={t("common.search")} className="w-64">
          <TextField value={q} onChange={(e) => { setQ(e.target.value); }} data-testid="price-search" />
        </Field>
        {mayManage ? (
          <Button onClick={() => { void openEntry(null); }} data-testid="new-price">
            <Plus aria-hidden="true" />
            {t("pricing.addPrice")}
          </Button>
        ) : null}
      </div>
      {l.parentCode && (entries.data ?? []).length === 0 ? <p className="mb-2 text-sm text-fg-muted">{t("pricing.inheritsAll", { parent: l.parentCode })}</p> : null}
      <Table aria-label={t("pricing.prices")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pricing.item")}</TableHead>
            <TableHead>{t("pricing.variant")}</TableHead>
            <TableHead>{t("pricing.uom")}</TableHead>
            <TableHead className="text-end">{t("pricing.fromQuantity")}</TableHead>
            <TableHead className="text-end">{t("pricing.price")}</TableHead>
            <TableHead>{t("pricing.validity")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(entries.data ?? []).map((e) => (
            <TableRow key={e.id} data-testid="price-row">
              <TableCell>
                <span className="font-medium" dir="ltr">
                  {e.itemCode}
                </span>{" "}
                <span className="text-fg-muted" dir="auto">
                  {localized(e.itemName)}
                </span>
              </TableCell>
              <TableCell dir="ltr">{e.variantSku ?? ""}</TableCell>
              <TableCell dir="ltr">{e.uomCode}</TableCell>
              <TableCell className="text-end tabular">{formatNumber(e.minQuantity)}</TableCell>
              <TableCell className="text-end tabular" dir="ltr">
                {price(e.price, l.currency)}
              </TableCell>
              <TableCell>
                <Validity from={e.validFrom} to={e.validTo} />
              </TableCell>
              <TableCell className="text-end">
                {mayManage ? (
                  <span className="inline-flex gap-1">
                    <Button variant="ghost" size="sm" aria-label={t("pricing.editPrice", { item: e.itemCode })} onClick={() => { void openEntry(e); }}>
                      <Pencil aria-hidden="true" />
                    </Button>
                    <Button variant="ghost" size="sm" aria-label={t("pricing.deletePrice", { item: e.itemCode })} onClick={() => { removeEntry.mutate(e.id); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </span>
                ) : null}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {editing ? <PriceListDialog list={l} companyId={l.companyId} currency={l.currency} onClose={() => { setEditing(false); }} onSaved={() => { setEditing(false); void refresh(); }} /> : null}

      <Dialog open={Boolean(entry)} onOpenChange={(isOpen) => { if (!isOpen) { setEntry(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {entry ? (
            <form onSubmit={submitEntry} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{entry.id ? t("pricing.editPriceTitle") : t("pricing.addPrice")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("pricing.item")} required>
                  <ItemCodeField
                    value={entry.itemCode}
                    onChange={(code) => { setEntry({ ...entry, itemCode: code, item: null }); }}
                    onBlur={() => { void findItemByCode(entry.itemCode).then((item) => { setEntry((prev) => (prev ? { ...prev, item, uomId: item ? prev.uomId || (item.salesUomId ?? item.baseUomId) : "", variantId: "" } : prev)); }); }}
                    required
                    data-testid="price-item"
                  />
                </Field>
                <Field label={t("pricing.uom")}>
                  <SelectField value={entry.uomId} onChange={(e) => { setEntry({ ...entry, uomId: e.target.value }); }} data-testid="price-uom">
                    {(entry.item?.uoms ?? []).map((u) => (
                      <option key={u.uomId} value={u.uomId}>
                        {u.uomCode}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                {(entry.item?.variants ?? []).length > 0 ? (
                  <Field label={t("pricing.variant")}>
                    <SelectField value={entry.variantId} onChange={(e) => { setEntry({ ...entry, variantId: e.target.value }); }}>
                      <option value="">{t("pricing.allVariants")}</option>
                      {(entry.item?.variants ?? []).map((v) => (
                        <option key={v.id} value={v.id}>
                          {v.sku}
                        </option>
                      ))}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("pricing.fromQuantity")} description={t("pricing.fromQuantityHint")}>
                  <TextField type="number" min={0} step="any" value={entry.minQuantity} onChange={(e) => { setEntry({ ...entry, minQuantity: e.target.value }); }} dir="ltr" data-testid="price-min" />
                </Field>
                <Field label={t("pricing.priceIn", { currency: l.currency })} required>
                  <TextField type="number" min={0} step="any" value={entry.price} onChange={(e) => { setEntry({ ...entry, price: e.target.value }); }} required dir="ltr" data-testid="price-value" />
                </Field>
                <Field label={t("pricing.validFrom")}>
                  <TextField type="date" value={entry.validFrom} onChange={(e) => { setEntry({ ...entry, validFrom: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("pricing.validTo")}>
                  <TextField type="date" value={entry.validTo} onChange={(e) => { setEntry({ ...entry, validTo: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEntry(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveEntry.isPending} data-testid="save-price">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(adjusting)} onOpenChange={(isOpen) => { if (!isOpen) { setAdjusting(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-lg">
          {adjusting ? (
            <form onSubmit={(event) => { event.preventDefault(); adjust.mutate(adjusting); }} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("pricing.adjustPrices")}</DialogTitle>
              </DialogHeader>
              <p className="text-sm text-fg-muted">{t("pricing.adjustHint")}</p>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label={t("pricing.adjustmentPct")} required>
                  <TextField type="number" step="any" value={adjusting.pct} onChange={(e) => { setAdjusting({ ...adjusting, pct: e.target.value }); }} required dir="ltr" data-testid="adjust-pct" />
                </Field>
                <Field label={t("pricing.roundingIncrement")}>
                  <TextField type="number" min={0} step="any" value={adjusting.increment} onChange={(e) => { setAdjusting({ ...adjusting, increment: e.target.value }); }} dir="ltr" data-testid="adjust-increment" />
                </Field>
                <Field label={t("pricing.roundingMode")}>
                  <SelectField value={adjusting.mode} onChange={(e) => { setAdjusting({ ...adjusting, mode: e.target.value }); }}>
                    {["nearest", "up", "down"].map((m) => (
                      <option key={m} value={m}>
                        {t(`pricing.value.${m}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setAdjusting(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={adjust.isPending} data-testid="confirm-adjust">
                  {t("pricing.applyAdjustment")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
