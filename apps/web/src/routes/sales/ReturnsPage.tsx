import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { History, Plus } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useFollowOnSource, useOpenRecord } from "../../lib/documents";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, DocStatus, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";
import { useCompanyCustomers } from "./pricing/shared";
import { num, SalesStatus } from "./shared";

type SalesReturn = components["schemas"]["SalesReturnSummary"];
type ReturnLine = components["schemas"]["SalesReturnLineSummary"];
type Returnable = components["schemas"]["ReturnableSalesLine"];

const statuses = ["requested", "authorised", "received", "inspected", "credited", "closed", "rejected", "reversed"] as const;
const dispositions = ["restock", "quarantine", "scrap", "repair"] as const;
const costPolicies = ["original", "current", "specified"] as const;

interface ReturnLineForm {
  shipmentLineId: string;
  quantity: string;
  serialNumbers: string;
  reasonCode: string;
  reason: string;
  disposition: string;
  unitCost: string;
}

interface ReturnForm {
  id: string | null;
  partnerId: string;
  requestDate: string;
  costPolicy: string;
  reason: string;
  customerReference: string;
  notes: string;
  lines: ReturnLineForm[];
}

const emptyForm = (partnerId = ""): ReturnForm => ({ id: null, partnerId, requestDate: today(), costPolicy: "original", reason: "", customerReference: "", notes: "", lines: [] });

const lineOf = (l: Returnable, quantity = ""): ReturnLineForm => ({ shipmentLineId: l.shipmentLineId, quantity, serialNumbers: "", reasonCode: "", reason: "", disposition: "", unitCost: "" });

const serialTracked = (tracking: string | undefined): boolean => tracking === "serial" || tracking === "lot_and_serial";

/** Sales returns, the RMA of roadmap 5.8: a customer's request to send goods of posted shipment lines back, authorised
 * or rejected, received into stock at the cost they left with (a blocked lot comes back on hold and quarantined, a
 * serial as returned), inspected with a disposition per line, credited by credit notes on the invoices that billed the
 * goods, and closed; a received return is reversed as a whole. A returned serial's whole chain reads here. */
