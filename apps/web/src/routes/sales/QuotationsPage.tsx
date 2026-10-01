import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, ShoppingCart } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, SelectField, TextareaField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { useCompanyCustomers } from "./pricing/shared";
import { emptySalesLine, Money, money, salesLineBodies, SalesLinesEditor, SalesLinesTable, SalesStatus, type Quotation, type SalesLineForm } from "./shared";
import { CurrencyField } from "../CurrencyField";

interface QuotationForm {
  partnerId: string;
  currency: string;
  quoteDate: string;
  validUntil: string;
  notes: string;
  lines: SalesLineForm[];
}

const emptyForm = (): QuotationForm => ({ partnerId: "", currency: "", quoteDate: "", validUntil: "", notes: "", lines: [emptySalesLine()] });

/** Sales quotations (roadmap 5.4a): priced and taxed for a customer, sent, accepted or rejected, an accepted one converts to an order with its lines frozen. */
export function QuotationsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const can = useCan();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [creating, setCreating] = useState<QuotationForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/sales/quotations");
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState("");
  const [converting, setConverting] = useState<string | null>(null);
  const [convertWarehouse, setConvertWarehouse] = useState("");
  const customers = useCompanyCustomers(companyId);
  const warehouses = useWarehouses(companyId);
  const canManage = can("sales.quote.manage");

  const list = useQuery({
    queryKey: ["quotations", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/quotations", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["quotation", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/sales/quotations/{quotationId}", { params: { path: { quotationId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["quotations"] });
    await queryClient.invalidateQueries({ queryKey: ["quotation"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: QuotationForm) => unwrap(await api.POST("/api/v1/sales/quotations", {
      body: { companyId, partnerId: f.partnerId, currency: f.currency || null, quoteDate: f.quoteDate || null, validUntil: f.validUntil || null, notes: f.notes || null, lines: salesLineBodies(f.lines) },
    })),
    onSuccess: async (saved) => { setProblem(null); setCreating(null); await refresh(); setOpenId(saved.id); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "send" | "accept" | "reject"; reason?: string }) => {
      const params = { path: { quotationId: input.id } };
      switch (input.action) {
        case "send": return unwrap(await api.POST("/api/v1/sales/quotations/{quotationId}/send", { params }));
        case "accept": return unwrap(await api.POST("/api/v1/sales/quotations/{quotationId}/accept", { params }));
        case "reject": return unwrap(await api.POST("/api/v1/sales/quotations/{quotationId}/reject", { params, body: { reason: input.reason ?? "" } }));
      }
    },
    onSuccess: async () => { setProblem(null); setRejecting(null); setRejectReason(""); await refresh(); },
    onError: fail,
  });
  const convert = useMutation({
    mutationFn: async (input: { id: string; warehouseId: string }) => unwrap(await api.POST("/api/v1/sales/quotations/{quotationId}/convert", { params: { path: { quotationId: input.id } }, body: { warehouseId: input.warehouseId } })),
    onSuccess: async (order) => { setProblem(null); setConverting(null); setConvertWarehouse(""); await refresh(); void navigate({ to: "/sales/orders", search: { open: order.id } }); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Quotation, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("sales.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 130, cell: ({ row }) => <SalesStatus status={row.original.status} /> },
      { id: "customer", accessorKey: "partnerCode", header: t("sales.customer"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "quoteDate", header: t("sales.quoteDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.quoteDate)}</span> },
      { id: "validUntil", accessorKey: "validUntil", header: t("sales.validUntil"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.validUntil) || "—"}</span> },
      { id: "total", accessorKey: "totalGross", header: t("sales.total"), size: 150, cell: ({ row }) => <Money amount={row.original.totalGross} currency={row.original.currency} /> },
    ],
    [t],
  );

  const submitCreate = (event: FormEvent): void => { event.preventDefault(); if (creating) { create.mutate(creating); } };
  const q = detail.data;
  const expired = Boolean(q?.isExpired);

  return (
    <>
      <PageHeader
        title={t("nav.quotations")}
        description={t("sales.quotationsDescription")}
        actions={
          canManage ? (
            <Button onClick={() => { setProblem(null); setCreating(emptyForm()); }} disabled={!companyId} data-testid="new-quotation">
              <Plus aria-hidden="true" />
              {t("sales.newQuotation")}
            </Button>
          ) : null
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "sent", "accepted", "rejected", "converted"].map((s) => (
              <option key={s} value={s}>{t(`sales.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Quotation> label="nav.quotations" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("sales.emptyQuotations")} emptyDescription={t("sales.emptyQuotationsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(creating)} onOpenChange={(isOpen) => { if (!isOpen) { setCreating(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {creating ? (
            <form onSubmit={submitCreate} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("sales.newQuotation")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("sales.customer")} required>
                  <SelectField value={creating.partnerId} onChange={(e) => { setCreating({ ...creating, partnerId: e.target.value }); }} required data-testid="quotation-customer">
                    <option value="">—</option>
                    {(customers.data ?? []).map((c) => (
                      <option key={c.partnerId} value={c.partnerId}>{c.partnerCode} · {localized(c.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("partners.currency")} description={t("purchasing.currencyHelp")}>
                  <CurrencyField value={creating.currency} onChange={(code) => { setCreating({ ...creating, currency: code }); }} allowEmpty data-testid="quotation-currency" />
                </Field>
                <Field label={t("sales.quoteDate")}>
                  <TextField type="date" value={creating.quoteDate} onChange={(e) => { setCreating({ ...creating, quoteDate: e.target.value }); }} dir="ltr" data-testid="quotation-date" />
                </Field>
                <Field label={t("sales.validUntil")}>
                  <TextField type="date" value={creating.validUntil} onChange={(e) => { setCreating({ ...creating, validUntil: e.target.value }); }} dir="ltr" data-testid="quotation-valid-until" />
                </Field>
                <Field label={t("sales.notes")} className="sm:col-span-2">
                  <TextareaField value={creating.notes} onChange={(e) => { setCreating({ ...creating, notes: e.target.value }); }} rows={2} />
                </Field>
              </div>
              <SalesLinesEditor lines={creating.lines} onChange={(lines) => { setCreating({ ...creating, lines }); }} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setCreating(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-quotation">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setRejecting(null); setConverting(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {q ? (
            <div className="flex flex-col gap-4" data-testid="quotation-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{q.number}</span>
                  <SalesStatus status={q.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("sales.customer"), `${q.partnerCode} · ${localized(q.partnerName)}`],
                [t("sales.quoteDate"), formatDate(q.quoteDate)],
                [t("sales.validUntil"), q.validUntil ? `${formatDate(q.validUntil)}${expired ? ` · ${t("sales.expired")}` : ""}` : "—"],
                [t("sales.total"), <span key="total" data-testid="quotation-total">{money(q.totalGross, q.currency)}</span>],
                ...(q.rejectionReason ? [[t("sales.rejectionReason"), q.rejectionReason] as [string, string]] : []),
              ]} />
              <SalesLinesTable lines={q.lines} currency={q.currency} testId="quotation-lines" />
              {rejecting === q.id ? (
                <Field label={t("sales.rejectionReason")} required>
                  <TextField value={rejectReason} onChange={(e) => { setRejectReason(e.target.value); }} required data-testid="reject-reason" />
                </Field>
              ) : null}
              {converting === q.id ? (
                <WarehouseSelect warehouses={warehouses.data ?? []} value={convertWarehouse} onChange={setConvertWarehouse} label={t("sales.shipFrom")} testId="convert-warehouse" required />
              ) : null}
              <DialogFooter>
                {q.status === "draft" && canManage ? <Button onClick={() => { act.mutate({ id: q.id, action: "send" }); }} loading={act.isPending} data-testid="send-quotation">{t("sales.send")}</Button> : null}
                {q.status === "sent" && canManage ? <Button onClick={() => { act.mutate({ id: q.id, action: "accept" }); }} loading={act.isPending} disabled={expired} data-testid="accept-quotation">{t("sales.accept")}</Button> : null}
                {q.status === "sent" && canManage && rejecting !== q.id ? <Button variant="secondary" onClick={() => { setRejecting(q.id); setRejectReason(""); }} data-testid="start-reject-quotation">{t("sales.reject")}</Button> : null}
                {rejecting === q.id ? <Button variant="secondary" onClick={() => { act.mutate({ id: q.id, action: "reject", reason: rejectReason }); }} loading={act.isPending} data-testid="confirm-reject-quotation">{t("sales.confirmReject")}</Button> : null}
                {q.status === "accepted" && !q.orderId && can("sales.order.manage") && converting !== q.id ? <Button onClick={() => { setConverting(q.id); setConvertWarehouse(""); }} data-testid="start-convert-quotation"><ShoppingCart aria-hidden="true" />{t("sales.convertToOrder")}</Button> : null}
                {converting === q.id ? <Button onClick={() => { convert.mutate({ id: q.id, warehouseId: convertWarehouse }); }} loading={convert.isPending} disabled={!convertWarehouse} data-testid="confirm-convert-quotation">{t("sales.confirmConvert")}</Button> : null}
                {q.orderId ? <Button variant="secondary" onClick={() => { void navigate({ to: "/sales/orders", search: { open: q.orderId ?? "" } }); }} data-testid="open-converted-order">{t("sales.openOrder")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
