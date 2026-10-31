import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../api";
import { useCan } from "../lib/permissions";
import { localized } from "../lib/format";
import { toFormProblem, type FormProblem } from "../lib/problem";
import { CodeField } from "./CodeField";
import { Field, FormError, SelectField, TextField } from "./common";
import { useCompanyContext } from "./inventory/shared";

export type QuickKind = "customer" | "supplier" | "item";

/** What a quick create hands back: enough to select the new record where it was asked for. */
export interface QuickCreated {
  id: string;
  code: string;
  name: string;
}

interface PartnerDraft {
  code: string;
  nameEn: string;
  nameAr: string;
  phone: string;
  email: string;
}

interface ItemDraft {
  code: string;
  nameEn: string;
  nameAr: string;
  type: string;
  baseUom: string;
  listPrice: string;
}

/** The typed text seeds the name unless it looks like a code (short, no spaces, upper case). */
function seedName(initial: string | undefined): string {
  const text = (initial ?? "").trim();
  return text && !(text.length <= 12 && !/\s/u.test(text) && text === text.toUpperCase()) ? text : "";
}

/**
 * A customer, supplier or item made without leaving the document that needs it (founder programme item 1c): the
 * few fields a record must have, the code filled in, saved and handed back selected. The full record can be
 * completed later on its own screen.
 */
