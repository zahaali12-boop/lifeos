import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { useState, type FormEvent, type ReactNode } from "react";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../../api";
import { formatNumber, localized } from "../../../lib/format";
import { useCan } from "../../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../../common";
import { ItemCodeField } from "../../inventory/ItemCodeField";
import { CompanyFilter, Tabs, findItemByCode, useCompanyContext } from "../../inventory/shared";
import { usePaymentTerms } from "../../purchasing/shared";
import { useCustomerGroups } from "../shared";
import { Validity, price, refLabel, useBrands, useCategories, useCompanyCustomers, type DiscountRule, type PriceAgreement, type PriceFloor, type Promotion } from "./shared";

const n = (value: string): number | null => (value.trim() === "" ? null : Number(value));
const s = (value: number | string | null | undefined): string => (value === null ? "" : String(value));

/** Discounts and promotions (roadmap 5.2): what was agreed with customers, discount rules, promotions and the floors under them all. */
export function PricingRulesPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState("agreements");
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const currency = company?.functionalCurrency ?? "";
  return (
    <>
      <PageHeader title={t("nav.pricingRules")} description={t("pricing.rulesDescription")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <Tabs
        tabs={[
          { id: "agreements", label: t("pricing.agreements"), testId: "tab-agreements" },
          { id: "rules", label: t("pricing.discountRules"), testId: "tab-rules" },
          { id: "promotions", label: t("pricing.promotions"), testId: "tab-promotions" },
          { id: "floors", label: t("pricing.floors"), testId: "tab-floors" },
        ]}
        value={tab}
        onChange={setTab}
      />
      {companyId && tab === "agreements" ? <AgreementsTab companyId={companyId} currency={currency} /> : null}
      {companyId && tab === "rules" ? <RulesTab companyId={companyId} currency={currency} /> : null}
      {companyId && tab === "promotions" ? <PromotionsTab companyId={companyId} currency={currency} /> : null}
      {companyId && tab === "floors" ? <FloorsTab companyId={companyId} currency={currency} /> : null}
    </>
  );
}

