import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatMoney, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { num, useSuppliers } from "../purchasing/shared";
import { ItemStatus, kindLabel, useOpenItems, type OpenItemRow } from "./shared";

/** Payable open items (roadmap 4.7): what is owed per supplier, aged at any date from the items and their settlements; holds; credits, advances and payments on account applied to invoices. */
export function OpenItemsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [partnerId, setPartnerId] = useState("");
  const [status, setStatus] = useState("live");
  const [tab, setTab] = useState("items");
  const [asOf, setAsOf] = useState(today());
  const [openId, setOpenId] = useState<string | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [hold, setHold] = useState<string | null>(null);
  const [apply, setApply] = useState<{ settledItemId: string; amount: string } | null>(null);
  const suppliers = useSuppliers(companyId);
  const list = useOpenItems(companyId, partnerId, status);
  const aging = useQuery({
    queryKey: ["aging", companyId, asOf, partnerId],
    enabled: Boolean(companyId) && tab === "aging",
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/aging", { params: { query: { companyId, asOf, ...(partnerId ? { partnerId } : {}) } } })),
  });
  const detail = useQuery({
    queryKey: ["open-item", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/{itemId}", { params: { path: { itemId: openId ?? "" } } })),
  });
  const settlements = useQuery({
    queryKey: ["settlements", companyId, openId],
    enabled: Boolean(openId) && Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/settlements", { params: { query: { companyId, openItemId: openId ?? "" } } })),
  });
  const targets = useOpenItems(companyId, detail.data?.item.partnerId ?? "", "live");
  const refresh = async (): Promise<void> => {
    await Promise.all([["open-items"], ["open-item"], ["aging"], ["settlements"], ["invoice"], ["invoices"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const act = useMutation({
    mutationFn: async (input: { action: "hold" | "release" | "apply" | "reverse"; id: string; reason?: string; settledItemId?: string; amount?: number }) => {
      switch (input.action) {
        case "hold": return unwrap(await api.POST("/api/v1/payables/open-items/{itemId}/hold", { params: { path: { itemId: input.id } }, body: { reason: input.reason ?? "" } }));
        case "release": return unwrap(await api.POST("/api/v1/payables/open-items/{itemId}/release", { params: { path: { itemId: input.id } } }));
        case "apply": return unwrap(await api.POST("/api/v1/payables/settlements/apply", { body: { settlingItemId: input.id, settledItemId: input.settledItemId ?? "", amount: input.amount ?? 0 } }));
        case "reverse": return unwrap(await api.POST("/api/v1/payables/settlements/{settlementId}/reverse", { params: { path: { settlementId: input.id } }, body: { reason: input.reason ?? "" } }));
      }
    },
    onSuccess: async () => { setProblem(null); setHold(null); setApply(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<OpenItemRow, unknown>[]>(
    () => [
      { id: "document", accessorFn: (r) => r.item.documentNumber, header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.item.documentNumber}{Number(row.original.item.instalment) > 1 ? ` / ${String(row.original.item.instalment)}` : ""}</span> },
      { id: "kind", accessorFn: (r) => r.item.kind, header: t("purchasing.kind"), size: 130, cell: ({ row }) => kindLabel(t, row.original.item.kind) },
      { id: "status", accessorFn: (r) => r.item.status, header: t("common.status"), size: 130, cell: ({ row }) => <span className="flex items-center gap-1"><ItemStatus status={row.original.item.status} />{row.original.item.paymentBlocked ? <Badge tone="danger">{t("payables.held")}</Badge> : null}</span> },
      { id: "supplier", accessorKey: "partnerCode", header: t("partners.supplier"), size: 200, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "due", accessorFn: (r) => r.item.dueDate, header: t("purchasing.dueDate"), size: 110, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.item.dueDate)}</span> },
      { id: "original", accessorFn: (r) => r.item.originalTc, header: t("purchasing.amount"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.item.originalTc, row.original.item.currency)}</span> },
      { id: "remaining", accessorFn: (r) => r.item.remainingTc, header: t("purchasing.remaining"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.item.remainingTc, row.original.item.currency)}</span> },
      { id: "remainingFc", accessorFn: (r) => r.item.remainingFc, header: t("payables.remainingFc"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.item.remainingFc, row.original.functionalCurrency)}</span> },
    ],
    [t],
  );

  const d = detail.data;
  const item = d?.item;
  const canApply = item !== undefined && Number(item.originalTc) < 0 && (item.status === "open" || item.status === "partially_settled");
  const canHold = item !== undefined && (item.status === "open" || item.status === "partially_settled");
  const bucket = (value: number | string, currency: string): string => (Number(value) === 0 ? "" : formatMoney(value, currency));

  return (
    <>
      <PageHeader title={t("nav.payables")} description={t("payables.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("partners.supplier")}>
          <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="supplier-filter">
            <option value="">{t("common.all")}</option>
            {(suppliers.data ?? []).map((sup) => (
              <option key={sup.partnerId} value={sup.partnerId}>{sup.partnerCode} · {localized(sup.partnerName)}</option>
            ))}
          </SelectField>
        </Field>
        {tab === "items" ? (
          <Field label={t("common.status")}>
            <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }} data-testid="status-filter">
              <option value="live">{t("payables.live")}</option>
              {["open", "partially_settled", "settled", "reversed"].map((s) => (
                <option key={s} value={s}>{t(`purchasing.statuses.${s}`)}</option>
              ))}
            </SelectField>
          </Field>
        ) : (
          <Field label={t("payables.asOf")}>
            <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="aging-as-of" />
          </Field>
        )}
      </div>
      <Tabs tabs={[{ id: "items", label: t("payables.openItems"), testId: "tab-items" }, { id: "aging", label: t("payables.aging"), testId: "tab-aging" }]} value={tab} onChange={setTab} />
      {tab === "items" ? (
        <DataGrid<OpenItemRow> label="nav.payables" columns={columns} data={list.data ?? []} rowKey={(row) => row.item.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("payables.emptyItems")} emptyDescription={t("payables.emptyItemsDescription")} onOpen={(row) => { setProblem(null); setHold(null); setApply(null); setOpenId(row.item.id); }} />
      ) : (
        <div className="mt-3" data-testid="aging-report">
          {aging.data ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("partners.supplier")}</TableHead>
                  <TableHead>{t("payables.notDue")}</TableHead>
                  <TableHead>1–30</TableHead>
                  <TableHead>31–60</TableHead>
                  <TableHead>61–90</TableHead>
                  <TableHead>90+</TableHead>
                  <TableHead>{t("payables.total")}</TableHead>
                  <TableHead>{t("payables.advances")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {aging.data.rows.map((r) => (
                  <TableRow key={r.partnerId} data-testid="aging-row">
                    <TableCell dir="auto">{r.partnerCode} · {localized(r.partnerName)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.notDue, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days1To30, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days31To60, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.days61To90, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.over90, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular font-semibold" dir="ltr">{formatMoney(r.totalFc, aging.data.functionalCurrency)}</TableCell>
                    <TableCell className="tabular" dir="ltr">{bucket(r.advancesFc, aging.data.functionalCurrency)}</TableCell>
                  </TableRow>
                ))}
                <TableRow data-testid="aging-totals">
                  <TableCell className="font-semibold">{t("payables.total")}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.notDue, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days1To30, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days31To60, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.days61To90, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.over90, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular font-semibold" dir="ltr" data-testid="aging-total">{formatMoney(aging.data.totals.totalFc, aging.data.functionalCurrency)}</TableCell>
                  <TableCell className="tabular" dir="ltr">{bucket(aging.data.totals.advancesFc, aging.data.functionalCurrency)}</TableCell>
                </TableRow>
              </TableBody>
            </Table>
          ) : <p className="text-sm text-fg-muted">{companyId ? t("common.loading") : t("payables.chooseCompany")}</p>}
        </div>
      )}

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {d && item ? (
            <div className="flex flex-col gap-4" data-testid="open-item-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{item.documentNumber}</span>
                  <ItemStatus status={item.status} />
                  {item.paymentBlocked ? <Badge tone="danger">{t("payables.held")}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("purchasing.kind"), kindLabel(t, item.kind)],
                [t("partners.supplier"), `${d.partnerCode} · ${localized(d.partnerName)}`],
                [t("purchasing.postingDate"), formatDate(item.postingDate)],
                [t("purchasing.dueDate"), formatDate(item.dueDate)],
                [t("purchasing.amount"), formatMoney(item.originalTc, item.currency)],
                [t("purchasing.remaining"), <span key="rem" data-testid="item-remaining">{formatMoney(item.remainingTc, item.currency)}</span>],
                [t("payables.remainingFc"), formatMoney(item.remainingFc, d.functionalCurrency)],
                ...(item.blockReason ? [[t("payables.holdReason"), item.blockReason] as [string, string]] : []),
              ]} />
              <h3 className="text-sm font-semibold">{t("purchasing.settlements")}</h3>
              {(settlements.data ?? []).length === 0 ? <p className="text-xs text-fg-muted">{t("purchasing.noSettlements")}</p> : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("purchasing.postingDate")}</TableHead>
                      <TableHead>{t("purchasing.kind")}</TableHead>
                      <TableHead>{t("purchasing.appliedTo")}</TableHead>
                      <TableHead>{t("purchasing.amount")}</TableHead>
                      <TableHead>{t("purchasing.fxGainLoss")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(settlements.data ?? []).map((st) => (
                      <TableRow key={st.id} data-testid="settlement-row">
                        <TableCell dir="ltr">{formatDate(st.settlementDate)}</TableCell>
                        <TableCell>{t(`purchasing.settlementKinds.${st.kind}`)}</TableCell>
                        <TableCell dir="ltr">{st.settlingDocumentNumber} → {st.settledDocumentNumber}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(st.amountTc, st.currency)}</TableCell>
                        <TableCell className="tabular" dir="ltr">{formatMoney(st.fxGainLossFc, d.functionalCurrency)}</TableCell>
                        <TableCell>{st.status === "posted" && !st.reversesSettlementId && (st.kind === "credit_application" || st.kind === "advance_application") ? <Button variant="ghost" size="sm" onClick={() => { act.mutate({ action: "reverse", id: st.id, reason: t("payables.unappliedReason") }); }} data-testid="unapply">{t("payables.unapply")}</Button> : null}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              {hold !== null ? (
                <Field label={t("payables.holdReason")} required>
                  <TextField value={hold} onChange={(e) => { setHold(e.target.value); }} data-testid="hold-reason" />
                </Field>
              ) : null}
              {apply ? (
                <div className="grid gap-3 sm:grid-cols-2" data-testid="apply-form">
                  <Field label={t("purchasing.appliedTo")} required>
                    <SelectField value={apply.settledItemId} onChange={(e) => { setApply({ ...apply, settledItemId: e.target.value }); }} data-testid="apply-target">
                      <option value="">—</option>
                      {(targets.data ?? []).map((o) => o.item).filter((o) => Number(o.originalTc) > 0 && o.currency === item.currency).map((o) => (
                        <option key={o.id} value={o.id}>{o.documentNumber} · {formatMoney(o.remainingTc, o.currency)}</option>
                      ))}
                    </SelectField>
                  </Field>
                  <Field label={t("purchasing.creditAmount")} required>
                    <TextField inputMode="decimal" value={apply.amount} onChange={(e) => { setApply({ ...apply, amount: e.target.value }); }} dir="ltr" data-testid="apply-amount" />
                  </Field>
                </div>
              ) : null}
              <DialogFooter>
                {canHold && !item.paymentBlocked && hold === null ? <Button variant="secondary" onClick={() => { setHold(""); }} data-testid="hold-item">{t("payables.hold")}</Button> : null}
                {hold !== null ? <Button onClick={() => { act.mutate({ action: "hold", id: item.id, reason: hold }); }} loading={act.isPending} disabled={!hold.trim()} data-testid="confirm-hold">{t("payables.hold")}</Button> : null}
                {item.paymentBlocked ? <Button variant="secondary" onClick={() => { act.mutate({ action: "release", id: item.id }); }} loading={act.isPending} data-testid="release-item">{t("payables.release")}</Button> : null}
                {canApply && !apply ? <Button onClick={() => { setApply({ settledItemId: "", amount: String(Math.abs(Number(item.remainingTc))) }); }} data-testid="apply-item">{t("purchasing.applyCredit")}</Button> : null}
                {apply ? <Button onClick={() => { act.mutate({ action: "apply", id: item.id, settledItemId: apply.settledItemId, amount: num(apply.amount) }); }} loading={act.isPending} disabled={!apply.settledItemId || num(apply.amount) <= 0} data-testid="confirm-apply">{t("purchasing.applyCredit")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
