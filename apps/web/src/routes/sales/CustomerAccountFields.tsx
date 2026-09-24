import { useTranslation } from "react-i18next";
import { localized } from "../../lib/format";
import { Field, SelectField, TextField } from "../common";
import { useWarehouses } from "../inventory/shared";
import { useDeliveryTerms, usePaymentTerms } from "../purchasing/shared";
import { useCustomerGroups, useCustomerPostingGroups, useSalesReps, type CustomerAccount } from "./shared";

/** A customer account as the form edits it: strings until the request is built, so nothing becomes a float on the way. */
export interface CustomerAccountForm {
  customerGroupId: string;
  paymentTermsId: string;
  deliveryTermsId: string;
  postingGroupId: string;
  salesRepId: string;
  defaultWarehouseId: string;
  currency: string;
  statementFrequency: string;
  creditLimit: string;
  creditExposureBasis: string;
  overdueBlockDays: string;
  isActive: boolean;
}

export const emptyCustomerAccount = (currency: string): CustomerAccountForm => ({
  customerGroupId: "", paymentTermsId: "", deliveryTermsId: "", postingGroupId: "", salesRepId: "", defaultWarehouseId: "", currency,
  statementFrequency: "monthly", creditLimit: "", creditExposureBasis: "open_ar_plus_orders", overdueBlockDays: "", isActive: true,
});

export function customerAccountForm(a: CustomerAccount): CustomerAccountForm {
  return {
    customerGroupId: a.customerGroupId ?? "",
    paymentTermsId: a.paymentTermsId ?? "",
    deliveryTermsId: a.deliveryTermsId ?? "",
    postingGroupId: a.postingGroupId ?? "",
    salesRepId: a.salesRepId ?? "",
    defaultWarehouseId: a.defaultWarehouseId ?? "",
    currency: a.currency,
    statementFrequency: a.statementFrequency,
    creditLimit: a.creditLimit === null ? "" : String(a.creditLimit),
    creditExposureBasis: a.creditExposureBasis,
    overdueBlockDays: a.overdueBlockDays === null ? "" : String(a.overdueBlockDays),
    isActive: a.isActive,
  };
}

/** The request body; an empty credit limit means no limit and an empty overdue block means none. */
export function customerAccountBody(f: CustomerAccountForm) {
  return {
    customerGroupId: f.customerGroupId || null,
    paymentTermsId: f.paymentTermsId || null,
    deliveryTermsId: f.deliveryTermsId || null,
    postingGroupId: f.postingGroupId || null,
    taxGroupId: null,
    salesRepId: f.salesRepId || null,
    defaultWarehouseId: f.defaultWarehouseId || null,
    currency: f.currency || null,
    statementFrequency: f.statementFrequency,
    creditLimit: f.creditLimit.trim() === "" ? null : f.creditLimit.trim(),
    creditExposureBasis: f.creditExposureBasis,
    overdueBlockDays: f.overdueBlockDays.trim() === "" ? null : Number(f.overdueBlockDays),
    isActive: f.isActive,
  };
}

/**
 * The account's fields for one company. Terms, rep and warehouse are the customer permission's; the credit fields are
 * the credit permission's and show read-only to anyone else, who sends them back as they stand.
 */
