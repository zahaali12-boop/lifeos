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
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useReasonCodes, useWarehouses } from "./shared";

type Adjustment = components["schemas"]["AdjustmentSummary"];

interface LineForm {
  itemCode: string;
  binId: string;
  quantity: string;
  uom: string;
  unitCost: string;
  reasonCode: string;
  note: string;
  lotNumber: string;
  expiresOn: string;
  serialNumbers: string;
}

interface AdjustmentForm {
  warehouseId: string;
  kind: string;
  postingDate: string;
  reference: string;
  notes: string;
  lines: LineForm[];
}

const kinds = ["positive", "negative", "scrap", "opening"];
const statuses = ["", "draft", "pending_approval", "approved", "posted", "rejected", "cancelled"];
const emptyLine: LineForm = { itemCode: "", binId: "", quantity: "", uom: "", unitCost: "", reasonCode: "", note: "", lotNumber: "", expiresOn: "", serialNumbers: "" };

function toForm(a: Adjustment): AdjustmentForm {
  return {
    warehouseId: a.warehouseId,
    kind: a.kind,
    postingDate: a.postingDate,
    reference: a.reference ?? "",
    notes: a.notes ?? "",
    lines: a.lines.map((l) => ({ itemCode: l.itemCode, binId: l.binId ?? "", quantity: String(l.quantity), uom: l.uomCode, unitCost: l.unitCost === null ? "" : String(l.unitCost), reasonCode: l.reasonCode, note: l.note ?? "", lotNumber: l.lotNumber ?? "", expiresOn: l.expiresOn ?? "", serialNumbers: l.serialNumbers.join(", ") })),
  };
}

function toRequest(companyId: string, f: AdjustmentForm): components["schemas"]["SaveAdjustmentRequest"] {
  return {
    companyId,
    warehouseId: f.warehouseId,
    kind: f.kind,
    postingDate: f.postingDate || null,
    reference: f.reference || null,
    notes: f.notes || null,
    lines: f.lines
      .filter((l) => l.itemCode.trim())
      .map((l) => ({
        itemCode: l.itemCode.trim(),
        binId: l.binId || null,
        quantity: l.quantity || "0",
        uom: l.uom || null,
        unitCost: l.unitCost || null,
        reasonCode: l.reasonCode || null,
        note: l.note || null,
        lotNumber: l.lotNumber || null,
        expiresOn: l.expiresOn || null,
        serialNumbers: l.serialNumbers.trim() ? l.serialNumbers.split(/[\s,;]+/).filter(Boolean) : null,
      })),
  };
}

