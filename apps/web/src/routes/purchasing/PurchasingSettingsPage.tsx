import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { Tabs, useCompanyContext } from "../inventory/shared";
import { num, useDeliveryTerms, usePaymentTerms, useSupplierGroups, useSupplierPostingGroups, useWhtCodes } from "./shared";

type PaymentTerms = components["schemas"]["PaymentTermsSummary"];
type DeliveryTerms = components["schemas"]["DeliveryTermsSummary"];
type SupplierGroup = components["schemas"]["SupplierGroupSummary"];
type WhtCode = components["schemas"]["WhtCodeSummary"];

interface TermsForm {
  id: string | null;
  code: string;
  name: string;
  nameAr: string;
  dueBasis: string;
  dueDays: string;
  earlyDiscountPct: string;
  earlyDiscountDays: string;
  businessDaysOnly: boolean;
  lines: { sequence: string; percentage: string; days: string }[];
  isActive: boolean;
}

interface SimpleForm {
  id: string | null;
  code: string;
  name: string;
  nameAr: string;
  isActive: boolean;
  postingGroupId: string;
  paymentTermsId: string;
  deliveryTermsId: string;
  ratePct: string;
  withholdAt: string;
  thresholdAmount: string;
  thresholdCurrency: string;
}

const emptySimple = (): SimpleForm => ({ id: null, code: "", name: "", nameAr: "", isActive: true, postingGroupId: "", paymentTermsId: "", deliveryTermsId: "", ratePct: "0", withholdAt: "payment", thresholdAmount: "", thresholdCurrency: "" });
const names = (f: { name: string; nameAr: string }) => ({ en: f.name, ar: f.nameAr || f.name });

