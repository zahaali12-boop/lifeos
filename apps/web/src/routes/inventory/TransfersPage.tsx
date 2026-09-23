import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Trash2 } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";
import { ItemCodeField } from "./ItemCodeField";
import { TransferQuantities } from "./TransferQuantities";

type Transfer = components["schemas"]["TransferSummary"];

interface LineForm {
  itemCode: string;
  quantity: string;
  uom: string;
  fromBinId: string;
  toBinId: string;
  lotNumber: string;
  serialNumbers: string;
}

interface TransferForm {
  fromWarehouseId: string;
  toWarehouseId: string;
  transitWarehouseId: string;
  kind: string;
  reference: string;
  lines: LineForm[];
}

const statuses = ["", "draft", "shipped", "partially_received", "received", "cancelled"];
const emptyLine: LineForm = { itemCode: "", quantity: "", uom: "", fromBinId: "", toBinId: "", lotNumber: "", serialNumbers: "" };

function toRequest(companyId: string, f: TransferForm): components["schemas"]["SaveTransferRequest"] {
  return {
    companyId,
    fromWarehouseId: f.fromWarehouseId,
    toWarehouseId: f.toWarehouseId,
    transitWarehouseId: f.kind === "two_step" && f.transitWarehouseId ? f.transitWarehouseId : null,
    kind: f.kind,
    reference: f.reference || null,
    lines: f.lines
      .filter((l) => l.itemCode.trim())
      .map((l) => ({ itemCode: l.itemCode.trim(), quantity: l.quantity || "0", uom: l.uom || null, fromBinId: l.fromBinId || null, toBinId: l.toBinId || null, lotNumber: l.lotNumber || null, serialNumbers: l.serialNumbers.trim() ? l.serialNumbers.split(/[\s,;]+/).filter(Boolean) : null })),
  };
}