export function CustomerAccountFields({ form, onChange, companyId, functionalCurrency, canManage, canCredit }: { form: CustomerAccountForm; onChange: (patch: Partial<CustomerAccountForm>) => void; companyId: string; functionalCurrency: string; canManage: boolean; canCredit: boolean }) {
  const { t } = useTranslation();
  const groups = useCustomerGroups();
  const paymentTerms = usePaymentTerms();
  const deliveryTerms = useDeliveryTerms();
  const postingGroups = useCustomerPostingGroups();
  const reps = useSalesReps();
  const warehouses = useWarehouses(companyId);
  const repsHere = (reps.data ?? []).filter((r) => r.isActive && (!r.companyId || r.companyId === companyId));
  return (
    <div className="flex flex-col gap-4" data-testid="customer-account-fields">
      <fieldset className="grid gap-4 sm:grid-cols-3" disabled={!canManage}>
        <legend className="sr-only">{t("sales.accountTerms")}</legend>
        <Field label={t("sales.customerGroup")}>
          <SelectField value={form.customerGroupId} onChange={(e) => { onChange({ customerGroupId: e.target.value }); }} data-testid="customer-group">
            <option value="">{t("partners.noGroup")}</option>
            {(groups.data ?? []).filter((g) => g.isActive || g.id === form.customerGroupId).map((g) => (
              <option key={g.id} value={g.id}>
                {g.code} · {localized(g.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("partners.paymentTerms")}>
          <SelectField value={form.paymentTermsId} onChange={(e) => { onChange({ paymentTermsId: e.target.value }); }} data-testid="customer-payment-terms">
            <option value="">— {t("partners.fromGroup")}</option>
            {(paymentTerms.data ?? []).filter((p) => p.isActive || p.id === form.paymentTermsId).map((p) => (
              <option key={p.id} value={p.id}>
                {p.code} · {localized(p.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("partners.deliveryTerms")}>
          <SelectField value={form.deliveryTermsId} onChange={(e) => { onChange({ deliveryTermsId: e.target.value }); }}>
            <option value="">— {t("partners.fromGroup")}</option>
            {(deliveryTerms.data ?? []).filter((d) => d.isActive || d.id === form.deliveryTermsId).map((d) => (
              <option key={d.id} value={d.id}>
                {d.code} · {localized(d.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("partners.postingGroup")}>
          <SelectField value={form.postingGroupId} onChange={(e) => { onChange({ postingGroupId: e.target.value }); }}>
            <option value="">— {t("partners.fromGroup")}</option>
            {(postingGroups.data ?? []).map((g) => (
              <option key={g.id} value={g.id}>
                {g.code} · {localized(g.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.salesRep")}>
          <SelectField value={form.salesRepId} onChange={(e) => { onChange({ salesRepId: e.target.value }); }} data-testid="customer-rep">
            <option value="">{t("sales.noRep")}</option>
            {repsHere.map((r) => (
              <option key={r.id} value={r.id}>
                {r.code} · {localized(r.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.defaultWarehouse")}>
          <SelectField value={form.defaultWarehouseId} onChange={(e) => { onChange({ defaultWarehouseId: e.target.value }); }}>
            <option value="">—</option>
            {(warehouses.data ?? []).filter((w) => w.isActive || w.id === form.defaultWarehouseId).map((w) => (
              <option key={w.id} value={w.id}>
                {w.code} · {localized(w.name)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("partners.currency")} required>
          <TextField value={form.currency} onChange={(e) => { onChange({ currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="customer-currency" />
        </Field>
        <Field label={t("sales.statementFrequency")}>
          <SelectField value={form.statementFrequency} onChange={(e) => { onChange({ statementFrequency: e.target.value }); }}>
            {["monthly", "weekly", "none"].map((f) => (
              <option key={f} value={f}>
                {t(`sales.statementFrequencies.${f}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <label className="flex items-center gap-2 self-end text-sm">
          <input type="checkbox" checked={form.isActive} onChange={(e) => { onChange({ isActive: e.target.checked }); }} />
          {t("common.active")}
        </label>
      </fieldset>
      <fieldset className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-3" disabled={!canCredit} data-testid="credit-fields">
        <legend className="px-1 text-sm font-medium">{t("sales.credit")}</legend>
        <Field label={t("sales.creditLimit", { currency: functionalCurrency })} description={t("sales.creditLimitHint")}>
          <TextField inputMode="decimal" value={form.creditLimit} onChange={(e) => { onChange({ creditLimit: e.target.value }); }} dir="ltr" data-testid="credit-limit" />
        </Field>
        <Field label={t("sales.exposureBasis")}>
          <SelectField value={form.creditExposureBasis} onChange={(e) => { onChange({ creditExposureBasis: e.target.value }); }}>
            {["open_ar_plus_orders", "open_ar"].map((b) => (
              <option key={b} value={b}>
                {t(`sales.exposureBases.${b}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.overdueBlockDays")} description={t("sales.overdueBlockDaysHint")}>
          <TextField type="number" min={0} value={form.overdueBlockDays} onChange={(e) => { onChange({ overdueBlockDays: e.target.value }); }} dir="ltr" data-testid="overdue-block-days" />
        </Field>
        {!canCredit ? <p className="text-sm text-fg-muted sm:col-span-3">{t("sales.creditReadOnly")}</p> : null}
      </fieldset>
    </div>
  );
}
