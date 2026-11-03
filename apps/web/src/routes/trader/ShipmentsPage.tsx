import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Check, Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { CurrencyField } from "../CurrencyField";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { useBankAccounts } from "../payables/shared";

type Shipment = components["schemas"]["PurchaseShipmentSummary"];

const stages = ["ordered", "in_production", "shipped", "at_port", "customs", "received"] as const;
const tones: Record<string, "info" | "accent" | "warning" | "success" | "danger" | "neutral"> = { ordered: "info", in_production: "info", shipped: "accent", at_port: "accent", customs: "warning", received: "success", cancelled: "danger" };
const closedOrderStatuses = new Set(["cancelled", "closed", "rejected"]);

function Stage({ status, testId }: { status: string; testId?: string }) {
  const { t } = useTranslation();
  return <Badge tone={tones[status] ?? "neutral"} data-testid={testId}>{t(`trader.shipments.stages.${status}`, { defaultValue: status })}</Badge>;
}

interface TrackForm {
  orderId: string;
  expectedArrival: string;
  trackingReference: string;
}

interface CostForm {
  chargeTypeId: string;
  description: string;
  amount: string;
  currency: string;
  exchangeRate: string;
  paidOn: string;
  partnerId: string;
}

interface PayForm {
  bankAccountId: string;
  amount: string;
  paidRc: string;
  on: string;
}

interface CostPayForm {
  costId: string;
  bankAccountId: string;
  partnerId: string;
  on: string;
}

/**
 * Shipments (trader edition, ADR-0035 §4): every purchase order on its way, with its stages and dates, the costs paid
 * along the way, what was paid for the goods, the landed cost of each unit, and the hints a trader should act on.
 * Arrival posts the goods receipt and lands the costs on the stock; the supplier's invoice is recorded against it.
 * Paying is real (ADR-0035 §11): the goods are paid to the supplier and each cost to its agent from a cash box or bank
 * account, as advances before the invoices and payments after, so the cash and what is owed always agree.
 */