/** Transfers (roadmap 3.4): one-step moves and two-step moves through an in-transit warehouse, shipped and received with shortages accounted for. */
export function TransfersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<TransferForm | null>(null);
  const [date, setDate] = useState(today());
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [quantities, setQuantities] = useState<"ship" | "receive" | null>(null);
  const openId = search.open;
  const fromWarehouse = warehouses.data?.find((w) => w.id === editing?.fromWarehouseId);
  const toWarehouse = warehouses.data?.find((w) => w.id === editing?.toWarehouseId);
  const fromBins = useQuery({
    queryKey: ["bins", fromWarehouse?.id ?? ""],
    enabled: fromWarehouse?.binsEnabled === true,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: fromWarehouse?.id ?? "" } } })),
  });
  const toBins = useQuery({
    queryKey: ["bins", toWarehouse?.id ?? ""],
    enabled: toWarehouse?.binsEnabled === true,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: toWarehouse?.id ?? "" } } })),
  });

  const transfers = useQuery({
    queryKey: ["transfers", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/transfers", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const transfer = useQuery({
    queryKey: ["transfer", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/transfers/{transferId}", { params: { path: { transferId: openId ?? "" } } })),
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["transfers"] });
    await queryClient.invalidateQueries({ queryKey: ["stock"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["transfer", id] });
    }
  };
  const open = (id: string | null): void => { setQuantities(null); void navigate({ to: "/inventory/transfers", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (form: TransferForm) => unwrap(await api.POST("/api/v1/inventory/transfers", { body: toRequest(companyId, form) })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await refresh(saved.id);
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const act = useMutation({
    mutationFn: async (action: "ship" | "receive" | "cancel") => {
      const transferId = openId ?? "";
      switch (action) {
        case "ship":
          return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/ship", { params: { path: { transferId } }, body: { shipDate: date || null } }));
        case "receive":
          return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/receive", { params: { path: { transferId } }, body: { receiveDate: date || null } }));
        case "cancel":
          return unwrap(await api.POST("/api/v1/inventory/transfers/{transferId}/cancel", { params: { path: { transferId } } }));
      }
    },
    onSuccess: async () => {
      setProblem(null);
      await refresh(openId);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Transfer, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "from", accessorKey: "fromWarehouseCode", header: t("inventory.transfers.from"), size: 120 },
      { id: "to", accessorKey: "toWarehouseCode", header: t("inventory.transfers.to"), size: 120 },
      { id: "kind", accessorKey: "kind", header: t("inventory.transfers.kind"), size: 110, cell: ({ row }) => t(`inventory.transfers.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
      { id: "lines", accessorFn: (row) => row.lines.length, header: t("inventory.lines"), size: 80 },
      { id: "shipDate", accessorKey: "shipDate", header: t("inventory.transfers.shipped"), size: 110, cell: ({ row }) => formatDate(row.original.shipDate) },
      { id: "receiveDate", accessorKey: "receiveDate", header: t("inventory.transfers.received"), size: 110, cell: ({ row }) => formatDate(row.original.receiveDate) },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 150, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const detail = transfer.data;
  const setForm = (patch: Partial<TransferForm>): void => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
  const updateLine = (index: number, patch: Partial<LineForm>): void => {
    if (editing) {
      setForm({ lines: editing.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
    }
  };
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const transits = (warehouses.data ?? []).filter((w) => w.kind === "in_transit");
  const stockWarehouses = (warehouses.data ?? []).filter((w) => w.kind !== "in_transit");

  return (
    <>
      <PageHeader
        title={t("nav.transfers")}
        description={t("inventory.transfers.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ fromWarehouseId: stockWarehouses[0]?.id ?? "", toWarehouseId: stockWarehouses[1]?.id ?? "", transitWarehouseId: transits[0]?.id ?? "", kind: transits.length > 0 ? "two_step" : "one_step", reference: "", lines: [{ ...emptyLine }] }); }} disabled={!companyId} data-testid="new-transfer">
            <Plus aria-hidden="true" />
            {t("inventory.transfers.new")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s ? t(`inventory.statuses.${s}`) : t("accounting.anyStatus")}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Transfer> label="nav.transfers" columns={columns} data={transfers.data ?? []} rowKey={(row) => row.id} loading={transfers.isPending && Boolean(companyId)} emptyTitle={t("inventory.transfers.emptyTitle")} emptyDescription={t("inventory.transfers.emptyDescription")} onOpen={(row) => { open(row.id); }} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${detail.fromWarehouseCode} → ${detail.toWarehouseCode}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="transfer-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>{t(`inventory.transfers.kinds.${detail.kind}`, { defaultValue: detail.kind })}</span>
                {detail.transitWarehouseCode ? <span className="text-fg-muted">{t("inventory.transfers.via", { warehouse: detail.transitWarehouseCode })}</span> : null}
                {detail.shipDate ? <span className="text-fg-muted">{t("inventory.transfers.shipped")}: {formatDate(detail.shipDate)}</span> : null}
                {detail.receiveDate ? <span className="text-fg-muted">{t("inventory.transfers.received")}: {formatDate(detail.receiveDate)}</span> : null}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.item")}</TableHead>
                    <TableHead className="text-end">{t("inventory.transfers.requested")}</TableHead>
                    <TableHead className="text-end">{t("inventory.transfers.shipped")}</TableHead>
                    <TableHead className="text-end">{t("inventory.transfers.received")}</TableHead>
                    <TableHead className="text-end">{t("inventory.transfers.shortage")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {detail.lines.map((line) => (
                    <TableRow key={line.id}>
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell>
                        <span dir="ltr">{line.itemCode}</span> {localized(line.itemName)}
                        {line.lotNumber ? <span className="text-fg-muted"> · {line.lotNumber}</span> : null}
                      </TableCell>
                      <TableNumberCell><Qty value={line.qtyRequested} uom={line.uomCode} /></TableNumberCell>
                      <TableNumberCell><Qty value={line.qtyShipped} /></TableNumberCell>
                      <TableNumberCell><Qty value={line.qtyReceived} /></TableNumberCell>
                      <TableNumberCell><Qty value={line.qtyShortage} /></TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              {detail.status === "draft" || detail.status === "shipped" || detail.status === "partially_received" ? (
                <Field label={t("accounting.date")}>
                  <TextField type="date" value={date} onChange={(e) => { setDate(e.target.value); }} dir="ltr" data-testid="transfer-date" />
                </Field>
              ) : null}
              {quantities ? (
                <TransferQuantities
                  transfer={detail}
                  mode={quantities}
                  date={date}
                  toWarehouseBins={warehouses.data?.find((w) => w.id === detail.toWarehouseId)?.binsEnabled === true}
                  onDone={async () => { setQuantities(null); await refresh(openId); }}
                  onCancel={() => { setQuantities(null); }}
                />
              ) : (
                <DialogFooter>
                  {detail.status === "draft" ? (
                    <>
                      <Button variant="secondary" onClick={() => { setProblem(null); setQuantities("ship"); }} data-testid="ship-partial">
                        {t("inventory.transfers.shipPart")}
                      </Button>
                      <Button onClick={() => { act.mutate("ship"); }} loading={act.isPending} data-testid="ship-transfer">
                        {t("inventory.transfers.ship")}
                      </Button>
                    </>
                  ) : null}
                  {detail.status === "shipped" || detail.status === "partially_received" ? (
                    <>
                      <Button variant="secondary" onClick={() => { setProblem(null); setQuantities("receive"); }} data-testid="receive-partial">
                        {t("inventory.transfers.receiveDifferences")}
                      </Button>
                      <Button onClick={() => { act.mutate("receive"); }} loading={act.isPending} data-testid="receive-transfer">
                        {t("inventory.transfers.receive")}
                      </Button>
                    </>
                  ) : null}
                  {detail.status !== "received" && detail.status !== "cancelled" ? (
                    <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                      {t("common.cancel")}
                    </Button>
                  ) : null}
                </DialogFooter>
              )}
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("inventory.transfers.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <WarehouseSelect warehouses={stockWarehouses} value={editing.fromWarehouseId} onChange={(id) => { setForm({ fromWarehouseId: id }); }} label={t("inventory.transfers.from")} required testId="transfer-from" />
                <WarehouseSelect warehouses={stockWarehouses} value={editing.toWarehouseId} onChange={(id) => { setForm({ toWarehouseId: id }); }} label={t("inventory.transfers.to")} required testId="transfer-to" />
                <Field label={t("inventory.transfers.kind")}>
                  <SelectField value={editing.kind} onChange={(e) => { setForm({ kind: e.target.value }); }} data-testid="transfer-kind">
                    <option value="one_step">{t("inventory.transfers.kinds.one_step")}</option>
                    <option value="two_step">{t("inventory.transfers.kinds.two_step")}</option>
                  </SelectField>
                </Field>
                {editing.kind === "two_step" ? <WarehouseSelect warehouses={transits} value={editing.transitWarehouseId} onChange={(id) => { setForm({ transitWarehouseId: id }); }} label={t("inventory.transfers.transit")} testId="transfer-transit" /> : (
                  <Field label={t("accounting.reference")}>
                    <TextField value={editing.reference} onChange={(e) => { setForm({ reference: e.target.value }); }} />
                  </Field>
                )}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("inventory.itemCode")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead>{t("inventory.uom")}</TableHead>
                    {fromWarehouse?.binsEnabled ? <TableHead>{t("inventory.transfers.fromBin")}</TableHead> : null}
                    {toWarehouse?.binsEnabled ? <TableHead>{t("inventory.transfers.toBin")}</TableHead> : null}
                    <TableHead>{t("inventory.stock.lot")}</TableHead>
                    <TableHead>{t("inventory.serials")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <ItemCodeField aria-label={t("inventory.itemCode")} value={line.itemCode} onChange={(code) => { updateLine(index, { itemCode: code }); }} data-testid={`line-item-${index}`} />
                      </TableCell>
                      <TableNumberCell>
                        <TextField aria-label={t("inventory.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { updateLine(index, { quantity: e.target.value }); }} dir="ltr" className="text-end" data-testid={`line-qty-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.uom")} value={line.uom} onChange={(e) => { updateLine(index, { uom: e.target.value.toUpperCase() }); }} dir="ltr" className="w-20" />
                      </TableCell>
                      {fromWarehouse?.binsEnabled ? (
                        <TableCell>
                          <SelectField aria-label={t("inventory.transfers.fromBin")} value={line.fromBinId} onChange={(e) => { updateLine(index, { fromBinId: e.target.value }); }} data-testid={`line-from-bin-${index}`}>
                            <option value="">—</option>
                            {(fromBins.data ?? []).map((b) => (
                              <option key={b.id} value={b.id}>
                                {b.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                      ) : null}
                      {toWarehouse?.binsEnabled ? (
                        <TableCell>
                          <SelectField aria-label={t("inventory.transfers.toBin")} value={line.toBinId} onChange={(e) => { updateLine(index, { toBinId: e.target.value }); }} data-testid={`line-to-bin-${index}`}>
                            <option value="">—</option>
                            {(toBins.data ?? []).map((b) => (
                              <option key={b.id} value={b.id}>
                                {b.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                      ) : null}
                      <TableCell>
                        <TextField aria-label={t("inventory.stock.lot")} value={line.lotNumber} onChange={(e) => { updateLine(index, { lotNumber: e.target.value }); }} dir="ltr" />
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.serials")} value={line.serialNumbers} onChange={(e) => { updateLine(index, { serialNumbers: e.target.value }); }} dir="ltr" />
                      </TableCell>
                      <TableCell>
                        <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setForm({ lines: editing.lines.filter((_, i) => i !== index) }); }}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div>
                <Button type="button" variant="secondary" onClick={() => { setForm({ lines: [...editing.lines, { ...emptyLine }] }); }} data-testid="add-line">
                  <Plus aria-hidden="true" />
                  {t("accounting.addLine")}
                </Button>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-transfer">
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