function RuleDialog({ title, open, onClose, onSubmit, busy, problem, children, testId }: { title: string; open: boolean; onClose: () => void; onSubmit: () => void; busy: boolean; problem: FormProblem | null; children: ReactNode; testId: string }) {
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

function Toolbar({ canManage, onNew, label, testId }: { canManage: boolean; onNew: () => void; label: string; testId: string }) {
  return canManage ? (
    <div className="my-3 flex justify-end">
      <Button onClick={onNew} data-testid={testId}>
        <Plus aria-hidden="true" />
        {label}
      </Button>
    </div>
  ) : (
    <div className="my-3" />
  );
}

function RowActions({ canManage, label, onEdit, onDelete }: { canManage: boolean; label: string; onEdit: () => void; onDelete: () => void }) {
  const { t } = useTranslation();
  return canManage ? (
    <span className="inline-flex gap-1">
      <Button variant="ghost" size="sm" aria-label={t("pricing.editNamed", { name: label })} onClick={onEdit}>
        <Pencil aria-hidden="true" />
      </Button>
      <Button variant="ghost" size="sm" aria-label={t("pricing.deleteNamed", { name: label })} onClick={onDelete}>
        <Trash2 aria-hidden="true" />
      </Button>
    </span>
  ) : null;
}

function useSaver<T>(invalidate: string, save: (body: T) => Promise<unknown>, onDone: () => void) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const mutation = useMutation({
    mutationFn: save,
    onSuccess: async () => { setProblem(null); onDone(); await queryClient.invalidateQueries({ queryKey: [invalidate] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  return { mutation, problem, setProblem };
}

/** Resolves an item code typed in a form to its id, or fails the save with the code that is not known. */
async function itemIdOf(code: string, t: TFunction): Promise<string | null> {
  if (!code.trim()) {
    return null;
  }
  const item = await findItemByCode(code);
  if (!item) {
    throw new Error(t("pricing.itemUnknown", { code }));
  }
  return item.id;
}

function ScopeFields({ form, patch, items = true, customer = true }: { form: ScopeForm; patch: (p: Partial<ScopeForm>) => void; items?: boolean; customer?: boolean }) {
  const { t } = useTranslation();
  const categories = useCategories();
  const brands = useBrands();
  const groups = useCustomerGroups();
  return (
    <>
      {items ? (
        <>
          <Field label={t("pricing.item")}>
            <ItemCodeField value={form.itemCode} onChange={(code) => { patch({ itemCode: code }); }} data-testid="scope-item" />
          </Field>
          <Field label={t("pricing.category")}>
            <SelectField value={form.categoryId} onChange={(e) => { patch({ categoryId: e.target.value }); }} data-testid="scope-category">
              <option value="">{t("pricing.any")}</option>
              {(categories.data ?? []).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.code} · {localized(c.name)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("pricing.brand")}>
            <SelectField value={form.brandId} onChange={(e) => { patch({ brandId: e.target.value }); }}>
              <option value="">{t("pricing.any")}</option>
              {(brands.data ?? []).map((b) => (
                <option key={b.id} value={b.id}>
                  {b.code} · {localized(b.name)}
                </option>
              ))}
            </SelectField>
          </Field>
        </>
      ) : null}
      {customer ? (
        <>
          <Field label={t("pricing.customerGroup")}>
            <SelectField value={form.customerGroupId} onChange={(e) => { patch({ customerGroupId: e.target.value }); }} data-testid="scope-group">
              <option value="">{t("pricing.any")}</option>
              {(groups.data ?? []).map((g) => (
                <option key={g.id} value={g.id}>
                  {g.code} · {localized(g.name)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("pricing.channel")}>
            <TextField value={form.channel} onChange={(e) => { patch({ channel: e.target.value }); }} dir="ltr" />
          </Field>
        </>
      ) : null}
    </>
  );
}

interface ScopeForm {
  itemCode: string;
  categoryId: string;
  brandId: string;
  partnerId: string;
  customerGroupId: string;
  channel: string;
}

function scopeText(t: TFunction, parts: (string | null | undefined)[]): string {
  const named = parts.filter((p): p is string => Boolean(p));
  return named.length > 0 ? named.join(" · ") : t("pricing.everything");
}

// ------------------------------------------------------------------ agreements

interface AgreementForm {
  id: string | null;
  partnerId: string;
  reference: string;
  onCategory: boolean;
  itemCode: string;
  categoryId: string;
  uomId: string;
  minQuantity: string;
  kind: "price" | "discount";
  price: string;
  currency: string;
  discountPct: string;
  validFrom: string;
  validTo: string;
  isActive: boolean;
}

function AgreementsTab({ companyId, currency }: { companyId: string; currency: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("pricing.agreement.manage");
  const [partnerFilter, setPartnerFilter] = useState("");
  const [form, setForm] = useState<AgreementForm | null>(null);
  const customers = useCompanyCustomers(companyId);
  const categories = useCategories();
  const agreements = useQuery({ queryKey: ["price-agreements", companyId, partnerFilter], queryFn: async () => unwrap(await api.GET("/api/v1/pricing/agreements", { params: { query: { companyId, ...(partnerFilter ? { partnerId: partnerFilter } : {}) } } })) });
  const itemUoms = useQuery({ queryKey: ["item-by-code", form?.itemCode ?? ""], enabled: Boolean(form?.itemCode), queryFn: () => findItemByCode(form?.itemCode ?? "") });
  const { mutation, problem, setProblem } = useSaver("price-agreements", async (f: AgreementForm) => {
    const body = {
      companyId,
      partnerId: f.partnerId,
      reference: f.reference || null,
      itemId: f.onCategory ? null : await itemIdOf(f.itemCode, t),
      variantId: null,
      categoryId: f.onCategory ? f.categoryId || null : null,
      uomId: f.kind === "price" ? f.uomId || null : null,
      minQuantity: Number(f.minQuantity || "0"),
      price: f.kind === "price" ? n(f.price) : null,
      currency: f.kind === "price" ? f.currency || null : null,
      discountPct: f.kind === "discount" ? n(f.discountPct) : null,
      validFrom: f.validFrom || null,
      validTo: f.validTo || null,
      isActive: f.isActive,
    };
    return f.id ? unwrap(await api.PUT("/api/v1/pricing/agreements/{agreementId}", { params: { path: { agreementId: f.id } }, body })) : unwrap(await api.POST("/api/v1/pricing/agreements", { body }));
  }, () => { setForm(null); });
  const remove = useSaver("price-agreements", async (id: string) => { unwrap(await api.DELETE("/api/v1/pricing/agreements/{agreementId}", { params: { path: { agreementId: id } } })); }, () => undefined);
  const edit = (a: PriceAgreement | null): void => {
    setProblem(null);
    setForm(a
      ? { id: a.id, partnerId: a.partner.id, reference: a.reference ?? "", onCategory: Boolean(a.category), itemCode: a.item?.code ?? "", categoryId: a.category?.id ?? "", uomId: a.uomId ?? "", minQuantity: String(a.minQuantity), kind: a.price !== null ? "price" : "discount", price: s(a.price), currency: a.currency ?? currency, discountPct: s(a.discountPct), validFrom: a.validFrom ?? "", validTo: a.validTo ?? "", isActive: a.isActive }
      : { id: null, partnerId: partnerFilter, reference: "", onCategory: false, itemCode: "", categoryId: "", uomId: "", minQuantity: "0", kind: "price", price: "", currency, discountPct: "", validFrom: "", validTo: "", isActive: true });
  };
  const patch = (p: Partial<AgreementForm>): void => { setForm((prev) => (prev ? { ...prev, ...p } : prev)); };

  return (
    <>
      <div className="mt-3 grid gap-3 sm:grid-cols-3">
        <Field label={t("pricing.customer")}>
          <SelectField value={partnerFilter} onChange={(e) => { setPartnerFilter(e.target.value); }}>
            <option value="">{t("pricing.allCustomers")}</option>
            {(customers.data ?? []).map((c) => (
              <option key={c.partnerId} value={c.partnerId}>
                {c.partnerCode} · {localized(c.partnerName)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("pricing.newAgreement")} testId="new-agreement" />
      <Table aria-label={t("pricing.agreements")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pricing.customer")}</TableHead>
            <TableHead>{t("pricing.reference")}</TableHead>
            <TableHead>{t("pricing.forWhat")}</TableHead>
            <TableHead className="text-end">{t("pricing.fromQuantity")}</TableHead>
            <TableHead className="text-end">{t("pricing.agreed")}</TableHead>
            <TableHead>{t("pricing.validity")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(agreements.data ?? []).map((a) => (
            <TableRow key={a.id} data-testid="agreement-row">
              <TableCell dir="auto">{refLabel(a.partner)}</TableCell>
              <TableCell dir="ltr">{a.reference ?? ""}</TableCell>
              <TableCell dir="auto">{a.item ? refLabel(a.item) : t("pricing.categoryNamed", { name: refLabel(a.category) })}</TableCell>
              <TableCell className="text-end tabular">
                {formatNumber(a.minQuantity)} {a.uomCode ?? ""}
              </TableCell>
              <TableCell className="text-end tabular" dir="ltr">
                {a.price !== null ? price(a.price, a.currency ?? "") : `−${formatNumber(a.discountPct ?? 0)}%`}
              </TableCell>
              <TableCell>
                <Validity from={a.validFrom} to={a.validTo} />
                {!a.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={a.reference ?? a.partner.code} onEdit={() => { edit(a); }} onDelete={() => { remove.mutation.mutate(a.id); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <RuleDialog title={form?.id ? t("pricing.editAgreement") : t("pricing.newAgreement")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { mutation.mutate(form); } }} busy={mutation.isPending} problem={problem} testId="save-agreement">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("pricing.customer")} required>
              <SelectField value={form.partnerId} onChange={(e) => { patch({ partnerId: e.target.value }); }} required data-testid="agreement-customer">
                <option value="">—</option>
                {(customers.data ?? []).map((c) => (
                  <option key={c.partnerId} value={c.partnerId}>
                    {c.partnerCode} · {localized(c.partnerName)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("pricing.reference")}>
              <TextField value={form.reference} onChange={(e) => { patch({ reference: e.target.value }); }} dir="ltr" data-testid="agreement-reference" />
            </Field>
            <Field label={t("pricing.agreementKind")}>
              <SelectField value={form.kind} onChange={(e) => { patch({ kind: e.target.value === "discount" ? "discount" : "price", onCategory: e.target.value === "price" ? false : form.onCategory }); }} data-testid="agreement-kind">
                <option value="price">{t("pricing.agreedPrice")}</option>
                <option value="discount">{t("pricing.agreedDiscount")}</option>
              </SelectField>
            </Field>
            {form.kind === "discount" ? (
              <label className="flex items-center gap-2 self-end pb-2 text-sm">
                <input type="checkbox" checked={form.onCategory} onChange={(e) => { patch({ onCategory: e.target.checked }); }} />
                {t("pricing.onWholeCategory")}
              </label>
            ) : null}
            {form.onCategory ? (
              <Field label={t("pricing.category")} required>
                <SelectField value={form.categoryId} onChange={(e) => { patch({ categoryId: e.target.value }); }} required>
                  <option value="">—</option>
                  {(categories.data ?? []).map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.code} · {localized(c.name)}
                    </option>
                  ))}
                </SelectField>
              </Field>
            ) : (
              <Field label={t("pricing.item")} required>
                <ItemCodeField value={form.itemCode} onChange={(code) => { patch({ itemCode: code }); }} required data-testid="agreement-item" />
              </Field>
            )}
            {form.kind === "price" ? (
              <>
                <Field label={t("pricing.uom")}>
                  <SelectField value={form.uomId} onChange={(e) => { patch({ uomId: e.target.value }); }} data-testid="agreement-uom">
                    <option value="">{t("pricing.salesUnit")}</option>
                    {(itemUoms.data?.uoms ?? []).map((u) => (
                      <option key={u.uomId} value={u.uomId}>
                        {u.uomCode}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("pricing.price")} required>
                  <TextField type="number" min={0} step="any" value={form.price} onChange={(e) => { patch({ price: e.target.value }); }} required dir="ltr" data-testid="agreement-price" />
                </Field>
                <Field label={t("pricing.currency")}>
                  <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} maxLength={3} dir="ltr" />
                </Field>
              </>
            ) : (
              <Field label={t("pricing.discountPct")} required>
                <TextField type="number" min={0} max={100} step="any" value={form.discountPct} onChange={(e) => { patch({ discountPct: e.target.value }); }} required dir="ltr" data-testid="agreement-discount" />
              </Field>
            )}
            <Field label={t("pricing.fromQuantity")}>
              <TextField type="number" min={0} step="any" value={form.minQuantity} onChange={(e) => { patch({ minQuantity: e.target.value }); }} dir="ltr" data-testid="agreement-min" />
            </Field>
            <Field label={t("pricing.validFrom")}>
              <TextField type="date" value={form.validFrom} onChange={(e) => { patch({ validFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validTo")}>
              <TextField type="date" value={form.validTo} onChange={(e) => { patch({ validTo: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
        ) : null}
      </RuleDialog>
    </>
  );
}

// ------------------------------------------------------------------ discount rules

interface RuleForm extends ScopeForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  level: string;
  valueType: string;
  value: string;
  currency: string;
  combination: string;
  priority: string;
  paymentTermsId: string;
  minQuantity: string;
  minAmount: string;
  weekdays: number[];
  validFrom: string;
  validTo: string;
  isActive: boolean;
}

function RulesTab({ companyId, currency }: { companyId: string; currency: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("pricing.promotion.manage");
  const [form, setForm] = useState<RuleForm | null>(null);
  const customers = useCompanyCustomers(companyId);
  const terms = usePaymentTerms();
  const rules = useQuery({ queryKey: ["discount-rules", companyId], queryFn: async () => unwrap(await api.GET("/api/v1/pricing/discount-rules", { params: { query: { companyId } } })) });
  const { mutation, problem, setProblem } = useSaver("discount-rules", async (f: RuleForm) => {
    const line = f.level === "line";
    const body = {
      companyId,
      code: f.code,
      name: { en: f.nameEn, ar: f.nameAr || f.nameEn },
      level: f.level,
      valueType: f.valueType,
      value: Number(f.value),
      currency: f.valueType !== "percentage" || f.minAmount ? f.currency || null : null,
      combination: f.combination,
      priority: Number(f.priority || "100"),
      itemId: line ? await itemIdOf(f.itemCode, t) : null,
      categoryId: line ? f.categoryId || null : null,
      brandId: line ? f.brandId || null : null,
      partnerId: f.partnerId || null,
      customerGroupId: f.customerGroupId || null,
      channel: f.channel || null,
      paymentTermsId: f.paymentTermsId || null,
      minQuantity: line ? n(f.minQuantity) : null,
      minAmount: n(f.minAmount),
      weekdays: f.weekdays.length > 0 ? f.weekdays : null,
      validFrom: f.validFrom || null,
      validTo: f.validTo || null,
      isActive: f.isActive,
    };
    return f.id ? unwrap(await api.PUT("/api/v1/pricing/discount-rules/{ruleId}", { params: { path: { ruleId: f.id } }, body })) : unwrap(await api.POST("/api/v1/pricing/discount-rules", { body }));
  }, () => { setForm(null); });
  const remove = useSaver("discount-rules", async (id: string) => { unwrap(await api.DELETE("/api/v1/pricing/discount-rules/{ruleId}", { params: { path: { ruleId: id } } })); }, () => undefined);
  const edit = (r: DiscountRule | null): void => {
    setProblem(null);
    setForm(r
      ? { id: r.id, code: r.code, nameEn: r.name.en ?? "", nameAr: r.name.ar ?? "", level: r.level, valueType: r.valueType, value: String(r.value), currency: r.currency ?? currency, combination: r.combination, priority: String(r.priority), itemCode: r.item?.code ?? "", categoryId: r.category?.id ?? "", brandId: r.brand?.id ?? "", partnerId: r.partner?.id ?? "", customerGroupId: r.customerGroup?.id ?? "", channel: r.channel ?? "", paymentTermsId: r.paymentTerms?.id ?? "", minQuantity: s(r.minQuantity), minAmount: s(r.minAmount), weekdays: (r.weekdays ?? []).map(Number), validFrom: r.validFrom ?? "", validTo: r.validTo ?? "", isActive: r.isActive }
      : { id: null, code: "", nameEn: "", nameAr: "", level: "line", valueType: "percentage", value: "", currency, combination: "exclusive", priority: "100", itemCode: "", categoryId: "", brandId: "", partnerId: "", customerGroupId: "", channel: "", paymentTermsId: "", minQuantity: "", minAmount: "", weekdays: [], validFrom: "", validTo: "", isActive: true });
  };
  const patch = (p: Partial<RuleForm>): void => { setForm((prev) => (prev ? { ...prev, ...p } : prev)); };
  const valueText = (r: DiscountRule): string => (r.valueType === "percentage" ? `${formatNumber(r.value)}%` : r.valueType === "amount" ? `−${price(r.value, r.currency ?? "")}` : `= ${price(r.value, r.currency ?? "")}`);

  return (
    <>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("pricing.newRule")} testId="new-rule" />
      <Table aria-label={t("pricing.discountRules")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pricing.code")}</TableHead>
            <TableHead>{t("pricing.level")}</TableHead>
            <TableHead>{t("pricing.appliesTo")}</TableHead>
            <TableHead className="text-end">{t("pricing.ruleValue")}</TableHead>
            <TableHead>{t("pricing.combination")}</TableHead>
            <TableHead className="text-end">{t("pricing.priority")}</TableHead>
            <TableHead>{t("pricing.validity")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(rules.data ?? []).map((r) => (
            <TableRow key={r.id} data-testid="rule-row">
              <TableCell>
                <span className="font-medium" dir="ltr">
                  {r.code}
                </span>{" "}
                <span className="text-fg-muted" dir="auto">
                  {localized(r.name)}
                </span>
              </TableCell>
              <TableCell>{t(`pricing.value.${r.level}`)}</TableCell>
              <TableCell dir="auto" className="text-xs">
                {scopeText(t, [refLabel(r.item), refLabel(r.category), refLabel(r.brand), refLabel(r.partner), refLabel(r.customerGroup), r.channel, refLabel(r.paymentTerms), r.minQuantity ? t("pricing.minQuantityShort", { value: r.minQuantity }) : null, r.minAmount ? t("pricing.minAmountShort", { value: price(r.minAmount, r.currency ?? "") }) : null])}
              </TableCell>
              <TableCell className="text-end tabular" dir="ltr">
                {valueText(r)}
              </TableCell>
              <TableCell>{t(`pricing.value.${r.combination}`)}</TableCell>
              <TableCell className="text-end tabular">{r.priority}</TableCell>
              <TableCell>
                <Validity from={r.validFrom} to={r.validTo} />
                {!r.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={r.code} onEdit={() => { edit(r); }} onDelete={() => { remove.mutation.mutate(r.id); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <RuleDialog title={form?.id ? t("pricing.editRule") : t("pricing.newRule")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { mutation.mutate(form); } }} busy={mutation.isPending} problem={problem} testId="save-rule">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("pricing.code")} required>
              <TextField value={form.code} onChange={(e) => { patch({ code: e.target.value }); }} required dir="ltr" data-testid="rule-code" />
            </Field>
            <Field label={t("pricing.nameEn")} required>
              <TextField value={form.nameEn} onChange={(e) => { patch({ nameEn: e.target.value }); }} required data-testid="rule-name" />
            </Field>
            <Field label={t("pricing.nameAr")}>
              <TextField value={form.nameAr} onChange={(e) => { patch({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("pricing.level")}>
              <SelectField value={form.level} onChange={(e) => { patch({ level: e.target.value, valueType: e.target.value === "document" && form.valueType === "fixed_price" ? "percentage" : form.valueType }); }} data-testid="rule-level">
                <option value="line">{t("pricing.value.line")}</option>
                <option value="document">{t("pricing.value.document")}</option>
              </SelectField>
            </Field>
            <Field label={t("pricing.valueType")}>
              <SelectField value={form.valueType} onChange={(e) => { patch({ valueType: e.target.value }); }} data-testid="rule-value-type">
                {["percentage", "amount", ...(form.level === "line" ? ["fixed_price"] : [])].map((v) => (
                  <option key={v} value={v}>
                    {t(`pricing.value.${v}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={form.valueType === "percentage" ? t("pricing.discountPct") : t("pricing.amountPerBaseUnit")} required>
              <TextField type="number" min={0} step="any" value={form.value} onChange={(e) => { patch({ value: e.target.value }); }} required dir="ltr" data-testid="rule-value" />
            </Field>
            <Field label={t("pricing.combination")} description={t("pricing.combinationHint")}>
              <SelectField value={form.combination} onChange={(e) => { patch({ combination: e.target.value }); }} data-testid="rule-combination">
                <option value="exclusive">{t("pricing.value.exclusive")}</option>
                <option value="stackable">{t("pricing.value.stackable")}</option>
              </SelectField>
            </Field>
            <Field label={t("pricing.priority")}>
              <TextField type="number" min={0} value={form.priority} onChange={(e) => { patch({ priority: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.currency")}>
              <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} maxLength={3} dir="ltr" />
            </Field>
            <ScopeFields form={form} patch={patch} items={form.level === "line"} />
            <Field label={t("pricing.customer")}>
              <SelectField value={form.partnerId} onChange={(e) => { patch({ partnerId: e.target.value }); }}>
                <option value="">{t("pricing.any")}</option>
                {(customers.data ?? []).map((c) => (
                  <option key={c.partnerId} value={c.partnerId}>
                    {c.partnerCode} · {localized(c.partnerName)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("pricing.paymentTerms")}>
              <SelectField value={form.paymentTermsId} onChange={(e) => { patch({ paymentTermsId: e.target.value }); }}>
                <option value="">{t("pricing.any")}</option>
                {(terms.data ?? []).map((term) => (
                  <option key={term.id} value={term.id}>
                    {term.code} · {localized(term.name)}
                  </option>
                ))}
              </SelectField>
            </Field>
            {form.level === "line" ? (
              <Field label={t("pricing.minQuantity")}>
                <TextField type="number" min={0} step="any" value={form.minQuantity} onChange={(e) => { patch({ minQuantity: e.target.value }); }} dir="ltr" />
              </Field>
            ) : null}
            <Field label={t("pricing.minAmount")}>
              <TextField type="number" min={0} step="any" value={form.minAmount} onChange={(e) => { patch({ minAmount: e.target.value }); }} dir="ltr" data-testid="rule-min-amount" />
            </Field>
            <Field label={t("pricing.validFrom")}>
              <TextField type="date" value={form.validFrom} onChange={(e) => { patch({ validFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validTo")}>
              <TextField type="date" value={form.validTo} onChange={(e) => { patch({ validTo: e.target.value }); }} dir="ltr" />
            </Field>
            <fieldset className="flex flex-wrap items-center gap-3 text-sm sm:col-span-3">
              <legend className="mb-1 text-sm font-medium">{t("pricing.weekdays")}</legend>
              {[0, 1, 2, 3, 4, 5, 6].map((day) => (
                <label key={day} className="flex items-center gap-1">
                  <input type="checkbox" checked={form.weekdays.includes(day)} onChange={(e) => { patch({ weekdays: e.target.checked ? [...form.weekdays, day].sort((a, b) => a - b) : form.weekdays.filter((d) => d !== day) }); }} />
                  {t(`pricing.weekday.${String(day)}`)}
                </label>
              ))}
            </fieldset>
          </div>
        ) : null}
      </RuleDialog>
    </>
  );
}

// ------------------------------------------------------------------ promotions

interface PromotionForm extends ScopeForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
  couponCode: string;
  buyQuantity: string;
  getItemCode: string;
  getQuantity: string;
  getDiscountPct: string;
  maxApplications: string;
  bundlePrice: string;
  currency: string;
  discountPct: string;
  combination: string;
  priority: string;
  usageLimit: string;
  usageLimitPerCustomer: string;
  validFrom: string;
  validTo: string;
  isActive: boolean;
  components: { itemCode: string; quantity: string }[];
  tiers: { minQuantity: string; discountPct: string }[];
}

function PromotionsTab({ companyId, currency }: { companyId: string; currency: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("pricing.promotion.manage");
  const [form, setForm] = useState<PromotionForm | null>(null);
  const promotions = useQuery({ queryKey: ["promotions", companyId], queryFn: async () => unwrap(await api.GET("/api/v1/pricing/promotions", { params: { query: { companyId } } })) });
  const { mutation, problem, setProblem } = useSaver("promotions", async (f: PromotionForm) => {
    const components = f.kind === "bundle" ? await Promise.all(f.components.filter((c) => c.itemCode.trim()).map(async (c) => ({ itemId: (await itemIdOf(c.itemCode, t)) ?? "", quantity: Number(c.quantity) }))) : null;
    const body = {
      companyId,
      code: f.code,
      name: { en: f.nameEn, ar: f.nameAr || f.nameEn },
      kind: f.kind,
      couponCode: f.couponCode || null,
      itemId: await itemIdOf(f.itemCode, t),
      categoryId: f.categoryId || null,
      brandId: f.brandId || null,
      partnerId: f.partnerId || null,
      customerGroupId: f.customerGroupId || null,
      channel: f.channel || null,
      buyQuantity: f.kind === "buy_x_get_y" ? n(f.buyQuantity) : null,
      getItemId: f.kind === "buy_x_get_y" ? await itemIdOf(f.getItemCode, t) : null,
      getQuantity: f.kind === "buy_x_get_y" ? n(f.getQuantity) : null,
      getDiscountPct: f.kind === "buy_x_get_y" ? n(f.getDiscountPct) : null,
      maxApplications: f.kind === "buy_x_get_y" ? n(f.maxApplications) : null,
      bundlePrice: f.kind === "bundle" ? n(f.bundlePrice) : null,
      currency: f.kind === "bundle" ? f.currency || null : null,
      discountPct: f.kind === "coupon" ? n(f.discountPct) : null,
      combination: f.combination,
      priority: Number(f.priority || "100"),
      usageLimit: n(f.usageLimit),
      usageLimitPerCustomer: n(f.usageLimitPerCustomer),
      validFrom: f.validFrom || null,
      validTo: f.validTo || null,
      isActive: f.isActive,
      components,
      tiers: f.kind === "volume_tier" ? f.tiers.filter((tier) => tier.minQuantity.trim()).map((tier) => ({ minQuantity: Number(tier.minQuantity), discountPct: Number(tier.discountPct) })) : null,
    };
    return f.id ? unwrap(await api.PUT("/api/v1/pricing/promotions/{promotionId}", { params: { path: { promotionId: f.id } }, body })) : unwrap(await api.POST("/api/v1/pricing/promotions", { body }));
  }, () => { setForm(null); });
  const remove = useSaver("promotions", async (id: string) => { unwrap(await api.DELETE("/api/v1/pricing/promotions/{promotionId}", { params: { path: { promotionId: id } } })); }, () => undefined);
  const edit = (p: Promotion | null): void => {
    setProblem(null);
    setForm(p
      ? {
          id: p.id, code: p.code, nameEn: p.name.en ?? "", nameAr: p.name.ar ?? "", kind: p.kind, couponCode: p.couponCode ?? "", itemCode: p.item?.code ?? "", categoryId: p.category?.id ?? "", brandId: p.brand?.id ?? "", partnerId: p.partner?.id ?? "", customerGroupId: p.customerGroup?.id ?? "", channel: p.channel ?? "",
          buyQuantity: s(p.buyQuantity), getItemCode: p.getItem?.code ?? "", getQuantity: s(p.getQuantity), getDiscountPct: s(p.getDiscountPct), maxApplications: s(p.maxApplications), bundlePrice: s(p.bundlePrice), currency: p.currency ?? currency, discountPct: s(p.discountPct), combination: p.combination, priority: String(p.priority),
          usageLimit: s(p.usageLimit), usageLimitPerCustomer: s(p.usageLimitPerCustomer), validFrom: p.validFrom ?? "", validTo: p.validTo ?? "", isActive: p.isActive,
          components: p.components.map((c) => ({ itemCode: c.itemCode, quantity: String(c.quantity) })), tiers: p.tiers.map((tier) => ({ minQuantity: String(tier.minQuantity), discountPct: String(tier.discountPct) })),
        }
      : { id: null, code: "", nameEn: "", nameAr: "", kind: "buy_x_get_y", couponCode: "", itemCode: "", categoryId: "", brandId: "", partnerId: "", customerGroupId: "", channel: "", buyQuantity: "", getItemCode: "", getQuantity: "1", getDiscountPct: "100", maxApplications: "", bundlePrice: "", currency, discountPct: "", combination: "exclusive", priority: "100", usageLimit: "", usageLimitPerCustomer: "", validFrom: "", validTo: "", isActive: true, components: [{ itemCode: "", quantity: "1" }, { itemCode: "", quantity: "1" }], tiers: [{ minQuantity: "", discountPct: "" }] });
  };
  const patch = (p: Partial<PromotionForm>): void => { setForm((prev) => (prev ? { ...prev, ...p } : prev)); };
  const offer = (p: Promotion): string => {
    switch (p.kind) {
      case "buy_x_get_y":
        return Number(p.getDiscountPct ?? 100) === 100
          ? t("pricing.offer.buyXGetYFree", { buy: p.buyQuantity ?? 0, get: p.getQuantity ?? 0, item: p.getItem?.code ?? p.item?.code ?? "" })
          : t("pricing.offer.buyXGetY", { buy: p.buyQuantity ?? 0, get: p.getQuantity ?? 0, item: p.getItem?.code ?? p.item?.code ?? "", pct: p.getDiscountPct ?? 100 });
      case "bundle":
        return t("pricing.offer.bundle", { items: p.components.map((c) => `${String(c.quantity)} × ${c.itemCode}`).join(" + "), price: price(p.bundlePrice, p.currency ?? "") });
      case "volume_tier":
        return p.tiers.map((tier) => t("pricing.offer.tier", { quantity: tier.minQuantity, pct: tier.discountPct })).join(" · ");
      default:
        return t("pricing.offer.coupon", { pct: p.discountPct ?? 0 });
    }
  };

  return (
    <>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("pricing.newPromotion")} testId="new-promotion" />
      <Table aria-label={t("pricing.promotions")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pricing.code")}</TableHead>
            <TableHead>{t("pricing.kind")}</TableHead>
            <TableHead>{t("pricing.offerLabel")}</TableHead>
            <TableHead>{t("pricing.appliesTo")}</TableHead>
            <TableHead>{t("pricing.coupon")}</TableHead>
            <TableHead className="text-end">{t("pricing.used")}</TableHead>
            <TableHead>{t("pricing.validity")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(promotions.data ?? []).map((p) => (
            <TableRow key={p.id} data-testid="promotion-row">
              <TableCell>
                <span className="font-medium" dir="ltr">
                  {p.code}
                </span>{" "}
                <span className="text-fg-muted" dir="auto">
                  {localized(p.name)}
                </span>
              </TableCell>
              <TableCell>{t(`pricing.value.${p.kind}`)}</TableCell>
              <TableCell dir="auto" className="text-sm">
                {offer(p)}
              </TableCell>
              <TableCell dir="auto" className="text-xs">
                {scopeText(t, [refLabel(p.item), refLabel(p.category), refLabel(p.brand), refLabel(p.partner), refLabel(p.customerGroup), p.channel])}
              </TableCell>
              <TableCell dir="ltr">{p.couponCode ?? ""}</TableCell>
              <TableCell className="text-end tabular">
                {p.used}
                {p.usageLimit ? ` / ${String(p.usageLimit)}` : ""}
              </TableCell>
              <TableCell>
                <Validity from={p.validFrom} to={p.validTo} />
                {!p.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={p.code} onEdit={() => { edit(p); }} onDelete={() => { remove.mutation.mutate(p.id); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <FormError message={remove.problem?.message ?? null} />
      <RuleDialog title={form?.id ? t("pricing.editPromotion") : t("pricing.newPromotion")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { mutation.mutate(form); } }} busy={mutation.isPending} problem={problem} testId="save-promotion">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t("pricing.code")} required>
              <TextField value={form.code} onChange={(e) => { patch({ code: e.target.value }); }} required dir="ltr" data-testid="promotion-code" />
            </Field>
            <Field label={t("pricing.nameEn")} required>
              <TextField value={form.nameEn} onChange={(e) => { patch({ nameEn: e.target.value }); }} required data-testid="promotion-name" />
            </Field>
            <Field label={t("pricing.nameAr")}>
              <TextField value={form.nameAr} onChange={(e) => { patch({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
            </Field>
            <Field label={t("pricing.kind")}>
              <SelectField value={form.kind} onChange={(e) => { patch({ kind: e.target.value }); }} data-testid="promotion-kind">
                {["buy_x_get_y", "bundle", "volume_tier", "coupon"].map((k) => (
                  <option key={k} value={k}>
                    {t(`pricing.value.${k}`)}
                  </option>
                ))}
              </SelectField>
            </Field>
            <Field label={t("pricing.coupon")} description={form.kind === "coupon" ? undefined : t("pricing.couponHint")} required={form.kind === "coupon"}>
              <TextField value={form.couponCode} onChange={(e) => { patch({ couponCode: e.target.value.toUpperCase() }); }} required={form.kind === "coupon"} dir="ltr" data-testid="promotion-coupon" />
            </Field>
            <Field label={t("pricing.combination")}>
              <SelectField value={form.combination} onChange={(e) => { patch({ combination: e.target.value }); }}>
                <option value="exclusive">{t("pricing.value.exclusive")}</option>
                <option value="stackable">{t("pricing.value.stackable")}</option>
              </SelectField>
            </Field>
            {form.kind !== "bundle" ? <ScopeFields form={form} patch={patch} customer={false} /> : null}
            {form.kind === "buy_x_get_y" ? (
              <>
                <Field label={t("pricing.buyQuantity")} required>
                  <TextField type="number" min={0} step="any" value={form.buyQuantity} onChange={(e) => { patch({ buyQuantity: e.target.value }); }} required dir="ltr" data-testid="promotion-buy" />
                </Field>
                <Field label={t("pricing.getItem")} description={t("pricing.getItemHint")}>
                  <ItemCodeField value={form.getItemCode} onChange={(code) => { patch({ getItemCode: code }); }} data-testid="promotion-get-item" />
                </Field>
                <Field label={t("pricing.getQuantity")} required>
                  <TextField type="number" min={0} step="any" value={form.getQuantity} onChange={(e) => { patch({ getQuantity: e.target.value }); }} required dir="ltr" data-testid="promotion-get" />
                </Field>
                <Field label={t("pricing.getDiscountPct")}>
                  <TextField type="number" min={0} max={100} step="any" value={form.getDiscountPct} onChange={(e) => { patch({ getDiscountPct: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("pricing.maxApplications")}>
                  <TextField type="number" min={1} value={form.maxApplications} onChange={(e) => { patch({ maxApplications: e.target.value }); }} dir="ltr" />
                </Field>
              </>
            ) : null}
            {form.kind === "coupon" ? (
              <Field label={t("pricing.discountPct")} required>
                <TextField type="number" min={0} max={100} step="any" value={form.discountPct} onChange={(e) => { patch({ discountPct: e.target.value }); }} required dir="ltr" data-testid="promotion-discount" />
              </Field>
            ) : null}
            {form.kind === "bundle" ? (
              <>
                <Field label={t("pricing.bundlePrice")} required>
                  <TextField type="number" min={0} step="any" value={form.bundlePrice} onChange={(e) => { patch({ bundlePrice: e.target.value }); }} required dir="ltr" data-testid="promotion-bundle-price" />
                </Field>
                <Field label={t("pricing.currency")}>
                  <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} maxLength={3} dir="ltr" />
                </Field>
                <fieldset className="flex flex-col gap-2 sm:col-span-3">
                  <legend className="mb-1 text-sm font-medium">{t("pricing.components")}</legend>
                  {form.components.map((c, index) => (
                    <div key={index} className="grid gap-2 sm:grid-cols-[1fr_8rem_auto]">
                      <ItemCodeField aria-label={t("pricing.item")} value={c.itemCode} onChange={(code) => { patch({ components: form.components.map((x, i) => (i === index ? { ...x, itemCode: code } : x)) }); }} data-testid={`component-item-${String(index)}`} />
                      <TextField aria-label={t("pricing.quantity")} type="number" min={0} step="any" value={c.quantity} onChange={(e) => { patch({ components: form.components.map((x, i) => (i === index ? { ...x, quantity: e.target.value } : x)) }); }} dir="ltr" data-testid={`component-qty-${String(index)}`} />
                      <Button type="button" variant="ghost" size="sm" aria-label={t("pricing.removeLine")} onClick={() => { patch({ components: form.components.filter((_, i) => i !== index) }); }}>
                        <Trash2 aria-hidden="true" />
                      </Button>
                    </div>
                  ))}
                  <Button type="button" variant="secondary" size="sm" className="self-start" onClick={() => { patch({ components: [...form.components, { itemCode: "", quantity: "1" }] }); }}>
                    <Plus aria-hidden="true" />
                    {t("pricing.addComponent")}
                  </Button>
                </fieldset>
              </>
            ) : null}
            {form.kind === "volume_tier" ? (
              <fieldset className="flex flex-col gap-2 sm:col-span-3">
                <legend className="mb-1 text-sm font-medium">{t("pricing.tiers")}</legend>
                {form.tiers.map((tier, index) => (
                  <div key={index} className="grid gap-2 sm:grid-cols-[1fr_1fr_auto]">
                    <TextField aria-label={t("pricing.fromQuantity")} type="number" min={0} step="any" value={tier.minQuantity} onChange={(e) => { patch({ tiers: form.tiers.map((x, i) => (i === index ? { ...x, minQuantity: e.target.value } : x)) }); }} dir="ltr" data-testid={`tier-min-${String(index)}`} />
                    <TextField aria-label={t("pricing.discountPct")} type="number" min={0} max={100} step="any" value={tier.discountPct} onChange={(e) => { patch({ tiers: form.tiers.map((x, i) => (i === index ? { ...x, discountPct: e.target.value } : x)) }); }} dir="ltr" data-testid={`tier-pct-${String(index)}`} />
                    <Button type="button" variant="ghost" size="sm" aria-label={t("pricing.removeLine")} onClick={() => { patch({ tiers: form.tiers.filter((_, i) => i !== index) }); }}>
                      <Trash2 aria-hidden="true" />
                    </Button>
                  </div>
                ))}
                <Button type="button" variant="secondary" size="sm" className="self-start" onClick={() => { patch({ tiers: [...form.tiers, { minQuantity: "", discountPct: "" }] }); }}>
                  <Plus aria-hidden="true" />
                  {t("pricing.addTier")}
                </Button>
              </fieldset>
            ) : null}
            <Field label={t("pricing.usageLimit")}>
              <TextField type="number" min={1} value={form.usageLimit} onChange={(e) => { patch({ usageLimit: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.usageLimitPerCustomer")}>
              <TextField type="number" min={1} value={form.usageLimitPerCustomer} onChange={(e) => { patch({ usageLimitPerCustomer: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.priority")}>
              <TextField type="number" min={0} value={form.priority} onChange={(e) => { patch({ priority: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validFrom")}>
              <TextField type="date" value={form.validFrom} onChange={(e) => { patch({ validFrom: e.target.value }); }} dir="ltr" />
            </Field>
            <Field label={t("pricing.validTo")}>
              <TextField type="date" value={form.validTo} onChange={(e) => { patch({ validTo: e.target.value }); }} dir="ltr" />
            </Field>
          </div>
        ) : null}
      </RuleDialog>
    </>
  );
}

// ------------------------------------------------------------------ floors

interface FloorForm {
  id: string | null;
  onCategory: boolean;
  itemCode: string;
  categoryId: string;
  minPrice: string;
  currency: string;
  minMarginPct: string;
  onBreach: string;
  isActive: boolean;
}

function FloorsTab({ companyId, currency }: { companyId: string; currency: string }) {
  const { t } = useTranslation();
  const can = useCan();
  const mayManage = can("pricing.floor.manage");
  const [form, setForm] = useState<FloorForm | null>(null);
  const categories = useCategories();
  const floors = useQuery({ queryKey: ["price-floors", companyId], queryFn: async () => unwrap(await api.GET("/api/v1/pricing/floors", { params: { query: { companyId } } })) });
  const { mutation, problem, setProblem } = useSaver("price-floors", async (f: FloorForm) => {
    const body = { companyId, itemId: f.onCategory ? null : await itemIdOf(f.itemCode, t), categoryId: f.onCategory ? f.categoryId || null : null, minPrice: n(f.minPrice), currency: f.minPrice ? f.currency || null : null, minMarginPct: n(f.minMarginPct), onBreach: f.onBreach, isActive: f.isActive };
    return f.id ? unwrap(await api.PUT("/api/v1/pricing/floors/{floorId}", { params: { path: { floorId: f.id } }, body })) : unwrap(await api.POST("/api/v1/pricing/floors", { body }));
  }, () => { setForm(null); });
  const remove = useSaver("price-floors", async (id: string) => { unwrap(await api.DELETE("/api/v1/pricing/floors/{floorId}", { params: { path: { floorId: id } } })); }, () => undefined);
  const edit = (f: PriceFloor | null): void => {
    setProblem(null);
    setForm(f
      ? { id: f.id, onCategory: Boolean(f.category), itemCode: f.item?.code ?? "", categoryId: f.category?.id ?? "", minPrice: s(f.minPrice), currency: f.currency ?? currency, minMarginPct: s(f.minMarginPct), onBreach: f.onBreach, isActive: f.isActive }
      : { id: null, onCategory: false, itemCode: "", categoryId: "", minPrice: "", currency, minMarginPct: "", onBreach: "block", isActive: true });
  };
  const patch = (p: Partial<FloorForm>): void => { setForm((prev) => (prev ? { ...prev, ...p } : prev)); };

  return (
    <>
      <p className="mt-3 text-sm text-fg-muted">{t("pricing.floorsHint")}</p>
      <Toolbar canManage={mayManage} onNew={() => { edit(null); }} label={t("pricing.newFloor")} testId="new-floor" />
      <Table aria-label={t("pricing.floors")}>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pricing.forWhat")}</TableHead>
            <TableHead className="text-end">{t("pricing.minPrice")}</TableHead>
            <TableHead className="text-end">{t("pricing.minMarginPct")}</TableHead>
            <TableHead>{t("pricing.onBreach")}</TableHead>
            <TableHead>
              <span className="sr-only">{t("common.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {(floors.data ?? []).map((f) => (
            <TableRow key={f.id} data-testid="floor-row">
              <TableCell dir="auto">{f.item ? refLabel(f.item) : t("pricing.categoryNamed", { name: refLabel(f.category) })}</TableCell>
              <TableCell className="text-end tabular" dir="ltr">
                {f.minPrice !== null ? price(f.minPrice, f.currency ?? "") : "—"}
              </TableCell>
              <TableCell className="text-end tabular">{f.minMarginPct !== null ? `${formatNumber(f.minMarginPct)}%` : "—"}</TableCell>
              <TableCell>
                <Badge tone={f.onBreach === "block" ? "danger" : "warning"}>{t(`pricing.value.${f.onBreach}`)}</Badge>
                {!f.isActive ? <Badge tone="neutral" className="ms-2">{t("common.inactive")}</Badge> : null}
              </TableCell>
              <TableCell className="text-end">
                <RowActions canManage={mayManage} label={f.item?.code ?? f.category?.code ?? ""} onEdit={() => { edit(f); }} onDelete={() => { remove.mutation.mutate(f.id); }} />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <RuleDialog title={form?.id ? t("pricing.editFloor") : t("pricing.newFloor")} open={Boolean(form)} onClose={() => { setForm(null); }} onSubmit={() => { if (form) { mutation.mutate(form); } }} busy={mutation.isPending} problem={problem} testId="save-floor">
        {form ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="flex items-center gap-2 self-end pb-2 text-sm">
              <input type="checkbox" checked={form.onCategory} onChange={(e) => { patch({ onCategory: e.target.checked }); }} />
              {t("pricing.onWholeCategory")}
            </label>
            {form.onCategory ? (
              <Field label={t("pricing.category")} required>
                <SelectField value={form.categoryId} onChange={(e) => { patch({ categoryId: e.target.value }); }} required data-testid="floor-category">
                  <option value="">—</option>
                  {(categories.data ?? []).map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.code} · {localized(c.name)}
                    </option>
                  ))}
                </SelectField>
              </Field>
            ) : (
              <Field label={t("pricing.item")} required>
                <ItemCodeField value={form.itemCode} onChange={(code) => { patch({ itemCode: code }); }} required data-testid="floor-item" />
              </Field>
            )}
            <Field label={t("pricing.onBreach")}>
              <SelectField value={form.onBreach} onChange={(e) => { patch({ onBreach: e.target.value }); }} data-testid="floor-on-breach">
                <option value="block">{t("pricing.value.block")}</option>
                <option value="warn">{t("pricing.value.warn")}</option>
              </SelectField>
            </Field>
            <Field label={t("pricing.minPrice")} description={t("pricing.perBaseUnit")}>
              <TextField type="number" min={0} step="any" value={form.minPrice} onChange={(e) => { patch({ minPrice: e.target.value }); }} dir="ltr" data-testid="floor-min-price" />
            </Field>
            <Field label={t("pricing.currency")}>
              <TextField value={form.currency} onChange={(e) => { patch({ currency: e.target.value.toUpperCase() }); }} maxLength={3} dir="ltr" />
            </Field>
            <Field label={t("pricing.minMarginPct")} description={t("pricing.marginHint")}>
              <TextField type="number" step="any" value={form.minMarginPct} onChange={(e) => { patch({ minMarginPct: e.target.value }); }} dir="ltr" data-testid="floor-min-margin" />
            </Field>
          </div>
        ) : null}
      </RuleDialog>
    </>
  );
}
