import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { ItemCodeField } from "../inventory/ItemCodeField";
import { useTaxRegime, useTaxRegistrations } from "../tax/shared";
import { useCompanyCustomers } from "./pricing/shared";
import { num } from "./shared";
import { CodeField } from "../CodeField";

type Recurring = components["schemas"]["RecurringInvoiceSummary"];

interface LineForm {
  kind: string;
  itemCode: string;
  description: string;
  quantity: string;
  unitPrice: string;
  taxCodeId: string;
}

interface RecurringForm {
  id: string | null;
  partnerId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  cron: string;
  startsOn: string;
  endsOn: string;
  customerReference: string;
  autoPost: boolean;
  isActive: boolean;
  notes: string;
  lines: LineForm[];
}

/** The cron presets an admin picks from; the field stays editable for anything else. */
const presets = [
  { key: "monthly", cron: "0 9 1 * *" },
  { key: "quarterly", cron: "0 9 1 1,4,7,10 *" },
  { key: "yearly", cron: "0 9 1 1 *" },
  { key: "weekly", cron: "0 9 * * 1" },
];

/** Recurring invoices (roadmap 5.6b): a customer's item and service lines issued on a schedule in the company's time zone, by the daily job or by hand, posted at once or left as drafts to review. */
export function RecurringInvoicesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<RecurringForm | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const customers = useCompanyCustomers(companyId);
  const registrations = useTaxRegistrations(companyId);
  const regime = useTaxRegime((registrations.data ?? []).find((r) => r.isPrimary)?.regimeId ?? registrations.data?.[0]?.regimeId ?? "");
  const codes = useMemo(() => (regime.data?.codes ?? []).filter((c) => c.isActive && (c.appliesTo === "sales" || c.appliesTo === "both")), [regime.data]);
  const canManage = can("sales.invoice.manage");

  const list = useQuery({
    queryKey: ["recurring-invoices", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/recurring-invoices", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["recurring-invoice", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/recurring-invoices/{recurringId}", { params: { path: { recurringId: openId ?? "" } } })),
  });
  const generated = useQuery({
    queryKey: ["sales-invoices", companyId, "recurring", openId],
    enabled: Boolean(openId) && Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["recurring-invoices"], ["recurring-invoice"], ["sales-invoices"], ["ar-open-items"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: RecurringForm) => {
      const body = {
        companyId,
        partnerId: f.partnerId,
        code: f.code,
        name: { en: f.nameEn, ar: f.nameAr },
        cron: f.cron,
        startsOn: f.startsOn || null,
        endsOn: f.endsOn || null,
        customerReference: f.customerReference || null,
        autoPost: f.autoPost,
        isActive: f.isActive,
        notes: f.notes || null,
        lines: f.lines.map((l) => ({ kind: l.kind, quantity: num(l.quantity), discountPct: 0, itemCode: l.kind === "item" ? l.itemCode || null : null, description: l.description || null, unitPrice: l.unitPrice.trim() ? num(l.unitPrice) : null, taxCodeId: l.taxCodeId || null })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/sales/recurring-invoices/{recurringId}", { params: { path: { recurringId: f.id } }, body })) : unwrap(await api.POST("/api/v1/sales/recurring-invoices", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const generate = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/sales/recurring-invoices/{recurringId}/generate", { params: { path: { recurringId: id } }, body: {} })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Recurring, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("sales.invoicing.recurringCode"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("common.name"), size: 220, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "cron", accessorKey: "cron", header: t("sales.invoicing.schedule"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.cron}</span> },
      { id: "next", accessorKey: "nextRunOn", header: t("sales.invoicing.nextRun"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.nextRunOn ? formatDate(row.original.nextRunOn) : "—"}</span> },
      { id: "generated", accessorKey: "generated", header: t("sales.invoicing.generated"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.generated)}</span> },
      { id: "active", accessorKey: "isActive", header: t("common.status"), size: 110, cell: ({ row }) => <Badge tone={row.original.isActive ? "success" : "neutral"}>{row.original.isActive ? t("common.active") : t("common.inactive")}</Badge> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ id: null, partnerId: "", code: "", nameEn: "", nameAr: "", cron: presets[0]?.cron ?? "0 9 1 * *", startsOn: today(), endsOn: "", customerReference: "", autoPost: false, isActive: true, notes: "", lines: [{ kind: "service", itemCode: "", description: "", quantity: "1", unitPrice: "", taxCodeId: "" }] }); };
  const openEdit = (r: Recurring): void => {
    setProblem(null);
    setForm({ id: r.id, partnerId: r.partnerId, code: r.code, nameEn: r.name.en ?? "", nameAr: r.name.ar ?? "", cron: r.cron, startsOn: "", endsOn: r.endsOn ?? "", customerReference: r.customerReference ?? "", autoPost: r.autoPost, isActive: r.isActive, notes: r.notes ?? "", lines: r.lines.map((l) => ({ kind: l.kind, itemCode: l.itemCode ?? "", description: l.description ?? "", quantity: String(l.quantity), unitPrice: l.unitPrice === null || l.unitPrice === undefined ? "" : String(l.unitPrice), taxCodeId: l.taxCodeId ?? "" })) });
  };
  const patchLine = (index: number, change: Partial<LineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const r = detail.data;
  const issued = (generated.data ?? []).filter((i) => i.recurringInvoiceId === openId);

  return (
    <>
      <PageHeader
        title={t("nav.recurringInvoices")}
        description={t("sales.invoicing.recurringDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId || !canManage} data-testid="new-recurring-invoice">
            <Plus aria-hidden="true" />
            {t("sales.invoicing.newRecurring")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Recurring> label="nav.recurringInvoices" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.invoicing.emptyRecurring")} emptyDescription={t("sales.invoicing.emptyRecurringDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("sales.invoicing.editRecurring") : t("sales.invoicing.newRecurring")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("sales.customer")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required data-testid="recurring-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("sales.invoicing.recurringCode")} required>
                  <CodeField kind="recurring-invoice" scope={companyId} recordId={form.id} value={form.code} onChange={(v) => { setForm({ ...form, code: v.toUpperCase() }); }} required data-testid="recurring-code" />
                </Field>
                <Field label={t("sales.invoicing.customerReference")}>
                  <TextField value={form.customerReference} onChange={(e) => { setForm({ ...form, customerReference: e.target.value }); }} dir="ltr" data-testid="recurring-reference" />
                </Field>
                <Field label={t("common.nameEn")} required>
                  <TextField value={form.nameEn} onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} required data-testid="recurring-name-en" />
                </Field>
                <Field label={t("common.nameAr")} required>
                  <TextField value={form.nameAr} onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} required dir="rtl" data-testid="recurring-name-ar" />
                </Field>
                <Field label={t("sales.invoicing.schedule")} description={t("sales.invoicing.scheduleHint")}>
                  <div className="flex gap-2">
                    <SelectField aria-label={t("sales.invoicing.preset")} value={presets.find((p) => p.cron === form.cron)?.key ?? "custom"} onChange={(e) => { const preset = presets.find((p) => p.key === e.target.value); if (preset) { setForm({ ...form, cron: preset.cron }); } }} data-testid="recurring-preset">
                      {presets.map((p) => <option key={p.key} value={p.key}>{t(`sales.invoicing.presets.${p.key}`)}</option>)}
                      <option value="custom">{t("sales.invoicing.presets.custom")}</option>
                    </SelectField>
                    <TextField aria-label={t("sales.invoicing.cron")} value={form.cron} onChange={(e) => { setForm({ ...form, cron: e.target.value }); }} dir="ltr" className="w-36" data-testid="recurring-cron" />
                  </div>
                </Field>
                <Field label={t("sales.invoicing.startsOn")}>
                  <TextField type="date" value={form.startsOn} onChange={(e) => { setForm({ ...form, startsOn: e.target.value }); }} dir="ltr" data-testid="recurring-starts" />
                </Field>
                <Field label={t("sales.invoicing.endsOn")}>
                  <TextField type="date" value={form.endsOn} onChange={(e) => { setForm({ ...form, endsOn: e.target.value }); }} dir="ltr" data-testid="recurring-ends" />
                </Field>
                <div className="flex flex-col gap-2 self-end text-sm">
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.autoPost} onChange={(e) => { setForm({ ...form, autoPost: e.target.checked }); }} data-testid="recurring-auto-post" />{t("sales.invoicing.autoPost")}</label>
                  <label className="flex items-center gap-2"><input type="checkbox" checked={form.isActive} onChange={(e) => { setForm({ ...form, isActive: e.target.checked }); }} data-testid="recurring-active" />{t("common.active")}</label>
                </div>
              </div>
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
                <div className="flex gap-2">
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: [...form.lines, { kind: "item", itemCode: "", description: "", quantity: "1", unitPrice: "", taxCodeId: "" }] }); }} data-testid="add-item-line">{t("sales.invoicing.addItemLine")}</Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: [...form.lines, { kind: "service", itemCode: "", description: "", quantity: "1", unitPrice: "", taxCodeId: "" }] }); }} data-testid="add-service-line">{t("sales.invoicing.addServiceLine")}</Button>
                </div>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("sales.invoicing.kind")}</TableHead>
                    <TableHead>{t("sales.item")}</TableHead>
                    <TableHead>{t("sales.quantity")}</TableHead>
                    <TableHead>{t("sales.unitPrice")}</TableHead>
                    <TableHead>{t("tax.code")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {form.lines.map((line, index) => (
                    <TableRow key={index} data-testid="recurring-line">
                      <TableCell>{t(`sales.invoicing.lineKinds.${line.kind}`)}</TableCell>
                      <TableCell dir="auto">
                        {line.kind === "item" ? <ItemCodeField aria-label={t("sales.itemCode")} value={line.itemCode} onChange={(code) => { patchLine(index, { itemCode: code }); }} className="w-32" data-testid={`recurring-item-${String(index)}`} />
                          : <TextField aria-label={t("sales.invoicing.lineDescription")} value={line.description} onChange={(e) => { patchLine(index, { description: e.target.value }); }} className="w-56" data-testid={`recurring-description-${String(index)}`} />}
                      </TableCell>
                      <TableCell><DecimalField aria-label={t("sales.quantity")} value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`recurring-qty-${String(index)}`} /></TableCell>
                      <TableCell><DecimalField aria-label={t("sales.unitPrice")} value={line.unitPrice} onChange={(e) => { patchLine(index, { unitPrice: e.target.value }); }} dir="ltr" className="w-28" placeholder={line.kind === "item" ? t("sales.autoPrice") : ""} data-testid={`recurring-price-${String(index)}`} /></TableCell>
                      <TableCell>
                        <SelectField aria-label={t("tax.code")} value={line.taxCodeId} onChange={(e) => { patchLine(index, { taxCodeId: e.target.value }); }} data-testid={`recurring-tax-${String(index)}`}>
                          <option value="">{line.kind === "item" ? t("sales.invoicing.taxByItem") : "—"}</option>
                          {codes.map((c) => <option key={c.id} value={c.id}>{c.code} · {formatNumber(c.currentRatePct ?? 0, { maximumFractionDigits: 2 })}%</option>)}
                        </SelectField>
                      </TableCell>
                      <TableCell><Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: form.lines.filter((_, x) => x !== index) }); }}>{t("workflow.remove")}</Button></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <Field label={t("sales.notes")}>
                <TextareaField value={form.notes} onChange={(e) => { setForm({ ...form, notes: e.target.value }); }} rows={2} data-testid="recurring-notes" />
              </Field>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || form.lines.length === 0} data-testid="save-recurring-invoice">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="recurring-invoice-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.code}</span>
                  <span dir="auto">{localized(r.name)}</span>
                  <Badge tone={r.isActive ? "success" : "neutral"}>{r.isActive ? t("common.active") : t("common.inactive")}</Badge>
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.customer"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                [t("sales.invoicing.schedule"), <span key="cron" dir="ltr">{r.cron} ({r.timeZone})</span>],
                [t("sales.invoicing.nextRun"), <span key="next" dir="ltr" data-testid="recurring-next-run">{r.nextRunOn ? formatDate(r.nextRunOn) : "—"}</span>],
                [t("sales.invoicing.endsOn"), r.endsOn ? formatDate(r.endsOn) : "—"],
                [t("sales.invoicing.lastGenerated"), r.lastGeneratedOn ? formatDate(r.lastGeneratedOn) : "—"],
                [t("sales.invoicing.autoPost"), r.autoPost ? t("common.yes") : t("common.no")],
                [t("sales.invoicing.generated"), <span key="gen" data-testid="recurring-generated">{String(r.generated)}</span>],
              ]} />
              <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("sales.invoicing.kind")}</TableHead>
                    <TableHead>{t("sales.item")}</TableHead>
                    <TableHead>{t("sales.quantity")}</TableHead>
                    <TableHead>{t("sales.unitPrice")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {r.lines.map((l, index) => (
                    <TableRow key={index}>
                      <TableCell>{t(`sales.invoicing.lineKinds.${l.kind}`)}</TableCell>
                      <TableCell dir="auto">{l.kind === "item" ? l.itemCode : l.description}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })}</TableCell>
                      <TableCell className="tabular" dir="ltr">{l.unitPrice === null || l.unitPrice === undefined ? t("sales.autoPrice") : formatNumber(l.unitPrice, { maximumFractionDigits: 4 })}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <h3 className="text-sm font-semibold">{t("sales.invoicing.issued")}</h3>
              {issued.length === 0 ? <p className="text-xs text-fg-muted">{t("sales.invoicing.nothingIssued")}</p> : (
                <ul className="flex flex-col gap-1 text-sm" data-testid="issued-invoices">
                  {issued.map((i) => (
                    <li key={i.id} className="flex items-center gap-3">
                      <Link to="/sales/invoices" search={{ open: i.id }} dir="ltr" className="underline-offset-2 hover:underline">{i.number}</Link>
                      <span dir="ltr">{formatDate(i.documentDate)}</span>
                      <span>{t(`sales.statuses.${i.status}`, { defaultValue: i.status })}</span>
                    </li>
                  ))}
                </ul>
              )}
              <DialogFooter>
                {canManage ? <Button variant="secondary" onClick={() => { openEdit(r); }} data-testid="edit-recurring-invoice">{t("common.edit")}</Button> : null}
                {canManage && r.isActive ? <Button onClick={() => { generate.mutate(r.id); }} loading={generate.isPending} data-testid="generate-recurring-invoice">{t("sales.invoicing.issueNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
