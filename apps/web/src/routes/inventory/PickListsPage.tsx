import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { ScanLine } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { PickProgress, PickStatus, type PickListRow } from "./picking";
import { CompanyFilter, KeyValues, Qty, useCompanyContext, useWarehouses, WarehouseSelect } from "./shared";

const statusFilters = ["live", "released", "in_progress", "picked", "closed", "cancelled", ""] as const;

/**
 * The warehouse's picking work (roadmap 5.5, A-154): pick lists released from shipments, in walking order, with who has
 * each and how far they got. A supervisor hands lists to pickers or cancels one; the pickers work them on the scanner.
 */
export function PickListsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [status, setStatus] = useState<string>("live");
  const [openId, setOpenId] = useOpenRecord("/inventory/pick-lists");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [cancelReason, setCancelReason] = useState("");
  const [assignee, setAssignee] = useState("");
  const canManage = can("inventory.pick.manage");
  const canPick = can("inventory.pick.execute");

  const list = useQuery({
    queryKey: ["pick-lists", companyId, warehouseId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/pick-lists", { params: { query: { companyId, ...(warehouseId ? { warehouseId } : {}), ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["pick-list", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/pick-lists/{pickListId}", { params: { path: { pickListId: openId ?? "" } } })),
  });
  const members = useQuery({
    queryKey: ["members"],
    enabled: canManage && can("identity.user.read") && Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/users")),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["pick-lists"] });
    await queryClient.invalidateQueries({ queryKey: ["pick-list"] });
    await queryClient.invalidateQueries({ queryKey: ["sales-shipment"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const assign = useMutation({
    mutationFn: async (input: { id: string; membershipId: string | null }) => unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/assign", { params: { path: { pickListId: input.id } }, body: { membershipId: input.membershipId } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const claim = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/claim", { params: { path: { pickListId: id } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });
  const cancel = useMutation({
    mutationFn: async (input: { id: string; reason: string }) => unwrap(await api.POST("/api/v1/inventory/pick-lists/{pickListId}/cancel", { params: { path: { pickListId: input.id } }, body: { reason: input.reason } })),
    onSuccess: async () => { setProblem(null); setCancelling(false); setCancelReason(""); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<PickListRow, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("picking.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <PickStatus status={row.original.status} /> },
      { id: "source", accessorKey: "sourceNumber", header: t("picking.source"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.sourceNumber}</span> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.warehouseCode}</span> },
      { id: "assigned", accessorKey: "assignedName", header: t("picking.picker"), size: 170, cell: ({ row }) => row.original.assignedName ?? <span className="text-fg-muted">{t("picking.unassigned")}</span> },
      { id: "progress", accessorKey: "linesDone", header: t("picking.lines"), size: 150, cell: ({ row }) => <PickProgress done={row.original.linesDone} total={row.original.linesTotal} /> },
      { id: "quantity", accessorKey: "qtyPicked", header: t("picking.pickedOfPlanned"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr"><Qty value={row.original.qtyPicked} /> / <Qty value={row.original.qtyToPick} /></span> },
      { id: "created", accessorKey: "createdAt", header: t("picking.released"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.createdAt)}</span> },
    ],
    [t],
  );

  const p = detail.data;
  const live = p ? ["released", "in_progress", "picked"].includes(p.status) : false;
  const close = (): void => { setOpenId(null); setCancelling(false); setProblem(null); };

  return (
    <>
      <PageHeader
        title={t("nav.pickLists")}
        description={t("picking.description")}
        actions={
          canPick ? (
            <Button asChild variant="secondary">
              <Link to="/m/pick" data-testid="open-scanner-picking">
                <ScanLine aria-hidden="true" />
                {t("picking.openScanner")}
              </Link>
            </Button>
          ) : null
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={(id) => { setCompanyId(id); setWarehouseId(""); }} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll testId="pick-warehouse-filter" />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="pick-status-filter">
            {statusFilters.map((st) => (
              <option key={st || "all"} value={st}>{st === "" ? t("common.all") : st === "live" ? t("picking.live") : t(`picking.statuses.${st}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<PickListRow>
        label="nav.pickLists"
        entityType="stock_pick_list"
        columns={columns}
        data={list.data ?? []}
        rowKey={(row) => row.id}
        loading={list.isPending && Boolean(companyId)}
        emptyTitle={t("picking.empty")}
        emptyDescription={t("picking.emptyDescription")}
        onOpen={(row) => { setProblem(null); setOpenId(row.id); }}
      />

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { close(); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {p ? (
            <div className="flex flex-col gap-4" data-testid="pick-list-detail">
              <DialogHeader>
                <DialogTitle className="flex flex-wrap items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{p.number}</span>
                  <PickStatus status={p.status} />
                  <PickProgress done={p.linesDone} total={p.linesTotal} testId="pick-list-progress" />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues
                entries={[
                  [t("picking.source"), (
                    <Link key="source" to="/sales/shipments" search={{ open: p.sourceDocumentId }} className="text-accent underline-offset-2 hover:underline" dir="ltr" data-testid="pick-source-link">
                      {p.sourceNumber}
                    </Link>
                  )],
                  [t("inventory.warehouse"), <span key="wh" dir="ltr">{p.warehouseCode}</span>],
                  [t("picking.picker"), p.assignedName ?? t("picking.unassigned")],
                  [t("picking.released"), formatDateTime(p.createdAt)],
                  ...(p.startedAt ? [[t("picking.started"), formatDateTime(p.startedAt)] as [string, string]] : []),
                  ...(p.completedAt ? [[t("picking.completed"), formatDateTime(p.completedAt)] as [string, string]] : []),
                  ...(p.cancelReason ? [[t("picking.cancelReason"), p.cancelReason] as [string, string]] : []),
                ]}
              />
              <Table data-testid="pick-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("picking.walk")}</TableHead>
                    <TableHead>{t("picking.bin")}</TableHead>
                    <TableHead>{t("sales.item")}</TableHead>
                    <TableHead>{t("sales.lot")}</TableHead>
                    <TableHead>{t("picking.toPick")}</TableHead>
                    <TableHead>{t("picking.picked")}</TableHead>
                    <TableHead>{t("common.status")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {p.lines.map((l) => {
                    const elsewhere = l.status !== "open" && Number(l.qtyPicked) > 0 && (l.pickedBinId !== l.binId || l.pickedLotId !== l.lotId);
                    return (
                      <TableRow key={l.id} data-testid="pick-line">
                        <TableCell className="tabular">{String(l.lineNo)}</TableCell>
                        <TableCell dir="ltr">
                          {l.binCode ?? "—"}
                          {l.zone ? <span className="ms-1 text-xs text-fg-muted">({l.zone})</span> : null}
                        </TableCell>
                        <TableCell>
                          <span dir="ltr">{l.itemCode}</span> <span className="text-fg-muted">{localized(l.itemName)}</span>
                          {l.serialNumbers.length > 0 ? <div className="text-xs text-fg-muted" dir="ltr">{l.serialNumbers.join(", ")}</div> : null}
                        </TableCell>
                        <TableCell dir="ltr">{l.lotNumber ? `${l.lotNumber}${l.expiresOn ? ` · ${formatDate(l.expiresOn)}` : ""}` : "—"}</TableCell>
                        <TableCell><Qty value={l.qtyToPick} uom={l.uomCode} /></TableCell>
                        <TableCell>
                          {l.status === "open" ? "—" : <Qty value={l.qtyPicked} uom={l.uomCode} />}
                          {elsewhere ? (
                            <div className="text-xs text-fg-muted" data-testid="picked-elsewhere">
                              {t("picking.pickedFrom", { place: [l.pickedBinCode, l.pickedLotNumber].filter(Boolean).join(" · ") })}
                            </div>
                          ) : null}
                        </TableCell>
                        <TableCell>
                          <PickStatus status={l.status} testId="pick-line-status" />
                          {l.shortReason ? <div className="text-xs text-fg-muted">{l.shortReason}</div> : null}
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              {live && canManage ? (
                <div className="flex flex-wrap items-end gap-3 rounded-md border border-border p-3">
                  {members.data ? (
                    <Field label={t("picking.assignTo")}>
                      <SelectField value={assignee} onChange={(e) => { setAssignee(e.target.value); }} data-testid="pick-assignee">
                        <option value="">{t("picking.unassigned")}</option>
                        {members.data.filter((m) => m.status === "active").map((m) => (
                          <option key={m.membershipId} value={m.membershipId}>{m.displayName}</option>
                        ))}
                      </SelectField>
                    </Field>
                  ) : null}
                  {members.data ? (
                    <Button variant="secondary" onClick={() => { assign.mutate({ id: p.id, membershipId: assignee || null }); }} loading={assign.isPending} data-testid="pick-assign">
                      {t("picking.assign")}
                    </Button>
                  ) : null}
                  {cancelling ? (
                    <Field label={t("picking.cancelReason")} required className="min-w-64 flex-1">
                      <TextField value={cancelReason} onChange={(e) => { setCancelReason(e.target.value); }} required data-testid="pick-cancel-reason" />
                    </Field>
                  ) : null}
                </div>
              ) : null}
              <DialogFooter>
                {live && canPick && !p.assignedTo ? (
                  <Button variant="secondary" onClick={() => { claim.mutate(p.id); }} loading={claim.isPending} data-testid="pick-claim">{t("picking.takeIt")}</Button>
                ) : null}
                {live && canPick ? (
                  <Button asChild variant="secondary">
                    <Link to="/m/pick" search={{ list: p.id }} data-testid="pick-on-scanner">
                      <ScanLine aria-hidden="true" />
                      {t("picking.pickOnScanner")}
                    </Link>
                  </Button>
                ) : null}
                {live && canManage && !cancelling ? <Button variant="secondary" onClick={() => { setCancelling(true); setCancelReason(""); }} data-testid="pick-start-cancel">{t("picking.cancel")}</Button> : null}
                {cancelling ? <Button variant="danger" onClick={() => { cancel.mutate({ id: p.id, reason: cancelReason }); }} loading={cancel.isPending} disabled={!cancelReason.trim()} data-testid="pick-confirm-cancel">{t("picking.confirmCancel")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