/** Adjustments (roadmap 3.4): positive, negative, scrap and opening documents with reason codes; approval when the company asks for it; posting through the engines. */
export function AdjustmentsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const reasons = useReasonCodes();
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<{ id: string | null; form: AdjustmentForm } | null>(null);
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [reasonsOpen, setReasonsOpen] = useState(false);
  const openId = search.open;
  const formWarehouse = warehouses.data?.find((w) => w.id === editing?.form.warehouseId);
  const bins = useQuery({
    queryKey: ["bins", formWarehouse?.id ?? ""],
    enabled: formWarehouse?.binsEnabled === true,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: formWarehouse?.id ?? "" } } })),
  });

  const adjustments = useQuery({
    queryKey: ["adjustments", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/adjustments", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const adjustment = useQuery({
    queryKey: ["adjustment", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/adjustments/{adjustmentId}", { params: { path: { adjustmentId: openId ?? "" } } })),
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["adjustments"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["adjustment", id] });
    }
  };
  const open = (id: string | null): void => { void navigate({ to: "/inventory/adjustments", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: AdjustmentForm }) =>
      input.id
        ? unwrap(await api.PUT("/api/v1/inventory/adjustments/{adjustmentId}", { params: { path: { adjustmentId: input.id } }, body: toRequest(companyId, input.form) }))
        : unwrap(await api.POST("/api/v1/inventory/adjustments", { body: toRequest(companyId, input.form) })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await refresh(saved.id);
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const act = useMutation({
    mutationFn: async (action: "submit" | "approve" | "reject" | "cancel") => {
      const adjustmentId = openId ?? "";
      switch (action) {
        case "submit":
          return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/submit", { params: { path: { adjustmentId } } }));
        case "approve":
          return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/approve", { params: { path: { adjustmentId } } }));
        case "reject":
          return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/reject", { params: { path: { adjustmentId } }, body: { reason } }));
        case "cancel":
          return unwrap(await api.POST("/api/v1/inventory/adjustments/{adjustmentId}/cancel", { params: { path: { adjustmentId } } }));
      }
    },
    onSuccess: async () => {
      setProblem(null);
      setReason("");
      await refresh(openId);
      await queryClient.invalidateQueries({ queryKey: ["stock"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Adjustment, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "kind", accessorKey: "kind", header: t("inventory.adjustments.kind"), size: 110, cell: ({ row }) => t(`inventory.adjustments.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
      { id: "lines", accessorFn: (row) => row.lines.length, header: t("inventory.lines"), size: 80 },
      { id: "reference", accessorKey: "reference", header: t("accounting.reference"), size: 160 },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const detail = adjustment.data;
  const editable = detail?.status === "draft" || detail?.status === "rejected";
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const setForm = (patch: Partial<AdjustmentForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const updateLine = (index: number, patch: Partial<LineForm>): void => {
    if (!editing) {
      return;
    }
    setForm({ lines: editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
  };
  const newForm = (): AdjustmentForm => ({ warehouseId: warehouses.data?.[0]?.id ?? "", kind: "positive", postingDate: today(), reference: "", notes: "", lines: [{ ...emptyLine }] });

  return (
    <>
      <PageHeader
        title={t("nav.adjustments")}
        description={t("inventory.adjustments.description")}
        actions={
          <>
            <Button variant="secondary" onClick={() => { setReasonsOpen(true); }} data-testid="reason-codes">
              {t("inventory.reasons.title")}
            </Button>
            <Button onClick={() => { setProblem(null); setEditing({ id: null, form: newForm() }); }} disabled={!companyId} data-testid="new-adjustment">
              <Plus aria-hidden="true" />
              {t("inventory.adjustments.new")}
            </Button>
          </>
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
      <DataGrid<Adjustment> label="nav.adjustments" columns={columns} data={adjustments.data ?? []} rowKey={(row) => row.id} loading={adjustments.isPending && Boolean(companyId)} emptyTitle={t("inventory.adjustments.emptyTitle")} emptyDescription={t("inventory.adjustments.emptyDescription")} onOpen={(row) => { open(row.id); }} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="adjustment-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>{t(`inventory.adjustments.kinds.${detail.kind}`, { defaultValue: detail.kind })}</span>
                <span className="text-fg-muted">{detail.warehouseCode}</span>
                {detail.reference ? <span className="text-fg-muted">{detail.reference}</span> : null}
                {detail.rejectionReason ? <span className="text-danger">{t("accounting.rejectedBecause", { reason: detail.rejectionReason })}</span> : null}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.item")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead className="text-end">{t("inventory.unitCost")}</TableHead>
                    <TableHead className="text-end">{t("inventory.costAmount")}</TableHead>
                    <TableHead>{t("inventory.reason")}</TableHead>
                    <TableHead>{t("inventory.stock.lot")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {detail.lines.map((line) => (
                    <TableRow key={line.id}>
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell>
                        <span dir="ltr">{line.itemCode}</span> {localized(line.itemName)}
                      </TableCell>
                      <TableNumberCell><Qty value={line.quantity} uom={line.uomCode} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.unitCost} /></TableNumberCell>
                      <TableNumberCell><Amount value={line.costAmount} /></TableNumberCell>
                      <TableCell>{line.reasonCode}{line.note ? ` · ${line.note}` : ""}</TableCell>
                      <TableCell>{line.lotNumber ?? ""}{line.serialNumbers.length > 0 ? ` ${line.serialNumbers.join(", ")}` : ""}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              {detail.status === "pending_approval" ? (
                <Field label={t("common.reason")}>
                  <TextField value={reason} onChange={(e) => { setReason(e.target.value); }} />
                </Field>
              ) : null}
              <DialogFooter>
                {editable ? (
                  <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }}>
                    {t("accounting.edit")}
                  </Button>
                ) : null}
                {editable ? (
                  <Button onClick={() => { act.mutate("submit"); }} loading={act.isPending} data-testid="submit-adjustment">
                    {t("inventory.adjustments.submit")}
                  </Button>
                ) : null}
                {detail.status === "pending_approval" ? (
                  <>
                    <Button variant="secondary" onClick={() => { act.mutate("reject"); }} loading={act.isPending}>
                      {t("accounting.reject")}
                    </Button>
                    <Button onClick={() => { act.mutate("approve"); }} loading={act.isPending} data-testid="approve-adjustment">
                      {t("accounting.approve")}
                    </Button>
                  </>
                ) : null}
                {detail.status !== "posted" && detail.status !== "cancelled" ? (
                  <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                    {t("common.cancel")}
                  </Button>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.id ? t("inventory.adjustments.edit") : t("inventory.adjustments.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <WarehouseSelect warehouses={warehouses.data ?? []} value={editing.form.warehouseId} onChange={(id) => { setForm({ warehouseId: id }); }} required testId="adjustment-warehouse" />
                <Field label={t("inventory.adjustments.kind")}>
                  <SelectField value={editing.form.kind} onChange={(e) => { setForm({ kind: e.target.value }); }} data-testid="adjustment-kind">
                    {kinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`inventory.adjustments.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.postingDate")} required error={problem?.fields.postingDate}>
                  <TextField type="date" value={editing.form.postingDate} onChange={(e) => { setForm({ postingDate: e.target.value }); }} required dir="ltr" />
                </Field>
                <Field label={t("accounting.reference")}>
                  <TextField value={editing.form.reference} onChange={(e) => { setForm({ reference: e.target.value }); }} />
                </Field>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("inventory.itemCode")}</TableHead>
                    {formWarehouse?.binsEnabled ? <TableHead>{t("inventory.warehouses.bin")}</TableHead> : null}
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead>{t("inventory.uom")}</TableHead>
                    <TableHead className="text-end">{t("inventory.unitCost")}</TableHead>
                    <TableHead>{t("inventory.reason")}</TableHead>
                    <TableHead>{t("inventory.stock.lot")}</TableHead>
                    <TableHead>{t("inventory.expiresOn")}</TableHead>
                    <TableHead>{t("inventory.serials")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.form.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <TextField aria-label={t("inventory.itemCode")} value={line.itemCode} onChange={(e) => { updateLine(index, { itemCode: e.target.value.toUpperCase() }); }} dir="ltr" data-testid={`line-item-${index}`} />
                      </TableCell>
                      {formWarehouse?.binsEnabled ? (
                        <TableCell>
                          <SelectField aria-label={t("inventory.warehouses.bin")} value={line.binId} onChange={(e) => { updateLine(index, { binId: e.target.value }); }} data-testid={`line-bin-${index}`}>
                            <option value="">—</option>
                            {(bins.data ?? []).map((b) => (
                              <option key={b.id} value={b.id}>
                                {b.code}
                              </option>
                            ))}
                          </SelectField>
                        </TableCell>
                      ) : null}
                      <TableNumberCell>
                        <TextField aria-label={t("inventory.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { updateLine(index, { quantity: e.target.value }); }} dir="ltr" className="text-end" data-testid={`line-qty-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.uom")} value={line.uom} onChange={(e) => { updateLine(index, { uom: e.target.value.toUpperCase() }); }} dir="ltr" className="w-20" />
                      </TableCell>
                      <TableNumberCell>
                        <TextField aria-label={t("inventory.unitCost")} inputMode="decimal" value={line.unitCost} onChange={(e) => { updateLine(index, { unitCost: e.target.value }); }} dir="ltr" className="text-end" data-testid={`line-cost-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <SelectField aria-label={t("inventory.reason")} value={line.reasonCode} onChange={(e) => { updateLine(index, { reasonCode: e.target.value }); }} data-testid={`line-reason-${index}`}>
                          <option value="">—</option>
                          {(reasons.data ?? []).map((r) => (
                            <option key={r.id} value={r.code}>
                              {r.code} · {localized(r.name)}
                            </option>
                          ))}
                        </SelectField>
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.stock.lot")} value={line.lotNumber} onChange={(e) => { updateLine(index, { lotNumber: e.target.value }); }} dir="ltr" className="w-28" />
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.expiresOn")} type="date" value={line.expiresOn} onChange={(e) => { updateLine(index, { expiresOn: e.target.value }); }} dir="ltr" />
                      </TableCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.serials")} value={line.serialNumbers} onChange={(e) => { updateLine(index, { serialNumbers: e.target.value }); }} dir="ltr" className="w-32" />
                      </TableCell>
                      <TableCell>
                        <Button type="button" variant="ghost" size="icon" aria-label={t("accounting.removeLine")} onClick={() => { setForm({ lines: editing.form.lines.filter((_, i) => i !== index) }); }}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div>
                <Button type="button" variant="secondary" onClick={() => { setForm({ lines: [...editing.form.lines, { ...emptyLine }] }); }} data-testid="add-line">
                  <Plus aria-hidden="true" />
                  {t("accounting.addLine")}
                </Button>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-adjustment">
                  {t("common.save")}
                </Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <ReasonCodesDialog open={reasonsOpen} onOpenChange={setReasonsOpen} />
    </>
  );
}

const appliesTo = ["adjustment", "count", "return", "scrap", "write_off", "shortage"];

/** Reason codes: why stock was adjusted, scrapped, short or counted differently; a reason may override the account the movement offsets. */
function ReasonCodesDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const reasons = useReasonCodes();
  const [form, setForm] = useState({ code: "", en: "", ar: "", appliesTo: "adjustment", requiresNote: false });
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const add = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/reason-codes", { body: { code: form.code, name: { en: form.en, ...(form.ar ? { ar: form.ar } : {}) }, appliesTo: form.appliesTo, requiresNote: form.requiresNote, isActive: true } })),
    onSuccess: async () => { setForm({ code: "", en: "", ar: "", appliesTo: "adjustment", requiresNote: false }); setProblem(null); await queryClient.invalidateQueries({ queryKey: ["reason-codes"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
        <DialogHeader>
          <DialogTitle className="text-lg font-semibold">{t("inventory.reasons.title")}</DialogTitle>
        </DialogHeader>
        <ul className="max-h-48 overflow-y-auto text-sm">
          {(reasons.data ?? []).map((r) => (
            <li key={r.id} data-testid="reason-row">
              <span dir="ltr">{r.code}</span> · {localized(r.name)} <span className="text-fg-subtle">({t(`inventory.reasons.appliesTo.${r.appliesTo}`, { defaultValue: r.appliesTo })})</span>
            </li>
          ))}
        </ul>
        <FormError message={problem?.message ?? null} />
        <form className="grid gap-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); add.mutate(); }}>
          <Field label={t("inventory.items.code")} required>
            <TextField value={form.code} onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="reason-code" />
          </Field>
          <Field label={t("inventory.reasons.applies")}>
            <SelectField value={form.appliesTo} onChange={(e) => { setForm({ ...form, appliesTo: e.target.value }); }}>
              {appliesTo.map((k) => (
                <option key={k} value={k}>
                  {t(`inventory.reasons.appliesTo.${k}`)}
                </option>
              ))}
            </SelectField>
          </Field>
          <Field label={t("inventory.items.nameEn")} required>
            <TextField value={form.en} onChange={(e) => { setForm({ ...form, en: e.target.value }); }} required data-testid="reason-name-en" />
          </Field>
          <Field label={t("inventory.items.nameAr")}>
            <TextField value={form.ar} onChange={(e) => { setForm({ ...form, ar: e.target.value }); }} dir="rtl" />
          </Field>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={form.requiresNote} onChange={(e) => { setForm({ ...form, requiresNote: e.target.checked }); }} />
            {t("inventory.reasons.requiresNote")}
          </label>
          <div className="text-end">
            <Button type="submit" variant="secondary" loading={add.isPending} data-testid="save-reason">
              {t("inventory.reasons.add")}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
