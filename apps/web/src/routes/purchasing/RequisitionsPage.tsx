import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, num, optionalNum, PurchaseStatus, useSuppliers, type LineForm, type Requisition } from "./shared";

interface RequisitionForm {
  neededBy: string;
  justification: string;
  lines: LineForm[];
}

/** Purchase requisitions (roadmap 4.2): what a department needs, submitted through the workflow, then turned into one order per supplier. */
export function RequisitionsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<RequisitionForm | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const suppliers = useSuppliers(companyId);

  const list = useQuery({
    queryKey: ["requisitions", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/requisitions", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["requisition", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/requisitions/{requisitionId}", { params: { path: { requisitionId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["requisitions"] });
    await queryClient.invalidateQueries({ queryKey: ["requisition"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: RequisitionForm) =>
      unwrap(await api.POST("/api/v1/purchasing/requisitions", {
        body: {
          companyId,
          neededBy: f.neededBy || null,
          justification: f.justification || null,
          lines: f.lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null, estimatedPrice: optionalNum(l.price), suggestedSupplierId: l.supplierId || null })),
        },
      })),
    onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "submit" | "cancel" | "orders" }) => {
      const params = { path: { requisitionId: input.id } };
      switch (input.action) {
        case "submit": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/submit", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/cancel", { params }));
        case "orders": return unwrap(await api.POST("/api/v1/purchasing/requisitions/{requisitionId}/orders", { params, body: {} }));
      }
    },
    onSuccess: async (result, input) => {
      setProblem(null);
      if (input.action === "orders" && "orders" in result) {
        setCreatedOrders(result.orders.map((o) => o.number));
      }
      await refresh();
      await queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
    onError: fail,
  });
  const [createdOrders, setCreatedOrders] = useState<string[]>([]);

  const columns = useMemo<ColumnDef<Requisition, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 140, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "requester", accessorKey: "requesterName", header: t("purchasing.requester"), size: 160, cell: ({ row }) => row.original.requesterName ?? "" },
      { id: "neededBy", accessorKey: "neededBy", header: t("purchasing.neededBy"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.neededBy)}</span> },
      { id: "lines", accessorFn: (row) => row.lines.length, header: t("purchasing.lines"), size: 80, cell: ({ row }) => String(row.original.lines.length) },
      { id: "total", accessorKey: "totalEstimated", header: t("purchasing.estimatedTotal"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalEstimated, row.original.currency)}</span> },
      { id: "updated", accessorKey: "updatedAt", header: t("common.updated"), size: 160, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.updatedAt)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ neededBy: "", justification: "", lines: [emptyLine()] }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const r = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.requisitions")}
        description={t("purchasing.requisitionsDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-requisition">
            <Plus aria-hidden="true" />
            {t("purchasing.newRequisition")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "pending_approval", "approved", "ordered", "rejected", "cancelled"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Requisition> label="nav.requisitions" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyRequisitions")} emptyDescription={t("purchasing.emptyRequisitionsDescription")} onOpen={(row) => { setProblem(null); setCreatedOrders([]); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("purchasing.newRequisition")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("purchasing.neededBy")}>
                  <TextField type="date" value={form.neededBy} onChange={(e) => { setForm({ ...form, neededBy: e.target.value }); }} dir="ltr" data-testid="requisition-needed-by" />
                </Field>
                <Field label={t("purchasing.justification")}>
                  <TextareaField value={form.justification} onChange={(e) => { setForm({ ...form, justification: e.target.value }); }} rows={2} data-testid="requisition-justification" />
                </Field>
              </div>
              <LinesEditor lines={form.lines} onChange={(lines) => { setForm({ ...form, lines }); }} priceLabel={t("purchasing.estimatedPrice")} suppliers={suppliers.data ?? []} showDescription />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-requisition">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="requisition-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <PurchaseStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("purchasing.requester"), r.requesterName ?? "—"],
                [t("purchasing.neededBy"), formatDate(r.neededBy) || "—"],
                [t("purchasing.justification"), r.justification ?? "—"],
                [t("purchasing.estimatedTotal"), formatMoney(r.totalEstimated, r.currency)],
                ...(r.rejectionReason ? [[t("purchasing.rejectionReason"), r.rejectionReason] as [string, string]] : []),
              ]} />
              <LinesTable lines={r.lines.map((l) => ({ id: l.id, lineNo: l.lineNo, itemCode: l.itemCode, description: l.description, quantity: l.quantity, uomCode: l.uomCode, unitPrice: l.estimatedPrice, status: l.status }))} currency={r.currency} testId="requisition-lines" />
              {createdOrders.length > 0 ? <p className="text-sm" data-testid="created-orders">{t("purchasing.ordersCreated", { numbers: createdOrders.join(", ") })}</p> : null}
              <DialogFooter>
                {r.status === "draft" || r.status === "rejected" ? <Button onClick={() => { act.mutate({ id: r.id, action: "submit" }); }} loading={act.isPending} data-testid="submit-requisition">{t("purchasing.submit")}</Button> : null}
                {r.status === "approved" ? <Button onClick={() => { act.mutate({ id: r.id, action: "orders" }); }} loading={act.isPending} data-testid="create-orders">{t("purchasing.createOrders")}</Button> : null}
                {r.status === "draft" || r.status === "pending_approval" || r.status === "approved" || r.status === "rejected" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-requisition">{t("purchasing.cancelDocument")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
