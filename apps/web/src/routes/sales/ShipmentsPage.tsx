import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { PackageOpen, Plus, ScanLine, Truck } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useFollowOnSource, useOpenRecord } from "../../lib/documents";
import { formatDate, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { Allocations, PickProgress, PickStatus } from "../inventory/picking";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { num, SalesStatus, type Shipment, type SalesOrderLine } from "./shared";
import { PackagesEditor, PackagesTable } from "./ShipmentPackages";

interface ShipmentForm {
  orderId: string;
  carrier: string;
  trackingNumber: string;
  postingDate: string;
  notes: string;
}

const shippable = (line: SalesOrderLine): boolean => !line.dropShip && line.status !== "cancelled" && Number(line.qtyReserved) > 0;

/** Shipments of a confirmed order's reserved lines (roadmap 5.5): located bin by bin and lot by lot, released to the
 * warehouse as a pick list or posted directly, then packed. Posting goes through the stock engine, which consumes the
 * reservation exactly as much as it ships and books cost of goods sold; what is not shipped stays reserved. */
export function ShipmentsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [creating, setCreating] = useState<ShipmentForm | null>(null);
  const [lineQuantities, setLineQuantities] = useState<Record<string, string>>({});
  const [openId, setOpenId] = useOpenRecord("/sales/shipments");
  const [reversing, setReversing] = useState(false);
  const [reversalReason, setReversalReason] = useState("");
  const [cancellingPick, setCancellingPick] = useState(false);
  const [pickCancelReason, setPickCancelReason] = useState("");
  const [packing, setPacking] = useState(false);
  const [carrierForm, setCarrierForm] = useState<{ carrier: string; trackingNumber: string } | null>(null);
  const [followSource, clearFollowOn] = useFollowOnSource("/sales/shipments");
  const canManage = can("sales.shipment.manage");
  const canPost = can("sales.shipment.post");

  useEffect(() => {
    if (followSource?.type === "sales_order" && !creating) {
      setProblem(null);
      setCreating({ orderId: followSource.id, carrier: "", trackingNumber: "", postingDate: "", notes: "" });
      clearFollowOn();
    }
  }, [followSource, creating, clearFollowOn]);

  const list = useQuery({
    queryKey: ["sales-shipments", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/shipments", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const orders = useQuery({
    queryKey: ["sales-orders", companyId],
    enabled: Boolean(companyId) && Boolean(creating),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/orders", { params: { query: { companyId } } })),
  });
  const shippableOrders = useMemo(() => (orders.data ?? []).filter((o) => o.status === "confirmed" || o.status === "partially_shipped"), [orders.data]);
  const creatingOrder = useQuery({
    queryKey: ["sales-order-for-shipment", creating?.orderId],
    enabled: Boolean(creating?.orderId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/orders/{orderId}", { params: { path: { orderId: creating?.orderId ?? "" } } })),
  });
  useEffect(() => {
    if (creatingOrder.data) {
      setLineQuantities(Object.fromEntries(creatingOrder.data.lines.filter(shippable).map((l) => [l.id, String(l.qtyReserved)])));
    }
  }, [creatingOrder.data]);
  const detail = useQuery({
    queryKey: ["sales-shipment", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/shipments/{shipmentId}", { params: { path: { shipmentId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["sales-shipments"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-shipment"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-orders"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-order"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-order-for-shipment"] });
    await queryClient.invalidateQueries({ queryKey: ["pick-lists"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: ShipmentForm) => unwrap(await api.POST("/api/v1/sales/shipments", {
      body: {
        orderId: f.orderId,
        carrier: f.carrier || null,
        trackingNumber: f.trackingNumber || null,
        postingDate: f.postingDate || null,
        notes: f.notes || null,
        lines: Object.entries(lineQuantities).filter(([, q]) => num(q) > 0).map(([orderLineId, q]) => ({ orderLineId, quantity: num(q) })),
      },
    })),
    onSuccess: async (saved) => { setProblem(null); setCreating(null); await refresh(); setOpenId(saved.id); },
    onError: fail,
  });
  const post = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/sales/shipments/{shipmentId}/post", { params: { path: { shipmentId: id } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const release = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/sales/shipments/{shipmentId}/release", { params: { path: { shipmentId: id } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const cancelPicking = useMutation({
    mutationFn: async (input: { id: string; reason: string }) => unwrap(await api.POST("/api/v1/sales/shipments/{shipmentId}/cancel-picking", { params: { path: { shipmentId: input.id } }, body: { reason: input.reason } })),
    onSuccess: async () => { setProblem(null); setCancellingPick(false); setPickCancelReason(""); await refresh(); },
    onError: fail,
  });
  const saveCarrier = useMutation({
    mutationFn: async (input: { id: string; carrier: string; trackingNumber: string }) => unwrap(await api.PUT("/api/v1/sales/shipments/{shipmentId}/carrier", { params: { path: { shipmentId: input.id } }, body: { carrier: input.carrier || null, trackingNumber: input.trackingNumber || null } })),
    onSuccess: async () => { setProblem(null); setCarrierForm(null); await refresh(); },
    onError: fail,
  });
  const reverse = useMutation({
    mutationFn: async (input: { id: string; reason: string }) => unwrap(await api.POST("/api/v1/sales/shipments/{shipmentId}/reverse", { params: { path: { shipmentId: input.id } }, body: { reason: input.reason } })),
    onSuccess: async () => { setProblem(null); setReversing(false); setReversalReason(""); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Shipment, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("sales.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 130, cell: ({ row }) => <SalesStatus status={row.original.status} /> },
      { id: "order", accessorKey: "orderNumber", header: t("sales.order"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.orderNumber}</span> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "postingDate", header: t("sales.postingDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
      { id: "cogs", accessorKey: "totalCogs", header: t("sales.totalCogs"), size: 150, cell: ({ row }) => (row.original.status === "posted" ? <span className="tabular" dir="ltr">{formatNumber(row.original.totalCogs, { maximumFractionDigits: 2 })}</span> : "—") },
    ],
    [t],
  );

  const submitCreate = (event: FormEvent): void => { event.preventDefault(); if (creating) { create.mutate(creating); } };
  const s = detail.data;
  const shippableLines = (creatingOrder.data?.lines ?? []).filter(shippable);

  return (
    <>
      <PageHeader
        title={t("nav.shipments")}
        description={t("sales.shipmentsDescription")}
        actions={
          canManage ? (
            <Button onClick={() => { setProblem(null); setCreating({ orderId: "", carrier: "", trackingNumber: "", postingDate: "", notes: "" }); }} disabled={!companyId} data-testid="new-shipment">
              <Plus aria-hidden="true" />
              {t("sales.newShipment")}
            </Button>
          ) : null
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "picking", "posted", "reversed"].map((st) => (
              <option key={st} value={st}>{t(`sales.statuses.${st}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Shipment> label="nav.shipments" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.emptyShipments")} emptyDescription={t("sales.emptyShipmentsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(creating)} onOpenChange={(isOpen) => { if (!isOpen) { setCreating(null); setLineQuantities({}); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {creating ? (
            <form onSubmit={submitCreate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("sales.newShipment")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("sales.order")} required className="sm:col-span-2">
                  <SelectField value={creating.orderId} onChange={(e) => { setCreating({ ...creating, orderId: e.target.value }); }} required data-testid="shipment-order">
                    <option value="">—</option>
                    {shippableOrders.map((o) => (
                      <option key={o.id} value={o.id}>{o.number} · {o.partnerCode} · {localized(o.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("sales.postingDate")}>
                  <TextField type="date" value={creating.postingDate} onChange={(e) => { setCreating({ ...creating, postingDate: e.target.value }); }} dir="ltr" data-testid="shipment-date" />
                </Field>
                <Field label={t("sales.carrier")}>
                  <TextField value={creating.carrier} onChange={(e) => { setCreating({ ...creating, carrier: e.target.value }); }} data-testid="shipment-carrier" />
                </Field>
                <Field label={t("sales.trackingNumber")}>
                  <TextField value={creating.trackingNumber} onChange={(e) => { setCreating({ ...creating, trackingNumber: e.target.value }); }} dir="ltr" data-testid="shipment-tracking" />
                </Field>
                <Field label={t("sales.notes")} className="sm:col-span-3">
                  <TextareaField value={creating.notes} onChange={(e) => { setCreating({ ...creating, notes: e.target.value }); }} rows={2} />
                </Field>
              </div>
              {creating.orderId ? (
                shippableLines.length > 0 ? (
                  <div className="flex flex-col gap-2">
                    <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
                    <Table data-testid="shipment-line-editor">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("sales.item")}</TableHead>
                          <TableHead>{t("sales.reserved")}</TableHead>
                          <TableHead>{t("sales.toShip")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {shippableLines.map((l) => (
                          <TableRow key={l.id}>
                            <TableCell dir="auto">{l.itemCode}</TableCell>
                            <TableCell className="tabular" dir="ltr">{formatNumber(l.qtyReserved, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                            <TableCell>
                              <TextField aria-label={t("sales.toShip")} inputMode="decimal" value={lineQuantities[l.id] ?? ""} onChange={(e) => { setLineQuantities({ ...lineQuantities, [l.id]: e.target.value }); }} dir="ltr" className="w-24" data-testid={`shipment-line-qty-${l.itemCode}`} />
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                ) : (
                  <p className="text-xs text-fg-muted">{t("sales.noShippableLines")}</p>
                )
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCreating(null); setLineQuantities({}); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} disabled={shippableLines.length === 0} data-testid="save-shipment">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversing(false); setCancellingPick(false); setPacking(false); setCarrierForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {s ? (
            <div className="flex flex-col gap-4" data-testid="shipment-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{s.number}</span>
                  <SalesStatus status={s.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.order"), <span key="order" dir="ltr">{s.orderNumber}</span>],
                [t("sales.customer"), `${s.partnerCode} · ${localized(s.partnerName)}`],
                [t("sales.postingDate"), formatDate(s.postingDate)],
                ...(s.carrier ? [[t("sales.carrier"), s.carrier] as [string, string]] : []),
                ...(s.trackingNumber ? [[t("sales.trackingNumber"), s.trackingNumber] as [string, string]] : []),
                ...(s.status === "posted" ? [[t("sales.totalCogs"), formatNumber(s.totalCogs, { maximumFractionDigits: 2 })] as [string, string]] : []),
                ...(s.totalWeightKg !== null ? [[t("packing.totalWeight"), `${formatNumber(s.totalWeightKg, { maximumFractionDigits: 3 })} kg`] as [string, string]] : []),
                ...(s.reversalReason ? [[t("sales.reversalReason"), s.reversalReason] as [string, string]] : []),
              ]} />
              {s.pickListId ? (
                <div className="flex flex-wrap items-center gap-3 rounded-md border border-border bg-surface-sunken px-3 py-2 text-sm" data-testid="shipment-pick-list">
                  <span className="text-fg-muted">{t("picking.pickList")}</span>
                  <Link to="/inventory/pick-lists" search={{ open: s.pickListId }} className="font-medium text-accent underline-offset-2 hover:underline" dir="ltr">{s.pickListNumber}</Link>
                  {s.pickListStatus ? <PickStatus status={s.pickListStatus} /> : null}
                  {s.status === "picking" ? <PickProgress done={s.lines.filter((l) => l.qtyPicked !== null && Number(l.qtyPicked) >= Number(l.quantity)).length} total={s.lines.length} /> : null}
                  {s.status === "picking" && s.pickListStatus !== "picked" ? <span className="text-xs text-fg-muted">{t("picking.waitingForWarehouse")}</span> : null}
                  {s.status === "picking" && s.pickListStatus === "picked" ? <span className="text-xs text-success">{t("picking.readyToPost")}</span> : null}
                </div>
              ) : null}
              <Table data-testid="shipment-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("sales.item")}</TableHead>
                    <TableHead>{t("sales.quantity")}</TableHead>
                    <TableHead>{t("picking.from")}</TableHead>
                    {s.status === "picking" ? <TableHead>{t("picking.picked")}</TableHead> : null}
                    {s.packages.length > 0 ? <TableHead>{t("packing.packed")}</TableHead> : null}
                    {s.status === "posted" ? <TableHead>{t("sales.cogsAmount")}</TableHead> : null}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {s.lines.map((l) => (
                    <TableRow key={l.id} data-testid="shipment-line">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">{l.itemCode}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                      <TableCell><Allocations parts={l.allocations} uom={l.uomCode} /></TableCell>
                      {s.status === "picking" ? <TableCell className="tabular" dir="ltr" data-testid="line-picked">{l.qtyPicked === null ? "—" : formatNumber(l.qtyPicked, { maximumFractionDigits: 3 })}</TableCell> : null}
                      {s.packages.length > 0 ? <TableCell className="tabular" dir="ltr" data-testid="line-packed">{formatNumber(l.qtyPacked, { maximumFractionDigits: 3 })}</TableCell> : null}
                      {s.status === "posted" ? <TableCell className="tabular" dir="ltr">{formatNumber(l.cogsAmount, { maximumFractionDigits: 2 })}</TableCell> : null}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <section className="flex flex-col gap-2" aria-labelledby="packages-heading">
                <div className="flex items-center justify-between gap-2">
                  <h3 id="packages-heading" className="text-sm font-semibold">{t("packing.title")}</h3>
                  {canManage && s.status !== "reversed" && !packing ? (
                    <Button variant="secondary" size="sm" onClick={() => { setPacking(true); }} data-testid="edit-packages">
                      <PackageOpen aria-hidden="true" />
                      {s.packages.length > 0 ? t("packing.edit") : t("packing.pack")}
                    </Button>
                  ) : null}
                </div>
                {packing ? <PackagesEditor shipment={s} onSaved={async () => { setPacking(false); await refresh(); }} onCancel={() => { setPacking(false); }} /> : <PackagesTable shipment={s} />}
              </section>
              {carrierForm ? (
                <div className="grid gap-3 rounded-md border border-border p-3 sm:grid-cols-2" data-testid="carrier-form">
                  <Field label={t("sales.carrier")}>
                    <TextField value={carrierForm.carrier} onChange={(e) => { setCarrierForm({ ...carrierForm, carrier: e.target.value }); }} data-testid="carrier-name" />
                  </Field>
                  <Field label={t("sales.trackingNumber")}>
                    <TextField value={carrierForm.trackingNumber} onChange={(e) => { setCarrierForm({ ...carrierForm, trackingNumber: e.target.value }); }} dir="ltr" data-testid="carrier-tracking" />
                  </Field>
                  <div className="flex justify-end gap-2 sm:col-span-2">
                    <Button variant="secondary" onClick={() => { setCarrierForm(null); }}>{t("common.cancel")}</Button>
                    <Button onClick={() => { saveCarrier.mutate({ id: s.id, ...carrierForm }); }} loading={saveCarrier.isPending} data-testid="save-carrier">{t("common.save")}</Button>
                  </div>
                </div>
              ) : null}
              {cancellingPick ? (
                <Field label={t("picking.takeBackReason")} required>
                  <TextField value={pickCancelReason} onChange={(e) => { setPickCancelReason(e.target.value); }} required data-testid="cancel-picking-reason" />
                </Field>
              ) : null}
              {reversing ? (
                <Field label={t("sales.reversalReason")} required>
                  <TextField value={reversalReason} onChange={(e) => { setReversalReason(e.target.value); }} required data-testid="reversal-reason" />
                </Field>
              ) : null}
              <DialogFooter>
                {canManage && s.status !== "reversed" && !carrierForm ? (
                  <Button variant="ghost" onClick={() => { setCarrierForm({ carrier: s.carrier ?? "", trackingNumber: s.trackingNumber ?? "" }); }} data-testid="edit-carrier">
                    <Truck aria-hidden="true" />
                    {t("packing.carrierDetails")}
                  </Button>
                ) : null}
                {s.status === "draft" && canManage ? (
                  <Button variant="secondary" onClick={() => { release.mutate(s.id); }} loading={release.isPending} data-testid="release-shipment">
                    <ScanLine aria-hidden="true" />
                    {t("picking.release")}
                  </Button>
                ) : null}
                {s.status === "picking" && canManage && !cancellingPick ? <Button variant="secondary" onClick={() => { setCancellingPick(true); setPickCancelReason(""); }} data-testid="start-cancel-picking">{t("picking.takeBack")}</Button> : null}
                {cancellingPick ? <Button variant="danger" onClick={() => { cancelPicking.mutate({ id: s.id, reason: pickCancelReason }); }} loading={cancelPicking.isPending} disabled={!pickCancelReason.trim()} data-testid="confirm-cancel-picking">{t("picking.confirmTakeBack")}</Button> : null}
                {(s.status === "draft" || (s.status === "picking" && s.pickListStatus === "picked")) && canPost ? <Button onClick={() => { post.mutate(s.id); }} loading={post.isPending} data-testid="post-shipment">{s.status === "picking" ? t("picking.postPicked") : t("sales.post")}</Button> : null}
                {s.status === "posted" && canPost && !reversing ? <Button variant="secondary" onClick={() => { setReversing(true); setReversalReason(""); }} data-testid="start-reverse-shipment">{t("sales.reverse")}</Button> : null}
                {reversing ? <Button onClick={() => { reverse.mutate({ id: s.id, reason: reversalReason }); }} loading={reverse.isPending} data-testid="confirm-reverse-shipment">{t("sales.confirmReverse")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
