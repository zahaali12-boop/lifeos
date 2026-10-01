import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Input, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { today } from "../accounting/shared";
import { DocStatus, KeyValues, Qty, Tabs, findItemByCode } from "./shared";
import { ItemCodeField } from "./ItemCodeField";

type Lot = components["schemas"]["LotInfo"];
type Serial = components["schemas"]["SerialInfo"];

/** An active lot past its expiry date is expired already (stock rules block it by date); the daily job only records it. */
function lotStatus(lot: Lot): string {
  return lot.status === "active" && lot.expiresOn && lot.expiresOn < today() ? "expired" : lot.status;
}

const lotStatuses = ["active", "quarantine", "recalled", "expired", "consumed"];
const serialStatuses = ["in_stock", "in_transit", "sold", "returned", "in_repair", "scrapped", "consumed", "consigned"];

/** Lots and serials (roadmap 3.5): where each lot is, its expiry and status, its trace forward and back; every serial's timeline. */
export function TrackingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const [tab, setTab] = useState(search.tab === "serials" ? "serials" : "lots");
  const [itemCode, setItemCode] = useState("");
  const [status, setStatus] = useState("");
  const [query, setQuery] = useState("");
  const [nextStatus, setNextStatus] = useState("");
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openLot = search.lot;
  const openSerial = search.serial;

  const item = useQuery({ queryKey: ["item-by-code", itemCode], enabled: itemCode.trim().length > 0, queryFn: () => findItemByCode(itemCode) });
  const itemId = item.data?.id;
  const lots = useQuery({
    queryKey: ["lots", itemId ?? "", status, query],
    enabled: tab === "lots",
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/lots", { params: { query: { ...(itemId ? { itemId } : {}), ...(status ? { status } : {}), ...(query.trim() ? { q: query.trim() } : {}) } } })),
  });
  const serials = useQuery({
    queryKey: ["serials", itemId ?? "", status, query],
    enabled: tab === "serials",
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials", { params: { query: { ...(itemId ? { itemId } : {}), ...(status ? { status } : {}), ...(query.trim() ? { q: query.trim() } : {}) } } })),
  });
  const trace = useQuery({
    queryKey: ["lot-trace", openLot],
    enabled: Boolean(openLot),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/lots/{lotId}/trace", { params: { path: { lotId: openLot ?? "" } } })),
  });
  const history = useQuery({
    queryKey: ["serial-history", openSerial],
    enabled: Boolean(openSerial),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/serials/{serialId}/history", { params: { path: { serialId: openSerial ?? "" } } })),
  });
  const go = (patch: { lot?: string; serial?: string }): void => { void navigate({ to: "/inventory/tracking", search: { tab, ...patch } }); };

  const changeLot = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/lots/{lotId}/status", { params: { path: { lotId: openLot ?? "" } }, body: { status: nextStatus, reason: reason || null, recallReference: nextStatus === "recalled" ? reason || null : null } })),
    onSuccess: async () => {
      setProblem(null);
      setReason("");
      await queryClient.invalidateQueries({ queryKey: ["lots"] });
      await queryClient.invalidateQueries({ queryKey: ["lot-trace", openLot] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const changeSerial = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/serials/{serialId}/status", { params: { path: { serialId: openSerial ?? "" } }, body: { status: nextStatus, note: reason || null } })),
    onSuccess: async () => {
      setProblem(null);
      setReason("");
      await queryClient.invalidateQueries({ queryKey: ["serials"] });
      await queryClient.invalidateQueries({ queryKey: ["serial-history", openSerial] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const lotColumns = useMemo<ColumnDef<Lot, unknown>[]>(
    () => [
      { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.lotNumber}</span> },
      { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.itemCode}</span> },
      { id: "mfg", accessorKey: "manufacturedOn", header: t("inventory.tracking.manufactured"), size: 120, cell: ({ row }) => formatDate(row.original.manufacturedOn) },
      { id: "exp", accessorKey: "expiresOn", header: t("inventory.expiresOn"), size: 120, cell: ({ row }) => formatDate(row.original.expiresOn) },
      { id: "supplierLot", accessorKey: "supplierLot", header: t("inventory.tracking.supplierLot"), size: 130 },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={lotStatus(row.original)} /> },
    ],
    [t],
  );
  const serialColumns = useMemo<ColumnDef<Serial, unknown>[]>(
    () => [
      { id: "serial", accessorKey: "serialNumber", header: t("inventory.stock.serial"), size: 160, cell: ({ row }) => <span dir="ltr">{row.original.serialNumber}</span> },
      { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.itemCode}</span> },
      { id: "lot", accessorKey: "lotNumber", header: t("inventory.stock.lot"), size: 120 },
      { id: "warehouse", accessorKey: "currentWarehouseCode", header: t("inventory.warehouse"), size: 120 },
      { id: "warranty", accessorKey: "warrantyUntil", header: t("inventory.tracking.warranty"), size: 120, cell: ({ row }) => formatDate(row.original.warrantyUntil) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const lotDetail = trace.data;
  const serialDetail = history.data;

  return (
    <>
      <PageHeader title={t("nav.tracking")} description={t("inventory.tracking.description")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <Field label={t("inventory.itemCode")} description={item.isSuccess && item.data === null && itemCode.trim() ? t("inventory.itemUnknown") : undefined}>
          <ItemCodeField value={itemCode} onChange={setItemCode} data-testid="tracking-item" />
        </Field>
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            <option value="">{t("accounting.anyStatus")}</option>
            {(tab === "lots" ? lotStatuses : serialStatuses).map((s) => (
              <option key={s} value={s}>
                {t(`inventory.statuses.${s}`)}
              </option>
            ))}
          </SelectField>
        </Field>
        <Field label={t("common.search")}>
          <Input type="search" value={query} onChange={(e) => { setQuery(e.target.value); }} aria-label={t("common.search")} data-testid="tracking-search" />
        </Field>
      </div>
      <Tabs value={tab} onChange={(id) => { setTab(id); setStatus(""); }} tabs={[{ id: "lots", label: t("inventory.tracking.lots"), testId: "tab-lots" }, { id: "serials", label: t("inventory.tracking.serials"), testId: "tab-serials" }]} />
      {tab === "lots" ? <DataGrid<Lot> label="inventory.tracking.lots" columns={lotColumns} data={lots.data ?? []} rowKey={(row) => row.id} loading={lots.isPending} onOpen={(row) => { go({ lot: row.id }); }} emptyTitle={t("inventory.tracking.noLots")} emptyDescription={t("inventory.tracking.noLotsHint")} /> : null}
      {tab === "serials" ? <DataGrid<Serial> label="inventory.tracking.serials" columns={serialColumns} data={serials.data ?? []} rowKey={(row) => row.id} loading={serials.isPending} onOpen={(row) => { go({ serial: row.id }); }} emptyTitle={t("inventory.tracking.noSerials")} emptyDescription={t("inventory.tracking.noSerialsHint")} /> : null}

      <Dialog open={Boolean(openLot)} onOpenChange={(isOpen) => { if (!isOpen) { go({}); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {lotDetail ? `${lotDetail.lot.itemCode} · ${lotDetail.lot.lotNumber}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {lotDetail ? (
            <div className="flex flex-col gap-4" data-testid="lot-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={lotStatus(lotDetail.lot)} />
                {lotDetail.lot.expiresOn ? <span className="text-fg-muted">{t("inventory.expiresOn")}: {formatDate(lotDetail.lot.expiresOn)}</span> : null}
                {lotDetail.lot.statusReason ? <span className="text-fg-muted">{lotDetail.lot.statusReason}</span> : null}
                {lotDetail.lot.recallReference ? <span className="text-danger">{t("inventory.tracking.recall")}: {lotDetail.lot.recallReference}</span> : null}
              </div>
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.stock.onHand")}</h3>
                <KeyValues entries={lotDetail.onHand.map((b) => [b.warehouseCode, <Qty key={b.warehouseId} value={b.onHand} />])} />
              </section>
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.tracking.movements")}</h3>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("accounting.date")}</TableHead>
                      <TableHead>{t("inventory.stock.entryType")}</TableHead>
                      <TableHead>{t("inventory.warehouse")}</TableHead>
                      <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                      <TableHead>{t("inventory.stock.source")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {[...lotDetail.inbound, ...lotDetail.outbound].map((m) => (
                      <TableRow key={m.sleId}>
                        <TableCell>{formatDate(m.postingDate)}</TableCell>
                        <TableCell>{t(`inventory.entryTypes.${m.entryType}`, { defaultValue: m.entryType })}</TableCell>
                        <TableCell>{m.warehouseCode}</TableCell>
                        <TableNumberCell><Qty value={m.quantity} /></TableNumberCell>
                        <TableCell>{m.sourceDocumentType}{m.serialNumber ? ` · ${m.serialNumber}` : ""}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>
              {lotDetail.shippedTo.length > 0 ? (
                <section>
                  <h3 className="mb-1 text-sm font-semibold">{t("inventory.tracking.shippedTo")}</h3>
                  <KeyValues entries={lotDetail.shippedTo.map((p) => [p.partnerId, `${String(p.quantity)} · ${formatDate(p.lastShippedOn)}`])} />
                </section>
              ) : null}
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label={t("inventory.tracking.newStatus")}>
                  <SelectField value={nextStatus} onChange={(e) => { setNextStatus(e.target.value); }} data-testid="lot-status">
                    <option value="">—</option>
                    {lotStatuses.map((s) => (
                      <option key={s} value={s}>
                        {t(`inventory.statuses.${s}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("common.reason")}>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} data-testid="lot-reason" />
                </Field>
                <div className="self-end">
                  <Button onClick={() => { changeLot.mutate(); }} disabled={!nextStatus} loading={changeLot.isPending} data-testid="lot-apply">
                    {t("inventory.tracking.apply")}
                  </Button>
                </div>
              </div>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openSerial)} onOpenChange={(isOpen) => { if (!isOpen) { go({}); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {serialDetail ? `${serialDetail.serial.itemCode} · ${serialDetail.serial.serialNumber}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {serialDetail ? (
            <div className="flex flex-col gap-4" data-testid="serial-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={serialDetail.serial.status} />
                {serialDetail.serial.currentWarehouseCode ? <span className="text-fg-muted">{serialDetail.serial.currentWarehouseCode}</span> : null}
                {serialDetail.serial.lotNumber ? <span className="text-fg-muted">{t("inventory.stock.lot")}: {serialDetail.serial.lotNumber}</span> : null}
              </div>
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
                  {serialDetail.events.map((e) => (
                    <TableRow key={e.id}>
                      <TableCell>{formatDateTime(e.at)}</TableCell>
                      <TableCell>{e.entryType ? t(`inventory.entryTypes.${e.entryType}`, { defaultValue: e.entryType }) : t(`inventory.tracking.kinds.${e.kind}`, { defaultValue: e.kind })}</TableCell>
                      <TableCell>{t(`inventory.statuses.${e.toStatus}`, { defaultValue: e.toStatus })}</TableCell>
                      <TableCell>{e.warehouseCode ?? ""}</TableCell>
                      <TableCell>{e.sourceDocumentType ?? e.note ?? ""}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label={t("inventory.tracking.newStatus")}>
                  <SelectField value={nextStatus} onChange={(e) => { setNextStatus(e.target.value); }}>
                    <option value="">—</option>
                    {serialStatuses.map((s) => (
                      <option key={s} value={s}>
                        {t(`inventory.statuses.${s}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("inventory.tracking.note")}>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} />
                </Field>
                <div className="self-end">
                  <Button onClick={() => { changeSerial.mutate(); }} disabled={!nextStatus} loading={changeSerial.isPending}>
                    {t("inventory.tracking.apply")}
                  </Button>
                </div>
              </div>
              <DialogFooter />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
