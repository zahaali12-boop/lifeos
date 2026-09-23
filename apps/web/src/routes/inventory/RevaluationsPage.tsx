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
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";

type Revaluation = components["schemas"]["RevaluationSummary"];

interface LineForm {
  itemCode: string;
  warehouseId: string;
  newUnitCost: string;
  note: string;
}

interface RevaluationForm {
  kind: string;
  postingDate: string;
  reference: string;
  notes: string;
  lines: LineForm[];
}

const kinds = ["nrv_writedown", "manual"];
const statuses = ["", "draft", "posted", "cancelled"];
const emptyLine: LineForm = { itemCode: "", warehouseId: "", newUnitCost: "", note: "" };

function toForm(r: Revaluation): RevaluationForm {
  return {
    kind: r.kind,
    postingDate: r.postingDate,
    reference: r.reference ?? "",
    notes: r.notes ?? "",
    lines: r.lines.map((l) => ({ itemCode: l.itemCode, warehouseId: l.warehouseId ?? "", newUnitCost: String(l.newUnitCost), note: l.note ?? "" })),
  };
}

function toRequest(companyId: string, f: RevaluationForm): components["schemas"]["SaveRevaluationRequest"] {
  return {
    companyId,
    kind: f.kind,
    postingDate: f.postingDate || null,
    reference: f.reference || null,
    notes: f.notes || null,
    lines: f.lines.filter((l) => l.itemCode.trim()).map((l) => ({ itemCode: l.itemCode.trim(), warehouseId: l.warehouseId || null, newUnitCost: l.newUnitCost || "0", note: l.note || null })),
  };
}