export function ShipmentsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [track, setTrack] = useState<TrackForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/trader/shipments");
  const [nextStage, setNextStage] = useState<{ status: string; occurredOn: string; note: string }>({ status: "", occurredOn: "", note: "" });
  const [cost, setCost] = useState<CostForm | null>(null);
  const [pay, setPay] = useState<PayForm | null>(null);
  const [costPay, setCostPay] = useState<CostPayForm | null>(null);
  const [receive, setReceive] = useState<{ warehouseId: string; date: string }>({ warehouseId: "", date: "" });
  const [invoice, setInvoice] = useState<{ number: string; date: string }>({ number: "", date: "" });
  const warehouses = useWarehouses(companyId);

  const list = useQuery({
    queryKey: ["shipments", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/shipments", { params: { query: { companyId } } })),
  });
  const orders = useQuery({
    queryKey: ["orders", companyId, "for-shipments"],
    enabled: Boolean(companyId) && track !== null,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders", { params: { query: { companyId } } })),
  });
  const chargeTypes = useQuery({ queryKey: ["charge-types"], queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/charge-types")) });
  const accounts = useBankAccounts(companyId);
  const suppliers = useQuery({
    queryKey: ["supplier-accounts", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["shipment", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/shipments/{shipmentId}", { params: { path: { shipmentId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["shipments"] });
    await queryClient.invalidateQueries({ queryKey: ["shipment"] });
    await queryClient.invalidateQueries({ queryKey: ["orders"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const settle = async (): Promise<void> => { setProblem(null); await refresh(); };

  const create = useMutation({
    mutationFn: async (f: TrackForm) => unwrap(await api.POST("/api/v1/purchasing/shipments", { body: { orderId: f.orderId, expectedArrival: f.expectedArrival || null, trackingReference: f.trackingReference || null } })),
    onSuccess: async (created) => { setTrack(null); setOpenId(created.id); await settle(); },
    onError: fail,
  });
  const advance = useMutation({
    mutationFn: async (input: { id: string; status: string; occurredOn: string; note: string }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/advance", { params: { path: { shipmentId: input.id } }, body: { status: input.status, occurredOn: input.occurredOn || null, note: input.note || null } })),
    onSuccess: async () => { setNextStage({ status: "", occurredOn: "", note: "" }); await settle(); },
    onError: fail,
  });
  const addCost = useMutation({
    mutationFn: async (input: { id: string; form: CostForm }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/costs", {
        params: { path: { shipmentId: input.id } },
        body: { chargeTypeId: input.form.chargeTypeId, amount: Number(input.form.amount), currency: input.form.currency || null, exchangeRate: input.form.exchangeRate.trim() ? Number(input.form.exchangeRate) : null, description: input.form.description || null, paidOn: input.form.paidOn || null, partnerId: input.form.partnerId || null },
      })),
    onSuccess: async () => { setCost(null); await settle(); },
    onError: fail,
  });
  const removeCost = useMutation({
    mutationFn: async (input: { id: string; costId: string }) => unwrap(await api.DELETE("/api/v1/purchasing/shipments/{shipmentId}/costs/{costId}", { params: { path: { shipmentId: input.id, costId: input.costId } } })),
    onSuccess: settle,
    onError: fail,
  });
  const moneyMoved = async (): Promise<void> => {
    await settle();
    await Promise.all([["bank-accounts"], ["ap-aging"], ["trader-health"], ["home"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const paySupplier = useMutation({
    mutationFn: async (input: { id: string; form: PayForm }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/pay", {
        params: { path: { shipmentId: input.id } },
        body: { bankAccountId: input.form.bankAccountId, amount: input.form.amount.trim() ? Number(input.form.amount) : null, paidRc: input.form.paidRc.trim() ? Number(input.form.paidRc) : null, paidOn: input.form.on || null },
      })),
    onSuccess: async () => { setPay(null); await moneyMoved(); },
    onError: fail,
  });
  const payCost = useMutation({
    mutationFn: async (input: { id: string; form: CostPayForm }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/costs/{costId}/pay", {
        params: { path: { shipmentId: input.id, costId: input.form.costId } },
        body: { bankAccountId: input.form.bankAccountId, partnerId: input.form.partnerId || null, paidOn: input.form.on || null },
      })),
    onSuccess: async () => { setCostPay(null); await moneyMoved(); },
    onError: fail,
  });
  const createCash = useMutation({
    mutationFn: async (currency: string) => unwrap(await api.POST("/api/v1/banking/bank-accounts", { body: { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency, isActive: true } })),
    onSuccess: async () => { setProblem(null); await queryClient.invalidateQueries({ queryKey: ["bank-accounts"] }); },
    onError: fail,
  });
  const receiveGoods = useMutation({
    mutationFn: async (input: { id: string; warehouseId: string; date: string }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/receive", { params: { path: { shipmentId: input.id } }, body: { warehouseId: input.warehouseId || null, postingDate: input.date || null } })),
    onSuccess: async () => {
      await settle();
      await queryClient.invalidateQueries({ queryKey: ["receipts"] });
      await queryClient.invalidateQueries({ queryKey: ["stock"] });
    },
    onError: fail,
  });
  const recordInvoice = useMutation({
    mutationFn: async (input: { id: string; number: string; date: string }) =>
      unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/invoice", { params: { path: { shipmentId: input.id } }, body: { supplierInvoiceNumber: input.number || null, documentDate: input.date || null } })),
    onSuccess: async () => { setInvoice({ number: "", date: "" }); await settle(); await queryClient.invalidateQueries({ queryKey: ["invoices"] }); },
    onError: fail,
  });
  const cancel = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/purchasing/shipments/{shipmentId}/cancel", { params: { path: { shipmentId: id } } })),
    onSuccess: settle,
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Shipment, unknown>[]>(
    () => [
      { id: "order", accessorKey: "orderNumber", header: t("trader.shipments.order"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.orderNumber}</span> },
      { id: "supplier", accessorFn: (row) => `${row.partnerCode} ${localized(row.partnerName)}`, header: t("trader.shipments.supplier"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.partnerCode} · {localized(row.original.partnerName)}</span> },
      { id: "status", accessorKey: "status", header: t("trader.shipments.stage"), size: 140, cell: ({ row }) => <Stage status={row.original.status} /> },
      { id: "eta", accessorKey: "expectedArrival", header: t("trader.shipments.expectedArrival"), size: 130, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.expectedArrival)}</span> },
      { id: "goods", accessorKey: "goodsTotal", header: t("trader.shipments.goodsTotal"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.goodsTotal, row.original.currency)}</span> },
      { id: "landed", accessorKey: "landedTotalRc", header: t("trader.shipments.landedTotal"), size: 160, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.landedTotalRc, row.original.functionalCurrency)}</span> },
      { id: "hints", accessorFn: (row) => row.hints.length, header: t("trader.shipments.hintsTitle"), size: 120, cell: ({ row }) => (row.original.hints.length > 0 ? <Badge tone="warning">{t("trader.shipments.attention")} · {formatNumber(row.original.hints.length)}</Badge> : null) },
    ],
    [t],
  );

  const s = detail.data;
  const underway = s !== undefined && s.status !== "received" && s.status !== "cancelled";
  const currentIndex = s ? stages.indexOf(s.status as (typeof stages)[number]) : -1;
  const nextStages = stages.filter((stage, index) => index > currentIndex && stage !== "received");
  const eventOf = (stage: string) => s?.events.find((e) => e.status === stage);
  const fc = s?.functionalCurrency ?? "";
  const untracked = (orders.data ?? []).filter((o) => !closedOrderStatuses.has(o.status) && !(list.data ?? []).some((x) => x.orderId === o.id));
  const hintText = (code: string, value: number | string | null): string => {
    const n = value === null ? 0 : Number(value);
    return t(`trader.shipments.hints.${code}`, { defaultValue: code, days: formatNumber(n), pct: formatNumber(n, { maximumFractionDigits: 1 }) });
  };
  const openTrack = (): void => { setProblem(null); setTrack({ orderId: "", expectedArrival: "", trackingReference: "" }); };
  const submitTrack = (event: FormEvent): void => { event.preventDefault(); if (track?.orderId) { create.mutate(track); } };
  const openCost = (): void => { setProblem(null); setCost({ chargeTypeId: chargeTypes.data?.[0]?.id ?? "", description: "", amount: "", currency: fc, exchangeRate: "", paidOn: "", partnerId: "" }); };
  const liveAccounts = (accounts.data ?? []).filter((a) => a.isActive);
  const payAccount = pay ? liveAccounts.find((a) => a.id === pay.bankAccountId) : undefined;
  const needsRc = s !== undefined && s.currency !== fc && payAccount?.currency === fc;
  const goodsPayments = s?.payments.filter((p) => p.kind === "goods") ?? [];
  const supplierOptions = (suppliers.data ?? []).filter((x) => x.partnerId !== s?.partnerId);

  return (
    <>
      <PageHeader
        title={t("trader.shipments.title")}
        description={t("trader.shipments.description")}
        actions={
          <Button onClick={openTrack} disabled={!companyId} data-testid="track-order">
            <Plus aria-hidden="true" />
            {t("trader.shipments.track")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Shipment> label="trader.shipments.title" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("trader.shipments.empty")} emptyDescription={t("trader.shipments.emptyDescription")} onOpen={(row) => { setProblem(null); setCost(null); setPay(null); setCostPay(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(track)} onOpenChange={(isOpen) => { if (!isOpen) { setTrack(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {track ? (
            <form onSubmit={submitTrack} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.shipments.track")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <Field label={t("trader.shipments.order")} required>
                <SelectField value={track.orderId} onChange={(e) => { setTrack({ ...track, orderId: e.target.value }); }} required data-testid="track-order-select">
                  <option value="">{untracked.length === 0 && orders.data ? t("trader.shipments.noUntracked") : t("trader.shipments.chooseOrder")}</option>
                  {untracked.map((o) => (
                    <option key={o.id} value={o.id}>{o.number} · {o.partnerCode} · {formatMoney(o.totalGross, o.currency)}</option>
                  ))}
                </SelectField>
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("trader.shipments.expectedArrival")}>
                  <TextField type="date" value={track.expectedArrival} onChange={(e) => { setTrack({ ...track, expectedArrival: e.target.value }); }} dir="ltr" data-testid="track-eta" />
                </Field>
                <Field label={t("trader.shipments.tracking")}>
                  <TextField value={track.trackingReference} onChange={(e) => { setTrack({ ...track, trackingReference: e.target.value }); }} dir="auto" data-testid="track-reference" />
                </Field>
              </div>
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setTrack(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} disabled={!track.orderId} data-testid="save-track">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {s ? (
            <div className="flex flex-col gap-4" data-testid="shipment-detail">
              <DialogHeader>
                <DialogTitle className="flex flex-wrap items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{s.orderNumber}</span>
                  <span dir="auto" className="font-normal text-fg-muted">{s.partnerCode} · {localized(s.partnerName)}</span>
                  <Stage status={s.status} testId="shipment-stage" />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />

              {s.hints.length > 0 ? (
                <section className="rounded-md border border-warning/40 bg-warning-soft p-3" data-testid="shipment-hints">
                  <h3 className="text-sm font-semibold">{t("trader.shipments.hintsTitle")}</h3>
                  <ul className="mt-1 list-inside list-disc text-sm">
                    {s.hints.map((h) => <li key={h.code} data-testid="shipment-hint">{hintText(h.code, h.value)}</li>)}
                  </ul>
                </section>
              ) : null}

              <section className="flex flex-col gap-2">
                <h3 className="text-sm font-semibold">{t("trader.shipments.timeline")}</h3>
                <ol className="grid gap-2 sm:grid-cols-3 lg:grid-cols-6" data-testid="shipment-timeline">
                  {stages.map((stage, index) => {
                    const event = eventOf(stage);
                    const done = s.status !== "cancelled" && index <= currentIndex;
                    return (
                      <li key={stage} className={`flex flex-col gap-0.5 rounded-md border p-2 text-sm ${index === currentIndex ? "border-accent bg-accent-soft/40" : done ? "border-success/40" : "border-border text-fg-muted"}`} data-testid={`stage-${stage}`} aria-current={index === currentIndex ? "step" : undefined}>
                        <span className="flex items-center gap-1 font-medium">{done ? <Check className="size-3.5 text-success" aria-hidden="true" /> : null}{t(`trader.shipments.stages.${stage}`)}</span>
                        <span dir="ltr" className="text-xs">{event ? formatDate(event.occurredOn) : stage === "received" && s.expectedArrival ? `${t("trader.shipments.expectedArrival")}: ${formatDate(s.expectedArrival)}` : ""}</span>
                        {event?.note ? <span dir="auto" className="text-xs text-fg-muted">{event.note}</span> : null}
                      </li>
                    );
                  })}
                </ol>
                {underway && nextStages.length > 0 ? (
                  <form onSubmit={(e) => { e.preventDefault(); if (nextStage.status) { advance.mutate({ id: s.id, ...nextStage }); } }} className="grid items-end gap-3 sm:grid-cols-[1fr_auto_1fr_auto]" data-testid="next-step-form">
                    <Field label={t("trader.shipments.nextStep")}>
                      <SelectField value={nextStage.status} onChange={(e) => { setNextStage({ ...nextStage, status: e.target.value }); }} data-testid="next-stage">
                        <option value="">{t("trader.shipments.nextStep")}</option>
                        {nextStages.map((stage) => <option key={stage} value={stage}>{t(`trader.shipments.stages.${stage}`)}</option>)}
                      </SelectField>
                    </Field>
                    <Field label={t("trader.shipments.stepDate")}>
                      <TextField type="date" value={nextStage.occurredOn} onChange={(e) => { setNextStage({ ...nextStage, occurredOn: e.target.value }); }} dir="ltr" data-testid="next-stage-date" />
                    </Field>
                    <Field label={t("trader.shipments.stepNote")}>
                      <TextField value={nextStage.note} onChange={(e) => { setNextStage({ ...nextStage, note: e.target.value }); }} dir="auto" data-testid="next-stage-note" />
                    </Field>
                    <Button type="submit" variant="secondary" disabled={!nextStage.status} loading={advance.isPending} data-testid="advance-shipment">{nextStage.status ? t("trader.shipments.moveTo", { stage: t(`trader.shipments.stages.${nextStage.status}`) }) : t("trader.shipments.nextStep")}</Button>
                  </form>
                ) : null}
              </section>

              <section className="grid gap-4 lg:grid-cols-2">
                <div className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("trader.shipments.goods")}</h3>
                  <KeyValues entries={[
                    [t("trader.shipments.goodsTotal"), <span key="g" dir="ltr">{formatMoney(s.goodsTotal, s.currency)}</span>],
                    [t("trader.shipments.atOrderRate"), <span key="r" dir="ltr">{formatMoney(s.goodsTotalRc, fc)} ({formatNumber(s.orderExchangeRate, { maximumFractionDigits: 6 })})</span>],
                    [t("trader.shipments.paid"), <span key="p" dir="ltr" data-testid="paid-summary">{s.paidAmountRc === null ? "—" : `${formatMoney(s.paidAmountRc, fc)}${s.effectiveRate === null ? "" : ` (${formatNumber(s.effectiveRate, { maximumFractionDigits: 6 })})`}`}</span>],
                    [t("trader.shipments.costsTotal"), <span key="c" dir="ltr">{formatMoney(s.costsTotalRc, fc)}</span>],
                    [t("trader.shipments.landedTotal"), <span key="l" dir="ltr" className="font-semibold" data-testid="landed-total">{formatMoney(s.landedTotalRc, fc)}</span>],
                  ]} />
                  {goodsPayments.length > 0 ? (
                    <ul className="flex flex-col gap-1 text-sm" data-testid="goods-payments">
                      {goodsPayments.map((p) => (
                        <li key={p.id} className="flex flex-wrap items-center justify-between gap-2" data-testid="shipment-payment">
                          <span dir="auto">{formatDate(p.paidOn)} · <span dir="ltr">{p.paymentNumber}</span> · {t(`trader.shipments.paymentKinds.${p.paymentKind}`)}</span>
                          <span className="tabular" dir="ltr">{formatMoney(p.amountTc, p.currency)}{p.currency !== fc ? ` = ${formatMoney(p.amountRc, fc)}` : ""}</span>
                        </li>
                      ))}
                    </ul>
                  ) : null}
                  {Number(s.goodsPaidTc) > goodsPayments.reduce((sum, p) => sum + Number(p.amountTc), 0) ? <p className="text-sm" dir="auto" data-testid="paid-elsewhere">{t("trader.shipments.paidElsewhere", { amount: formatMoney(Number(s.goodsPaidTc) - goodsPayments.reduce((sum, p) => sum + Number(p.amountTc), 0), s.currency) })}</p> : null}
                  {s.status !== "cancelled" && Number(s.goodsOutstandingTc) > 0 ? <p className="text-sm" dir="auto" data-testid="goods-outstanding">{t("trader.shipments.leftToPay", { amount: formatMoney(s.goodsOutstandingTc, s.currency) })}</p> : null}
                  {s.status !== "cancelled" && Number(s.goodsOutstandingTc) > 0 ? (
                    pay ? (
                      <form onSubmit={(e) => { e.preventDefault(); paySupplier.mutate({ id: s.id, form: pay }); }} className="grid items-end gap-3 rounded-md border border-border p-3 sm:grid-cols-2" data-testid="payment-form">
                        <Field label={t("trader.shipments.payFrom")} required>
                          <SelectField value={pay.bankAccountId} onChange={(e) => { setPay({ ...pay, bankAccountId: e.target.value }); }} required data-testid="pay-account">
                            <option value="">{t("trader.shipments.chooseAccount")}</option>
                            {liveAccounts.map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)} ({a.currency})</option>)}
                          </SelectField>
                        </Field>
                        <Field label={t("trader.shipments.payAmount", { currency: s.currency })} required>
                          <DecimalField value={pay.amount} onChange={(e) => { setPay({ ...pay, amount: e.target.value }); }} dir="ltr" required data-testid="pay-amount" />
                        </Field>
                        {needsRc ? (
                          <Field label={t("trader.shipments.paidAmount", { currency: fc })} description={t("trader.shipments.paidRcHint")} required>
                            <DecimalField value={pay.paidRc} onChange={(e) => { setPay({ ...pay, paidRc: e.target.value }); }} dir="ltr" required data-testid="paid-amount" />
                          </Field>
                        ) : null}
                        <Field label={t("trader.shipments.paidOn")}>
                          <TextField type="date" value={pay.on} onChange={(e) => { setPay({ ...pay, on: e.target.value }); }} dir="ltr" data-testid="paid-on" />
                        </Field>
                        {liveAccounts.length === 0 ? (
                          <div className="flex flex-wrap items-center gap-2 text-sm sm:col-span-2">
                            <span>{t("trader.sales.noAccounts")}</span>
                            <Button type="button" variant="secondary" size="sm" onClick={() => { createCash.mutate(fc); }} loading={createCash.isPending} data-testid="create-cash">{t("trader.sales.createCash")}</Button>
                          </div>
                        ) : null}
                        <p className="text-xs text-fg-muted sm:col-span-2" dir="auto">{s.invoiceId ? t("trader.shipments.payInvoiceHint") : t("trader.shipments.payAdvanceHint")}</p>
                        <div className="flex justify-end gap-2 sm:col-span-2">
                          <Button type="button" variant="secondary" onClick={() => { setPay(null); }}>{t("common.cancel")}</Button>
                          <Button type="submit" loading={paySupplier.isPending} disabled={!pay.bankAccountId} data-testid="save-payment">{t("trader.shipments.payNow")}</Button>
                        </div>
                      </form>
                    ) : (
                      <div><Button type="button" variant="secondary" size="sm" onClick={() => { setProblem(null); setPay({ bankAccountId: liveAccounts[0]?.id ?? "", amount: String(s.goodsOutstandingTc), paidRc: "", on: "" }); }} data-testid="record-payment">{t("trader.shipments.paySupplier")}</Button></div>
                    )
                  ) : null}
                </div>

                <div className="flex flex-col gap-2">
                  <div className="flex items-center justify-between">
                    <h3 className="text-sm font-semibold">{t("trader.shipments.costs")}</h3>
                    {underway && !cost ? <Button type="button" variant="ghost" size="sm" onClick={openCost} data-testid="add-cost"><Plus aria-hidden="true" />{t("trader.shipments.addCost")}</Button> : null}
                  </div>
                  {s.costs.length === 0 ? <p className="text-sm text-fg-muted">{t("trader.shipments.noCosts")}</p> : (
                    <Table data-testid="shipment-costs">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("trader.shipments.type")}</TableHead>
                          <TableHead className="text-end">{t("trader.shipments.amount")}</TableHead>
                          <TableHead className="text-end">{t("trader.shipments.rate")}</TableHead>
                          <TableHead className="text-end">{t("trader.shipments.inYourCurrency")}</TableHead>
                          <TableHead>{t("trader.shipments.paidTo")}</TableHead>
                          <TableHead />
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {s.costs.map((c, index) => (
                          <TableRow key={c.id} data-testid="cost-row">
                            <TableCell dir="auto">{localized(c.chargeTypeName)}{c.description ? ` · ${c.description}` : ""}</TableCell>
                            <TableCell className="tabular text-end" dir="ltr">{formatMoney(c.amount, c.currency)}</TableCell>
                            <TableCell className="tabular text-end" dir="ltr">{formatNumber(c.exchangeRate, { maximumFractionDigits: 6 })}</TableCell>
                            <TableCell className="tabular text-end" dir="ltr">{formatMoney(c.amountRc, fc)}</TableCell>
                            <TableCell dir="auto">{c.partnerCode ?? "—"}</TableCell>
                            <TableCell>
                              <div className="flex flex-wrap items-center gap-1">
                                {c.paymentNumber ? <Badge tone="success" data-testid="cost-paid">{t("trader.shipments.costPaid", { number: c.paymentNumber })}</Badge> : null}
                                {!c.paymentNumber && s.status !== "cancelled" ? <Button type="button" variant="ghost" size="sm" onClick={() => { setProblem(null); setCostPay({ costId: c.id, bankAccountId: liveAccounts.find((a) => a.currency === fc)?.id ?? "", partnerId: c.partnerId ?? "", on: "" }); }} data-testid={`pay-cost-${String(index)}`}>{t("trader.shipments.payCost")}</Button> : null}
                                {underway && !c.paymentNumber ? <Button type="button" variant="ghost" size="sm" onClick={() => { removeCost.mutate({ id: s.id, costId: c.id }); }} data-testid={`remove-cost-${String(index)}`}>{t("trader.shipments.removeCost")}</Button> : null}
                              </div>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
                  {costPay ? (
                    <form onSubmit={(e) => { e.preventDefault(); payCost.mutate({ id: s.id, form: costPay }); }} className="grid items-end gap-3 rounded-md border border-border p-3 sm:grid-cols-3" data-testid="cost-pay-form">
                      <Field label={t("trader.shipments.paidTo")} required>
                        <SelectField value={costPay.partnerId} onChange={(e) => { setCostPay({ ...costPay, partnerId: e.target.value }); }} required data-testid="cost-pay-partner">
                          <option value="">{t("trader.shipments.choosePaidTo")}</option>
                          {supplierOptions.map((x) => <option key={x.partnerId} value={x.partnerId}>{x.partnerCode} · {localized(x.partnerName)}</option>)}
                        </SelectField>
                      </Field>
                      <Field label={t("trader.shipments.payFrom")} required>
                        <SelectField value={costPay.bankAccountId} onChange={(e) => { setCostPay({ ...costPay, bankAccountId: e.target.value }); }} required data-testid="cost-pay-account">
                          <option value="">{t("trader.shipments.chooseAccount")}</option>
                          {liveAccounts.filter((a) => a.currency === fc).map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
                        </SelectField>
                      </Field>
                      <Field label={t("trader.shipments.paidOn")}>
                        <TextField type="date" value={costPay.on} onChange={(e) => { setCostPay({ ...costPay, on: e.target.value }); }} dir="ltr" data-testid="cost-pay-date" />
                      </Field>
                      <p className="text-xs text-fg-muted sm:col-span-3" dir="auto">{t("trader.shipments.payCostHint")}</p>
                      <div className="flex justify-end gap-2 sm:col-span-3">
                        <Button type="button" variant="secondary" onClick={() => { setCostPay(null); }}>{t("common.cancel")}</Button>
                        <Button type="submit" loading={payCost.isPending} disabled={!costPay.bankAccountId || !costPay.partnerId} data-testid="save-cost-payment">{t("trader.shipments.payNow")}</Button>
                      </div>
                    </form>
                  ) : null}
                  {cost ? (
                    <form onSubmit={(e) => { e.preventDefault(); addCost.mutate({ id: s.id, form: cost }); }} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="cost-form">
                      <div className="grid gap-3 sm:grid-cols-2">
                        <Field label={t("trader.shipments.type")} required>
                          <SelectField value={cost.chargeTypeId} onChange={(e) => { setCost({ ...cost, chargeTypeId: e.target.value }); }} required data-testid="cost-type">
                            {(chargeTypes.data ?? []).filter((c) => c.isActive).map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.name)}</option>)}
                          </SelectField>
                        </Field>
                        <Field label={t("trader.shipments.descriptionCol")}>
                          <TextField value={cost.description} onChange={(e) => { setCost({ ...cost, description: e.target.value }); }} dir="auto" data-testid="cost-description" />
                        </Field>
                        <Field label={t("trader.shipments.amount")} required>
                          <DecimalField value={cost.amount} onChange={(e) => { setCost({ ...cost, amount: e.target.value }); }} dir="ltr" required data-testid="cost-amount" />
                        </Field>
                        <Field label={t("trader.shipments.currency")}>
                          <CurrencyField value={cost.currency} onChange={(code) => { setCost({ ...cost, currency: code }); }} data-testid="cost-currency" />
                        </Field>
                        <Field label={t("trader.shipments.rate")} description={t("trader.shipments.rateHint")}>
                          <DecimalField value={cost.exchangeRate} onChange={(e) => { setCost({ ...cost, exchangeRate: e.target.value }); }} dir="ltr" data-testid="cost-rate" />
                        </Field>
                        <Field label={t("trader.shipments.paidOn")}>
                          <TextField type="date" value={cost.paidOn} onChange={(e) => { setCost({ ...cost, paidOn: e.target.value }); }} dir="ltr" data-testid="cost-paid-on" />
                        </Field>
                        <Field label={t("trader.shipments.paidTo")} description={t("trader.shipments.paidToHint")}>
                          <SelectField value={cost.partnerId} onChange={(e) => { setCost({ ...cost, partnerId: e.target.value }); }} data-testid="cost-partner">
                            <option value="">{t("trader.shipments.choosePaidTo")}</option>
                            {supplierOptions.map((x) => <option key={x.partnerId} value={x.partnerId}>{x.partnerCode} · {localized(x.partnerName)}</option>)}
                          </SelectField>
                        </Field>
                      </div>
                      <div className="flex justify-end gap-2">
                        <Button type="button" variant="secondary" onClick={() => { setCost(null); }}>{t("common.cancel")}</Button>
                        <Button type="submit" loading={addCost.isPending} data-testid="save-cost">{t("trader.shipments.saveCost")}</Button>
                      </div>
                    </form>
                  ) : null}
                </div>
              </section>

              <section className="flex flex-col gap-2">
                <h3 className="text-sm font-semibold">{t("trader.shipments.items")}</h3>
                <Table data-testid="shipment-lines">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("trader.shipments.item")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.quantity")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.unitPrice")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.goodsShare")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.costShare")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.landed")}</TableHead>
                      <TableHead className="text-end">{t("trader.shipments.landedUnit")}</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {s.lines.map((l) => (
                      <TableRow key={l.orderLineId} data-testid="shipment-line">
                        <TableCell dir="auto">{l.itemCode} · {localized(l.itemName)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatMoney(l.unitPrice, s.currency)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatMoney(l.goodsRc, fc)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatMoney(l.costsRc, fc)}</TableCell>
                        <TableCell className="tabular text-end" dir="ltr">{formatMoney(l.landedRc, fc)}</TableCell>
                        <TableCell className="tabular text-end font-semibold" dir="ltr" data-testid="landed-unit">{formatMoney(l.landedUnitCostRc, fc)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </section>

              {underway ? (
                <form onSubmit={(e) => { e.preventDefault(); receiveGoods.mutate({ id: s.id, ...receive }); }} className="flex flex-col gap-3 rounded-md border border-success/40 bg-success-soft p-3" data-testid="receive-form">
                  <h3 className="text-sm font-semibold">{t("trader.shipments.arrived")}</h3>
                  <div className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_auto]">
                    <WarehouseSelect warehouses={warehouses.data ?? []} value={receive.warehouseId} onChange={(id) => { setReceive({ ...receive, warehouseId: id }); }} label={t("trader.shipments.receiveInto")} testId="receive-warehouse" />
                    <Field label={t("trader.shipments.receiveDate")}>
                      <TextField type="date" value={receive.date} onChange={(e) => { setReceive({ ...receive, date: e.target.value }); }} dir="ltr" data-testid="receive-date" />
                    </Field>
                    <Button type="submit" loading={receiveGoods.isPending} data-testid="receive-shipment">{t("trader.shipments.receiveNow")}</Button>
                  </div>
                </form>
              ) : null}
              {s.receiptNumber ? (
                <p className="text-sm" data-testid="received-note">
                  {t("trader.shipments.receivedNote", { number: s.receiptNumber })}{s.landedCostId ? ` ${t("trader.shipments.landedNote")}` : ""}
                </p>
              ) : null}
              {s.status === "received" && s.invoiceId === null ? (
                <form onSubmit={(e) => { e.preventDefault(); recordInvoice.mutate({ id: s.id, ...invoice }); }} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="invoice-form">
                  <h3 className="text-sm font-semibold">{t("trader.shipments.recordInvoice")}</h3>
                  <div className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_auto]">
                    <Field label={t("trader.shipments.supplierInvoiceNumber")}>
                      <TextField value={invoice.number} onChange={(e) => { setInvoice({ ...invoice, number: e.target.value }); }} dir="ltr" data-testid="invoice-number" />
                    </Field>
                    <Field label={t("trader.shipments.invoiceDate")}>
                      <TextField type="date" value={invoice.date} onChange={(e) => { setInvoice({ ...invoice, date: e.target.value }); }} dir="ltr" data-testid="invoice-date" />
                    </Field>
                    <Button type="submit" variant="secondary" loading={recordInvoice.isPending} data-testid="record-invoice">{t("trader.shipments.saveInvoice")}</Button>
                  </div>
                </form>
              ) : null}
              {s.invoiceNumber && s.invoiceStatus ? <p className="text-sm" data-testid="invoice-note">{t("trader.shipments.invoiced", { number: s.invoiceNumber, status: t(`trader.shipments.invoiceStatuses.${s.invoiceStatus}`, { defaultValue: s.invoiceStatus }) })}</p> : null}

              <DialogFooter>
                <Button asChild variant="ghost"><Link to="/purchasing/orders" search={{ open: s.orderId }}>{t("trader.shipments.openOrder")}</Link></Button>
                {underway ? <Button variant="ghost" onClick={() => { cancel.mutate(s.id); }} loading={cancel.isPending} data-testid="cancel-shipment">{t("trader.shipments.cancel")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
