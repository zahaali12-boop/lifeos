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
import { ItemCodeField } from "./ItemCodeField";
import { RecordActivity } from "../RecordDiscussion";

type Assembly = components["schemas"]["AssemblySummary"];

interface LineForm {
  itemCode: string;
  quantity: string;
  uom: string;
}

interface AssemblyForm {
  warehouseId: string;
  outputItemCode: string;
  outputQuantity: string;
  outputUom: string;
  postingDate: string;
  reference: string;
  lines: LineForm[];
}

const statuses = ["", "draft", "posted", "cancelled"];

/** Assemblies (roadmap 3.4): build an item from components (from its bill of materials or listed by hand); the output is worth what the components cost. */
export function AssembliesPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [status, setStatus] = useState("");
  const [editing, setEditing] = useState<AssemblyForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const openId = search.open;

  const assemblies = useQuery({
    queryKey: ["assemblies", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/assemblies", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const assembly = useQuery({
    queryKey: ["assembly", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/assemblies/{assemblyId}", { params: { path: { assemblyId: openId ?? "" } } })),
  });
  const refresh = async (id?: string | null): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["assemblies"] });
    await queryClient.invalidateQueries({ queryKey: ["stock"] });
    if (id) {
      await queryClient.invalidateQueries({ queryKey: ["assembly", id] });
    }
  };
  const open = (id: string | null): void => { void navigate({ to: "/inventory/assemblies", search: id ? { open: id } : {} }); };

  const save = useMutation({
    mutationFn: async (f: AssemblyForm) =>
      unwrap(await api.POST("/api/v1/inventory/assemblies", {
        body: {
          companyId,
          warehouseId: f.warehouseId,
          outputItemCode: f.outputItemCode.trim(),
          outputQuantity: f.outputQuantity || "0",
          outputUom: f.outputUom || null,
          postingDate: f.postingDate || null,
          reference: f.reference || null,
          lines: f.lines.filter((l) => l.itemCode.trim()).length > 0 ? f.lines.filter((l) => l.itemCode.trim()).map((l) => ({ itemCode: l.itemCode.trim(), quantity: l.quantity || "0", uom: l.uom || null })) : null,
        },
      })),
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
      const assemblyId = openId ?? "";
      return action === "post"
        ? unwrap(await api.POST("/api/v1/inventory/assemblies/{assemblyId}/post", { params: { path: { assemblyId } } }))
        : unwrap(await api.POST("/api/v1/inventory/assemblies/{assemblyId}/cancel", { params: { path: { assemblyId } } }));
    },
    onSuccess: async () => {
      setProblem(null);
      await refresh(openId);
    },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const columns = useMemo<ColumnDef<Assembly, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("accounting.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "date", accessorKey: "postingDate", header: t("accounting.date"), size: 110, cell: ({ row }) => formatDate(row.original.postingDate) },
      { id: "output", accessorKey: "outputItemCode", header: t("inventory.assemblies.output"), size: 200, cell: ({ row }) => `${row.original.outputItemCode} · ${localized(row.original.outputItemName)}` },
      { id: "qty", accessorKey: "outputQuantity", header: t("inventory.quantity"), size: 110, cell: ({ row }) => <Qty value={row.original.outputQuantity} uom={row.original.outputUomCode} /> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 120 },
      { id: "cost", accessorKey: "outputCost", header: t("inventory.costAmount"), size: 130, cell: ({ row }) => <Amount value={row.original.outputCost} /> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <DocStatus status={row.original.status} /> },
    ],
    [t],
  );

  const detail = assembly.data;
  const setForm = (patch: Partial<AssemblyForm>): void => { setEditing((prev) => (prev ? { ...prev, ...patch } : prev)); };
  const submitForm = (event: FormEvent): void => {
    event.preventDefault();
    if (editing) {
      save.mutate(editing);
    }
  };

  return (
    <>
      <PageHeader
        title={t("nav.assemblies")}
        description={t("inventory.assemblies.description")}
        actions={
          <Button onClick={() => { setProblem(null); setEditing({ warehouseId: warehouses.data?.[0]?.id ?? "", outputItemCode: "", outputQuantity: "1", outputUom: "", postingDate: today(), reference: "", lines: [{ itemCode: "", quantity: "", uom: "" }] }); }} disabled={!companyId} data-testid="new-assembly">
            <Plus aria-hidden="true" />
            {t("inventory.assemblies.new")}
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
      <DataGrid<Assembly> label="nav.assemblies" columns={columns} data={assemblies.data ?? []} rowKey={(row) => row.id} loading={assemblies.isPending && Boolean(companyId)} emptyTitle={t("inventory.assemblies.emptyTitle")} emptyDescription={t("inventory.assemblies.emptyDescription")} onOpen={(row) => { open(row.id); }} />

      <Dialog open={Boolean(openId) && !editing} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.number} · ${detail.outputItemCode}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="assembly-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>
                  <Qty value={detail.outputQuantity} uom={detail.outputUomCode} /> {localized(detail.outputItemName)}
                </span>
                <span className="text-fg-muted">{detail.warehouseCode}</span>
                {detail.outputCost !== null ? (
                  <span className="text-fg-muted">
                    {t("inventory.costAmount")}: <Amount value={detail.outputCost} />
                  </span>
                ) : null}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("inventory.assemblies.component")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead className="text-end">{t("inventory.costAmount")}</TableHead>
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
                      <TableNumberCell><Amount value={line.costAmount} /></TableNumberCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <FormError message={problem?.message ?? null} />
              <RecordActivity entityType="stock_assembly" entityId={detail.id} />
              <DialogFooter>
                {detail.status === "draft" ? (
                  <>
                    <Button variant="secondary" onClick={() => { act.mutate("cancel"); }} loading={act.isPending}>
                      {t("common.cancel")}
                    </Button>
                    <Button onClick={() => { act.mutate("post"); }} loading={act.isPending} data-testid="post-assembly">
                      {t("accounting.post")}
                    </Button>
                  </>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(editing)} onOpenChange={(isOpen) => { if (!isOpen) { setEditing(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {editing ? (
            <form onSubmit={submitForm} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("inventory.assemblies.new")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem && Object.keys(problem.fields).length === 0 ? problem.message : null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <WarehouseSelect warehouses={warehouses.data ?? []} value={editing.warehouseId} onChange={(id) => { setForm({ warehouseId: id }); }} required />
                <Field label={t("inventory.assemblies.output")} required>
                  <TextField value={editing.outputItemCode} onChange={(e) => { setForm({ outputItemCode: e.target.value.toUpperCase() }); }} required dir="ltr" data-testid="assembly-output" />
                </Field>
                <Field label={t("inventory.quantity")} required>
                  <TextField inputMode="decimal" value={editing.outputQuantity} onChange={(e) => { setForm({ outputQuantity: e.target.value }); }} required dir="ltr" data-testid="assembly-qty" />
                </Field>
                <Field label={t("accounting.postingDate")}>
                  <TextField type="date" value={editing.postingDate} onChange={(e) => { setForm({ postingDate: e.target.value }); }} dir="ltr" />
                </Field>
                <Field label={t("accounting.reference")}>
                  <TextField value={editing.reference} onChange={(e) => { setForm({ reference: e.target.value }); }} />
                </Field>
              </div>
              <p className="text-sm text-fg-muted">{t("inventory.assemblies.linesHint")}</p>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("inventory.assemblies.component")}</TableHead>
                    <TableHead className="text-end">{t("inventory.quantity")}</TableHead>
                    <TableHead>{t("inventory.uom")}</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {editing.lines.map((line, index) => (
                    <TableRow key={index}>
                      <TableCell>
                        <ItemCodeField aria-label={t("inventory.assemblies.component")} value={line.itemCode} onChange={(code) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, itemCode: code } : l)) }); }} data-testid={`line-item-${index}`} />
                      </TableCell>
                      <TableNumberCell>
                        <TextField aria-label={t("inventory.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, quantity: e.target.value } : l)) }); }} dir="ltr" className="text-end" data-testid={`line-qty-${index}`} />
                      </TableNumberCell>
                      <TableCell>
                        <TextField aria-label={t("inventory.uom")} value={line.uom} onChange={(e) => { setForm({ lines: editing.lines.map((l, i) => (i === index ? { ...l, uom: e.target.value.toUpperCase() } : l)) }); }} dir="ltr" className="w-20" />
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
                <Button type="button" variant="secondary" onClick={() => { setForm({ lines: [...editing.lines, { itemCode: "", quantity: "", uom: "" }] }); }} data-testid="add-line">
                  <Plus aria-hidden="true" />
                  {t("accounting.addLine")}
                </Button>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setEditing(null); }}>
                  {t("common.cancel")}
                </Button>
                <Button type="submit" loading={save.isPending} data-testid="save-assembly">
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
