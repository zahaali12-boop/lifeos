import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useFollowOnSource, useOpenRecord, type FollowOnSource } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, PurchaseStatus, useSuppliers } from "./shared";
import { DocumentFlowBar } from "./DocumentFlow";
import { asCustomFieldValues, CustomFieldsFieldset, CustomFieldValuesList, type CustomFieldValues } from "../CustomFieldsFieldset";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

type Invoice = components["schemas"]["InvoiceSummary"];
type Invoicable = components["schemas"]["InvoicableLine"];

interface InvoiceLineForm {
  kind: string;
  receiptLineId: string;
  orderLineId: string;
  landedCostChargeId: string;
  returnLineId: string;
  label: string;
  quantity: string;
  unitPrice: string;
  description: string;
}

interface InvoiceForm {
  id: string | null;
  customFields: CustomFieldValues;
  kind: string;
  partnerId: string;
  supplierInvoiceNumber: string;
  documentDate: string;
  currency: string;
  applyWht: boolean;
  lines: InvoiceLineForm[];
}

const editable = (status: string): boolean => status === "draft" || status === "rejected" || status === "blocked";

/** Whether an open line belongs to the document an invoice is started from: an order's receipts and service lines, a receipt's lines, a return's lines. */
const belongsTo = (line: Invoicable, source: FollowOnSource): boolean =>
  source.type === "purchase_order" ? line.orderId === source.id && (line.kind === "receipt" || line.kind === "order")
    : source.type === "purchase_receipt" ? line.kind === "receipt" && line.receiptId === source.id
      : line.kind === "return" && line.returnId === source.id;

function lineFrom(line: Invoicable): InvoiceLineForm {
  const label = line.kind === "charge" ? `${line.landedCostNumber ?? ""} · ${line.itemCode}` : `${line.itemCode} · ${line.returnNumber ?? line.receiptNumber ?? line.orderNumber ?? ""}`;
  return { kind: line.kind, receiptLineId: line.kind === "return" ? "" : (line.receiptLineId ?? ""), orderLineId: line.orderLineId ?? "", landedCostChargeId: line.landedCostChargeId ?? "", returnLineId: line.returnLineId ?? "", label, quantity: String(line.remaining), unitPrice: String(line.unitPrice), description: "" };
}

