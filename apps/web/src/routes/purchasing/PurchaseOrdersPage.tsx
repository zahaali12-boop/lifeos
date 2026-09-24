import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, orderLineBodies, PurchaseStatus, useAgreements, useSuppliers, type LineForm, type PurchaseOrder } from "./shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

interface OrderForm {
  id: string | null;
  partnerId: string;
  currency: string;
  expectedDate: string;
  warehouseId: string;
  agreementId: string;
  notes: string;
  lines: LineForm[];
  change: boolean;
  reason: string;
}

const editable = (status: string): boolean => status === "draft" || status === "rejected";
const changeable = (status: string): boolean => status === "approved" || status === "sent";

/** Purchase orders (roadmap 4.2): drafted in the supplier's currency, submitted through the workflow, sent by email, changed through revisions, cancelled or closed. */
export function PurchaseOrdersPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<OrderForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/orders");
  const [tab, setTab] = useState("lines");
  const [sendTo, setSendTo] = useState<{ to: string; message: string } | null>(null);
  const suppliers = useSuppliers(companyId);
  const warehouses = useWarehouses(companyId);
  const agreements = useAgreements(companyId);

  const list = useQuery({
    queryKey: ["orders", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["order", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders/{orderId}", { params: { path: { orderId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["orders"] });
    await queryClient.invalidateQueries({ queryKey: ["order"] });
    await queryClient.invalidateQueries({ queryKey: ["agreements"] });
    await queryClient.invalidateQueries({ queryKey: ["agreement"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: OrderForm) => {
      const order = { companyId, partnerId: f.partnerId, currency: f.currency || null, expectedDate: f.expectedDate || null, warehouseId: f.warehouseId || null, agreementId: f.agreementId || null, notes: f.notes || null, lines: orderLineBodies(f.lines) };
      if (!f.id) {
        return unwrap(await api.POST("/api/v1/purchasing/orders", { body: order }));
      }
      return f.change
        ? unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/change", { params: { path: { orderId: f.id } }, body: { order, reason: f.reason } }))
        : unwrap(await api.PUT("/api/v1/purchasing/orders/{orderId}", { params: { path: { orderId: f.id } }, body: order }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "submit" | "send" | "cancel" | "close"; to?: string; message?: string }) => {
      const params = { path: { orderId: input.id } };
      switch (input.action) {
        case "submit": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/submit", { params }));
        case "send": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/send", { params, body: { to: input.to ?? null, message: input.message ?? null } }));
        case "cancel": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/cancel", { params, body: { message: input.message ?? null } }));
        case "close": return unwrap(await api.POST("/api/v1/purchasing/orders/{orderId}/close", { params }));
      }
    },
    onSuccess: async () => { setProblem(null); setSendTo(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<PurchaseOrder, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}{Number(row.original.revision) > 1 ? ` · r${String(row.original.revision)}` : ""}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "orderDate", header: t("purchasing.orderDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.orderDate)}</span> },
      { id: "expected", accessorKey: "expectedDate", header: t("purchasing.expectedDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.expectedDate)}</span> },
      { id: "total", accessorKey: "totalGross", header: t("purchasing.total"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalGross, row.original.currency)}</span> },
    ],
    [t],
  );

  const openForm = (o: PurchaseOrder | null, change = false): void => {
    setProblem(null);
    setForm(o
      ? { id: o.id, partnerId: o.partnerId, currency: o.currency, expectedDate: o.expectedDate ?? "", warehouseId: o.warehouseId ?? "", agreementId: o.agreementId ?? "", notes: o.notes ?? "", change, reason: "", lines: o.lines.map((l) => ({ itemCode: l.itemCode, description: l.description ?? "", quantity: String(l.quantity), uom: l.uomCode, price: String(l.unitPrice), supplierId: "", blanketLineId: l.blanketLineId ?? "" })) }
      : { id: null, partnerId: "", currency: "", expectedDate: "", warehouseId: "", agreementId: "", notes: "", change: false, reason: "", lines: [emptyLine()] });
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const o = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.purchaseOrders")}
        description={t("purchasing.ordersDescription")}
        actions={
          <Button onClick={() => { openForm(null); }} disabled={!companyId} data-testid="new-order">
            <Plus aria-hidden="true" />
            {t("purchasing.newOrder")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "pending_approval", "approved", "sent", "partially_received", "received", "closed", "rejected", "cancelled"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<PurchaseOrder> label="nav.purchaseOrders" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyOrders")} emptyDescription={t("purchasing.emptyOrdersDescription")} onOpen={(row) => { setProblem(null); setTab("lines"); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? (form.change ? t("purchasing.changeOrder") : t("purchasing.editOrder")) : t("purchasing.newOrder")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("partners.supplier")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required data-testid="order-supplier">
                    <option value="">—</option>
                    {(suppliers.data ?? []).map((s) => (
                      <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("partners.currency")} description={t("purchasing.currencyHelp")}>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} data-testid="order-currency" />
                </Field>
                <Field label={t("purchasing.expectedDate")}>
                  <TextField type="date" value={form.expectedDate} onChange={(e) => { setForm({ ...form, expectedDate: e.target.value }); }} dir="ltr" data-testid="order-expected" />
                </Field>
                <WarehouseSelect warehouses={warehouses.data ?? []} value={form.warehouseId} onChange={(id) => { setForm({ ...form, warehouseId: id }); }} label={t("purchasing.deliverTo")} testId="order-warehouse" />
                <Field label={t("nav.agreements")}>
                  <SelectField value={form.agreementId} onChange={(e) => { setForm({ ...form, agreementId: e.target.value }); }} data-testid="order-agreement">
                    <option value="">—</option>
                    {(agreements.data ?? []).filter((a) => a.status === "active" && a.partnerId === form.partnerId).map((a) => (
                      <option key={a.id} value={a.id}>{a.number}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("purchasing.notes")}>
                  <TextareaField value={form.notes} onChange={(e) => { setForm({ ...form, notes: e.target.value }); }} rows={2} />
                </Field>
                {form.change ? (
                  <Field label={t("purchasing.changeReason")} required>
                    <TextField value={form.reason} onChange={(e) => { setForm({ ...form, reason: e.target.value }); }} required data-testid="change-reason" />
                  </Field>
                ) : null}
              </div>
              <LinesEditor lines={form.lines} onChange={(lines) => { setForm({ ...form, lines }); }} showDescription />
              {form.agreementId ? (
                <div className="grid gap-2 sm:grid-cols-2">
                  {form.lines.map((line, index) => (
                    <Field key={index} label={t("purchasing.blanketLineFor", { line: String(index + 1) })}>
                      <SelectField value={line.blanketLineId} onChange={(e) => { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, blanketLineId: e.target.value } : l)) }); }} data-testid={`line-blanket-${String(index)}`}>
                        <option value="">—</option>
                        {(agreements.data?.find((a) => a.id === form.agreementId)?.lines ?? []).map((b) => (
                          <option key={b.id} value={b.id}>{b.itemCode} · {formatNumber(b.remainingQty, { maximumFractionDigits: 3 })} {b.uomCode}</option>
                        ))}
                      </SelectField>
                    </Field>
                  ))}
                </div>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-order">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setSendTo(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {o ? (
            <div className="flex flex-col gap-4" data-testid="order-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{o.number}</span>
                  <span className="text-sm font-normal text-fg-muted" data-testid="order-revision">{t("purchasing.revisionN", { n: String(o.revision) })}</span>
                  <PurchaseStatus status={o.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("partners.supplier"), `${o.partnerCode} · ${localized(o.partnerName)}`],
                [t("purchasing.orderDate"), formatDate(o.orderDate)],
                [t("purchasing.expectedDate"), formatDate(o.expectedDate) || "—"],
                [t("purchasing.terms"), [o.paymentTermsCode, o.deliveryTermsCode].filter(Boolean).join(" · ") || "—"],
                [t("purchasing.total"), <span key="total" data-testid="order-total">{formatMoney(o.totalGross, o.currency)}{o.exchangeRate !== 1 ? ` (${formatNumber(o.totalGrossRc)} @ ${formatNumber(o.exchangeRate, { maximumFractionDigits: 6 })})` : ""}</span>],
                ...(o.sentTo ? [[t("purchasing.sentTo"), `${o.sentTo} · ${formatDateTime(o.sentAt)}`] as [string, string]] : []),
                ...(o.rejectionReason ? [[t("purchasing.rejectionReason"), o.rejectionReason] as [string, string]] : []),
              ]} />
              <Tabs tabs={[{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "revisions", label: t("purchasing.revisions"), testId: "tab-revisions" }, { id: "commitments", label: t("purchasing.commitments"), testId: "tab-commitments" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="purchase_order" entityId={o.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="purchase_order" entityId={o.id} /> : null}
              {tab === "lines" ? <LinesTable lines={o.lines} currency={o.currency} testId="order-lines" /> : null}
              {tab === "revisions" ? (
                o.revisions.length === 0 ? <p className="text-sm text-fg-muted">{t("purchasing.noRevisions")}</p> : (
                  <Table data-testid="order-revisions">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("purchasing.revision")}</TableHead>
                        <TableHead>{t("purchasing.changeReason")}</TableHead>
                        <TableHead>{t("purchasing.changedAt")}</TableHead>
                        <TableHead>{t("purchasing.total")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {o.revisions.map((r) => (
                        <TableRow key={r.revision} data-testid="revision-row">
                          <TableCell>{String(r.revision)}</TableCell>
                          <TableCell dir="auto">{r.reason ?? ""}</TableCell>
                          <TableCell dir="ltr">{formatDateTime(r.changedAt)}</TableCell>
                          <TableCell className="tabular" dir="ltr">{typeof r.snapshot === "object" && r.snapshot !== null && "totalGross" in r.snapshot ? formatMoney(String((r.snapshot as { totalGross: number | string }).totalGross), o.currency) : ""}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )
              ) : null}
              {tab === "commitments" ? (
                o.commitments.length === 0 ? <p className="text-sm text-fg-muted">{t("purchasing.noCommitments")}</p> : (
                  <Table data-testid="order-commitments">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("purchasing.accountRole")}</TableHead>
                        <TableHead>{t("purchasing.period")}</TableHead>
                        <TableHead>{t("purchasing.amount")}</TableHead>
                        <TableHead>{t("common.status")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {o.commitments.map((c) => (
                        <TableRow key={c.id} data-testid="commitment-row">
                          <TableCell>{c.accountRole}</TableCell>
                          <TableCell dir="ltr">{c.periodKey}</TableCell>
                          <TableCell className="tabular" dir="ltr">{formatMoney(c.amountFc, c.currency)}</TableCell>
                          <TableCell><PurchaseStatus status={c.status} /></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )
              ) : null}
              {sendTo ? (
                <div className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-2" data-testid="send-panel">
                  <Field label={t("purchasing.sendTo")} description={t("purchasing.sendToHelp")}>
                    <TextField type="email" value={sendTo.to} onChange={(e) => { setSendTo({ ...sendTo, to: e.target.value }); }} dir="ltr" data-testid="send-to" />
                  </Field>
                  <Field label={t("purchasing.message")}>
                    <TextField value={sendTo.message} onChange={(e) => { setSendTo({ ...sendTo, message: e.target.value }); }} data-testid="send-message" />
                  </Field>
                </div>
              ) : null}
              <DialogFooter>
                {editable(o.status) ? <Button variant="secondary" onClick={() => { openForm(o); }} data-testid="edit-order">{t("common.edit")}</Button> : null}
                {editable(o.status) ? <Button onClick={() => { act.mutate({ id: o.id, action: "submit" }); }} loading={act.isPending} data-testid="submit-order">{t("purchasing.submit")}</Button> : null}
                {changeable(o.status) ? <Button variant="secondary" onClick={() => { openForm(o, true); }} data-testid="change-order">{t("purchasing.changeOrder")}</Button> : null}
                {changeable(o.status) && !sendTo ? <Button onClick={() => { setSendTo({ to: "", message: "" }); }} data-testid="send-order">{t("purchasing.send")}</Button> : null}
                {sendTo ? <Button onClick={() => { act.mutate({ id: o.id, action: "send", ...(sendTo.to ? { to: sendTo.to } : {}), ...(sendTo.message ? { message: sendTo.message } : {}) }); }} loading={act.isPending} data-testid="confirm-send">{t("purchasing.sendNow")}</Button> : null}
                {o.status !== "cancelled" && o.status !== "closed" && o.status !== "received" ? <Button variant="secondary" onClick={() => { act.mutate({ id: o.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-order">{t("purchasing.cancelDocument")}</Button> : null}
                {o.status === "partially_received" || o.status === "sent" || o.status === "approved" ? <Button variant="secondary" onClick={() => { act.mutate({ id: o.id, action: "close" }); }} loading={act.isPending} data-testid="close-order">{t("purchasing.close")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
