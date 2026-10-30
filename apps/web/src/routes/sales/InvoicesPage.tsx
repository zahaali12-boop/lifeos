import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Fragment, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useFollowOnSource, useOpenRecord, type FollowOnSource } from "../../lib/documents";
import { formatDate, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { CostCentreSelect, costCentreText, useLineDimensions } from "../accounting/JournalLineDetails";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { ItemCodeField } from "../inventory/ItemCodeField";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";
import { CurrencyField } from "../CurrencyField";
import { useTaxRegime, useTaxRegistrations, type TaxCode } from "../tax/shared";
import { useCompanyCustomers } from "./pricing/shared";
import { money, Money, num, SalesStatus } from "./shared";

type Invoice = components["schemas"]["SalesInvoiceSummary"];
type Invoicable = components["schemas"]["InvoicableSalesLine"];

interface LineForm {
  kind: string;
  shipmentLineId: string;
  orderLineId: string;
  invoiceLineId: string;
  itemCode: string;
  label: string;
  quantity: string;
  unitPrice: string;
  discountPct: string;
  description: string;
  taxCodeId: string;
  deferralPeriods: string;
  dimensions: Record<string, string>;
}

interface InvoiceForm {
  id: string | null;
  kind: string;
  partnerId: string;
  creditsInvoiceId: string;
  customerReference: string;
  documentDate: string;
  currency: string;
  lines: LineForm[];
}

const emptyLine = (kind: string): LineForm => ({ kind, shipmentLineId: "", orderLineId: "", invoiceLineId: "", itemCode: "", label: "", quantity: "1", unitPrice: "", discountPct: "0", description: "", taxCodeId: "", deferralPeriods: "", dimensions: {} });

/** Lines priced here (an item or a service) take a price and a discount; lines of a shipment, an order or a credited invoice take the source's. */
const pricedHere = (kind: string): boolean => kind === "item" || kind === "service";

/** Whether an open line belongs to the document an invoice is started from: an order's shipments and service lines, or one shipment's lines. */
const belongsTo = (line: Invoicable, source: FollowOnSource): boolean =>
  source.type === "sales_order" ? line.orderId === source.id : line.kind === "shipment" && line.shipmentId === source.id;

function lineFrom(line: Invoicable): LineForm {
  return { ...emptyLine(line.kind), shipmentLineId: line.shipmentLineId ?? "", orderLineId: line.orderLineId, itemCode: line.itemCode, label: `${line.itemCode} · ${line.shipmentNumber ?? line.orderNumber}`, quantity: String(line.remaining), unitPrice: String(line.unitPrice), discountPct: String(line.discountPct) };
}

const einvoiceTones: Record<string, "neutral" | "info" | "success" | "danger" | "warning"> = { not_required: "neutral", pending: "info", cleared: "success", reported: "success", rejected: "danger" };

/**
 * Sales invoices and credit notes (roadmap 5.6): lines picked from what the customer's posted shipments and service
 * orders still have to invoice (at the order's price), items priced here, service lines on a revenue account, a line's
 * revenue deferred over months; a credit note credits a posted invoice's lines and is applied to it as it posts. Posting
 * books receivables, revenue and output tax, opens the customer's open items and accrues the rep's commission.
 */
export function InvoicesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [kindFilter, setKindFilter] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<InvoiceForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/sales/invoices");
  const [tab, setTab] = useState("lines");
  const [reversal, setReversal] = useState<string | null>(null);
  const [cancelReason, setCancelReason] = useState<string | null>(null);
  const [from, clearFrom] = useFollowOnSource("/sales/invoices");
  const [pendingSource, setPendingSource] = useState<FollowOnSource | null>(null);
  const started = useRef<string | null>(null);
  const customers = useCompanyCustomers(companyId);
  const lineDimensions = useLineDimensions(companyId);
  const canManage = can("sales.invoice.manage");
  const canPost = can("sales.invoice.post");

  const list = useQuery({
    queryKey: ["sales-invoices", companyId, status, kindFilter],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId, ...(status ? { status } : {}), ...(kindFilter ? { kind: kindFilter } : {}) } } })),
  });
  const invoicable = useQuery({
    queryKey: ["sales-invoicable", companyId, form?.partnerId ?? ""],
    enabled: Boolean(companyId) && Boolean(form?.partnerId) && form?.kind === "invoice",
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices/invoicable", { params: { query: { companyId, partnerId: form?.partnerId ?? "" } } })),
  });
  // What a credit note can credit: the customer's posted invoices with their lines.
  const creditable = useQuery({
    queryKey: ["sales-invoices", companyId, "posted", form?.partnerId ?? ""],
    enabled: Boolean(companyId) && Boolean(form?.partnerId) && form?.kind === "credit_note",
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId, partnerId: form?.partnerId ?? "", status: "posted", kind: "invoice" } } })),
  });
  // The codes a service line may name: those of the company's primary tax registration.
  const registrations = useTaxRegistrations(companyId);
  const primaryRegime = (registrations.data ?? []).find((r) => r.isPrimary)?.regimeId ?? registrations.data?.[0]?.regimeId ?? "";
  const regime = useTaxRegime(primaryRegime);
  const detail = useQuery({
    queryKey: ["sales-invoice", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices/{invoiceId}", { params: { path: { invoiceId: openId ?? "" } } })),
  });
  const settlements = useQuery({
    queryKey: ["ar-settlements", companyId, openId],
    enabled: Boolean(companyId) && detail.data?.status === "posted" && detail.data.openItems.length > 0,
    queryFn: async () => unwrap(await api.GET("/api/v1/receivables/settlements", { params: { query: { companyId, openItemId: detail.data?.openItems[0]?.id ?? "" } } })),
  });
  // "Create invoice" on an order or a shipment: the customer, currency and company come from the source document.
  const sourceDocument = useQuery({
    queryKey: ["follow-on", from?.type ?? "", from?.id ?? ""],
    enabled: from?.type === "sales_order" || from?.type === "sales_shipment",
    queryFn: async () => {
      const id = from?.id ?? "";
      if (from?.type === "sales_shipment") {
        const shipment = unwrap(await api.GET("/api/v1/sales/shipments/{shipmentId}", { params: { path: { shipmentId: id } } }));
        const order = unwrap(await api.GET("/api/v1/sales/orders/{orderId}", { params: { path: { orderId: shipment.orderId } } }));
        return { id: shipment.id, companyId: shipment.companyId, partnerId: shipment.partnerId, currency: order.currency };
      }
      const doc = unwrap(await api.GET("/api/v1/sales/orders/{orderId}", { params: { path: { orderId: id } } }));
      return { id: doc.id, companyId: doc.companyId, partnerId: doc.partnerId, currency: doc.currency };
    },
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["sales-invoices"], ["sales-invoice"], ["sales-invoicable"], ["sales-orders"], ["sales-order"], ["sales-shipments"], ["sales-shipment"], ["ar-open-items"], ["ar-open-item"], ["ar-settlements"], ["ar-aging"], ["customer-360"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: InvoiceForm) => {
      const body = {
        companyId,
        partnerId: f.partnerId,
        kind: f.kind,
        creditsInvoiceId: f.kind === "credit_note" && f.creditsInvoiceId ? f.creditsInvoiceId : null,
        customerReference: f.customerReference || null,
        documentDate: f.documentDate || null,
        currency: f.currency || null,
        lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({
          kind: l.kind,
          quantity: num(l.quantity),
          shipmentLineId: l.shipmentLineId || null,
          orderLineId: l.kind === "order" ? l.orderLineId || null : null,
          invoiceLineId: l.invoiceLineId || null,
          itemCode: l.kind === "item" ? l.itemCode || null : null,
          unitPrice: pricedHere(l.kind) && l.unitPrice.trim() ? num(l.unitPrice) : null,
          discountPct: pricedHere(l.kind) ? num(l.discountPct) : 0,
          description: l.description || null,
          taxCodeId: pricedHere(l.kind) && l.taxCodeId ? l.taxCodeId : null,
          deferralPeriods: l.deferralPeriods.trim() ? Math.trunc(num(l.deferralPeriods)) : null,
          dimensions: Object.keys(l.dimensions).length > 0 ? l.dimensions : null,
        })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/sales/invoices/{invoiceId}", { params: { path: { invoiceId: f.id } }, body })) : unwrap(await api.POST("/api/v1/sales/invoices", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setTab("lines"); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "cancel" | "einvoice"; reason?: string }) => {
      const params = { path: { invoiceId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/sales/invoices/{invoiceId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/sales/invoices/{invoiceId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "cancel": return unwrap(await api.POST("/api/v1/sales/invoices/{invoiceId}/cancel", { params, body: { reason: input.reason ?? "" } }));
        case "einvoice": return unwrap(await api.POST("/api/v1/sales/invoices/{invoiceId}/einvoice/submit", { params }));
      }
    },
    onSuccess: async () => { setProblem(null); setReversal(null); setCancelReason(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Invoice, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("sales.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <SalesStatus status={row.original.status} /> },
      { id: "kind", accessorKey: "kind", header: t("sales.invoicing.kind"), size: 110, cell: ({ row }) => t(`sales.invoicing.kinds.${row.original.kind}`) },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "reference", accessorKey: "customerReference", header: t("sales.invoicing.customerReference"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.customerReference ?? ""}</span> },
      { id: "date", accessorKey: "documentDate", header: t("sales.invoicing.documentDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.documentDate)}</span> },
      { id: "due", accessorKey: "dueDate", header: t("sales.invoicing.dueDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.dueDate)}</span> },
      { id: "total", accessorKey: "totalGross", header: t("sales.total"), size: 150, cell: ({ row }) => <Money amount={row.original.totalGross} currency={row.original.currency} /> },
      { id: "open", accessorFn: (r) => r.openItems.reduce((sum, o) => sum + Number(o.remainingTc), 0), header: t("sales.invoicing.openAmount"), size: 150, cell: ({ row }) => (row.original.status === "posted" ? <Money amount={row.original.openItems.reduce((sum, o) => sum + Number(o.remainingTc), 0)} currency={row.original.currency} /> : null) },
    ],
    [t],
  );

  const openNew = (kind = "invoice"): void => { setProblem(null); setForm({ id: null, kind, partnerId: "", creditsInvoiceId: "", customerReference: "", documentDate: today(), currency: "", lines: [] }); };
  const openEdit = (i: Invoice): void => {
    setProblem(null);
    setForm({
      id: i.id, kind: i.kind, partnerId: i.partnerId, creditsInvoiceId: i.creditsInvoiceId ?? "", customerReference: i.customerReference ?? "", documentDate: i.documentDate, currency: i.currency,
      lines: i.lines.map((l) => ({ kind: l.kind, shipmentLineId: l.shipmentLineId ?? "", orderLineId: l.orderLineId ?? "", invoiceLineId: l.invoiceLineId ?? "", itemCode: l.itemCode ?? "", label: pricedHere(l.kind) ? "" : `${l.itemCode ?? ""} · ${l.shipmentNumber ?? l.creditedInvoiceNumber ?? l.orderNumber ?? ""}`, quantity: String(l.quantity), unitPrice: String(l.unitPrice), discountPct: String(l.discountPct), description: l.description ?? "", taxCodeId: l.taxCodeId ?? "", deferralPeriods: l.deferralPeriods === null || l.deferralPeriods === undefined ? "" : String(l.deferralPeriods), dimensions: { ...(l.dimensions ?? {}) } })),
    });
  };
  const addInvoicable = (line: Invoicable): void => {
    if (!form || form.lines.some((l) => (line.kind === "shipment" ? l.shipmentLineId === line.shipmentLineId : l.kind === "order" && l.orderLineId === line.orderLineId))) {
      return;
    }
    setForm({ ...form, currency: form.currency || line.currency, lines: [...form.lines, lineFrom(line)] });
  };
  const addCredited = (invoice: Invoice, line: Invoice["lines"][number]): void => {
    if (!form || form.lines.some((l) => l.invoiceLineId === line.id)) {
      return;
    }
    setForm({ ...form, creditsInvoiceId: form.creditsInvoiceId || invoice.id, currency: form.currency || invoice.currency, lines: [...form.lines, { ...emptyLine("invoice"), invoiceLineId: line.id, itemCode: line.itemCode ?? "", label: `${invoice.number} · ${line.itemCode ?? line.description ?? ""}`, quantity: String(Number(line.quantity) - Number(line.qtyCredited)), unitPrice: String(line.unitPrice), discountPct: String(line.discountPct), description: "" }] });
  };
  const source = sourceDocument.data;
  useEffect(() => {
    if (!from || source?.id !== from.id || started.current === from.id) {
      return;
    }
    started.current = from.id;
    clearFrom();
    if (source.companyId !== companyId) {
      setCompanyId(source.companyId);
    }
    setProblem(null);
    setForm({ id: null, kind: "invoice", partnerId: source.partnerId, creditsInvoiceId: "", customerReference: "", documentDate: today(), currency: source.currency, lines: [] });
    setPendingSource(from);
  }, [from, source, clearFrom, companyId, setCompanyId]);
  useEffect(() => {
    if (!pendingSource || !form || invoicable.isFetching || !invoicable.isFetchedAfterMount) {
      return;
    }
    setPendingSource(null);
    const picked = (invoicable.data ?? []).filter((line) => belongsTo(line, pendingSource));
    if (picked.length > 0) {
      setForm({ ...form, lines: picked.map(lineFrom) });
    } else {
      setProblem({ message: t("documentFlow.nothingToInvoice"), fields: {} });
    }
  }, [pendingSource, form, invoicable.isFetching, invoicable.isFetchedAfterMount, invoicable.data, t]);
  const patchLine = (index: number, change: Partial<LineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const codes: TaxCode[] = useMemo(() => (regime.data?.codes ?? []).filter((c) => c.isActive && (c.appliesTo === "sales" || c.appliesTo === "both")), [regime.data]);
  const i = detail.data;
  const openTotal = i ? i.openItems.reduce((sum, o) => sum + Number(o.remainingTc), 0) : 0;
  const commissionTotal = i?.commissions?.reduce((sum, c) => (c.status === "accrued" ? sum + Number(c.commissionAmount) : sum), 0) ?? 0;

  return (
    <>
      <PageHeader
        title={t("nav.salesInvoices")}
        description={t("sales.invoicing.description")}
        actions={
          <div className="flex gap-2">
            <Button variant="secondary" onClick={() => { openNew("credit_note"); }} disabled={!companyId || !canManage} data-testid="new-credit-note">{t("sales.invoicing.newCreditNote")}</Button>
            <Button onClick={() => { openNew(); }} disabled={!companyId || !canManage} data-testid="new-sales-invoice">
              <Plus aria-hidden="true" />
              {t("sales.invoicing.newInvoice")}
            </Button>
          </div>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "posted", "reversed", "cancelled"].map((s) => (
              <option key={s} value={s}>{t(`sales.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("sales.invoicing.kind")}>
          <SelectField value={kindFilter} onChange={(e) => { setKindFilter(e.target.value); }} data-testid="kind-filter">
            <option value="">{t("common.all")}</option>
            <option value="invoice">{t("sales.invoicing.kinds.invoice")}</option>
            <option value="credit_note">{t("sales.invoicing.kinds.credit_note")}</option>
          </SelectField>
        </Field>
      </div>
      <DataGrid<Invoice> label="nav.salesInvoices" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.invoicing.empty")} emptyDescription={t("sales.invoicing.emptyDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setCancelReason(null); setTab("lines"); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("sales.invoicing.editInvoice") : form.kind === "credit_note" ? t("sales.invoicing.newCreditNote") : t("sales.invoicing.newInvoice")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("sales.customer")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value, creditsInvoiceId: "", lines: form.lines.filter((l) => pricedHere(l.kind)) }); }} required disabled={Boolean(form.id)} data-testid="invoice-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => (
                      <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("sales.invoicing.customerReference")} description={t("sales.invoicing.customerReferenceHint")}>
                  <TextField value={form.customerReference} onChange={(e) => { setForm({ ...form, customerReference: e.target.value }); }} dir="ltr" data-testid="invoice-reference" />
                </Field>
                <Field label={t("sales.invoicing.documentDate")}>
                  <TextField type="date" value={form.documentDate} onChange={(e) => { setForm({ ...form, documentDate: e.target.value }); }} dir="ltr" data-testid="invoice-date" />
                </Field>
                <Field label={t("partners.currency")} description={t("sales.invoicing.currencyHelp")}>
                  <CurrencyField value={form.currency} onChange={(code) => { setForm({ ...form, currency: code }); }} allowEmpty data-testid="invoice-currency" />
                </Field>
              </div>
              {form.kind === "invoice" && form.partnerId ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="invoicable">
                  <h3 className="text-sm font-semibold">{t("sales.invoicing.invoicable")}</h3>
                  {(invoicable.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("sales.invoicing.nothingInvoicable")}</p> : (
                    <ul className="flex flex-col gap-1 text-sm">
                      {(invoicable.data ?? []).map((line) => (
                        <li key={line.shipmentLineId ?? line.orderLineId} className="flex flex-wrap items-center gap-3">
                          <Badge tone={line.kind === "shipment" ? "info" : "neutral"}>{t(`sales.invoicing.lineKinds.${line.kind}`)}</Badge>
                          <span dir="ltr">{line.shipmentNumber ?? line.orderNumber}</span>
                          <span dir="auto">{line.itemCode} · {localized(line.itemName)}</span>
                          <span className="tabular" dir="ltr">{formatNumber(line.remaining, { maximumFractionDigits: 3 })} {line.uomCode} × {formatNumber(line.unitPrice, { maximumFractionDigits: 4 })} {line.currency}</span>
                          <Button type="button" variant="ghost" size="sm" onClick={() => { addInvoicable(line); }} data-testid={`add-invoicable-${line.itemCode}`}>{t("sales.addLine")}</Button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ) : null}
              {form.kind === "credit_note" && form.partnerId ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="creditable">
                  <h3 className="text-sm font-semibold">{t("sales.invoicing.creditable")}</h3>
                  {(creditable.data ?? []).filter((inv) => !form.creditsInvoiceId || inv.id === form.creditsInvoiceId).length === 0 ? <p className="text-xs text-fg-muted">{t("sales.invoicing.nothingCreditable")}</p> : (
                    <ul className="flex flex-col gap-1 text-sm">
                      {(creditable.data ?? []).filter((inv) => !form.creditsInvoiceId || inv.id === form.creditsInvoiceId).flatMap((inv) => inv.lines.filter((l) => Number(l.quantity) > Number(l.qtyCredited)).map((l) => (
                        <li key={l.id} className="flex flex-wrap items-center gap-3">
                          <span dir="ltr">{inv.number}</span>
                          <span dir="auto">{l.itemCode ?? l.description ?? ""}</span>
                          <span className="tabular" dir="ltr">{formatNumber(Number(l.quantity) - Number(l.qtyCredited), { maximumFractionDigits: 3 })} {l.uomCode ?? ""} × {formatNumber(l.unitPrice, { maximumFractionDigits: 4 })} {inv.currency}</span>
                          <Button type="button" variant="ghost" size="sm" onClick={() => { addCredited(inv, l); }} data-testid={`credit-line-${String(l.lineNo)}`}>{t("sales.addLine")}</Button>
                        </li>
                      )))}
                    </ul>
                  )}
                </div>
              ) : null}
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
                <div className="flex gap-2">
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: [...form.lines, emptyLine("item")] }); }} data-testid="add-item-line">{t("sales.invoicing.addItemLine")}</Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: [...form.lines, emptyLine("service")] }); }} data-testid="add-service-line">{t("sales.invoicing.addServiceLine")}</Button>
                </div>
              </div>
              {form.lines.length > 0 ? (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("sales.invoicing.kind")}</TableHead>
                      <TableHead>{t("sales.item")}</TableHead>
                      <TableHead>{t("sales.quantity")}</TableHead>
                      <TableHead>{t("sales.unitPrice")}</TableHead>
                      <TableHead>{t("sales.discountPct")}</TableHead>
                      <TableHead>{t("tax.code")}</TableHead>
                      <TableHead>{t("sales.invoicing.deferMonths")}</TableHead>
                      {lineDimensions.dimensions.some((d) => d.code === "COST_CENTER") ? <TableHead>{t("accounting.costCentre")}</TableHead> : null}
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => (
                      <Fragment key={index}>
                        <TableRow data-testid="invoice-line">
                          <TableCell>{t(`sales.invoicing.lineKinds.${line.kind}`)}</TableCell>
                          <TableCell dir="auto">
                            {line.kind === "item" ? <ItemCodeField aria-label={t("sales.itemCode")} value={line.itemCode} onChange={(code) => { patchLine(index, { itemCode: code }); }} className="w-32" data-testid={`invoice-item-${String(index)}`} />
                              : line.kind === "service" ? <TextField aria-label={t("sales.invoicing.lineDescription")} value={line.description} onChange={(e) => { patchLine(index, { description: e.target.value }); }} className="w-56" data-testid={`invoice-description-${String(index)}`} />
                                : line.label}
                          </TableCell>
                          <TableCell><TextField aria-label={t("sales.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`invoice-qty-${String(index)}`} /></TableCell>
                          <TableCell>{pricedHere(line.kind) ? <TextField aria-label={t("sales.unitPrice")} inputMode="decimal" value={line.unitPrice} onChange={(e) => { patchLine(index, { unitPrice: e.target.value }); }} dir="ltr" className="w-28" placeholder={line.kind === "item" ? t("sales.autoPrice") : ""} data-testid={`invoice-price-${String(index)}`} /> : <span className="tabular" dir="ltr">{formatNumber(line.unitPrice, { maximumFractionDigits: 4 })}</span>}</TableCell>
                          <TableCell>{pricedHere(line.kind) ? <TextField aria-label={t("sales.discountPct")} inputMode="decimal" value={line.discountPct} onChange={(e) => { patchLine(index, { discountPct: e.target.value }); }} dir="ltr" className="w-16" data-testid={`invoice-discount-${String(index)}`} /> : <span className="tabular" dir="ltr">{formatNumber(line.discountPct, { maximumFractionDigits: 2 })}%</span>}</TableCell>
                          <TableCell>
                            {pricedHere(line.kind) ? (
                              <SelectField aria-label={t("tax.code")} value={line.taxCodeId} onChange={(e) => { patchLine(index, { taxCodeId: e.target.value }); }} data-testid={`invoice-tax-${String(index)}`}>
                                <option value="">{line.kind === "item" ? t("sales.invoicing.taxByItem") : "—"}</option>
                                {codes.map((c) => <option key={c.id} value={c.id}>{c.code} · {formatNumber(c.currentRatePct ?? 0, { maximumFractionDigits: 2 })}%</option>)}
                              </SelectField>
                            ) : null}
                          </TableCell>
                          <TableCell>{form.kind === "invoice" ? <TextField aria-label={t("sales.invoicing.deferMonths")} inputMode="numeric" value={line.deferralPeriods} onChange={(e) => { patchLine(index, { deferralPeriods: e.target.value }); }} dir="ltr" className="w-16" data-testid={`invoice-defer-${String(index)}`} /> : null}</TableCell>
                          {lineDimensions.dimensions.some((d) => d.code === "COST_CENTER") ? (
                            <TableCell><CostCentreSelect label={t("accounting.costCentreOfLine", { line: index + 1 })} values={line.dimensions} reference={lineDimensions} emptyLabel={pricedHere(line.kind) ? "—" : t("sales.invoicing.dimensionAsOrdered")} onChange={(dimensions) => { patchLine(index, { dimensions }); }} testId={`invoice-cost-centre-${String(index)}`} /></TableCell>
                          ) : null}
                          <TableCell><Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: form.lines.filter((_, x) => x !== index) }); }}>{t("workflow.remove")}</Button></TableCell>
                        </TableRow>
                      </Fragment>
                    ))}
                  </TableBody>
                </Table>
              ) : <p className="text-xs text-fg-muted">{t("sales.noLines")}</p>}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || form.lines.length === 0} data-testid="save-sales-invoice">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); setCancelReason(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {i ? (
            <div className="flex flex-col gap-4" data-testid="sales-invoice-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{i.number}</span>
                  <SalesStatus status={i.status} />
                  <Badge tone="neutral">{t(`sales.invoicing.kinds.${i.kind}`)}</Badge>
                  {i.einvoiceScheme ? <Badge tone={einvoiceTones[i.einvoiceStatus] ?? "neutral"} data-testid="einvoice-status">{t(`sales.invoicing.einvoiceStatuses.${i.einvoiceStatus}`, { defaultValue: i.einvoiceStatus })}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.customer"), `${i.partnerCode} · ${localized(i.partnerName)}`],
                [t("sales.invoicing.customerReference"), i.customerReference ?? "—"],
                [t("sales.invoicing.documentDate"), formatDate(i.documentDate)],
                [t("sales.invoicing.dueDate"), formatDate(i.dueDate) || "—"],
                [t("sales.invoicing.terms"), i.paymentTermsCode ?? "—"],
                [t("sales.net"), money(i.totalNet, i.currency)],
                [t("sales.taxAmount"), money(i.totalTax, i.currency)],
                [t("sales.total"), <span key="total" data-testid="sales-invoice-total">{money(i.totalGross, i.currency)}{Number(i.exchangeRate) !== 1 ? ` (@ ${formatNumber(i.exchangeRate, { maximumFractionDigits: 6 })})` : ""}</span>],
                ...(i.status === "posted" ? [[t("sales.invoicing.openAmount"), <span key="open" data-testid="sales-invoice-open">{money(openTotal, i.currency)}</span>] as [string, React.ReactNode]] : []),
                ...(i.creditsInvoiceNumber ? [[t("sales.invoicing.credits"), <span key="credits" dir="ltr">{i.creditsInvoiceNumber}</span>] as [string, React.ReactNode]] : []),
                ...(i.reversalReason ? [[i.status === "cancelled" ? t("sales.cancelReason") : t("sales.reversalReason"), i.reversalReason] as [string, string]] : []),
              ]} />
              <Tabs tabs={[{ id: "lines", label: t("sales.lines"), testId: "tab-lines" }, { id: "receivables", label: t("nav.receivables"), testId: "tab-receivables" }, { id: "commissions", label: t("sales.commission"), testId: "tab-commissions" }, { id: "einvoice", label: t("sales.invoicing.einvoice"), testId: "tab-einvoice" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="sales_invoice" entityId={i.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="sales_invoice" entityId={i.id} /> : null}
              {tab === "lines" ? (
                <Table data-testid="sales-invoice-lines">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("sales.invoicing.kind")}</TableHead>
                      <TableHead>{t("sales.item")}</TableHead>
                      <TableHead>{t("sales.quantity")}</TableHead>
                      <TableHead>{t("sales.unitPrice")}</TableHead>
                      <TableHead>{t("sales.net")}</TableHead>
                      <TableHead>{t("tax.code")}</TableHead>
                      <TableHead>{t("sales.taxAmount")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {i.lines.map((l) => (
                      <TableRow key={l.id} data-testid="sales-invoice-line">
                        <TableCell>{String(l.lineNo)}</TableCell>
                        <TableCell>{t(`sales.invoicing.lineKinds.${l.kind}`)}</TableCell>
                        <TableCell dir="auto">
                          {l.kind === "service" ? `${l.description ?? ""} (${l.accountRole ?? ""})` : `${l.itemCode ?? ""}${l.description ? ` · ${l.description}` : ""} ${l.shipmentNumber ?? l.creditedInvoiceNumber ?? l.orderNumber ?? ""}`.trim()}
                          {l.deferralPeriods ? <p className="mt-0.5 text-xs text-fg-muted" data-testid="line-deferral">{t("sales.invoicing.deferredOver", { count: Number(l.deferralPeriods), from: formatDate(l.deferralStartsOn) })}</p> : null}
                          {l.dimensions?.COST_CENTER ? <p className="mt-0.5 text-xs text-fg-muted" data-testid="doc-line-cost-centre">{costCentreText(l.dimensions, lineDimensions)}</p> : null}
                        </TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode ?? ""}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(l.unitPrice, { maximumFractionDigits: 4 })}{Number(l.discountPct) > 0 ? ` −${formatNumber(l.discountPct, { maximumFractionDigits: 2 })}%` : ""}</TableCell>
                        <TableCell className="tabular" dir="ltr">{money(l.netAmount, i.currency)}</TableCell>
                        <TableCell dir="ltr">{l.taxCode ?? "—"}</TableCell>
                        <TableCell className="tabular" dir="ltr">{money(l.taxAmount, i.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {tab === "receivables" ? (
                i.openItems.length === 0 ? <p className="text-sm text-fg-muted">{t("sales.invoicing.noReceivables")}</p> : (
                  <div className="flex flex-col gap-3">
                    <Table data-testid="sales-invoice-open-items">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("purchasing.instalment")}</TableHead>
                          <TableHead>{t("sales.invoicing.dueDate")}</TableHead>
                          <TableHead>{t("sales.amount")}</TableHead>
                          <TableHead>{t("purchasing.remaining")}</TableHead>
                          <TableHead>{t("common.status")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {i.openItems.map((o) => (
                          <TableRow key={o.id} data-testid="open-item-row">
                            <TableCell>{String(o.instalment)}</TableCell>
                            <TableCell dir="ltr">{formatDate(o.dueDate)}</TableCell>
                            <TableCell className="tabular" dir="ltr">{money(o.originalTc, o.currency)}</TableCell>
                            <TableCell className="tabular" dir="ltr" data-testid="open-item-remaining">{money(o.remainingTc, o.currency)}</TableCell>
                            <TableCell><SalesStatus status={o.status} /></TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                    <h3 className="text-sm font-semibold">{t("purchasing.settlements")}</h3>
                    {(settlements.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.noSettlements")}</p> : (
                      <Table>
                        <TableHeader>
                          <TableRow>
                            <TableHead>{t("purchasing.postingDate")}</TableHead>
                            <TableHead>{t("sales.invoicing.kind")}</TableHead>
                            <TableHead>{t("purchasing.appliedTo")}</TableHead>
                            <TableHead>{t("sales.amount")}</TableHead>
                            <TableHead>{t("purchasing.fxGainLoss")}</TableHead>
                          </TableRow>
                        </TableHeader>
                        <TableBody>
                          {(settlements.data ?? []).map((st) => (
                            <TableRow key={st.id} data-testid="settlement-row">
                              <TableCell dir="ltr">{formatDate(st.settlementDate)}</TableCell>
                              <TableCell>{t(`receivables.settlementKinds.${st.kind}`, { defaultValue: st.kind })}</TableCell>
                              <TableCell dir="ltr">{st.settlingDocumentNumber} → {st.settledDocumentNumber}</TableCell>
                              <TableCell className="tabular" dir="ltr">{money(st.amountTc, st.currency)}</TableCell>
                              <TableCell className="tabular" dir="ltr">{money(st.fxGainLossFc, i.functionalCurrency)}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    )}
                  </div>
                )
              ) : null}
              {tab === "commissions" ? (
                (i.commissions ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("sales.invoicing.noCommission")}</p> : (
                  <Table data-testid="commission-entries">
                    <TableHeader>
                      <TableRow>
                        <TableHead>#</TableHead>
                        <TableHead>{t("sales.basis")}</TableHead>
                        <TableHead>{t("sales.commission")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {(i.commissions ?? []).map((c) => (
                        <TableRow key={c.id} data-testid="commission-row">
                          <TableCell>{String(c.lineNo)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{money(c.basisAmount, c.currency)} <span className="text-fg-muted">({t(`sales.bases.${c.basis}`, { defaultValue: c.basis })})</span></TableCell>
                          <TableCell className="tabular" dir="ltr">{money(c.commissionAmount, c.currency)}</TableCell>
                          <TableCell><SalesStatus status={c.status} /></TableCell>
                        </TableRow>
                      ))}
                      <TableRow>
                        <TableCell colSpan={2} className="font-semibold">{t("sales.commissionTotal")}</TableCell>
                        <TableCell className="tabular font-semibold" dir="ltr" data-testid="commission-total">{money(commissionTotal, i.commissions?.[0]?.currency ?? i.currency)}</TableCell>
                        <TableCell />
                      </TableRow>
                    </TableBody>
                  </Table>
                )
              ) : null}
              {tab === "einvoice" ? (
                <div className="flex flex-col gap-3" data-testid="einvoice-panel">
                  <KeyValues entries={[
                    [t("sales.invoicing.einvoiceScheme"), i.einvoiceScheme ?? t("sales.invoicing.noScheme")],
                    [t("common.status"), t(`sales.invoicing.einvoiceStatuses.${i.einvoiceStatus}`, { defaultValue: i.einvoiceStatus })],
                    [t("sales.invoicing.submissionId"), i.einvoiceSubmissionId ?? "—"],
                    [t("sales.invoicing.submittedAt"), i.einvoiceSubmittedAt ? formatDate(i.einvoiceSubmittedAt) : "—"],
                  ]} />
                  {(i.einvoiceMessages ?? []).length > 0 ? <ul className="list-disc ps-5 text-sm">{(i.einvoiceMessages ?? []).map((m, idx) => <li key={idx}>{m}</li>)}</ul> : null}
                  <p className="text-xs text-fg-muted">{t("sales.invoicing.einvoiceHelp")}</p>
                  {i.status === "posted" && i.einvoiceScheme && canPost ? <div><Button variant="secondary" onClick={() => { act.mutate({ id: i.id, action: "einvoice" }); }} loading={act.isPending} data-testid="submit-einvoice">{t("sales.invoicing.submitEinvoice")}</Button></div> : null}
                </div>
              ) : null}
              {reversal !== null ? (
                <Field label={t("sales.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              {cancelReason !== null ? (
                <Field label={t("sales.cancelReason")} required>
                  <TextField value={cancelReason} onChange={(e) => { setCancelReason(e.target.value); }} data-testid="cancel-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {i.status === "draft" && canManage ? <Button variant="secondary" onClick={() => { openEdit(i); }} data-testid="edit-sales-invoice">{t("common.edit")}</Button> : null}
                {i.status === "draft" && canManage && cancelReason === null ? <Button variant="secondary" onClick={() => { setCancelReason(""); }} data-testid="start-cancel-invoice">{t("sales.invoicing.cancelDraft")}</Button> : null}
                {cancelReason !== null ? <Button variant="danger" onClick={() => { act.mutate({ id: i.id, action: "cancel", reason: cancelReason }); }} loading={act.isPending} disabled={!cancelReason.trim()} data-testid="confirm-cancel-invoice">{t("sales.confirmCancel")}</Button> : null}
                {i.status === "draft" && canPost ? <Button onClick={() => { act.mutate({ id: i.id, action: "post" }); }} loading={act.isPending} data-testid="post-sales-invoice">{t("sales.post")}</Button> : null}
                {i.status === "posted" && canPost && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="start-reverse-invoice">{t("sales.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: i.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse-invoice">{t("sales.confirmReverse")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