/** Revaluations (roadmap 3.4, IAS 2): an NRV write-down (never above cost) or a manual revaluation of the stock on hand per item at a date, posted through the costing engine. */
export function RevaluationsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<{ id: string | null; form: RevaluationForm } | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;

  const revaluations = useQuery({
    queryKey: ["revaluations", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/revaluations", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const revaluation = useQuery({
    queryKey: ["revaluation", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/revaluations/{revaluationId}", { params: { path: { revaluationId: openId ?? "" } } })),
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["revaluations"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["revaluation", id] });
    }
  };
  const open = (id: string | null): void => { void navigate({ to: "/inventory/revaluations", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (input: { id: string | null; form: RevaluationForm }) =>
      input.id
        ? unwrap(await api.PUT("/api/v1/inventory/revaluations/{revaluationId}", { params: { path: { revaluationId: input.id } }, body: toRequest(companyId, input.form) }))
        : unwrap(await api.POST("/api/v1/inventory/revaluations", { body: toRequest(companyId, input.form) })),
    onSuccess: async (saved) => {
      setEditing(null);
      setProblem(null);
      await refresh(saved.id);
      open(saved.id);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const act = useMutation({
    mutationFn: async (action: "post" | "cancel") => {
      const revaluationId = openId ?? "";
      return action === "post"
        ? unwrap(await api.POST("/api/v1/inventory/revaluations/{revaluationId}/post", { params: { path: { revaluationId } } }))
        : unwrap(await api.POST("/api/v1/inventory/revaluations/{revaluationId}/cancel", { params: { path: { revaluationId } } }));
    },
    onSuccess: async () => {
      setProblem(null);
      await refresh(openId);
      await queryClient.invalidateQueries({ queryKey: ["valuation"] });
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Revaluation, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "postingDate", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "kind", accessorKey: "kind", header: t("inventory.revaluations.kind"), size: 170, cell: ({ row }) => t(`inventory.revaluations.kinds.${row.original.kind}`, { defaultValue: row.original.kind }) },
      { id: "lines", accessorFn: (row) => row.lines.length, header: t("inventory.lines"), size: 80 },
      { id: "total", accessorKey: "totalAmount", header: t("inventory.revaluations.total"), size: 140, cell: ({ row }) => (row.original.status === "posted" ? <Amount value={row.original.totalAmount} /> : "—") },
      { id: "reference", accessorKey: "reference", header: t("accounting.reference"), size: 160 },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const detail = revaluation.data;
  const warehouseCode = (id: string | null | undefined) => (id ? warehouses.data?.find((w) => w.id === id)?.code ?? "" : t("inventory.allWarehouses"));
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };
  const setForm = (patch: Partial<RevaluationForm>): void => { setEditing((prev) => (prev ? { ...prev, form: { ...prev.form, ...patch } } : prev)); };
  const updateLine = (index: number, patch: Partial<LineForm>): void => {
    if (!editing) {
      return;
    }
    setForm({ lines: editing.form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)) });
  };

  return (
    <>
      <PageHeader
        title={t("nav.revaluations")}
        description={t("inventory.revaluations.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ id: null, form: { kind: "nrv_writedown", postingDate: today(), reference: "", notes: "", lines: [{ ...emptyLine }] } }); }} disabled={!companyId} data-testid="new-revaluation">
            <Plus aria-hidden="true" />
            {t("inventory.revaluations.new")}
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
      <DataGrid<Revaluation> label="nav.revaluations" columns={columns} data={revaluations.data ?? []} rowKey={(row) => row.id} loading={revaluations.isPending && Boolean(companyId)} emptyTitle={t("inventory.revaluations.emptyTitle")} emptyDescription={t("inventory.revaluations.emptyDescription")} onOpen={(row) => { open(row.id); }} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${formatDate(detail.postingDate)}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="revaluation-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>{t(`inventory.revaluations.kinds.${detail.kind}`, { defaultValue: detail.kind })}</span>
                {detail.reference ? <span className="text-fg-muted">{detail.reference}</span> : null}
                <span className="ms-auto">
                  {t("inventory.revaluations.total")}: <strong data-testid="revaluation-total">{detail.status === "posted" ? <Amount value={detail.totalAmount} /> : t("inventory.revaluations.atPosting")}</strong>
                </span>
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.item")}</TableHead>
                    <TableHead>{t("inventory.warehouse")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead className="text-end">{t("inventory.revaluations.currentUnitCost")}</TableHead>
                    <TableHead className="text-end">{t("inventory.revaluations.newUnitCost")}</TableHead>
                    <TableHead className="text-end">{t("inventory.revaluations.amount")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {detail.lines.map((line) => (
                    <TableRow key={line.id}>
                      <TableCell>{String(line.lineNo)}</TableCell>
                      <TableCell>
                        <span dir="ltr">{line.itemCode}</span> {localized(line.itemName)}
                        {line.note ? <span className="text-fg-muted"> · {line.note}</span> : null}
                      </TableCell>
                      <TableCell>{warehouseCode(line.warehouseId)}</TableCell>
                      <TableNumberCell>{detail.status === "posted" ? <Qty value={line.quantity} /> : "—"}</TableNumberCell>
                      <TableNumberCell>{detail.status === "posted" ? <Amount value={line.currentUnitCost} minorUnits={4} /> : "—"}</TableNumberCell>
                      <TableNumberCell><Amount value={line.newUnitCost} minorUnits={4} /></TableNumberCell>
                      <TableNumberCell>{detail.status === "posted" ? <Amount value={line.amount} /> : "—"}</TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <p className="text-sm text-fg-muted">{t("inventory.revaluations.postHint")}</p>
              <FormError message={problem?.message ?? null} />
              <DialogFooter>
                {detail.status === "draft" ? (
                  <>
                    <Button variant="secondary" onClick={() => { setProblem(null); setEditing({ id: detail.id, form: toForm(detail) }); }}>
                      {t("accounting.edit")}
                    </Button>
                    <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                      {t("common.cancel")}
                    </Button>
                    <Button onClick={() => { act.mutate("post"); }} loading={act.isPending} data-testid="post-revaluation">
                      {t("inventory.revaluations.post")}
                    </Button>
                  </>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{editing.id ? t("inventory.revaluations.edit") : t("inventory.revaluations.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("inventory.revaluations.kind")} required>
                  <SelectField value={editing.form.kind} onChange={(e) => { setForm({ kind: e.target.value }); }} data-testid="revaluation-kind">
                    {kinds.map((k) => (
                      <option key={k} value={k}>
                        {t(`inventory.revaluations.kinds.${k}`)}
                      </option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("accounting.postingDate")} required error={problem?.fields.postingDate}>
                  <TextField type="date" value={editing.form.postingDate} onChange={(e) => { setForm({ postingDate: e.target.value }); }} required dir="ltr" />
                </Field>
                <Field label={t("accounting.reference")}>
                  <TextField value={editing.form.reference} onChange={(e) => { setForm({ reference: e.target.value }); }} data-testid="revaluation-reference" />
                </Field>
              </div>
              <p className="text-sm text-fg-muted">{t(`inventory.revaluations.kindHints.${editing.form.kind}`, { defaultValue: "" })}</p>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("inventory.itemCode")}</TableHead>
                    <TableHead>{t("inventory.warehouse")}</TableHead>
                    <TableHead>{t("inventory.revaluations.newUnitCost")}</TableHead>
                    <TableHead>{t("inventory.revaluations.note")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.form.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <TextField value={line.itemCode} onChange={(e) => { updateLine(index, { itemCode: e.target.value.toUpperCase() }); }} dir="ltr" aria-label={t("inventory.itemCode")} data-testid={`reval-item-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <WarehouseSelect warehouses={warehouses.data ?? []} value={line.warehouseId} onChange={(id) => { updateLine(index, { warehouseId: id }); }} allowAll label={t("inventory.warehouse")} testId={`reval-warehouse-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <TextField inputMode="decimal" value={line.newUnitCost} onChange={(e) => { updateLine(index, { newUnitCost: e.target.value }); }} dir="ltr" aria-label={t("inventory.revaluations.newUnitCost")} data-testid={`reval-cost-${String(index)}`} />
                      </TableCell>
                      <TableCell>
                        <TextField value={line.note} onChange={(e) => { updateLine(index, { note: e.target.value }); }} aria-label={t("inventory.revaluations.note")} />
                      </TableCell>
                      <TableCell>
                        <Button type="button" variant="ghost" size="icon" onClick={() => { setForm({ lines: editing.form.lines.filter((_, i) => i !== index) }); }} aria-label={t("accounting.removeLine")}>
                          <Trash2 aria-hidden="true" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div>
                <Button type="button" variant="secondary" onClick={() => { setForm({ lines: [...editing.form.lines, { ...emptyLine }] }); }}>
                  <Plus aria-hidden="true" />
                  {t("accounting.addLine")}
                </Button>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-revaluation">
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
