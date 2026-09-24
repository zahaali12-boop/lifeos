import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { num, PurchaseStatus } from "./shared";
import { RecordActivity } from "../RecordDiscussion";

type Return = components["schemas"]["ReturnSummary"];
type Returnable = components["schemas"]["ReturnableLine"];

interface ReturnLineForm {
  receiptLineId: string;
  quantity: string;
  lotNumber: string;
  serialNumbers: string;
  reason: string;
}

interface ReturnForm {
  id: string | null;
  receiptId: string;
  postingDate: string;
  reason: string;
  supplierRma: string;
  lines: ReturnLineForm[];
}

/** Supplier returns (roadmap 4.6): quantities of a posted receipt sent back at their exact cost, GRNI relieved under the return's reference, credited by the supplier's debit note, reversed while nothing has been credited. */
export function ReturnsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [status, setStatus] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<ReturnForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/returns");
  const [reversal, setReversal] = useState<string | null>(null);

  const list = useQuery({
    queryKey: ["returns", companyId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns", { params: { query: { companyId, ...(status ? { status } : {}) } } })),
  });
  const returnable = useQuery({
    queryKey: ["returnable", companyId],
    enabled: Boolean(companyId) && Boolean(form),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns/returnable", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["return", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/returns/{returnId}", { params: { path: { returnId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await Promise.all([["returns"], ["return"], ["returnable"], ["receipts"], ["receipt"], ["invoicable"], ["stock"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const save = useMutation({
    mutationFn: async (f: ReturnForm) => {
      const body = {
        receiptId: f.receiptId,
        postingDate: f.postingDate || null,
        reason: f.reason || null,
        supplierRma: f.supplierRma || null,
        lines: f.lines.filter((l) => num(l.quantity) > 0).map((l) => ({ receiptLineId: l.receiptLineId, quantity: num(l.quantity), lotNumber: l.lotNumber || null, serialNumbers: l.serialNumbers.split(/[\s,]+/).filter(Boolean), reason: l.reason || null })),
      };
      return f.id ? unwrap(await api.PUT("/api/v1/purchasing/returns/{returnId}", { params: { path: { returnId: f.id } }, body })) : unwrap(await api.POST("/api/v1/purchasing/returns", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); setOpenId(saved.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "post" | "reverse" | "delete"; reason?: string }) => {
      const params = { path: { returnId: input.id } };
      switch (input.action) {
        case "post": return unwrap(await api.POST("/api/v1/purchasing/returns/{returnId}/post", { params }));
        case "reverse": return unwrap(await api.POST("/api/v1/purchasing/returns/{returnId}/reverse", { params, body: { reason: input.reason ?? "" } }));
        case "delete": { unwrap(await api.DELETE("/api/v1/purchasing/returns/{returnId}", { params })); return null; }
      }
    },
    onSuccess: async (result) => { setProblem(null); setReversal(null); if (result === null) { setOpenId(null); } await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Return, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 110, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "receipt", accessorKey: "receiptNumber", header: t("nav.receipts"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.receiptNumber}</span> },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "date", accessorKey: "postingDate", header: t("purchasing.postingDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.postingDate)}</span> },
      { id: "rma", accessorKey: "supplierRma", header: t("purchasing.supplierRma"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.supplierRma ?? ""}</span> },
      { id: "cost", accessorKey: "totalCostFc", header: t("purchasing.costValue"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.totalCostFc, row.original.functionalCurrency)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ id: null, receiptId: "", postingDate: today(), reason: "", supplierRma: "", lines: [] }); };
  const chooseReceipt = (receiptId: string): void => {
    if (!form) {
      return;
    }
    const lines = (returnable.data ?? []).filter((l) => l.receiptId === receiptId);
    setForm({ ...form, receiptId, lines: lines.map((l) => ({ receiptLineId: l.receiptLineId, quantity: "", lotNumber: l.lotNumber ?? "", serialNumbers: "", reason: "" })) });
  };
  const patchLine = (index: number, change: Partial<ReturnLineForm>): void => { if (form) { setForm({ ...form, lines: form.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }); } };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const receipts = useMemo(() => {
    const seen = new Map<string, string>();
    for (const l of returnable.data ?? []) {
      seen.set(l.receiptId, `${l.receiptNumber} · ${l.partnerCode}`);
    }
    return [...seen.entries()];
  }, [returnable.data]);
  const lineInfo = (receiptLineId: string): Returnable | undefined => returnable.data?.find((l) => l.receiptLineId === receiptLineId);
  const r = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.returns")}
        description={t("purchasing.returnsDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-return">
            <Plus aria-hidden="true" />
            {t("purchasing.newReturn")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
            <option value="">{t("common.all")}</option>
            {["draft", "posted", "reversed"].map((s) => (
              <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      <DataGrid<Return> label="nav.returns" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyReturns")} emptyDescription={t("purchasing.emptyReturnsDescription")} onOpen={(row) => { setProblem(null); setReversal(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{form.id ? t("purchasing.editReturn") : t("purchasing.newReturn")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-4">
                <Field label={t("nav.receipts")} required>
                  <SelectField value={form.receiptId} onChange={(e) => { chooseReceipt(e.target.value); }} required disabled={Boolean(form.id)} data-testid="return-receipt">
                    <option value="">—</option>
                    {receipts.map(([id, label]) => (
                      <option key={id} value={id}>{label}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("purchasing.postingDate")}>
                  <TextField type="date" value={form.postingDate} onChange={(e) => { setForm({ ...form, postingDate: e.target.value }); }} dir="ltr" data-testid="return-date" />
                </Field>
                <Field label={t("purchasing.supplierRma")}>
                  <TextField value={form.supplierRma} onChange={(e) => { setForm({ ...form, supplierRma: e.target.value }); }} dir="ltr" data-testid="return-rma" />
                </Field>
                <Field label={t("purchasing.returnReason")}>
                  <TextField value={form.reason} onChange={(e) => { setForm({ ...form, reason: e.target.value }); }} data-testid="return-reason" />
                </Field>
              </div>
              {form.lines.length > 0 ? (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.item")}</TableHead>
                      <TableHead>{t("purchasing.received")}</TableHead>
                      <TableHead>{t("purchasing.returnedSoFar")}</TableHead>
                      <TableHead>{t("purchasing.returnNow")}</TableHead>
                      <TableHead>{t("purchasing.lot")}</TableHead>
                      <TableHead>{t("purchasing.serials")}</TableHead>
                      <TableHead>{t("purchasing.returnReason")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {form.lines.map((line, index) => {
                      const info = lineInfo(line.receiptLineId);
                      const lotTracked = info?.tracking === "lot" || info?.tracking === "lot_and_serial";
                      const serialTracked = info?.tracking === "serial" || info?.tracking === "lot_and_serial";
                      return (
                        <TableRow key={line.receiptLineId} data-testid="return-line">
                          <TableCell dir="auto">{info ? `${info.itemCode} · ${localized(info.itemName)}` : ""}</TableCell>
                          <TableCell className="tabular" dir="ltr">{info ? `${formatNumber(info.received, { maximumFractionDigits: 3 })} ${info.uomCode}` : ""}</TableCell>
                          <TableCell className="tabular" dir="ltr">{info ? formatNumber(info.returned, { maximumFractionDigits: 3 }) : ""}</TableCell>
                          <TableCell><TextField aria-label={t("purchasing.returnNow")} inputMode="decimal" value={line.quantity} onChange={(e) => { patchLine(index, { quantity: e.target.value }); }} dir="ltr" className="w-24" data-testid={`return-qty-${String(index)}`} /></TableCell>
                          <TableCell>{lotTracked ? <TextField aria-label={t("purchasing.lot")} value={line.lotNumber} onChange={(e) => { patchLine(index, { lotNumber: e.target.value }); }} dir="ltr" className="w-28" data-testid={`return-lot-${String(index)}`} /> : null}</TableCell>
                          <TableCell>{serialTracked ? <TextField aria-label={t("purchasing.serials")} value={line.serialNumbers} onChange={(e) => { patchLine(index, { serialNumbers: e.target.value }); }} dir="ltr" className="w-40" placeholder={t("purchasing.serialsHelp")} data-testid={`return-serials-${String(index)}`} /> : null}</TableCell>
                          <TableCell><TextField aria-label={t("purchasing.returnReason")} value={line.reason} onChange={(e) => { patchLine(index, { reason: e.target.value }); }} className="w-40" data-testid={`return-line-reason-${String(index)}`} /></TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              ) : (
                <p className="text-xs text-fg-muted">{form.receiptId ? t("purchasing.nothingReturnable") : t("purchasing.chooseReceipt")}</p>
              )}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} disabled={!form.receiptId} data-testid="save-return">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setReversal(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="return-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <PurchaseStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("nav.receipts"), r.receiptNumber],
                [t("partners.supplier"), `${r.partnerCode} · ${localized(r.partnerName)}`],
                [t("purchasing.warehouse"), r.warehouseCode ?? "—"],
                [t("purchasing.postingDate"), formatDate(r.postingDate)],
                [t("purchasing.supplierRma"), r.supplierRma ?? "—"],
                [t("purchasing.returnReason"), r.reason ?? "—"],
                [t("purchasing.costValue"), <span key="value" data-testid="return-value">{formatMoney(r.totalCostFc, r.functionalCurrency)}</span>],
                ...(r.reversalReason ? [[t("purchasing.reversalReason"), r.reversalReason] as [string, string]] : []),
              ]} />
              <Table data-testid="return-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("purchasing.item")}</TableHead>
                    <TableHead>{t("purchasing.quantity")}</TableHead>
                    <TableHead>{t("purchasing.lot")}</TableHead>
                    <TableHead>{t("purchasing.costValue")}</TableHead>
                    <TableHead>{t("purchasing.credited")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {r.lines.map((l) => (
                    <TableRow key={l.id} data-testid="return-line-row">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">{l.itemCode} · {localized(l.itemName)}{l.reason ? ` — ${l.reason}` : ""}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                      <TableCell dir="ltr">{l.lotNumber ?? ""}{l.serialNumbers.length > 0 ? ` ${l.serialNumbers.join(", ")}` : ""}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatMoney(l.costAmountFc, r.functionalCurrency)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.qtyCredited, { maximumFractionDigits: 3 })} · {formatMoney(l.creditedAmountFc, r.functionalCurrency)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {reversal !== null ? (
                <Field label={t("purchasing.reversalReason")} required>
                  <TextField value={reversal} onChange={(e) => { setReversal(e.target.value); }} data-testid="reversal-reason" />
                </Field>
              ) : null}
              <RecordActivity entityType="purchase_return" entityId={r.id} />
              <DialogFooter>
                {r.status === "draft" ? <Button variant="secondary" onClick={() => { setProblem(null); setForm({ id: r.id, receiptId: r.receiptId, postingDate: r.postingDate, reason: r.reason ?? "", supplierRma: r.supplierRma ?? "", lines: r.lines.map((l) => ({ receiptLineId: l.receiptLineId, quantity: String(l.quantity), lotNumber: l.lotNumber ?? "", serialNumbers: l.serialNumbers.join(" "), reason: l.reason ?? "" })) }); }} data-testid="edit-return">{t("common.edit")}</Button> : null}
                {r.status === "draft" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "delete" }); }} loading={act.isPending} data-testid="delete-return">{t("purchasing.deleteDraft")}</Button> : null}
                {r.status === "draft" ? <Button onClick={() => { act.mutate({ id: r.id, action: "post" }); }} loading={act.isPending} data-testid="post-return">{t("purchasing.postReturn")}</Button> : null}
                {r.status === "posted" && reversal === null ? <Button variant="secondary" onClick={() => { setReversal(""); }} data-testid="reverse-return">{t("purchasing.reverse")}</Button> : null}
                {reversal !== null ? <Button onClick={() => { act.mutate({ id: r.id, action: "reverse", reason: reversal }); }} loading={act.isPending} disabled={!reversal.trim()} data-testid="confirm-reverse">{t("purchasing.reverseNow")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