export function ReturnsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ReturnForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/sales/returns");
  const [tab, setTab] = useState("lines");
  const [reason, setReason] = useState<{ action: "reject" | "reverse"; text: string } | null>(null);
  const [receiveDate, setReceiveDate] = useState<string | null>(null);
  const [inspection, setInspection] = useState<Record<string, { disposition: string; note: string }> | null>(null);
  const [serialHistory, setSerialHistory] = useState<{ itemId: string; serialNumber: string } | null>(null);
  const [from, clearFrom] = useFollowOnSource("/sales/returns");
  const [pendingShipment, setPendingShipment] = useState<string | null>(null);
  const started = useRef<string | null>(null);
  const canManage = can("sales.return.manage");
  const canReceive = can("sales.return.receive");
  const canCredit = can("sales.return.credit");

  const list = useQuery({
    queryKey: ["sales-returns", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/returns", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const customers = useCompanyCustomers(companyId);
  const returnable = useQuery({
    queryKey: ["sales-returnable", companyId, form?.partnerId ?? ""],
    enabled: Boolean(companyId) && Boolean(form?.partnerId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/returns/returnable", { params: { query: { companyId, partnerId: form?.partnerId ?? "" } } })),
  });
  const reasonCodes = useQuery({
    queryKey: ["reason-codes", "return"],
    enabled: Boolean(form),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/reason-codes", { params: { query: { appliesTo: "return" } } })),
  });
  const detail = useQuery({
    queryKey: ["sales-return", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/returns/{returnId}", { params: { path: { returnId: openId ?? "" } } })),
  });
  const sourceShipment = useQuery({
    queryKey: ["sales-shipment", from?.id ?? ""],
    enabled: from?.type === "sales_shipment",
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/shipments/{shipmentId}", { params: { path: { shipmentId: from?.id ?? "" } } })),
  });
  const serial = useQuery({
    queryKey: ["serial-by-number", serialHistory?.itemId ?? "", serialHistory?.serialNumber ?? ""],
    enabled: Boolean(serialHistory),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials/by-number", { params: { query: { itemId: serialHistory?.itemId ?? "", serialNumber: serialHistory?.serialNumber ?? "" } } })),
  });
  const history = useQuery({
    queryKey: ["serial-history", serial.data?.id ?? ""],
    enabled: Boolean(serial.data?.id),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials/{serialId}/history", { params: { path: { serialId: serial.data?.id ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["sales-returns"], ["sales-return"], ["sales-returnable"], ["sales-shipments"], ["sales-shipment"], ["sales-invoices"], ["sales-invoice"], ["invoicable"], ["stock"], ["serial-history"], ["serial-by-number"], ["record-history"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: ReturnForm) => {
      const body = {
        companyId,
        partnerId: f.partnerId,
        requestDate: f.requestDate || null,
        costPolicy: f.costPolicy,
        reason: f.reason || null,
        customerReference: f.customerReference || null,
        notes: f.notes || null,
        lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({
          shipmentLineId: l.shipmentLineId,
          quantity: num(l.quantity),
          serialNumbers: l.serialNumbers.trim() ? l.serialNumbers.split(/[\s,;]+/).filter(Boolean) : null,
          reasonCode: l.reasonCode || null,
          reason: l.reason || null,
          disposition: l.disposition || null,
          unitCost: l.unitCost.trim() ? num(l.unitCost) : null,
        })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/sales/returns/{returnId}", { params: { path: { returnId: f.id } }, body })) : unwrap(await api.POST("/api/v1/sales/returns", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "authorise" | "reject" | "receive" | "inspect" | "credit" | "close" | "reverse" | "delete"; reason?: string; date?: string | null; lines?: { lineId: string; disposition: string; note: string | null }[] }) => {
      const params = { path: { returnId: input.id } };
      switch (input.action) {
        case "authorise": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/authorise", { params }));
        case "reject": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/reject", { params, body: { reason: input.reason ?? "" } }));
        case "receive": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/receive", { params, body: { postingDate: input.date ?? null } }));
        case "inspect": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/inspect", { params, body: { lines: input.lines ?? [] } }));
        case "credit": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/credit", { params, body: {} }));
        case "close": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/close", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/sales/returns/{returnId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/sales/returns/{returnId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReason(null); setReceiveDate(null); setInspection(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<SalesReturn, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("sales.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <SalesStatus status={row.original.status} /> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "requested", accessorKey: "requestDate", header: t("sales.returns.requestDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.requestDate)}</span> },
      { id: "received", accessorKey: "postingDate", header: t("sales.returns.receivedOn"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate) || "—"}</span> },
      { id: "cost", accessorKey: "totalCost", header: t("sales.returns.costValue"), size: 140, cell: ({ row }) => (row.original.postingDate ? <span className="tabular" dir="ltr">{formatMoney(row.original.totalCost, row.original.functionalCurrency)}</span> : "—") },
      { id: "credited", accessorKey: "totalCredited", header: t("sales.returns.credited"), size: 140, cell: ({ row }) => (row.original.currency ? <span className="tabular" dir="ltr">{formatMoney(row.original.totalCredited, row.original.currency)}</span> : "—") },
    ],
    [t],
  );

  // "Return goods" on a posted shipment: a request for that shipment's lines, every remaining unit proposed.
  const source = sourceShipment.data;
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
    setForm(emptyForm(source.partnerId));
    setPendingShipment(source.id);
  }, [from, source, clearFrom, companyId, setCompanyId]);
  useEffect(() => {
    if (!pendingShipment || !form || returnable.isFetching || !returnable.isFetchedAfterMount) {
      return;
    }
    setPendingShipment(null);
    const lines = (returnable.data ?? []).filter((l) => l.shipmentId === pendingShipment);
    if (lines.length > 0) {
      setForm({ ...form, lines: lines.map((l) => lineOf(l, String(l.remaining))) });
    } else {
      setProblem({ message: t("sales.returns.nothingReturnable"), fields: {} });
    }
  }, [pendingShipment, form, returnable.isFetching, returnable.isFetchedAfterMount, returnable.data, t]);

  const info = (shipmentLineId: string): Returnable | undefined => returnable.data?.find((l) => l.shipmentLineId === shipmentLineId);
  const patchLine = (index: number, change: Partial<ReturnLineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const addLine = (l: Returnable): void => { if (form && !form.lines.some((x) => x.shipmentLineId === l.shipmentLineId)) { setForm({ ...form, lines: [...form.lines, lineOf(l, String(l.remaining))] }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const r = detail.data;
  const notYetAdded = (returnable.data ?? []).filter((l) => !form?.lines.some((x) => x.shipmentLineId === l.shipmentLineId));
  const editable = r?.status === "requested" || r?.status === "authorised";
  const received = r?.status === "received" || r?.status === "inspected" || r?.status === "credited" || r?.status === "closed";
  const startInspection = (ret: SalesReturn): void => {
    setProblem(null);
    setInspection(Object.fromEntries(ret.lines.map((l) => [l.id, { disposition: l.disposition ?? (l.lotBlocked ? "quarantine" : "restock"), note: l.dispositionNote ?? "" }])));
  };
  const openEdit = (ret: SalesReturn): void => {
    setProblem(null);
    setForm({
      id: ret.id, partnerId: ret.partnerId, requestDate: ret.requestDate, costPolicy: ret.costPolicy, reason: ret.reason ?? "", customerReference: ret.customerReference ?? "", notes: ret.notes ?? "",
      lines: ret.lines.map((l) => ({ shipmentLineId: l.shipmentLineId, quantity: String(l.quantity), serialNumbers: l.serialNumbers.join(" "), reasonCode: l.reasonCode ?? "", reason: l.reason ?? "", disposition: l.disposition ?? "", unitCost: l.unitCost === null ? "" : String(l.unitCost) })),
    });
  };
  const lotTone = (l: ReturnLine): string => l.lotStatus ?? "";

  return (
    <>
      <PageHeader
        title={t("nav.salesReturns")}
        description={t("sales.returns.description")}
        actions={
          canManage ? (
            <Button onClick={() => { setProblem(null); setForm(emptyForm()); }} disabled={!companyId} data-testid="new-return">
              <Plus aria-hidden="true" />
              {t("sales.returns.new")}
            </Button>
          ) : null
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {statuses.map((st) => (
              <option key={st} value={st}>{t(`sales.statuses.${st}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<SalesReturn> label="nav.salesReturns" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.returns.empty")} emptyDescription={t("sales.returns.emptyDescription")} onOpen={(row) => { setProblem(null); setReason(null); setInspection(null); setSerialHistory(null); setTab("lines"); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("sales.returns.edit") : t("sales.returns.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("sales.customer")} required className="sm:col-span-2">
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value, lines: [] }); }} required disabled={Boolean(form.id)} data-testid="return-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => (
                      <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("sales.returns.requestDate")}>
                  <TextField type="date" value={form.requestDate} onChange={(e) => { setForm({ ...form, requestDate: e.target.value }); }} dir="ltr" data-testid="return-date" />
                </Field>
                <Field label={t("sales.returns.costPolicy")}>
                  <SelectField value={form.costPolicy} onChange={(e) => { setForm({ ...form, costPolicy: e.target.value }); }} data-testid="return-cost-policy">
                    {costPolicies.map((p) => (
                      <option key={p} value={p}>{t(`sales.returns.costPolicies.${p}`)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("sales.returns.reason")} className="sm:col-span-2">
                  <TextField value={form.reason} onChange={(e) => { setForm({ ...form, reason: e.target.value }); }} data-testid="return-reason" />
                </Field>
                <Field label={t("sales.returns.customerReference")}>
                  <TextField value={form.customerReference} onChange={(e) => { setForm({ ...form, customerReference: e.target.value }); }} dir="ltr" data-testid="return-customer-reference" />
                </Field>
                <Field label={t("sales.notes")}>
                  <TextareaField value={form.notes} onChange={(e) => { setForm({ ...form, notes: e.target.value }); }} rows={1} />
                </Field>
              </div>
              {form.partnerId ? (
                <div className="flex flex-col gap-2">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
                    {notYetAdded.length > 0 ? (
                      <div className="flex flex-wrap gap-1" data-testid="returnable-lines">
                        {notYetAdded.map((l) => (
                          <Button key={l.shipmentLineId} type="button" variant="ghost" size="sm" onClick={() => { addLine(l); }} data-testid={`add-returnable-${l.shipmentNumber}-${String(l.lineNo)}`}>
                            <Plus aria-hidden="true" />
                            <span dir="ltr">{l.shipmentNumber}</span> · {l.itemCode} · {formatNumber(l.remaining, { maximumFractionDigits: 3 })} {l.uomCode}
                          </Button>
                        ))}
                      </div>
                    ) : form.lines.length === 0 && returnable.isFetched ? <p className="text-xs text-fg-muted">{t("sales.returns.nothingReturnable")}</p> : null}
                  </div>
                  {form.lines.length > 0 ? (
                    <Table data-testid="return-line-editor">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("nav.shipments")}</TableHead>
                          <TableHead>{t("sales.item")}</TableHead>
                          <TableHead>{t("sales.returns.shipped")}</TableHead>
                          <TableHead>{t("sales.returns.returnNow")}</TableHead>
                          <TableHead>{t("sales.serials")}</TableHead>
                          <TableHead>{t("sales.returns.reasonCode")}</TableHead>
                          <TableHead>{t("sales.returns.reason")}</TableHead>
                          <TableHead>{t("sales.returns.disposition")}</TableHead>
                          {form.costPolicy === "specified" ? <TableHead>{t("sales.returns.unitCost")}</TableHead> : null}
                          <TableHead />
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {form.lines.map((line, index) => {
                          const i = info(line.shipmentLineId);
                          return (
                            <TableRow key={line.shipmentLineId} data-testid="return-line">
                              <TableCell dir="ltr">{i?.shipmentNumber ?? ""}</TableCell>
                              <TableCell dir="auto">{i ? `${i.itemCode} · ${localized(i.itemName)}` : ""}</TableCell>
                              <TableCell className="tabular" dir="ltr">{i ? `${formatNumber(i.quantity, { maximumFractionDigits: 3 })} ${i.uomCode}` : ""}{i && Number(i.qtyReturned) > 0 ? <span className="text-fg-muted"> ({t("sales.returns.returnedSoFar", { quantity: formatNumber(i.qtyReturned, { maximumFractionDigits: 3 }) })})</span> : null}</TableCell>
                              <TableCell><TextField aria-label={t("sales.returns.returnNow")} inputMode="decimal" value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`return-qty-${String(index)}`} /></TableCell>
                              <TableCell>{serialTracked(i?.tracking) ? <TextField aria-label={t("sales.serials")} value={line.serialNumbers} onChange={(e) => { patchLine(index, { serialNumbers: e.target.value }); }} dir="ltr" className="w-36" placeholder={i?.serialNumbers.join(" ")} data-testid={`return-serials-${String(index)}`} /> : <span className="text-fg-muted">{i?.lotNumber ?? "—"}</span>}</TableCell>
                              <TableCell>
                                <SelectField aria-label={t("sales.returns.reasonCode")} value={line.reasonCode} onChange={(e) => { patchLine(index, { reasonCode: e.target.value }); }} className="w-32" data-testid={`return-reason-code-${String(index)}`}>
                                  <option value="">—</option>
                                  {(reasonCodes.data ?? []).map((c) => (
                                    <option key={c.id} value={c.code}>{c.code} · {localized(c.name)}</option>
                                  ))}
                                </SelectField>
                              </TableCell>
                              <TableCell><TextField aria-label={t("sales.returns.reason")} value={line.reason} onChange={(e) => { patchLine(index, { reason: e.target.value }); }} className="w-36" data-testid={`return-line-reason-${String(index)}`} /></TableCell>
                              <TableCell>
                                <SelectField aria-label={t("sales.returns.disposition")} value={line.disposition} onChange={(e) => { patchLine(index, { disposition: e.target.value }); }} className="w-32" data-testid={`return-disposition-${String(index)}`}>
                                  <option value="">—</option>
                                  {dispositions.map((d) => (
                                    <option key={d} value={d}>{t(`sales.returns.dispositions.${d}`)}</option>
                                  ))}
                                </SelectField>
                              </TableCell>
                              {form.costPolicy === "specified" ? <TableCell><TextField aria-label={t("sales.returns.unitCost")} inputMode="decimal" value={line.unitCost} onChange={(e) => { patchLine(index, { unitCost: e.target.value }); }} dir="ltr" className="w-24" data-testid={`return-unit-cost-${String(index)}`} /></TableCell> : null}
                              <TableCell><Button type="button" variant="ghost" size="sm" onClick={() => { setForm({ ...form, lines: form.lines.filter((_, x) => x !== index) }); }} aria-label={t("common.delete")}>×</Button></TableCell>
                            </TableRow>
                          );
                        })}
                      </TableBody>
                    </Table>
                  ) : null}
                </div>
              ) : (
                <p className="text-xs text-fg-muted">{t("sales.returns.chooseCustomer")}</p>
              )}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.partnerId || !form.lines.some((l) => num(l.quantity) > 0)} data-testid="save-return">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReason(null); setReceiveDate(null); setInspection(null); setSerialHistory(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="return-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <SalesStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.customer"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                [t("inventory.warehouse"), r.warehouseCode ?? "—"],
                [t("sales.returns.requestDate"), formatDate(r.requestDate)],
                ...(r.postingDate ? [[t("sales.returns.receivedOn"), formatDate(r.postingDate)] as [string, string]] : []),
                [t("sales.returns.costPolicy"), t(`sales.returns.costPolicies.${r.costPolicy}`)],
                ...(r.reason ? [[t("sales.returns.reason"), r.reason] as [string, string]] : []),
                ...(r.customerReference ? [[t("sales.returns.customerReference"), <span key="ref" dir="ltr">{r.customerReference}</span>] as [string, React.ReactNode]] : []),
                ...(r.postingDate ? [[t("sales.returns.costValue"), <span key="cost" data-testid="return-cost">{formatMoney(r.totalCost, r.functionalCurrency)}</span>] as [string, React.ReactNode]] : []),
                ...(r.creditNotes.length > 0 ? [[t("sales.returns.creditNotes"), (
                  <span key="notes" className="flex flex-wrap gap-2" data-testid="return-credit-notes">
                    {r.creditNotes.map((n) => (
                      <Link key={n.id} to="/sales/invoices" search={{ open: n.id }} className="font-medium text-accent underline-offset-2 hover:underline" dir="ltr">{n.number} · {formatMoney(n.totalNet, n.currency)}</Link>
                    ))}
                  </span>
                )] as [string, React.ReactNode]] : []),
                ...(r.rejectionReason ? [[t("sales.returns.rejectionReason"), r.rejectionReason] as [string, string]] : []),
                ...(r.reversalReason ? [[t("sales.reversalReason"), r.reversalReason] as [string, string]] : []),
              ]} />
              <Tabs tabs={[{ id: "lines", label: t("sales.lines"), testId: "tab-lines" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="sales_return" entityId={r.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="sales_return" entityId={r.id} /> : null}
              {tab === "lines" ? (
                <>
                  <Table data-testid="return-lines">
                    <TableHeader>
                      <TableRow>
                        <TableHead>#</TableHead>
                        <TableHead>{t("sales.item")}</TableHead>
                        <TableHead>{t("nav.shipments")}</TableHead>
                        <TableHead>{t("sales.quantity")}</TableHead>
                        <TableHead>{t("sales.returns.tracking")}</TableHead>
                        <TableHead>{t("sales.returns.reason")}</TableHead>
                        <TableHead>{t("sales.returns.disposition")}</TableHead>
                        {received ? <TableHead>{t("sales.returns.costValue")}</TableHead> : null}
                        {received ? <TableHead>{t("sales.returns.credited")}</TableHead> : null}
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {r.lines.map((l) => (
                        <TableRow key={l.id} data-testid="return-line-row">
                          <TableCell>{String(l.lineNo)}</TableCell>
                          <TableCell dir="auto">{l.itemCode} · {localized(l.itemName)}</TableCell>
                          <TableCell><Link to="/sales/shipments" search={{ open: l.shipmentId }} className="text-accent underline-offset-2 hover:underline" dir="ltr">{l.shipmentNumber}</Link></TableCell>
                          <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                          <TableCell>
                            <span className="flex flex-wrap items-center gap-1">
                              {l.lotNumber ? <span dir="ltr">{l.lotNumber}</span> : null}
                              {l.lotStatus && l.lotStatus !== "active" ? <span data-testid="line-lot-status"><DocStatus status={lotTone(l)} /></span> : null}
                              {l.serialNumbers.map((sn) => (
                                <Button key={sn} type="button" variant="ghost" size="sm" onClick={() => { setSerialHistory({ itemId: l.itemId, serialNumber: sn }); }} aria-label={t("sales.returns.serialHistoryOf", { serial: sn })} data-testid={`serial-${sn}`}>
                                  <History aria-hidden="true" />
                                  <span dir="ltr">{sn}</span>
                                </Button>
                              ))}
                            </span>
                          </TableCell>
                          <TableCell dir="auto">{[l.reasonCode, l.reason].filter(Boolean).join(" · ") || "—"}</TableCell>
                          <TableCell>
                            {inspection ? (
                              <span className="flex flex-col gap-1">
                                <SelectField aria-label={t("sales.returns.disposition")} value={inspection[l.id]?.disposition ?? ""} onChange={(e) => { setInspection({ ...inspection, [l.id]: { disposition: e.target.value, note: inspection[l.id]?.note ?? "" } }); }} className="w-32" data-testid={`inspect-disposition-${String(l.lineNo)}`}>
                                  {dispositions.map((d) => (
                                    <option key={d} value={d}>{t(`sales.returns.dispositions.${d}`)}</option>
                                  ))}
                                </SelectField>
                                <TextField aria-label={t("sales.returns.dispositionNote")} value={inspection[l.id]?.note ?? ""} onChange={(e) => { setInspection({ ...inspection, [l.id]: { disposition: inspection[l.id]?.disposition ?? "restock", note: e.target.value } }); }} className="w-40" placeholder={t("sales.returns.dispositionNote")} data-testid={`inspect-note-${String(l.lineNo)}`} />
                              </span>
                            ) : l.disposition ? (
                              <span className="flex flex-col" data-testid="line-disposition">
                                <Badge tone={l.disposition === "scrap" ? "danger" : l.disposition === "restock" ? "success" : "warning"}>{t(`sales.returns.dispositions.${l.disposition}`)}</Badge>
                                {l.dispositionNote ? <span className="text-xs text-fg-muted">{l.dispositionNote}</span> : null}
                              </span>
                            ) : "—"}
                          </TableCell>
                          {received ? <TableCell className="tabular" dir="ltr">{formatMoney(l.costAmount, r.functionalCurrency)}{Number(l.scrapCostAmount) > 0 ? <span className="block text-xs text-fg-muted">{t("sales.returns.scrapped", { amount: formatMoney(l.scrapCostAmount, r.functionalCurrency) })}</span> : null}</TableCell> : null}
                          {received ? <TableCell className="tabular" dir="ltr" data-testid="line-credited">{formatNumber(l.qtyCredited, { maximumFractionDigits: 3 })}{r.currency ? ` · ${formatMoney(l.netCredited, r.currency)}` : ""}</TableCell> : null}
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  {serialHistory ? (
                    <section className="flex flex-col gap-2 rounded-md border border-border p-3" aria-labelledby="serial-history-heading" data-testid="serial-history">
                      <div className="flex items-center justify-between gap-2">
                        <h3 id="serial-history-heading" className="text-sm font-semibold" dir="auto">{t("sales.returns.serialHistoryOf", { serial: serialHistory.serialNumber })}{history.data ? <> · <DocStatus status={history.data.serial.status} /></> : null}</h3>
                        <Button variant="ghost" size="sm" onClick={() => { setSerialHistory(null); }}>{t("common.close")}</Button>
                      </div>
                      {history.data ? (
                        <Table>
                          <TableHeader>
                            <TableRow>
                              <TableHead>{t("inventory.tracking.when")}</TableHead>
                              <TableHead>{t("inventory.tracking.event")}</TableHead>
                              <TableHead>{t("common.status")}</TableHead>
                              <TableHead>{t("inventory.warehouse")}</TableHead>
                              <TableHead>{t("inventory.stock.source")}</TableHead>
                            </TableRow>
                          </TableHeader>
                          <TableBody>
                            {history.data.events.map((e) => (
                              <TableRow key={e.id} data-testid="serial-event">
                                <TableCell dir="ltr">{formatDateTime(e.at)}</TableCell>
                                <TableCell>{e.entryType ? t(`inventory.entryTypes.${e.entryType}`, { defaultValue: e.entryType }) : t(`inventory.tracking.kinds.${e.kind}`, { defaultValue: e.kind })}</TableCell>
                                <TableCell>{t(`inventory.statuses.${e.toStatus}`, { defaultValue: e.toStatus })}</TableCell>
                                <TableCell dir="ltr">{e.warehouseCode ?? ""}</TableCell>
                                <TableCell dir="auto">{e.sourceDocumentType ? t(`sales.documentTypes.${e.sourceDocumentType}`, { defaultValue: e.sourceDocumentType }) : e.note ?? ""}</TableCell>
                              </TableRow>
                            ))}
                          </TableBody>
                        </Table>
                      ) : <p className="text-xs text-fg-muted">{t("common.loading")}</p>}
                    </section>
                  ) : null}
                </>
              ) : null}
              {reason ? (
                <Field label={reason.action === "reject" ? t("sales.returns.rejectionReason") : t("sales.reversalReason")} required>
                  <TextField value={reason.text} onChange={(e) => { setReason({ ...reason, text: e.target.value }); }} required data-testid="return-reason-text" />
                </Field>
              ) : null}
              {receiveDate !== null ? (
                <Field label={t("sales.returns.receivedOn")}>
                  <TextField type="date" value={receiveDate} onChange={(e) => { setReceiveDate(e.target.value); }} dir="ltr" data-testid="receive-date" />
                </Field>
              ) : null}
              <DialogFooter>
                {editable && canManage ? <Button variant="secondary" onClick={() => { openEdit(r); }} data-testid="edit-return">{t("common.edit")}</Button> : null}
                {editable && canManage ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-return">{t("common.delete")}</Button> : null}
                {editable && canManage && !reason ? <Button variant="secondary" onClick={() => { setReason({ action: "reject", text: "" }); }} data-testid="reject-return">{t("sales.returns.reject")}</Button> : null}
                {r.status === "requested" && canManage ? <Button onClick={() => { act.mutate({ id: r.id, action: "authorise" }); }} loading={act.isPending} data-testid="authorise-return">{t("sales.returns.authorise")}</Button> : null}
                {r.status === "authorised" && canReceive && receiveDate === null ? <Button onClick={() => { setReceiveDate(today()); }} data-testid="receive-return">{t("sales.returns.receive")}</Button> : null}
                {receiveDate !== null ? <Button onClick={() => { act.mutate({ id: r.id, action: "receive", date: receiveDate || null }); }} loading={act.isPending} data-testid="confirm-receive">{t("sales.returns.confirmReceive")}</Button> : null}
                {r.status === "received" && canReceive && !inspection ? <Button onClick={() => { startInspection(r); }} data-testid="inspect-return">{t("sales.returns.inspect")}</Button> : null}
                {inspection ? <Button variant="secondary" onClick={() => { setInspection(null); }}>{t("common.cancel")}</Button> : null}
                {inspection ? <Button onClick={() => { act.mutate({ id: r.id, action: "inspect", lines: Object.entries(inspection).map(([lineId, v]) => ({ lineId, disposition: v.disposition, note: v.note || null })) }); }} loading={act.isPending} data-testid="confirm-inspect">{t("sales.returns.confirmInspect")}</Button> : null}
                {(r.status === "received" || r.status === "inspected") && canCredit && !inspection ? <Button onClick={() => { act.mutate({ id: r.id, action: "credit" }); }} loading={act.isPending} data-testid="credit-return">{t("sales.returns.credit")}</Button> : null}
                {(r.status === "received" || r.status === "inspected" || r.status === "credited") && canManage && !inspection ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "close" }); }} loading={act.isPending} data-testid="close-return">{t("sales.returns.close")}</Button> : null}
                {received && canReceive && !reason && !inspection ? <Button variant="secondary" onClick={() => { setReason({ action: "reverse", text: "" }); }} data-testid="reverse-return">{t("sales.reverse")}</Button> : null}
                {reason ? <Button variant={reason.action === "reject" ? "danger" : "primary"} onClick={() => { act.mutate({ id: r.id, action: reason.action, reason: reason.text }); }} loading={act.isPending} disabled={!reason.text.trim()} data-testid="confirm-reason">{reason.action === "reject" ? t("sales.returns.confirmReject") : t("sales.confirmReverse")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
