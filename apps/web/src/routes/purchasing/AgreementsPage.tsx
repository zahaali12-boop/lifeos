import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, num, PurchaseStatus, useAgreements, useSuppliers, type Agreement, type LineForm } from "./shared";

interface AgreementForm {
  partnerId: string;
  validFrom: string;
  validTo: string;
  currency: string;
  committedAmount: string;
  lines: LineForm[];
}

/** Blanket purchase agreements (roadmap 4.2): agreed quantities and prices per item for a period, released by purchase orders and capped. */
export function AgreementsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<AgreementForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/purchasing/agreements");
  const suppliers = useSuppliers(companyId);
  const list = useAgreements(companyId);
  const detail = useQuery({
    queryKey: ["agreement", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/agreements/{agreementId}", { params: { path: { agreementId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["agreements"] });
    await queryClient.invalidateQueries({ queryKey: ["agreement"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: AgreementForm) =>
      unwrap(await api.POST("/api/v1/purchasing/agreements", {
        body: {
          companyId,
          partnerId: f.partnerId,
          validFrom: f.validFrom,
          validTo: f.validTo,
          currency: f.currency || null,
          committedAmount: num(f.committedAmount),
          lines: f.lines.map((l) => ({ itemCode: l.itemCode, agreedQty: num(l.quantity), uom: l.uom || null, agreedPrice: num(l.price) })),
        },
      })),
    onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "activate" | "close" | "cancel" }) => {
      const params = { path: { agreementId: input.id } };
      switch (input.action) {
        case "activate": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/activate", { params }));
        case "close": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/close", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/purchasing/agreements/{agreementId}/cancel", { params }));
      }
    },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Agreement, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "from", accessorKey: "validFrom", header: t("purchasing.validFrom"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.validFrom)}</span> },
      { id: "to", accessorKey: "validTo", header: t("purchasing.validTo"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.validTo)}</span> },
      { id: "released", accessorKey: "releasedAmount", header: t("purchasing.releasedAmount"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.releasedAmount, row.original.currency)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ partnerId: "", validFrom: today(), validTo: "", currency: "", committedAmount: "0", lines: [emptyLine()] }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const a = detail.data;

  return (
    <>
      <PageHeader
        title={t("nav.agreements")}
        description={t("purchasing.agreementsDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-agreement">
            <Plus aria-hidden="true" />
            {t("purchasing.newAgreement")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Agreement> label="nav.agreements" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyAgreements")} emptyDescription={t("purchasing.emptyAgreementsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("purchasing.newAgreement")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label={t("partners.supplier")} required>
                  <SelectField value={form.partnerId} onChange={(e) => { setForm({ ...form, partnerId: e.target.value }); }} required data-testid="agreement-supplier">
                    <option value="">—</option>
                    {(suppliers.data ?? []).map((s) => (
                      <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
                <Field label={t("purchasing.validFrom")} required>
                  <TextField type="date" value={form.validFrom} onChange={(e) => { setForm({ ...form, validFrom: e.target.value }); }} dir="ltr" required data-testid="agreement-from" />
                </Field>
                <Field label={t("purchasing.validTo")} required>
                  <TextField type="date" value={form.validTo} onChange={(e) => { setForm({ ...form, validTo: e.target.value }); }} dir="ltr" required data-testid="agreement-to" />
                </Field>
                <Field label={t("partners.currency")} description={t("purchasing.currencyHelp")}>
                  <TextField value={form.currency} onChange={(e) => { setForm({ ...form, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} />
                </Field>
                <Field label={t("purchasing.committedAmount")} description={t("purchasing.committedAmountHelp")}>
                  <TextField inputMode="decimal" value={form.committedAmount} onChange={(e) => { setForm({ ...form, committedAmount: e.target.value }); }} dir="ltr" />
                </Field>
              </div>
              <LinesEditor lines={form.lines} onChange={(lines) => { setForm({ ...form, lines }); }} priceLabel={t("purchasing.agreedPrice")} />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-agreement">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {a ? (
            <div className="flex flex-col gap-4" data-testid="agreement-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{a.number}</span>
                  <PurchaseStatus status={a.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("partners.supplier"), `${a.partnerCode} · ${localized(a.partnerName)}`],
                [t("purchasing.validity"), `${formatDate(a.validFrom)} → ${formatDate(a.validTo)}`],
                [t("purchasing.committedAmount"), Number(a.committedAmount) > 0 ? formatMoney(a.committedAmount, a.currency) : t("purchasing.uncapped")],
                [t("purchasing.releasedAmount"), formatMoney(a.releasedAmount, a.currency)],
              ]} />
              <Table data-testid="agreement-lines">
                <TableHeader>
                  <TableRow>
                    <TableHead>#</TableHead>
                    <TableHead>{t("purchasing.item")}</TableHead>
                    <TableHead>{t("purchasing.agreedQty")}</TableHead>
                    <TableHead>{t("purchasing.agreedPrice")}</TableHead>
                    <TableHead>{t("purchasing.releasedQty")}</TableHead>
                    <TableHead>{t("purchasing.remainingQty")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {a.lines.map((l) => (
                    <TableRow key={l.id} data-testid="agreement-line">
                      <TableCell>{String(l.lineNo)}</TableCell>
                      <TableCell dir="auto">{l.itemCode} · {localized(l.itemName)}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.agreedQty, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.agreedPrice, { maximumFractionDigits: 4 })}</TableCell>
                      <TableCell className="tabular" dir="ltr">{formatNumber(l.releasedQty, { maximumFractionDigits: 3 })}</TableCell>
                      <TableCell className="tabular" dir="ltr" data-testid="remaining-qty">{formatNumber(l.remainingQty, { maximumFractionDigits: 3 })}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <DialogFooter>
                {a.status === "draft" ? <Button onClick={() => { act.mutate({ id: a.id, action: "activate" }); }} loading={act.isPending} data-testid="activate-agreement">{t("purchasing.activate")}</Button> : null}
                {a.status === "active" ? <Button variant="secondary" onClick={() => { act.mutate({ id: a.id, action: "close" }); }} loading={act.isPending} data-testid="close-agreement">{t("purchasing.close")}</Button> : null}
                {a.status === "draft" || a.status === "active" ? <Button variant="secondary" onClick={() => { act.mutate({ id: a.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-agreement">{t("purchasing.cancelDocument")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