/** Purchasing settings (roadmap 4.1): payment terms with instalments and a due-date preview, delivery terms, supplier groups and withholding tax codes. */
export function PurchasingSettingsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companyId } = useCompanyContext();
  const [tab, setTab] = useState("payment");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [terms, setTerms] = useState<TermsForm | null>(null);
  const [simple, setSimple] = useState<{ kind: "delivery" | "groups" | "wht"; form: SimpleForm } | null>(null);
  const [preview, setPreview] = useState({ invoiceDate: today(), amount: "1000", currency: "USD" });
  const [schedule, setSchedule] = useState<components["schemas"]["PaymentSchedule"] | null>(null);

  const paymentTerms = usePaymentTerms();
  const deliveryTerms = useDeliveryTerms();
  const groups = useSupplierGroups();
  const whtCodes = useWhtCodes();
  const postingGroups = useSupplierPostingGroups();
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const refresh = async (key: string): Promise<void> => { await queryClient.invalidateQueries({ queryKey: [key] }); };

  const saveTerms = useMutation({
    mutationFn: async (f: TermsForm) => {
      const body = { code: f.code, name: names(f), dueBasis: f.dueBasis, dueDays: num(f.dueDays), earlyDiscountPct: num(f.earlyDiscountPct), earlyDiscountDays: num(f.earlyDiscountDays), businessDaysOnly: f.businessDaysOnly, isActive: f.isActive, lines: f.lines.map((l) => ({ sequence: num(l.sequence), percentage: num(l.percentage), days: num(l.days) })) };
      return f.id ? unwrap(await api.PUT("/api/v1/partners/payment-terms/{termsId}", { params: { path: { termsId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/payment-terms", { body }));
    },
    onSuccess: async () => { setProblem(null); setTerms(null); await refresh("payment-terms"); },
    onError: fail,
  });
  const previewSchedule = useMutation({
    mutationFn: async (termsId: string) => unwrap(await api.POST("/api/v1/partners/payment-terms/{termsId}/schedule", { params: { path: { termsId } }, body: { companyId, invoiceDate: preview.invoiceDate, amount: num(preview.amount), currency: preview.currency } })),
    onSuccess: (result) => { setProblem(null); setSchedule(result); },
    onError: fail,
  });
  const saveSimple = useMutation({
    mutationFn: async (input: { kind: "delivery" | "groups" | "wht"; form: SimpleForm }) => {
      const f = input.form;
      switch (input.kind) {
        case "delivery": {
          const body = { code: f.code, name: names(f), isActive: f.isActive };
          return f.id ? unwrap(await api.PUT("/api/v1/partners/delivery-terms/{termsId}", { params: { path: { termsId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/delivery-terms", { body }));
        }
        case "groups": {
          const body = { code: f.code, name: names(f), isActive: f.isActive, postingGroupId: f.postingGroupId || null, paymentTermsId: f.paymentTermsId || null, deliveryTermsId: f.deliveryTermsId || null };
          return f.id ? unwrap(await api.PUT("/api/v1/partners/supplier-groups/{groupId}", { params: { path: { groupId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/supplier-groups", { body }));
        }
        case "wht": {
          const body = { code: f.code, name: names(f), isActive: f.isActive, ratePct: num(f.ratePct), withholdAt: f.withholdAt, thresholdAmount: f.thresholdAmount ? num(f.thresholdAmount) : null, thresholdCurrency: f.thresholdAmount ? f.thresholdCurrency || null : null };
          return f.id ? unwrap(await api.PUT("/api/v1/partners/wht-codes/{whtCodeId}", { params: { path: { whtCodeId: f.id } }, body })) : unwrap(await api.POST("/api/v1/partners/wht-codes", { body }));
        }
      }
    },
    onSuccess: async (_, input) => {
      setProblem(null);
      setSimple(null);
      await refresh(input.kind === "delivery" ? "delivery-terms" : input.kind === "groups" ? "supplier-groups" : "wht-codes");
    },
    onError: fail,
  });

  const termsColumns = useMemo<ColumnDef<PaymentTerms, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "basis", accessorKey: "dueBasis", header: t("partners.dueBasis"), size: 180, cell: ({ row }) => t(`partners.dueBases.${row.original.dueBasis}`, { defaultValue: row.original.dueBasis }) },
      { id: "days", accessorKey: "dueDays", header: t("partners.dueDays"), size: 90, cell: ({ row }) => (row.original.lines.length > 0 ? row.original.lines.map((l) => `${String(l.percentage)}%/${String(l.days)}d`).join(" · ") : String(row.original.dueDays)) },
      { id: "discount", accessorKey: "earlyDiscountPct", header: t("partners.earlyDiscountPct"), size: 120, cell: ({ row }) => (Number(row.original.earlyDiscountPct) > 0 ? `${String(row.original.earlyDiscountPct)}% / ${String(row.original.earlyDiscountDays)}d` : "") },
      { id: "system", accessorKey: "isSystem", header: t("partners.system"), size: 80, cell: ({ row }) => (row.original.isSystem ? "✓" : "") },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );
  const deliveryColumns = useMemo<ColumnDef<DeliveryTerms, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 300, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "system", accessorKey: "isSystem", header: t("partners.system"), size: 80, cell: ({ row }) => (row.original.isSystem ? "✓" : "") },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );
  const groupColumns = useMemo<ColumnDef<SupplierGroup, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "terms", accessorKey: "paymentTermsId", header: t("partners.paymentTerms"), size: 120, cell: ({ row }) => paymentTerms.data?.find((p) => p.id === row.original.paymentTermsId)?.code ?? "" },
      { id: "delivery", accessorKey: "deliveryTermsId", header: t("partners.deliveryTerms"), size: 120, cell: ({ row }) => deliveryTerms.data?.find((d) => d.id === row.original.deliveryTermsId)?.code ?? "" },
      { id: "suppliers", accessorKey: "suppliers", header: t("partners.suppliersCount"), size: 100, cell: ({ row }) => String(row.original.suppliers) },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t, paymentTerms.data, deliveryTerms.data],
  );
  const whtColumns = useMemo<ColumnDef<WhtCode, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("partners.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (row) => localized(row.name), header: t("partners.name"), size: 240, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "rate", accessorKey: "ratePct", header: t("partners.ratePct"), size: 90, cell: ({ row }) => formatNumber(row.original.ratePct, { maximumFractionDigits: 3 }) },
      { id: "at", accessorKey: "withholdAt", header: t("partners.withholdAt"), size: 110, cell: ({ row }) => t(`partners.withholdAts.${row.original.withholdAt}`, { defaultValue: row.original.withholdAt }) },
      { id: "threshold", accessorKey: "thresholdAmount", header: t("partners.threshold"), size: 160, cell: ({ row }) => (row.original.thresholdAmount === null ? "" : `${formatNumber(row.original.thresholdAmount)} ${row.original.thresholdCurrency ?? ""}`) },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 100, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );

  const openTerms = (p: PaymentTerms | null): void => {
    setProblem(null);
    setSchedule(null);
    setTerms(p
      ? { id: p.id, code: p.code, name: p.name.en ?? "", nameAr: p.name.ar ?? "", dueBasis: p.dueBasis, dueDays: String(p.dueDays), earlyDiscountPct: String(p.earlyDiscountPct), earlyDiscountDays: String(p.earlyDiscountDays), businessDaysOnly: p.businessDaysOnly, lines: p.lines.map((l) => ({ sequence: String(l.sequence), percentage: String(l.percentage), days: String(l.days) })), isActive: p.isActive }
      : { id: null, code: "", name: "", nameAr: "", dueBasis: "invoice_date", dueDays: "30", earlyDiscountPct: "0", earlyDiscountDays: "0", businessDaysOnly: false, lines: [], isActive: true });
  };
  const openSimple = (kind: "delivery" | "groups" | "wht", row: DeliveryTerms | SupplierGroup | WhtCode | null): void => {
    setProblem(null);
    const base = emptySimple();
    if (row) {
      base.id = row.id;
      base.code = row.code;
      base.name = row.name.en ?? "";
      base.nameAr = row.name.ar ?? "";
      base.isActive = row.isActive;
      if ("postingGroupId" in row) {
        base.postingGroupId = row.postingGroupId ?? "";
        base.paymentTermsId = row.paymentTermsId ?? "";
        base.deliveryTermsId = row.deliveryTermsId ?? "";
      }
      if ("ratePct" in row) {
        base.ratePct = String(row.ratePct);
        base.withholdAt = row.withholdAt;
        base.thresholdAmount = row.thresholdAmount === null ? "" : String(row.thresholdAmount);
        base.thresholdCurrency = row.thresholdCurrency ?? "";
      }
    }
    setSimple({ kind, form: base });
  };
  const setTermsForm = (patch: Partial<TermsForm>): void => { setTerms((prev) => (prev ? { ...prev, ...patch } : prev)); };
  const setSimpleForm = (patch: Partial<SimpleForm>): void => { setSimple((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const submitTerms = (event: FormEvent): void => { event.preventDefault(); if (terms) { saveTerms.mutate(terms); } };
  const submitSimple = (event: FormEvent): void => { event.preventDefault(); if (simple) { saveSimple.mutate(simple); } };
  const newLabel = tab === "payment" ? t("partners.newPaymentTerms") : tab === "delivery" ? t("partners.newDeliveryTerms") : tab === "groups" ? t("partners.newGroup") : t("partners.newWhtCode");

  return (
    <>
      <PageHeader
        title={t("nav.purchasingSettings")}
        description={t("partners.settingsDescription")}
        actions={
          <Button onClick={() => { if (tab === "payment") { openTerms(null); } else { openSimple(tab as "delivery" | "groups" | "wht", null); } }} data-testid="new-setting">
            <Plus aria-hidden="true" />
            {newLabel}
          </Button>
        }
      />
      <Tabs
        tabs={[
          { id: "payment", label: t("partners.tabs.payment"), testId: "tab-payment" },
          { id: "delivery", label: t("partners.tabs.delivery"), testId: "tab-delivery" },
          { id: "groups", label: t("partners.tabs.groups"), testId: "tab-groups" },
          { id: "wht", label: t("partners.tabs.wht"), testId: "tab-wht" },
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === "payment" ? <DataGrid<PaymentTerms> label="partners.tabs.payment" columns={termsColumns} data={paymentTerms.data ?? []} rowKey={(row) => row.id} loading={paymentTerms.isPending} emptyTitle={t("partners.emptyTerms")} emptyDescription={t("partners.emptyTermsDescription")} onOpen={openTerms} /> : null}
      {tab === "delivery" ? <DataGrid<DeliveryTerms> label="partners.tabs.delivery" columns={deliveryColumns} data={deliveryTerms.data ?? []} rowKey={(row) => row.id} loading={deliveryTerms.isPending} emptyTitle={t("partners.emptyTerms")} emptyDescription={t("partners.emptyTermsDescription")} onOpen={(row) => { openSimple("delivery", row); }} /> : null}
      {tab === "groups" ? <DataGrid<SupplierGroup> label="partners.tabs.groups" columns={groupColumns} data={groups.data ?? []} rowKey={(row) => row.id} loading={groups.isPending} emptyTitle={t("partners.emptyTerms")} emptyDescription={t("partners.emptyTermsDescription")} onOpen={(row) => { openSimple("groups", row); }} /> : null}
      {tab === "wht" ? <DataGrid<WhtCode> label="partners.tabs.wht" columns={whtColumns} data={whtCodes.data ?? []} rowKey={(row) => row.id} loading={whtCodes.isPending} emptyTitle={t("partners.emptyTerms")} emptyDescription={t("partners.emptyTermsDescription")} onOpen={(row) => { openSimple("wht", row); }} /> : null}

      <Dialog open={Boolean(terms)} onOpenChange={(isOpen) => { if (!isOpen) { setTerms(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {terms ? (
            <form onSubmit={submitTerms} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{terms.id ? terms.code : t("partners.newPaymentTerms")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("partners.code")} required>
                  <TextField value={terms.code} onChange={(e) => { setTermsForm({ code: e.target.value }); }} dir="ltr" required data-testid="terms-code" />
                </Field>
                <Field label={t("partners.name")} required>
                  <TextField value={terms.name} onChange={(e) => { setTermsForm({ name: e.target.value }); }} required data-testid="terms-name" />
                </Field>
                <Field label={t("partners.nameAr")}>
                  <TextField value={terms.nameAr} onChange={(e) => { setTermsForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                <Field label={t("partners.dueBasis")}>
                  <SelectField value={terms.dueBasis} onChange={(e) => { setTermsForm({ dueBasis: e.target.value }); }} data-testid="terms-basis">
                    {["invoice_date", "end_of_month", "delivery"].map((b) => (
                      <option key={b} value={b}>
                        {t(`partners.dueBases.${b}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("partners.dueDays")}>
                  <TextField type="number" min={0} value={terms.dueDays} onChange={(e) => { setTermsForm({ dueDays: e.target.value }); }} dir="ltr" data-testid="terms-days" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={terms.businessDaysOnly} onChange={(e) => { setTermsForm({ businessDaysOnly: e.target.checked }); }} />
                  {t("partners.businessDaysOnly")}
                </label>
                <Field label={t("partners.earlyDiscountPct")}>
                  <TextField inputMode="decimal" value={terms.earlyDiscountPct} onChange={(e) => { setTermsForm({ earlyDiscountPct: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("partners.earlyDiscountDays")}>
                  <TextField type="number" min={0} value={terms.earlyDiscountDays} onChange={(e) => { setTermsForm({ earlyDiscountDays: e.target.value }); }} dir="ltr" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={terms.isActive} onChange={(e) => { setTermsForm({ isActive: e.target.checked }); }} />
                  {t("common.active")}
                </label>
              </div>
              <div className="flex flex-col gap-2">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-semibold">{t("partners.instalments")}</h3>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setTermsForm({ lines: [...terms.lines, { sequence: String(terms.lines.length + 1), percentage: "", days: "" }] }); }} data-testid="add-instalment">
                    {t("partners.addInstalment")}
                  </Button>
                </div>
                <p className="text-xs text-fg-muted">{t("partners.instalmentsHelp")}</p>
                {terms.lines.length > 0 ? (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("partners.sequence")}</TableHead>
                        <TableHead>{t("partners.percentage")}</TableHead>
                        <TableHead>{t("partners.days")}</TableHead>
                        <TableHead />
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {terms.lines.map((line, index) => (
                        <TableRow key={index}>
                          <TableCell><TextField aria-label={t("partners.sequence")} type="number" min={1} value={line.sequence} onChange={(e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, sequence: e.target.value } : l)) }); }} dir="ltr" className="w-20" /></TableCell>
                          <TableCell><TextField aria-label={t("partners.percentage")} inputMode="decimal" value={line.percentage} onChange={(e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, percentage: e.target.value } : l)) }); }} dir="ltr" className="w-24" data-testid={`instalment-pct-${String(index)}`} /></TableCell>
                          <TableCell><TextField aria-label={t("partners.days")} type="number" min={0} value={line.days} onChange={(e) => { setTermsForm({ lines: terms.lines.map((l, i) => (i === index ? { ...l, days: e.target.value } : l)) }); }} dir="ltr" className="w-24" data-testid={`instalment-days-${String(index)}`} /></TableCell>
                          <TableCell>
                            <Button type="button" variant="ghost" size="sm" onClick={() => { setTermsForm({ lines: terms.lines.filter((_, i) => i !== index) }); }}>
                              {t("workflow.remove")}
                            </Button>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                ) : null}
              </div>
              {terms.id ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="schedule-preview">
                  <h3 className="text-sm font-semibold">{t("partners.preview")}</h3>
                  <p className="text-xs text-fg-muted">{t("partners.previewHelp")}</p>
                  <div className="grid gap-3 sm:grid-cols-4">
                    <Field label={t("partners.invoiceDate")}>
                      <TextField type="date" value={preview.invoiceDate} onChange={(e) => { setPreview({ ...preview, invoiceDate: e.target.value }); }} dir="ltr" data-testid="preview-date" />
                    </Field>
                    <Field label={t("partners.amount")}>
                      <TextField inputMode="decimal" value={preview.amount} onChange={(e) => { setPreview({ ...preview, amount: e.target.value }); }} dir="ltr" data-testid="preview-amount" />
                    </Field>
                    <Field label={t("partners.currency")}>
                      <TextField value={preview.currency} onChange={(e) => { setPreview({ ...preview, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                    </Field>
                    <div className="flex items-end">
                      <Button type="button" variant="secondary" onClick={() => { if (terms.id) { previewSchedule.mutate(terms.id); } }} loading={previewSchedule.isPending} disabled={!companyId} data-testid="preview-schedule">
                        {t("partners.preview")}
                      </Button>
                    </div>
                  </div>
                  {schedule ? (
                    <ul className="text-sm" data-testid="schedule-lines">
                      {schedule.instalments.map((i) => (
                        <li key={i.sequence} className="flex gap-3">
                          <span className="tabular" dir="ltr">{formatDate(i.dueOn)}</span>
                          <span className="tabular" dir="ltr">{formatMoney(i.amount, preview.currency || "USD")}</span>
                          <span className="text-fg-muted">{String(i.percentage)}%</span>
                        </li>
                      ))}
                    </ul>
                  ) : null}
                </div>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setTerms(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveTerms.isPending} data-testid="save-terms">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(simple)} onOpenChange={(isOpen) => { if (!isOpen) { setSimple(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {simple ? (
            <form onSubmit={submitSimple} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{simple.form.id ? simple.form.code : newLabel}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("partners.code")} required>
                  <TextField value={simple.form.code} onChange={(e) => { setSimpleForm({ code: e.target.value }); }} dir="ltr" required data-testid="setting-code" />
                </Field>
                <Field label={t("partners.name")} required>
                  <TextField value={simple.form.name} onChange={(e) => { setSimpleForm({ name: e.target.value }); }} required data-testid="setting-name" />
                </Field>
                <Field label={t("partners.nameAr")}>
                  <TextField value={simple.form.nameAr} onChange={(e) => { setSimpleForm({ nameAr: e.target.value }); }} dir="rtl" lang="ar" />
                </Field>
                {simple.kind === "groups" ? (
                  <>
                    <Field label={t("partners.postingGroup")}>
                      <SelectField value={simple.form.postingGroupId} onChange={(e) => { setSimpleForm({ postingGroupId: e.target.value }); }}>
                        <option value="">—</option>
                        {(postingGroups.data ?? []).map((g) => (
                          <option key={g.id} value={g.id}>
                            {g.code} · {localized(g.name)}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                    <Field label={t("partners.paymentTerms")}>
                      <SelectField value={simple.form.paymentTermsId} onChange={(e) => { setSimpleForm({ paymentTermsId: e.target.value }); }} data-testid="group-payment-terms">
                        <option value="">—</option>
                        {(paymentTerms.data ?? []).filter((p) => p.isActive).map((p) => (
                          <option key={p.id} value={p.id}>
                            {p.code}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                    <Field label={t("partners.deliveryTerms")}>
                      <SelectField value={simple.form.deliveryTermsId} onChange={(e) => { setSimpleForm({ deliveryTermsId: e.target.value }); }} data-testid="group-delivery-terms">
                        <option value="">—</option>
                        {(deliveryTerms.data ?? []).filter((d) => d.isActive).map((d) => (
                          <option key={d.id} value={d.id}>
                            {d.code}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                  </>
                ) : null}
                {simple.kind === "wht" ? (
                  <>
                    <Field label={t("partners.ratePct")} required>
                      <TextField inputMode="decimal" value={simple.form.ratePct} onChange={(e) => { setSimpleForm({ ratePct: e.target.value }); }} dir="ltr" required data-testid="wht-rate" />
                    </Field>
                    <Field label={t("partners.withholdAt")}>
                      <SelectField value={simple.form.withholdAt} onChange={(e) => { setSimpleForm({ withholdAt: e.target.value }); }}>
                        {["invoice", "payment"].map((w) => (
                          <option key={w} value={w}>
                            {t(`partners.withholdAts.${w}`)}
                          </option>
                        ))}
                      </SelectField>
                    </Field>
                    <Field label={t("partners.threshold")}>
                      <TextField inputMode="decimal" value={simple.form.thresholdAmount} onChange={(e) => { setSimpleForm({ thresholdAmount: e.target.value }); }} dir="ltr" />
                    </Field>
                    <Field label={t("partners.thresholdCurrency")}>
                      <TextField value={simple.form.thresholdCurrency} onChange={(e) => { setSimpleForm({ thresholdCurrency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                    </Field>
                  </>
                ) : null}
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={simple.form.isActive} onChange={(e) => { setSimpleForm({ isActive: e.target.checked }); }} />
                  {t("common.active")}
                </label>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setSimple(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={saveSimple.isPending} data-testid="save-setting">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