export function QuickCreateDialog({ kind, companyId, currency, initial, onClose, onCreated }: { kind: QuickKind; companyId: string; currency: string; initial?: string | undefined; onClose: () => void; onCreated: (created: QuickCreated) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { company } = useCompanyContext();
  // A list price or an account needs a currency: the one given, else the chosen company's.
  const money = currency || (company?.functionalCurrency ?? "");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [partner, setPartner] = useState<PartnerDraft>({ code: "", nameEn: seedName(initial), nameAr: "", phone: "", email: "" });
  const [item, setItem] = useState<ItemDraft>({ code: seedName(initial) ? "" : (initial ?? "").trim().toUpperCase(), nameEn: seedName(initial), nameAr: "", type: "stock", baseUom: "PCS", listPrice: "" });
  const uoms = useQuery({ queryKey: ["uoms"], enabled: kind === "item", queryFn: async () => unwrap(await api.GET("/api/v1/organization/uoms")) });

  const create = useMutation({
    mutationFn: async (): Promise<QuickCreated> => {
      if (kind === "item") {
        const saved = unwrap(await api.POST("/api/v1/items", {
          body: { code: item.code, name: { en: item.nameEn, ...(item.nameAr ? { ar: item.nameAr } : {}) }, type: item.type, baseUom: item.baseUom, categoryCode: null, brandCode: null, tracking: "none", expiryRequired: false, shelfLifeDays: null, fefo: false, listPrice: item.listPrice || null, listPriceCurrency: item.listPrice ? money || null : null, isActive: true, customFields: {} },
        }));
        return { id: saved.id, code: saved.code, name: localized(saved.name) };
      }
      const saved = unwrap(await api.POST("/api/v1/partners", {
        body: { code: partner.code, legalName: { en: partner.nameEn, ar: partner.nameAr || partner.nameEn }, tradeName: null, kind: "organization", isSupplier: kind === "supplier", isCustomer: kind === "customer", isEmployee: false, defaultLanguage: "en", isActive: true, email: partner.email || null, phone: partner.phone || null, website: null, notes: null, customFields: {} },
      }));
      // The account in this company, so the partner is offered on its documents at once; terms and limits can follow on the partner's screen.
      if (kind === "customer") {
        unwrap(await api.PUT("/api/v1/partners/{partnerId}/customer-accounts/{companyId}", { params: { path: { partnerId: saved.id, companyId } }, body: { customerGroupId: null, paymentTermsId: null, deliveryTermsId: null, postingGroupId: null, salesRepId: null, defaultWarehouseId: null, currency: money || null, statementFrequency: "monthly", creditLimit: null, creditExposureBasis: "open_ar_plus_orders", overdueBlockDays: null, isActive: true } }));
      } else {
        unwrap(await api.PUT("/api/v1/partners/{partnerId}/supplier-accounts/{companyId}", { params: { path: { partnerId: saved.id, companyId } }, body: { supplierGroupId: null, paymentTermsId: null, deliveryTermsId: null, postingGroupId: null, whtCodeId: null, currency: money || null, leadTimeDays: 0, priceTolerancePct: 0, qtyTolerancePct: 0, requiresPo: false, isActive: true } }));
      }
      return { id: saved.id, code: saved.code, name: localized(saved.legalName) };
    },
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: kind === "item" ? ["items"] : kind === "customer" ? ["customers"] : ["suppliers"] });
      await queryClient.invalidateQueries({ queryKey: ["item-suggestions"] });
      await queryClient.invalidateQueries({ queryKey: ["partners"] });
      onCreated(created);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    event.stopPropagation();
    setProblem(null);
    create.mutate();
  };
  const title = t(`quick.${kind}`);

  return (
    <Dialog open onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent closeLabel={t("common.close")} className="max-w-lg" data-testid="quick-create">
        <form onSubmit={submit} className="flex flex-col gap-4">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{title}</DialogTitle>
            <p className="text-sm text-fg-muted">{t("quick.hint")}</p>
          </DialogHeader>
          <FormError message={problem?.message ?? null} />
          {kind === "item" ? (
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t("inventory.itemCode")} required error={problem?.fields.code}>
                <CodeField kind="item" value={item.code} onChange={(v) => { setItem({ ...item, code: v.toUpperCase() }); }} required data-testid="quick-code" />
              </Field>
              <Field label={t("inventory.items.type")} required>
                <SelectField value={item.type} onChange={(e) => { setItem({ ...item, type: e.target.value }); }} data-testid="quick-type">
                  {["stock", "non_stock", "service"].map((type) => <option key={type} value={type}>{t(`inventory.items.types.${type}`)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("inventory.items.nameEn")} required error={problem?.fields.name}>
                <TextField value={item.nameEn} onChange={(e) => { setItem({ ...item, nameEn: e.target.value }); }} required data-testid="quick-name-en" />
              </Field>
              <Field label={t("inventory.items.nameAr")}>
                <TextField value={item.nameAr} onChange={(e) => { setItem({ ...item, nameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="quick-name-ar" />
              </Field>
              <Field label={t("inventory.items.baseUom")} required>
                <SelectField value={item.baseUom} onChange={(e) => { setItem({ ...item, baseUom: e.target.value }); }} data-testid="quick-uom">
                  {(uoms.data ?? []).map((u) => <option key={u.code} value={u.code}>{u.code} · {localized(u.name)}</option>)}
                </SelectField>
              </Field>
              <Field label={t("inventory.items.listPrice")} description={money || undefined}>
                <TextField inputMode="decimal" value={item.listPrice} onChange={(e) => { setItem({ ...item, listPrice: e.target.value }); }} dir="ltr" data-testid="quick-price" />
              </Field>
            </div>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t("partners.code")} required error={problem?.fields.code}>
                <CodeField kind={kind} value={partner.code} onChange={(v) => { setPartner({ ...partner, code: v }); }} required data-testid="quick-code" />
              </Field>
              <Field label={t("partners.phone")}>
                <TextField value={partner.phone} onChange={(e) => { setPartner({ ...partner, phone: e.target.value }); }} dir="ltr" data-testid="quick-phone" />
              </Field>
              <Field label={t("partners.legalName")} required error={problem?.fields.legalName}>
                <TextField value={partner.nameEn} onChange={(e) => { setPartner({ ...partner, nameEn: e.target.value }); }} required data-testid="quick-name-en" />
              </Field>
              <Field label={t("partners.legalNameAr")}>
                <TextField value={partner.nameAr} onChange={(e) => { setPartner({ ...partner, nameAr: e.target.value }); }} dir="rtl" lang="ar" data-testid="quick-name-ar" />
              </Field>
              <Field label={t("partners.email")}>
                <TextField type="email" value={partner.email} onChange={(e) => { setPartner({ ...partner, email: e.target.value }); }} dir="ltr" data-testid="quick-email" />
              </Field>
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="secondary" onClick={onClose}>{t("common.cancel")}</Button>
            <Button type="submit" loading={create.isPending} data-testid="quick-save">{t("quick.createAndUse")}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

const permissionOf: Record<QuickKind, string> = { customer: "partners.customer.manage", supplier: "partners.supplier.manage", item: "inventory.item.manage" };

/** The "+" beside a customer, supplier or item field: opens the quick create and selects what it made. Hidden from members who may not create the record. */
export function QuickCreateButton({ kind, companyId, currency, onCreated }: { kind: QuickKind; companyId: string; currency: string; onCreated: (created: QuickCreated) => void }) {
  const { t } = useTranslation();
  const can = useCan();
  const [open, setOpen] = useState(false);
  if (!can(permissionOf[kind]) || !companyId) {
    return null;
  }
  return (
    <>
      <Button type="button" variant="secondary" size="icon" aria-label={t(`quick.${kind}`)} title={t(`quick.${kind}`)} onClick={() => { setOpen(true); }} data-testid={`quick-create-${kind}`}>
        <Plus aria-hidden="true" />
      </Button>
      {open ? <QuickCreateDialog kind={kind} companyId={companyId} currency={currency} onClose={() => { setOpen(false); }} onCreated={(created) => { setOpen(false); onCreated(created); }} /> : null}
    </>
  );
}