/** Supplier invoices (roadmap 4.4): lines picked from uninvoiced receipts and open service lines or entered as expenses, matched against tolerances, blocked breaches waiting for an override, approved, posted and reversed. */
export function InvoicesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<InvoiceForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/invoices");
  const [tab, setTab] = useState("lines");
  const [reversal, setReversal] = useState<string | null>(null);
  const [credit, setCredit] = useState<{ invoiceItemId: string; amount: string } | null>(null);
  const [from, clearFrom] = useFollowOnSource("/purchasing/invoices");
  const [pendingSource, setPendingSource] = useState<FollowOnSource | null>(null);
  const started = useRef<string | null>(null);
  const suppliers = useSuppliers(companyId);

  const list = useQuery({
    queryKey: ["invoices", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const invoicable = useQuery({
    queryKey: ["invoicable", companyId, form?.partnerId ?? ""],
    enabled: Boolean(companyId) && Boolean(form?.partnerId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices/invoicable", { params: { query: { companyId, partnerId: form?.partnerId ?? "" } } })),
  });
  const detail = useQuery({
    queryKey: ["invoice", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/invoices/{invoiceId}", { params: { path: { invoiceId: openId ?? "" } } })),
  });
  const supplierItems = useQuery({
    queryKey: ["open-items", companyId, detail.data?.partnerId ?? ""],
    enabled: Boolean(companyId) && detail.data?.kind === "debit_note" && detail.data.status === "posted",
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, partnerId: detail.data?.partnerId ?? "", status: "live" } } })),
  });
  const settlements = useQuery({
    queryKey: ["settlements", companyId, openId],
    enabled: Boolean(companyId) && detail.data?.status === "posted" && detail.data.openItems.length > 0,
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/settlements", { params: { query: { companyId, openItemId: detail.data?.openItems[0]?.id ?? "" } } })),
  });
  // "Create invoice" on an order or receipt, "Create debit note" on a return: the supplier, currency and company come from the source document.
  const sourceDocument = useQuery({
    queryKey: ["follow-on", from?.type ?? "", from?.id ?? ""],
    enabled: from?.type === "purchase_order" || from?.type === "purchase_receipt" || from?.type === "purchase_return",
    queryFn: async () => {
      const id = from?.id ?? "";
      const doc = from?.type === "purchase_order" ? unwrap(await api.GET("/api/v1/purchasing/orders/{orderId}", { params: { path: { orderId: id } } }))
        : from?.type === "purchase_receipt" ? unwrap(await api.GET("/api/v1/purchasing/receipts/{receiptId}", { params: { path: { receiptId: id } } }))
          : unwrap(await api.GET("/api/v1/purchasing/returns/{returnId}", { params: { path: { returnId: id } } }));
      return { id: doc.id, companyId: doc.companyId, partnerId: doc.partnerId, currency: doc.currency };
    },
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["invoices"], ["invoice"], ["invoicable"], ["orders"], ["order"], ["receipts"], ["receipt"], ["returns"], ["return"], ["open-items"], ["settlements"], ["document-flow"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: InvoiceForm) => {
      const body = {
        customFields: f.customFields,
        companyId,
        partnerId: f.partnerId,
        kind: f.kind,
        supplierInvoiceNumber: f.supplierInvoiceNumber || null,
        documentDate: f.documentDate || null,
        currency: f.currency || null,
        applyWht: f.applyWht,
        lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ kind: l.kind, quantity: num(l.quantity), unitPrice: num(l.unitPrice), receiptLineId: l.receiptLineId || null, orderLineId: l.orderLineId || null, landedCostChargeId: l.landedCostChargeId || null, returnLineId: l.returnLineId || null, description: l.description || null, discountPct: 0 })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/purchasing/invoices/{invoiceId}", { params: { path: { invoiceId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/invoices", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "submit" | "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { invoiceId: input.id } };
      switch (input.action) {
        case "submit": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/submit", { params }));
        case "post": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/purchasing/invoices/{invoiceId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/purchasing/invoices/{invoiceId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const apply = useMutation({
    mutationFn: async (input: { settlingItemId: string; settledItemId: string; amount: number }) => unwrap(await api.POST("/api/v1/payables/settlements/apply", { body: input })),
    onSuccess: async () => { setProblem(null); setCredit(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Invoice, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 130, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "kind", accessorKey: "kind", header: t("purchasing.kind"), size: 110, cell: ({ row }) => t(`purchasing.kinds.${row.original.kind}`) },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 190, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "reference", accessorKey: "supplierInvoiceNumber", header: t("purchasing.supplierReference"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.supplierInvoiceNumber ?? ""}</span> },
      { id: "date", accessorKey: "documentDate", header: t("purchasing.documentDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.documentDate)}</span> },
      { id: "due", accessorKey: "dueDate", header: t("purchasing.dueDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.dueDate)}</span> },
      { id: "total", accessorKey: "totalGross", header: t("purchasing.total"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
      { id: "payable", accessorKey: "totalPayable", header: t("purchasing.payable"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalPayable, row.original.currency)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ id: null, customFields: {}, kind: "invoice", partnerId: "", supplierInvoiceNumber: "", documentDate: today(), currency: "", applyWht: true, lines: [] }); };
  const openEdit = (i: Invoice): void => {
    setProblem(null);
    setForm({ id: i.id, customFields: asCustomFieldValues(i.customFields), kind: i.kind, partnerId: i.partnerId, supplierInvoiceNumber: i.supplierInvoiceNumber ?? "", documentDate: i.documentDate, currency: i.currency, applyWht: Boolean(i.whtCodeId) || i.totalWht !== 0, lines: i.lines.map((l) => ({ kind: l.kind, receiptLineId: l.receiptLineId ?? "", orderLineId: l.orderLineId ?? "", landedCostChargeId: l.landedCostChargeId ?? "", returnLineId: l.returnLineId ?? "", label: l.kind === "expense" ? "" : l.kind === "charge" ? `${l.landedCostNumber ?? ""} · ${l.description ?? ""}` : `${l.itemCode ?? ""} · ${l.returnNumber ?? l.receiptNumber ?? l.orderNumber ?? ""}`, quantity: String(l.quantity), unitPrice: String(l.unitPrice), description: l.description ?? "" })) });
  };
  const addInvoicable = (line: Invoicable): void => {
    if (!form || form.lines.some((l) => (line.kind === "receipt" ? l.receiptLineId === line.receiptLineId : line.kind === "charge" ? l.landedCostChargeId === line.landedCostChargeId : line.kind === "return" ? l.returnLineId === line.returnLineId : l.kind === "order" && l.orderLineId === line.orderLineId))) {
      return;
    }
    setForm({ ...form, currency: form.currency || line.currency, lines: [...form.lines, lineFrom(line)] });
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
    setForm({ id: null, customFields: {}, kind: from.type === "purchase_return" ? "debit_note" : "invoice", partnerId: source.partnerId, supplierInvoiceNumber: "", documentDate: today(), currency: source.currency, applyWht: true, lines: [] });
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
  const addExpense = (): void => { if (form) { setForm({ ...form, lines: [...form.lines, { kind: "expense", receiptLineId: "", orderLineId: "", landedCostChargeId: "", returnLineId: "", label: "", quantity: "1", unitPrice: "", description: "" }] }); } };
  const offered = (invoicable.data ?? []).filter((line) => (form?.kind === "debit_note" ? line.kind === "return" : line.kind !== "return"));
  const patchLine = (index: number, change: Partial<InvoiceLineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const i = detail.data;
  const latestMatch = i?.matches[0];

  return (
    <>
      <PageHeader
        title={t("nav.invoices")}
        description={t("purchasing.invoicesDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-invoice">
            <Plus aria-hidden="true" />
            {t("purchasing.newInvoice")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "pending_approval", "blocked", "approved", "posted", "reversed", "rejected"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Invoice> label="nav.invoices" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyInvoices")} emptyDescription={t("purchasing.emptyInvoicesDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setTab("lines"); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("purchasing.editInvoice") : t("purchasing.newInvoice")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("partners.supplier")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value, lines: form.lines.filter((l) => l.kind === "expense") }); }} required disabled={Boolean(form.id)} data-testid="invoice-supplier">
                    <option value="">—</option>
                    {(suppliers.data ?? []).map((s) => (
                      <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("purchasing.kind")}>
                  <SelectField value={form.kind} onChange={(e) => { setForm({ ...form, kind: e.target.value, lines: e.target.value === "expense" ? form.lines.filter((l) => l.kind === "expense") : e.target.value === "debit_note" ? form.lines.filter((l) => l.kind === "expense" || l.kind === "return") : form.lines.filter((l) => l.kind !== "return") }); }} data-testid="invoice-kind">
                    <option value="invoice">{t("purchasing.kinds.invoice")}</option>
                    <option value="expense">{t("purchasing.kinds.expense")}</option>
                    <option value="debit_note">{t("purchasing.kinds.debit_note")}</option>
                  </SelectField>
                </Field>
                <Field label={t("purchasing.supplierReference")}>
                  <TextField value={form.supplierInvoiceNumber} onChange={(e) => { setForm({ ...form, supplierInvoiceNumber: e.target.value }); }} dir="ltr" data-testid="invoice-reference" />
                </Field>
                <Field label={t("purchasing.documentDate")}>
                  <TextField type="date" value={form.documentDate} onChange={(e) => { setForm({ ...form, documentDate: e.target.value }); }} dir="ltr" data-testid="invoice-date" />
                </Field>
                <Field label={t("partners.currency")} description={t("purchasing.currencyHelp")}>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} data-testid="invoice-currency" />
                </Field>
                <label className="flex items-center gap-2 self-end text-sm">
                  <input type="checkbox" checked={form.applyWht} onChange={(e) => { setForm({ ...form, applyWht: e.target.checked }); }} data-testid="invoice-wht" />
                  {t("purchasing.applyWht")}
                </label>
              </div>
              {form.kind !== "expense" && form.partnerId ? (
                <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="invoicable">
                  <h3 className="text-sm font-semibold">{t("purchasing.invoicable")}</h3>
                  {offered.length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.nothingInvoicable")}</p> : (
                    <ul className="flex flex-col gap-1 text-sm">
                      {offered.map((line) => (
                        <li key={line.returnLineId ?? line.receiptLineId ?? line.landedCostChargeId ?? line.orderLineId} className="flex flex-wrap items-center gap-3">
                          <Badge tone={line.kind === "receipt" ? "info" : line.kind === "charge" || line.kind === "return" ? "warning" : "neutral"}>{t(`purchasing.lineKinds.${line.kind}`)}</Badge>
                          <span dir="ltr">{line.returnNumber ?? line.receiptNumber ?? line.landedCostNumber ?? line.orderNumber}</span>
                          <span dir="auto">{line.itemCode} · {localized(line.itemName)}</span>
                          <span className="tabular" dir="ltr">{formatNumber(line.remaining, { maximumFractionDigits: 3 })} {line.uomCode} × {formatNumber(line.unitPrice, { maximumFractionDigits: 4 })} {line.currency}</span>
                          <Button type="button" variant="ghost" size="sm" onClick={() => { addInvoicable(line); }} data-testid={`add-invoicable-${line.itemCode}${line.kind === "charge" ? "-charge" : line.kind === "return" ? "-return" : ""}`}>{t("purchasing.addLine")}</Button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ) : null}
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold">{t("purchasing.lines")}</h3>
                <Button type="button" variant="ghost" size="sm" onClick={addExpense} data-testid="add-expense-line">{t("purchasing.addExpenseLine")}</Button>
              </div>
              {form.lines.length > 0 ? (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.kind")}</TableHead>
                      <TableHead>{t("purchasing.item")}</TableHead>
                      <TableHead>{t("purchasing.quantity")}</TableHead>
                      <TableHead>{t("purchasing.unitPrice")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => (
                      <TableRow key={index} data-testid="invoice-line">
                        <TableCell>{t(`purchasing.lineKinds.${line.kind}`)}</TableCell>
                        <TableCell dir="auto">{line.kind === "expense" ? <TextField aria-label={t("purchasing.description")} value={line.description} onChange={(e) => { patchLine(index, { description: e.target.value }); }} className="w-56" data-testid={`invoice-description-${String(index)}`} /> : line.label}</TableCell>
                        <TableCell><TextField aria-label={t("purchasing.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`invoice-qty-${String(index)}`} /></TableCell>
                        <TableCell><TextField aria-label={t("purchasing.unitPrice")} inputMode="decimal" value={line.unitPrice} onChange={(e) => { patchLine(index, { unitPrice: e.target.value }); }} dir="ltr" className="w-28" data-testid={`invoice-price-${String(index)}`} /></TableCell>
                        <TableCell><Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: form.lines.filter((_, x) => x !== index) }); }}>{t("workflow.remove")}</Button></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : <p className="text-xs text-fg-muted">{t("purchasing.noLines")}</p>}
              <CustomFieldsFieldset entityType="purchase_invoice" values={form.customFields} onChange={(customFields) => { setForm({ ...form, customFields }); }} errors={problem?.fields} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || form.lines.length === 0} data-testid="save-invoice">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {i ? (
            <div className="flex flex-col gap-4" data-testid="invoice-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{i.number}</span>
                  <PurchaseStatus status={i.status} />
                  {i.blockKind ? <Badge tone="danger" data-testid="invoice-block">{t(`purchasing.matchStatuses.${i.blockKind}`, { defaultValue: i.blockKind })}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <DocumentFlowBar documentType="purchase_invoice" documentId={i.id} />
              <CustomFieldValuesList entityType="purchase_invoice" values={i.customFields} />
              <FormError message={problem?.message ?? null} />
              {i.blockReason ? <p className="text-sm text-warning" data-testid="block-reason">{i.blockReason}</p> : null}
              <KeyValues entries={[
                [t("partners.supplier"), `${i.partnerCode} · ${localized(i.partnerName)}`],
                [t("purchasing.supplierReference"), i.supplierInvoiceNumber ?? "—"],
                [t("purchasing.documentDate"), formatDate(i.documentDate)],
                [t("purchasing.dueDate"), formatDate(i.dueDate) || "—"],
                [t("purchasing.terms"), [i.paymentTermsCode, i.whtCode].filter(Boolean).join(" · ") || "—"],
                [t("purchasing.total"), <span key="total" data-testid="invoice-total">{formatMoney(i.totalGross, i.currency)}{i.exchangeRate !== 1 ? ` (@ ${formatNumber(i.exchangeRate, { maximumFractionDigits: 6 })})` : ""}</span>],
                [t("purchasing.withheld"), formatMoney(i.totalWht, i.currency)],
                [t("purchasing.payable"), <span key="payable" data-testid="invoice-payable">{formatMoney(i.totalPayable, i.currency)}</span>],
                ...(i.rejectionReason ? [[t("purchasing.rejectionReason"), i.rejectionReason] as [string, string]] : []),
                ...(i.reversalReason ? [[t("purchasing.reversalReason"), i.reversalReason] as [string, string]] : []),
              ]} />
              <Tabs tabs={[{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "match", label: t("purchasing.match"), testId: "tab-match" }, { id: "payables", label: t("purchasing.payables"), testId: "tab-payables" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="purchase_invoice" entityId={i.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="purchase_invoice" entityId={i.id} /> : null}
              {tab === "lines" ? (
                <Table data-testid="invoice-lines">
                  <TableHeader>
                    <TableRow>
                      <TableHead>#</TableHead>
                      <TableHead>{t("purchasing.kind")}</TableHead>
                      <TableHead>{t("purchasing.item")}</TableHead>
                      <TableHead>{t("purchasing.quantity")}</TableHead>
                      <TableHead>{t("purchasing.unitPrice")}</TableHead>
                      <TableHead>{t("purchasing.expectedPrice")}</TableHead>
                      <TableHead>{t("purchasing.net")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {i.lines.map((l) => (
                      <TableRow key={l.id} data-testid="invoice-line-row">
                        <TableCell>{String(l.lineNo)}</TableCell>
                        <TableCell>{t(`purchasing.lineKinds.${l.kind}`)}</TableCell>
                        <TableCell dir="auto">{l.kind === "expense" ? `${l.description ?? ""} (${l.accountRole ?? ""})` : l.kind === "charge" ? `${l.landedCostNumber ?? ""} · ${l.description ?? ""}` : `${l.itemCode ?? ""} · ${l.returnNumber ?? l.receiptNumber ?? l.orderNumber ?? ""}`}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode ?? ""}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatNumber(l.unitPrice, { maximumFractionDigits: 4 })}</TableCell>
                        <TableCell className="tabular" dir="ltr">{l.expectedUnitPrice === null ? "" : `${formatNumber(l.expectedUnitPrice, { maximumFractionDigits: 4 })}${l.priceVariancePct === null ? "" : ` (${formatNumber(l.priceVariancePct, { maximumFractionDigits: 2 })}%)`}`}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(l.netAmount, i.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {tab === "match" ? (
                latestMatch ? (
                  <KeyValues entries={[
                    [t("common.status"), <span key="s" data-testid="match-status">{t(`purchasing.matchStatuses.${latestMatch.status}`, { defaultValue: latestMatch.status })}</span>],
                    [t("purchasing.priceTolerance"), `${formatNumber(latestMatch.priceTolerancePct)}%`],
                    [t("purchasing.qtyTolerance"), `${formatNumber(latestMatch.qtyTolerancePct)}%`],
                    [t("purchasing.priceVariance"), `${formatMoney(latestMatch.priceVarianceAmount, i.currency)} (${formatNumber(latestMatch.priceVariancePct, { maximumFractionDigits: 2 })}%)`],
                    [t("purchasing.qtyVarianceLabel"), formatNumber(latestMatch.qtyVariance, { maximumFractionDigits: 3 })],
                    [t("purchasing.override"), latestMatch.overrideId ? "✓" : "—"],
                    [t("purchasing.matchedAt"), formatDate(latestMatch.matchedAt)],
                  ]} />
                ) : <p className="text-sm text-fg-muted">{t("purchasing.notMatchedYet")}</p>
              ) : null}
              {tab === "payables" ? (
                i.openItems.length === 0 ? <p className="text-sm text-fg-muted">{t("purchasing.noPayables")}</p> : (
                  <Table data-testid="invoice-open-items">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("purchasing.instalment")}</TableHead>
                        <TableHead>{t("purchasing.dueDate")}</TableHead>
                        <TableHead>{t("purchasing.amount")}</TableHead>
                        <TableHead>{t("purchasing.remaining")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {i.openItems.map((o) => (
                        <TableRow key={o.id} data-testid="open-item-row">
                          <TableCell>{String(o.instalment)}</TableCell>
                          <TableCell dir="ltr">{formatDate(o.dueDate)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(o.originalTc, o.currency)}</TableCell>
                          <TableCell className="tabular" dir="ltr" data-testid="open-item-remaining">{formatMoney(o.remainingTc, o.currency)}</TableCell>
                          <TableCell><PurchaseStatus status={o.status} /></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )
              ) : null}
              {tab === "payables" && i.kind === "debit_note" && i.status === "posted" && i.openItems[0]?.status === "open" ? (
                <div className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="apply-credit">
                  <h3 className="text-sm font-semibold">{t("purchasing.applyCredit")}</h3>
                  <p className="text-xs text-fg-muted">{t("purchasing.applyCreditHelp")}</p>
                  {credit ? (
                    <div className="grid gap-3 sm:grid-cols-3">
                      <Field label={t("purchasing.appliedTo")} required>
                        <SelectField value={credit.invoiceItemId} onChange={(e) => { setCredit({ ...credit, invoiceItemId: e.target.value }); }} data-testid="credit-target">
                          <option value="">—</option>
                          {(supplierItems.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === i.currency).map((o) => (
                            <option key={o.id} value={o.id}>{o.documentNumber} · {formatMoney(o.remainingTc, o.currency)}</option>
                          ))}
                        </SelectField>
                      </Field>
                      <Field label={t("purchasing.creditAmount")} required>
                        <TextField inputMode="decimal" value={credit.amount} onChange={(e) => { setCredit({ ...credit, amount: e.target.value }); }} dir="ltr" data-testid="credit-amount" />
                      </Field>
                      <div className="flex items-end gap-2">
                        <Button type="button" variant="secondary" onClick={() => { setCredit(null); }}>{t("common.cancel")}</Button>
                        <Button type="button" onClick={() => { const item = i.openItems[0]; if (item) { apply.mutate({ settlingItemId: item.id, settledItemId: credit.invoiceItemId, amount: num(credit.amount) }); } }} loading={apply.isPending} disabled={!credit.invoiceItemId || num(credit.amount) <= 0} data-testid="confirm-apply-credit">{t("purchasing.applyCredit")}</Button>
                      </div>
                    </div>
                  ) : <Button type="button" variant="secondary" onClick={() => { setCredit({ invoiceItemId: "", amount: String(Math.abs(Number(i.openItems[0]?.remainingTc ?? 0))) }); }} data-testid="start-apply-credit">{t("purchasing.applyCredit")}</Button>}
                </div>
              ) : null}
              {tab === "payables" && i.status === "posted" ? (
                <div className="flex flex-col gap-2" data-testid="settlements">
                  <h3 className="text-sm font-semibold">{t("purchasing.settlements")}</h3>
                  {(settlements.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.noSettlements")}</p> : (
                    <Table>
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("purchasing.postingDate")}</TableHead>
                          <TableHead>{t("purchasing.kind")}</TableHead>
                          <TableHead>{t("purchasing.appliedTo")}</TableHead>
                          <TableHead>{t("purchasing.amount")}</TableHead>
                          <TableHead>{t("purchasing.fxGainLoss")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {(settlements.data ?? []).map((st) => (
                          <TableRow key={st.id} data-testid="settlement-row">
                            <TableCell dir="ltr">{formatDate(st.settlementDate)}</TableCell>
                            <TableCell>{t(`purchasing.settlementKinds.${st.kind}`)}</TableCell>
                            <TableCell dir="ltr">{st.settlingDocumentNumber} → {st.settledDocumentNumber}</TableCell>
                            <TableCell className="tabular" dir="ltr">{formatMoney(st.amountTc, st.currency)}</TableCell>
                            <TableCell className="tabular" dir="ltr">{formatMoney(st.fxGainLossFc, i.functionalCurrency)}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
                </div>
              ) : null}
              {reversal !== null ? (
                <Field label={t("purchasing.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {editable(i.status) ? <Button variant="secondary" onClick={() => { openEdit(i); }} data-testid="edit-invoice">{t("common.edit")}</Button> : null}
                {editable(i.status) ? <Button variant="secondary" onClick={() => { act.mutate({ id: i.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-invoice">{t("purchasing.deleteDraft")}</Button> : null}
                {editable(i.status) ? <Button onClick={() => { act.mutate({ id: i.id, action: "submit" }); }} loading={act.isPending} data-testid="submit-invoice">{t("purchasing.submit")}</Button> : null}
                {i.status === "approved" ? <Button onClick={() => { act.mutate({ id: i.id, action: "post" }); }} loading={act.isPending} data-testid="post-invoice">{t("purchasing.postInvoice")}</Button> : null}
                {i.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-invoice">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: i.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
