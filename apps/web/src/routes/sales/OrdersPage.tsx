import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { PackageCheck, Plus, RefreshCw, Truck } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { followOn, useOpenRecord } from "../../lib/documents";
import { formatDate, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { useCompanyCustomers } from "./pricing/shared";
import { emptySalesLine, Money, money, orderLineBodies, SalesLinesEditor, SalesLinesTable, SalesStatus, type SalesLineForm, type SalesOrder, type SalesOrderLine } from "./shared";

interface OrderForm {
  partnerId: string;
  warehouseId: string;
  currency: string;
  orderDate: string;
  notes: string;
  lines: SalesLineForm[];
}

const emptyForm = (): OrderForm => ({ partnerId: "", warehouseId: "", currency: "", orderDate: "", notes: "", lines: [emptySalesLine()] });
const cancellable = (line: SalesOrderLine): boolean => line.status !== "cancelled" && Number(line.quantity) - Number(line.qtyCancelled) - Number(line.qtyShipped) > 0;

/** Sales orders (roadmap 5.4b): reserved against stock on confirmation (a short line backorders), credit-checked for the customer, cancelled with a reason. */
export function OrdersPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [creating, setCreating] = useState<OrderForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/sales/orders");
  const [cancelling, setCancelling] = useState(false);
  const [cancelReason, setCancelReason] = useState("");
  const [cancellingLine, setCancellingLine] = useState<string | null>(null);
  const [cancelLineQty, setCancelLineQty] = useState("");
  const [linkingLine, setLinkingLine] = useState<string | null>(null);
  const [linkPurchaseOrderLineId, setLinkPurchaseOrderLineId] = useState("");
  const customers = useCompanyCustomers(companyId);
  const warehouses = useWarehouses(companyId);
  const canManage = can("sales.order.manage");

  const list = useQuery({
    queryKey: ["sales-orders", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/orders", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["sales-order", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/orders/{orderId}", { params: { path: { orderId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["sales-orders"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-order"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: OrderForm) => unwrap(await api.POST("/api/v1/sales/orders", {
      body: { companyId, partnerId: f.partnerId, warehouseId: f.warehouseId, currency: f.currency || null, orderDate: f.orderDate || null, notes: f.notes || null, lines: orderLineBodies(f.lines) },
    })),
    onSuccess: async (saved) => { setProblem(null); setCreating(null); await refresh(); setOpenId(saved.id); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "confirm" | "retry" | "cancel"; reason?: string }) => {
      const params = { path: { orderId: input.id } };
      switch (input.action) {
        case "confirm": return unwrap(await api.POST("/api/v1/sales/orders/{orderId}/confirm", { params }));
        case "retry": return unwrap(await api.POST("/api/v1/sales/orders/{orderId}/retry-backorders", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/sales/orders/{orderId}/cancel", { params, body: { reason: input.reason ?? "" } }));
      }
    },
    onSuccess: async () => { setProblem(null); setCancelling(false); setCancelReason(""); await refresh(); },
    onError: fail,
  });
  const cancelLine = useMutation({
    mutationFn: async (input: { orderId: string; lineId: string; quantity: number }) => unwrap(await api.POST("/api/v1/sales/orders/{orderId}/lines/{lineId}/cancel", { params: { path: { orderId: input.orderId, lineId: input.lineId } }, body: { quantity: input.quantity } })),
    onSuccess: async () => { setProblem(null); setCancellingLine(null); setCancelLineQty(""); await refresh(); },
    onError: fail,
  });
  const linkLine = useMutation({
    mutationFn: async (input: { orderId: string; lineId: string; purchaseOrderLineId: string }) => unwrap(await api.POST("/api/v1/sales/orders/{orderId}/lines/{lineId}/purchase-order", { params: { path: { orderId: input.orderId, lineId: input.lineId } }, body: { purchaseOrderLineId: input.purchaseOrderLineId } })),
    onSuccess: async () => { setProblem(null); setLinkingLine(null); setLinkPurchaseOrderLineId(""); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<SalesOrder, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("sales.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 130, cell: ({ row }) => <SalesStatus status={row.original.status} /> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "orderDate", header: t("sales.orderDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.orderDate)}</span> },
      { id: "total", accessorKey: "totalGross", header: t("sales.total"), size: 150, cell: ({ row }) => <Money amount={row.original.totalGross} currency={row.original.currency} /> },
    ],
    [t],
  );

  const submitCreate = (event: FormEvent): void => { event.preventDefault(); if (creating) { create.mutate(creating); } };
  const o = detail.data;
  const hasBackorders = Boolean(o?.lines.some((l) => l.status === "backordered"));

  return (
    <>
      <PageHeader
        title={t("nav.salesOrders")}
        description={t("sales.ordersDescription")}
        actions={
          canManage ? (
            <Button onClick={() => { setProblem(null); setCreating(emptyForm()); }} disabled={!companyId} data-testid="new-order">
              <Plus aria-hidden="true" />
              {t("sales.newOrder")}
            </Button>
          ) : null
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "confirmed", "on_hold", "partially_shipped", "shipped", "cancelled"].map((s) => (
              <option key={s} value={s}>{t(`sales.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<SalesOrder> label="nav.salesOrders" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.emptyOrders")} emptyDescription={t("sales.emptyOrdersDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(creating)} onOpenChange={(isOpen) => { if (!isOpen) { setCreating(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {creating ? (
            <form onSubmit={submitCreate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("sales.newOrder")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("sales.customer")} required>
                  <SelectField value={creating.partnerId} onChange={(e) => { setCreating({ ...creating, partnerId: e.target.value }); }} required data-testid="order-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => (
                      <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <WarehouseSelect warehouses={warehouses.data ?? []} value={creating.warehouseId} onChange={(id) => { setCreating({ ...creating, warehouseId: id }); }} label={t("sales.shipFrom")} testId="order-warehouse" required />
                <Field label={t("partners.currency")} description={t("purchasing.currencyHelp")}>
                  <TextField value={creating.currency} onChange={(e) => { setCreating({ ...creating, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} data-testid="order-currency" />
                </Field>
                <Field label={t("sales.orderDate")}>
                  <TextField type="date" value={creating.orderDate} onChange={(e) => { setCreating({ ...creating, orderDate: e.target.value }); }} dir="ltr" data-testid="order-date" />
                </Field>
                <Field label={t("sales.notes")} className="sm:col-span-2">
                  <TextareaField value={creating.notes} onChange={(e) => { setCreating({ ...creating, notes: e.target.value }); }} rows={2} />
                </Field>
              </div>
              <SalesLinesEditor lines={creating.lines} onChange={(lines) => { setCreating({ ...creating, lines }); }} showDropShip />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCreating(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-order">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setCancelling(false); setCancellingLine(null); setLinkingLine(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {o ? (
            <div className="flex flex-col gap-4" data-testid="order-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{o.number}</span>
                  <SalesStatus status={o.status} />
                </DialogTitle>
              </DialogHeader>
              {o.status === "on_hold" ? (
                <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger" role="alert" data-testid="credit-hold-banner">
                  <span>{t(`sales.blockKinds.${o.blockKind ?? "credit_limit"}`, { defaultValue: o.blockKind ?? "" })}: {o.blockReason}</span>
                  <Button asChild variant="secondary" size="sm">
                    <Link to="/approvals" data-testid="open-approvals">{t("sales.openApprovals")}</Link>
                  </Button>
                </div>
              ) : null}
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.customer"), `${o.partnerCode} · ${localized(o.partnerName)}`],
                [t("sales.orderDate"), formatDate(o.orderDate)],
                [t("sales.total"), <span key="total" data-testid="order-total">{money(o.totalGross, o.currency)}</span>],
                ...(o.cancelReason ? [[t("sales.cancelReason"), o.cancelReason] as [string, string]] : []),
              ]} />
              <SalesLinesTable
                lines={o.lines}
                currency={o.currency}
                testId="order-lines"
                dropShipActions={(line) => (
                  line.purchaseOrderLineId ? (
                    <Badge tone="success" data-testid="drop-ship-linked">{t("sales.linkedToPurchaseOrder")}</Badge>
                  ) : linkingLine === line.id ? (
                    <span className="flex items-center gap-2">
                      <TextField aria-label={t("sales.purchaseOrderLineId")} value={linkPurchaseOrderLineId} onChange={(e) => { setLinkPurchaseOrderLineId(e.target.value); }} dir="ltr" className="w-48" placeholder={t("sales.purchaseOrderLineIdPlaceholder")} data-testid="link-purchase-order-line-input" />
                      <Button type="button" size="sm" onClick={() => { linkLine.mutate({ orderId: o.id, lineId: line.id, purchaseOrderLineId: linkPurchaseOrderLineId }); }} loading={linkLine.isPending} disabled={!linkPurchaseOrderLineId} data-testid="confirm-link-purchase-order-line">{t("common.save")}</Button>
                    </span>
                  ) : (
                    <span className="flex items-center gap-2">
                      <Button type="button" variant="secondary" size="sm" onClick={() => { void navigate({ to: "/purchasing/orders", search: followOn("sales_order_line", `${o.id}:${line.id}`) }); }} data-testid="create-purchase-order">
                        <Truck aria-hidden="true" />
                        {t("sales.createPurchaseOrder")}
                      </Button>
                      <Button type="button" variant="ghost" size="sm" onClick={() => { setLinkingLine(line.id); setLinkPurchaseOrderLineId(""); }} data-testid="start-link-purchase-order-line">{t("sales.linkExisting")}</Button>
                    </span>
                  )
                )}
              />
              {o.status === "confirmed" || o.status === "on_hold" ? (
                <div className="flex flex-col gap-2" data-testid="order-line-cancel">
                  {o.lines.filter((l) => cancellable(l) && canManage).map((l) => (
                    <div key={l.id} className="flex flex-wrap items-center gap-2 text-sm">
                      <span dir="ltr">{String(l.lineNo)} · {l.itemCode}</span>
                      {cancellingLine === l.id ? (
                        <>
                          <TextField aria-label={t("sales.cancelQuantity")} inputMode="decimal" value={cancelLineQty} onChange={(e) => { setCancelLineQty(e.target.value); }} dir="ltr" className="w-24" data-testid={`cancel-line-qty-${String(l.lineNo)}`} />
                          <Button type="button" size="sm" onClick={() => { cancelLine.mutate({ orderId: o.id, lineId: l.id, quantity: Number(cancelLineQty || "0") }); }} loading={cancelLine.isPending} data-testid={`confirm-cancel-line-${String(l.lineNo)}`}>{t("sales.confirmCancel")}</Button>
                        </>
                      ) : (
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setCancellingLine(l.id); setCancelLineQty(String(Number(l.quantity) - Number(l.qtyCancelled) - Number(l.qtyShipped))); }} data-testid={`cancel-line-${String(l.lineNo)}`}>{t("sales.cancelLine")}</Button>
                      )}
                    </div>
                  ))}
                </div>
              ) : null}
              {cancelling ? (
                <Field label={t("sales.cancelReason")} required>
                  <TextField value={cancelReason} onChange={(e) => { setCancelReason(e.target.value); }} required data-testid="cancel-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {o.status === "draft" && canManage ? <Button onClick={() => { act.mutate({ id: o.id, action: "confirm" }); }} loading={act.isPending} data-testid="confirm-order">{t("sales.confirmOrder")}</Button> : null}
                {o.status === "confirmed" && hasBackorders && canManage ? <Button variant="secondary" onClick={() => { act.mutate({ id: o.id, action: "retry" }); }} loading={act.isPending} data-testid="retry-backorders"><RefreshCw aria-hidden="true" />{t("sales.retryBackorders")}</Button> : null}
                {(o.status === "confirmed" || o.status === "partially_shipped") && can("sales.shipment.manage") ? (
                  <Button variant="secondary" onClick={() => { void navigate({ to: "/sales/shipments", search: followOn("sales_order", o.id) }); }} data-testid="create-shipment">
                    <PackageCheck aria-hidden="true" />
                    {t("sales.createShipment")}
                  </Button>
                ) : null}
                {o.status !== "cancelled" && canManage && !cancelling ? <Button variant="secondary" onClick={() => { setCancelling(true); setCancelReason(""); }} data-testid="start-cancel-order">{t("sales.cancelOrder")}</Button> : null}
                {cancelling ? <Button onClick={() => { act.mutate({ id: o.id, action: "cancel", reason: cancelReason }); }} loading={act.isPending} data-testid="confirm-cancel-order">{t("sales.confirmCancel")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
