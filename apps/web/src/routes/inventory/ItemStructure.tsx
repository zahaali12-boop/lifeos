import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ImageOff, Plus, Trash2, Upload } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, SelectField, TextField } from "../common";
import { ItemCodeField } from "./ItemCodeField";
import { useItemRefresh } from "./ItemEditors";
import { Qty, type Item } from "./shared";

type Bom = components["schemas"]["BomSummary"];
type Variant = components["schemas"]["VariantSummary"];

interface BomLineForm {
  itemCode: string;
  quantity: string;
  uom: string;
  scrapPct: string;
}

const emptyBomLine: BomLineForm = { itemCode: "", quantity: "1", uom: "", scrapPct: "0" };

/**
 * Bills of material of a kit or assembly item: its versions (one active at a time; a new version replaces the active
 * one when it is activated), the components of the selected version, and the exploded requirements for a quantity of
 * output (base units, exact, with and without scrap, through sub-assemblies).
 */
export function ItemBomEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draft, setDraft] = useState<{ outputQty: string; activate: boolean; lines: BomLineForm[] } | null>(null);
  const [explodeQty, setExplodeQty] = useState("1");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const structured = item.type === "kit" || item.type === "assembly";
  const boms = useQuery({ queryKey: ["boms", item.id], enabled: structured, queryFn: async () => unwrap(await api.GET("/api/v1/items/{itemId}/boms", { params: { path: { itemId: item.id } } })) });
  const versions = [...(boms.data ?? [])].sort((a, b) => Number(b.version) - Number(a.version));
  const selected = versions.find((b) => b.id === selectedId) ?? versions.find((b) => b.isActive) ?? versions[0];
  const explosion = useQuery({
    queryKey: ["bom-explode", selected?.id, explodeQty],
    enabled: Boolean(selected) && Number(explodeQty) > 0,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/items/boms/{bomId}/explode", { params: { path: { bomId: selected?.id ?? "" }, query: { quantity: explodeQty } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["boms", item.id] });
    await queryClient.invalidateQueries({ queryKey: ["bom-explode"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async () => {
      if (!draft) {
        throw new Error("no draft");
      }
      return unwrap(await api.POST("/api/v1/items/{itemId}/boms", {
        params: { path: { itemId: item.id } },
        body: {
          kind: item.type,
          outputQty: draft.outputQty || "1",
          activate: draft.activate,
          lines: draft.lines.filter((l) => l.itemCode.trim()).map((l) => ({ componentItemCode: l.itemCode.trim(), quantity: l.quantity || "0", uom: l.uom.trim() || null, scrapPct: l.scrapPct || "0" })),
        },
      }));
    },
    onSuccess: async (created) => { setDraft(null); setProblem(null); setSelectedId(created.id); await refresh(); },
    onError: fail,
  });
  const activate = useMutation({
    mutationFn: async (bomId: string) => unwrap(await api.POST("/api/v1/items/boms/{bomId}/activate", { params: { path: { bomId } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const startFrom = (bom: Bom | undefined): void => {
    setProblem(null);
    setDraft({
      outputQty: bom ? String(bom.outputQty) : "1",
      activate: true,
      lines: bom ? bom.lines.map((l) => ({ itemCode: l.componentItemCode, quantity: String(l.quantity), uom: l.uomCode, scrapPct: String(l.scrapPct) })) : [{ ...emptyBomLine }],
    });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };

  if (!structured) {
    return <p className="text-sm text-fg-muted" data-testid="item-bom">{t("itemStructure.bomNotApplicable")}</p>;
  }

  return (
    <div className="flex flex-col gap-4" data-testid="item-bom">
      <div className="flex flex-wrap items-center gap-2">
        <p className="flex-1 text-sm text-fg-muted">{item.type === "kit" ? t("itemStructure.kitHint") : t("itemStructure.assemblyHint")}</p>
        {draft ? null : (
          <Button size="sm" variant="secondary" onClick={() => { startFrom(selected); }} data-testid="new-bom-version">
            <Plus aria-hidden="true" />
            {versions.length === 0 ? t("itemStructure.firstBom") : t("itemStructure.newVersion")}
          </Button>
        )}
      </div>
      <FormError message={problem && !draft ? problem.message : null} />

      {versions.length > 0 && !draft ? (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("itemStructure.version")}</TableHead>
                <TableHead className="text-end">{t("itemStructure.outputQty")}</TableHead>
                <TableHead className="text-end">{t("itemStructure.components")}</TableHead>
                <TableHead>{t("common.status")}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {versions.map((b) => (
                <TableRow key={b.id} data-testid="bom-version-row" data-state={b.id === selected?.id ? "selected" : undefined}>
                  <TableCell>
                    <button type="button" className="text-accent underline-offset-4 hover:underline" onClick={() => { setSelectedId(b.id); }}>
                      {t("itemStructure.versionN", { version: b.version })}
                    </button>
                  </TableCell>
                  <TableNumberCell><Qty value={b.outputQty} uom={item.baseUom} /></TableNumberCell>
                  <TableNumberCell>{b.lines.length}</TableNumberCell>
                  <TableCell>{b.isActive ? <Badge tone="success">{t("common.active")}</Badge> : <Badge>{t("common.inactive")}</Badge>}</TableCell>
                  <TableCell>
                    {b.isActive ? null : (
                      <Button size="sm" variant="ghost" onClick={() => { activate.mutate(b.id); }} loading={activate.isPending} data-testid="activate-bom">
                        {t("itemStructure.activate")}
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          {selected ? (
            <section className="flex flex-col gap-2" data-testid="bom-lines">
              <h3 className="text-sm font-semibold">{t("itemStructure.componentsOf", { version: selected.version, quantity: String(selected.outputQty), uom: item.baseUom })}</h3>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.item")}</TableHead>
                    <TableHead className="text-end">{t("itemStructure.quantity")}</TableHead>
                    <TableHead className="text-end">{t("itemStructure.scrap")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {selected.lines.map((l, index) => (
                    <TableRow key={l.id}>
                      <TableCell>{index + 1}</TableCell>
                      <TableCell>
                        <span dir="ltr">{l.componentItemCode}</span> {localized(l.componentName)}
                        {l.componentVariantSku ? <span className="text-fg-muted"> · <span dir="ltr">{l.componentVariantSku}</span></span> : null}
                      </TableCell>
                      <TableNumberCell><Qty value={l.quantity} uom={l.uomCode} /></TableNumberCell>
                      <TableNumberCell>{String(l.scrapPct)}%</TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div className="flex flex-wrap items-end gap-3">
                <Field label={t("itemStructure.explodeFor", { uom: item.baseUom })}>
                  <TextField inputMode="decimal" value={explodeQty} onChange={(e) => { setExplodeQty(e.target.value); }} className="w-28" dir="ltr" data-testid="explode-qty" />
                </Field>
              </div>
              {explosion.data ? (
                <Table data-testid="bom-explosion">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("itemStructure.requirement")}</TableHead>
                      <TableHead className="text-end">{t("itemStructure.quantity")}</TableHead>
                      <TableHead className="text-end">{t("itemStructure.withScrap")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {explosion.data.requirements.map((r, index) => (
                      <TableRow key={`${r.itemId}-${String(index)}`} data-testid="explosion-row">
                        <TableCell>
                          <span style={{ paddingInlineStart: `${String(Math.max(0, Number(r.depth) - 1) * 16)}px` }}>
                            <span dir="ltr">{r.itemCode}</span> {localized(r.name)}
                          </span>
                        </TableCell>
                        <TableNumberCell><Qty value={r.quantity} uom={r.baseUom} /></TableNumberCell>
                        <TableNumberCell><Qty value={r.quantityWithScrap} uom={r.baseUom} /></TableNumberCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : explosion.isError ? <FormError message={toFormProblem(explosion.error, t("common.saveFailed")).message} /> : null}
            </section>
          ) : null}
        </>
      ) : null}

      {draft ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="bom-editor">
          <h3 className="text-sm font-semibold">{versions.length === 0 ? t("itemStructure.firstBom") : t("itemStructure.newVersion")}</h3>
          <FormError message={problem?.message ?? null} />
          <div className="flex flex-wrap items-end gap-4">
            <Field label={t("itemStructure.outputQtyIn", { uom: item.baseUom })} required>
              <TextField inputMode="decimal" value={draft.outputQty} onChange={(e) => { setDraft({ ...draft, outputQty: e.target.value }); }} required className="w-28" dir="ltr" data-testid="bom-output" />
            </Field>
            <label className="flex items-center gap-2 pb-2 text-sm">
              <input type="checkbox" checked={draft.activate} onChange={(e) => { setDraft({ ...draft, activate: e.target.checked }); }} />
              {t("itemStructure.activateOnSave")}
            </label>
          </div>
          {draft.lines.map((line, index) => (
            <div key={index} className="grid items-end gap-2 sm:grid-cols-[2fr_1fr_1fr_1fr_auto]">
              <Field label={t("inventory.item")}>
                <ItemCodeField value={line.itemCode} onChange={(code) => { setDraft({ ...draft, lines: draft.lines.map((l, i) => (i === index ? { ...l, itemCode: code } : l)) }); }} data-testid={`bom-line-item-${String(index)}`} />
              </Field>
              <Field label={t("itemStructure.quantity")}>
                <TextField inputMode="decimal" value={line.quantity} onChange={(e) => { setDraft({ ...draft, lines: draft.lines.map((l, i) => (i === index ? { ...l, quantity: e.target.value } : l)) }); }} dir="ltr" data-testid={`bom-line-qty-${String(index)}`} />
              </Field>
              <Field label={t("itemStructure.unit")} description={index === 0 ? t("itemStructure.unitHint") : undefined}>
                <TextField value={line.uom} onChange={(e) => { setDraft({ ...draft, lines: draft.lines.map((l, i) => (i === index ? { ...l, uom: e.target.value.toUpperCase() } : l)) }); }} dir="ltr" />
              </Field>
              <Field label={t("itemStructure.scrapPct")}>
                <TextField inputMode="decimal" value={line.scrapPct} onChange={(e) => { setDraft({ ...draft, lines: draft.lines.map((l, i) => (i === index ? { ...l, scrapPct: e.target.value } : l)) }); }} dir="ltr" data-testid={`bom-line-scrap-${String(index)}`} />
              </Field>
              <Button type="button" variant="ghost" size="icon" aria-label={t("itemStructure.removeLine")} onClick={() => { setDraft({ ...draft, lines: draft.lines.filter((_, i) => i !== index) }); }}>
                <Trash2 aria-hidden="true" />
              </Button>
            </div>
          ))}
          <div className="flex flex-wrap gap-2">
            <Button type="button" size="sm" variant="secondary" onClick={() => { setDraft({ ...draft, lines: [...draft.lines, { ...emptyBomLine }] }); }} data-testid="add-bom-line">
              <Plus aria-hidden="true" />
              {t("itemStructure.addComponent")}
            </Button>
            <span className="flex-1" />
            <Button type="button" variant="secondary" onClick={() => { setDraft(null); setProblem(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={save.isPending} data-testid="save-bom">
              {t("common.save")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/**
 * Variants of an item: a SKU per combination of attribute values (size, colour), each with an optional name and its own
 * status. The attributes and their values are defined once under Items → Lists.
 */
export function ItemVariantsEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const refresh = useItemRefresh(item.id);
  const [editing, setEditing] = useState<{ id: string | null; sku: string; nameEn: string; nameAr: string; values: Record<string, string>; isActive: boolean } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const attributes = useQuery({ queryKey: ["item-attributes"], queryFn: async () => unwrap(await api.GET("/api/v1/items/attributes")) });
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async () => {
      if (!editing) {
        return;
      }
      const body = {
        sku: editing.sku.trim(),
        name: editing.nameEn || editing.nameAr ? { ...(editing.nameEn ? { en: editing.nameEn } : {}), ...(editing.nameAr ? { ar: editing.nameAr } : {}) } : null,
        attributeValues: Object.fromEntries(Object.entries(editing.values).filter(([, v]) => v)),
        isActive: editing.isActive,
      };
      if (editing.id) {
        unwrap(await api.PUT("/api/v1/items/{itemId}/variants/{variantId}", { params: { path: { itemId: item.id, variantId: editing.id } }, body }));
      } else {
        unwrap(await api.POST("/api/v1/items/{itemId}/variants", { params: { path: { itemId: item.id } }, body }));
      }
    },
    onSuccess: async () => { setEditing(null); setProblem(null); await refresh(); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: async (variantId: string) => { await api.DELETE("/api/v1/items/{itemId}/variants/{variantId}", { params: { path: { itemId: item.id, variantId } } }).then(unwrap); },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const open = (v: Variant | null): void => {
    setProblem(null);
    setEditing(
      v
        ? { id: v.id, sku: v.sku, nameEn: v.name.en ?? "", nameAr: v.name.ar ?? "", values: Object.fromEntries(Object.entries(v.attributeValues).map(([code, value]) => [code, value.valueCode])), isActive: v.isActive }
        : { id: null, sku: `${item.code}-`, nameEn: "", nameAr: "", values: {}, isActive: true },
    );
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); save.mutate(); };
  const variants = item.variants ?? [];

  return (
    <div className="flex flex-col gap-3" data-testid="item-variants">
      <div className="flex flex-wrap items-center gap-2">
        <p className="flex-1 text-sm text-fg-muted">{(attributes.data ?? []).length === 0 ? t("itemStructure.noAttributes") : t("itemStructure.variantsHint")}</p>
        {editing ? null : (
          <Button size="sm" variant="secondary" onClick={() => { open(null); }} disabled={(attributes.data ?? []).length === 0} data-testid="new-variant">
            <Plus aria-hidden="true" />
            {t("itemStructure.newVariant")}
          </Button>
        )}
      </div>
      <FormError message={problem && !editing ? problem.message : null} />
      {variants.length > 0 ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("itemStructure.sku")}</TableHead>
              <TableHead>{t("inventory.items.name")}</TableHead>
              <TableHead>{t("itemStructure.attributes")}</TableHead>
              <TableHead>{t("common.status")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {variants.map((v) => (
              <TableRow key={v.id} data-testid="variant-row">
                <TableCell dir="ltr">{v.sku}</TableCell>
                <TableCell>{localized(v.name)}</TableCell>
                <TableCell>{Object.entries(v.attributeValues).map(([code, value]) => `${code}: ${localized(value.valueName) || value.valueCode}`).join(" · ")}</TableCell>
                <TableCell>{v.isActive ? <Badge tone="success">{t("common.active")}</Badge> : <Badge>{t("common.inactive")}</Badge>}</TableCell>
                <TableCell>
                  <div className="flex gap-1">
                    <Button size="sm" variant="ghost" onClick={() => { open(v); }}>
                      {t("common.edit")}
                    </Button>
                    <Button size="icon" variant="ghost" aria-label={t("itemStructure.removeVariant", { sku: v.sku })} onClick={() => { remove.mutate(v.id); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {editing ? (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="variant-editor">
          <FormError message={problem?.message ?? null} />
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("itemStructure.sku")} required error={problem?.fields.sku}>
              <TextField value={editing.sku} onChange={(e) => { setEditing({ ...editing, sku: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="variant-sku" />
            </Field>
            <Field label={t("inventory.items.nameEn")}>
              <TextField value={editing.nameEn} onChange={(e) => { setEditing({ ...editing, nameEn: e.target.value }); }} data-testid="variant-name-en" />
            </Field>
            <Field label={t("inventory.items.nameAr")}>
              <TextField value={editing.nameAr} onChange={(e) => { setEditing({ ...editing, nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            {(attributes.data ?? []).map((a) => (
              <Field key={a.id} label={localized(a.name) || a.code}>
                <SelectField value={editing.values[a.code] ?? ""} onChange={(e) => { setEditing({ ...editing, values: { ...editing.values, [a.code]: e.target.value } }); }} data-testid={`variant-attr-${a.code}`}>
                  <option value="">—</option>
                  {a.values.map((v) => (
                    <option key={v.id} value={v.code}>
                      {localized(v.name) || v.code}
                    </option>
                  ))}
                </SelectField>
              </Field>
            ))}
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={editing.isActive} onChange={(e) => { setEditing({ ...editing, isActive: e.target.checked }); }} />
            {t("common.active")}
          </label>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => { setEditing(null); setProblem(null); }}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={save.isPending} data-testid="save-variant">
              {t("common.save")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/** Substitutes: items that may replace this one when it is short, in order of preference (the list is saved as a whole). */
export function ItemSubstitutesEditor({ item }: { item: Item }) {
  const { t } = useTranslation();
  const refresh = useItemRefresh(item.id);
  const [code, setCode] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const current = [...(item.substitutes ?? [])].sort((a, b) => Number(a.priority) - Number(b.priority));
  const save = useMutation({
    mutationFn: async (codes: string[]) =>
      unwrap(await api.PUT("/api/v1/items/{itemId}/substitutes", { params: { path: { itemId: item.id } }, body: { substitutes: codes.map((c, i) => ({ itemCode: c, priority: i + 1 })) } })),
    onSuccess: async () => { setCode(""); setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const codes = current.map((s) => s.itemCode);
  const move = (index: number, by: number): void => {
    const next = [...codes];
    const [moved] = next.splice(index, 1);
    if (moved) {
      next.splice(index + by, 0, moved);
      save.mutate(next);
    }
  };

  return (
    <div className="flex flex-col gap-3" data-testid="item-substitutes">
      <p className="text-sm text-fg-muted">{t("itemStructure.substitutesHint")}</p>
      <FormError message={problem?.message ?? null} />
      {current.length === 0 ? <p className="text-sm text-fg-muted">{t("itemStructure.noSubstitutes")}</p> : (
        <ol className="flex flex-col gap-1">
          {current.map((s, index) => (
            <li key={s.itemId} className="flex items-center gap-2 rounded-md border border-border px-3 py-1.5 text-sm" data-testid="substitute-row">
              <span className="w-6 text-fg-muted">{index + 1}.</span>
              <span className="flex-1"><span dir="ltr">{s.itemCode}</span> {localized(s.name)}</span>
              <Button size="sm" variant="ghost" disabled={index === 0} onClick={() => { move(index, -1); }} aria-label={t("itemStructure.moveUp", { code: s.itemCode })}>↑</Button>
              <Button size="sm" variant="ghost" disabled={index === current.length - 1} onClick={() => { move(index, 1); }} aria-label={t("itemStructure.moveDown", { code: s.itemCode })}>↓</Button>
              <Button size="icon" variant="ghost" aria-label={t("itemStructure.removeSubstitute", { code: s.itemCode })} onClick={() => { save.mutate(codes.filter((c) => c !== s.itemCode)); }}>
                <Trash2 aria-hidden="true" />
              </Button>
            </li>
          ))}
        </ol>
      )}
      <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => { e.preventDefault(); if (code.trim()) { save.mutate([...codes, code.trim()]); } }}>
        <Field label={t("itemStructure.addSubstitute")}>
          <ItemCodeField value={code} onChange={setCode} data-testid="substitute-code" />
        </Field>
        <Button type="submit" variant="secondary" loading={save.isPending} disabled={!code.trim()} data-testid="add-substitute">
          <Plus aria-hidden="true" />
          {t("itemStructure.add")}
        </Button>
      </form>
    </div>
  );
}

/** The item's picture: stored as an attachment of the item and set as its image; shown in the item header. */
export function ItemImage({ item }: { item: Item }) {
  const { t } = useTranslation();
  const refresh = useItemRefresh(item.id);
  const [url, setUrl] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const attachmentId = item.imageAttachmentId;
  const image = useQuery({
    queryKey: ["item-image", attachmentId],
    enabled: Boolean(attachmentId),
    queryFn: async () => unwrap(await api.GET("/api/v1/collaboration/attachments/{attachmentId}/content", { params: { path: { attachmentId: attachmentId ?? "" } }, parseAs: "blob" })),
  });
  useEffect(() => {
    if (!image.data) {
      setUrl(null);
      return undefined;
    }
    const objectUrl = URL.createObjectURL(image.data);
    setUrl(objectUrl);
    return () => { URL.revokeObjectURL(objectUrl); };
  }, [image.data]);
  const set = useMutation({
    mutationFn: async (file: File | null) => {
      let id: string | null = null;
      if (file) {
        const form = new FormData();
        form.append("entityType", "item");
        form.append("entityId", item.id);
        form.append("file", file);
        id = unwrap(await api.POST("/api/v1/collaboration/attachments", { body: { entityType: "item", entityId: item.id, file: "" }, bodySerializer: () => form })).id;
      }
      unwrap(await api.PUT("/api/v1/items/{itemId}/image", { params: { path: { itemId: item.id } }, body: { attachmentId: id } }));
    },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed")).message); },
  });

  return (
    <div className="flex items-center gap-3" data-testid="item-image">
      <div className="flex size-16 items-center justify-center overflow-hidden rounded-md border border-border bg-surface-sunken">
        {url ? <img src={url} alt={t("itemStructure.imageOf", { name: localized(item.name) })} className="size-full object-cover" /> : <ImageOff className="size-5 text-fg-subtle" aria-hidden="true" />}
      </div>
      <div className="flex flex-col gap-1 text-sm">
        <label className="inline-flex cursor-pointer items-center gap-1.5 rounded-md border border-border px-2 py-1 hover:bg-surface-sunken focus-within:ring-2 focus-within:ring-accent">
          <Upload className="size-3.5" aria-hidden="true" />
          {set.isPending ? t("attachments.uploading") : attachmentId ? t("itemStructure.replaceImage") : t("itemStructure.addImage")}
          <input type="file" accept="image/*" className="sr-only" onChange={(e) => { const file = e.target.files?.[0]; if (file) { set.mutate(file); } e.target.value = ""; }} data-testid="item-image-input" />
        </label>
        {attachmentId ? (
          <button type="button" className="self-start text-xs text-fg-muted underline-offset-4 hover:underline" onClick={() => { set.mutate(null); }} data-testid="remove-item-image">
            {t("itemStructure.removeImage")}
          </button>
        ) : null}
        {problem ? <span role="alert" className="text-xs text-danger">{problem}</span> : null}
      </div>
    </div>
  );
}

/**
 * Variant attributes (size, colour) and their values, shared by every item: an attribute is created with its values
 * ("S, M, L"), and values can be added later. Values in use by a variant are kept by the server.
 */
export function ItemAttributesEditor() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState({ code: "", en: "", ar: "", values: "" });
  const [adding, setAdding] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const attributes = useQuery({ queryKey: ["item-attributes"], queryFn: async () => unwrap(await api.GET("/api/v1/items/attributes")) });
  const valuesOf = (text: string) => text.split(/[,;]+/).map((v) => v.trim()).filter(Boolean).map((v, i) => ({ code: v.toUpperCase().replace(/\s+/g, "_"), name: { en: v }, sortOrder: i }));
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const create = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/items/attributes", { body: { code: draft.code.trim(), name: { en: draft.en, ...(draft.ar ? { ar: draft.ar } : {}) }, sortOrder: 0, values: valuesOf(draft.values) } })),
    onSuccess: async () => { setDraft({ code: "", en: "", ar: "", values: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-attributes"] }); },
    onError: fail,
  });
  const addValues = useMutation({
    mutationFn: async (attribute: components["schemas"]["AttributeSummary"]) => {
      const extra = valuesOf(adding[attribute.id] ?? "").map((v, i) => ({ ...v, sortOrder: attribute.values.length + i }));
      return unwrap(await api.PUT("/api/v1/items/attributes/{attributeId}", {
        params: { path: { attributeId: attribute.id } },
        body: { code: attribute.code, name: attribute.name, sortOrder: Number(attribute.sortOrder), values: [...attribute.values.map((v) => ({ code: v.code, name: v.name, sortOrder: Number(v.sortOrder) })), ...extra] },
      }));
    },
    onSuccess: async (saved) => { setAdding({ ...adding, [saved.id]: "" }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["item-attributes"] }); },
    onError: fail,
  });

  return (
    <section className="flex flex-col gap-3" data-testid="item-attributes">
      <h3 className="text-sm font-semibold">{t("itemStructure.attributes")}</h3>
      <FormError message={problem?.message ?? null} />
      <ul className="flex flex-col gap-2 text-sm">
        {(attributes.data ?? []).map((a) => (
          <li key={a.id} className="flex flex-wrap items-center gap-2" data-testid="attribute-row">
            <span className="font-medium"><span dir="ltr">{a.code}</span> · {localized(a.name)}</span>
            {a.values.map((v) => <Badge key={v.id}>{localized(v.name) || v.code}</Badge>)}
            <form className="ms-auto flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); addValues.mutate(a); }}>
              <TextField value={adding[a.id] ?? ""} onChange={(e) => { setAdding({ ...adding, [a.id]: e.target.value }); }} aria-label={t("itemStructure.moreValues", { attribute: a.code })} placeholder={t("itemStructure.moreValuesPlaceholder")} className="h-8 w-40" />
              <Button type="submit" size="sm" variant="ghost" disabled={!(adding[a.id] ?? "").trim()}>
                {t("itemStructure.add")}
              </Button>
            </form>
          </li>
        ))}
      </ul>
      <form className="grid items-end gap-2 sm:grid-cols-[1fr_1fr_1fr_2fr_auto]" onSubmit={(e) => { e.preventDefault(); create.mutate(); }}>
        <Field label={t("inventory.items.code")} required>
          <TextField value={draft.code} onChange={(e) => { setDraft({ ...draft, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="attribute-code" />
        </Field>
        <Field label={t("inventory.items.nameEn")} required>
          <TextField value={draft.en} onChange={(e) => { setDraft({ ...draft, en: e.target.value }); }} required data-testid="attribute-name-en" />
        </Field>
        <Field label={t("inventory.items.nameAr")}>
          <TextField value={draft.ar} onChange={(e) => { setDraft({ ...draft, ar: e.target.value }); }} dir="rtl" lang="ar" />
        </Field>
        <Field label={t("itemStructure.values")} description={t("itemStructure.valuesHint")}>
          <TextField value={draft.values} onChange={(e) => { setDraft({ ...draft, values: e.target.value }); }} data-testid="attribute-values" />
        </Field>
        <Button type="submit" variant="secondary" loading={create.isPending} data-testid="save-attribute">
          {t("itemStructure.addAttribute")}
        </Button>
      </form>
    </section>
  );
}
