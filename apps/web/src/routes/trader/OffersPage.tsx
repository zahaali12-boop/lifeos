import { Badge, Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import type { TFunction } from "i18next";
import { Plus } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { CurrencyField } from "../CurrencyField";
import { DecimalField, Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, KeyValues, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, num, useSuppliers, type LineForm, type Rfq } from "../purchasing/shared";

type Comparison = components["schemas"]["QuoteComparison"];
type Ranking = components["schemas"]["QuoteRanking"];

interface SetForm {
  title: string;
  partnerIds: string[];
  lines: LineForm[];
}

interface OfferLine {
  price: string;
  moq: string;
  notes: string;
}

interface OfferForm {
  partnerId: string;
  currency: string;
  leadTimeDays: string;
  freightAmount: string;
  otherCharges: string;
  lines: Record<string, OfferLine>;
}

/** What the comparison left on a quote the last time it ran, so a reopened record still shows the rank. */
interface StoredScore {
  rank?: number;
  landedTotalRc?: number;
  meetsMoq?: boolean;
}

const tones: Record<string, "info" | "success" | "neutral" | "danger"> = { draft: "info", sent: "info", awarded: "success", closed: "neutral", cancelled: "danger" };

function OfferSetStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return <Badge tone={tones[status] ?? "neutral"} data-testid="offer-status">{t(`trader.offers.statuses.${status}`, { defaultValue: status })}</Badge>;
}

/** Why a quote is not the one to order from, in plain words. */
function whyNot(r: Ranking, t: TFunction): string[] {
  const notes: string[] = [];
  if (!r.complete) {
    notes.push(t("trader.offers.whyNot.incomplete"));
  }
  if (!r.meetsMoq) {
    notes.push(t("trader.offers.whyNot.moq", { lines: r.moqShortfallLines.join(", ") }));
  }
  const premium = r.premiumPct === null ? null : Number(r.premiumPct);
  if (premium !== null && premium > 0) {
    notes.push(t("trader.offers.whyNot.premium", { pct: formatNumber(premium, { maximumFractionDigits: 1 }) }));
  }
  if (premium !== null && premium < 0) {
    notes.push(t("trader.offers.whyNot.cheaperButBlocked", { pct: formatNumber(-premium, { maximumFractionDigits: 1 }) }));
  }
  return notes;
}

/**
 * Purchase offers (trader edition, ADR-0035): what to buy with quantities, the offers each supplier gave (price, minimum
 * quantity, notes, delivery, shipping), compared landed in the company's currency with the exchange rate of the day,
 * the recommendation with its reasons, the cheapest supplier per item, and "order from this supplier" into a purchase
 * order. It runs over the requests for quotation of the purchasing module; nothing is sent to anyone.
 */
export function OffersPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<SetForm | null>(null);
  const [openId, setOpenId] = useOpenRecord("/trader/offers");
  const [offer, setOffer] = useState<OfferForm | null>(null);
  const [comparison, setComparison] = useState<Comparison | null>(null);
  const [ordered, setOrdered] = useState<{ id: string; number: string; shipmentId: string } | null>(null);
  const suppliers = useSuppliers(companyId);

  const list = useQuery({
    queryKey: ["rfqs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/rfqs", { params: { query: { companyId } } })),
  });
  const detail = useQuery({
    queryKey: ["rfq", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/rfqs/{rfqId}", { params: { path: { rfqId: openId ?? "" } } })),
  });
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["rfqs"] });
    await queryClient.invalidateQueries({ queryKey: ["rfq"] });
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };

  const create = useMutation({
    mutationFn: async (f: SetForm) =>
      unwrap(await api.POST("/api/v1/purchasing/rfqs", {
        body: { companyId, title: f.title || null, partnerIds: f.partnerIds, lines: f.lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null })) },
      })),
    onSuccess: async (created) => { setProblem(null); setForm(null); setComparison(null); setOrdered(null); setOpenId(created.id); await refresh(); },
    onError: fail,
  });
  const compare = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/compare", { params: { path: { rfqId: id } } })),
    onSuccess: async (result) => { setProblem(null); setComparison(result); await refresh(); },
    onError: fail,
  });
  const award = useMutation({
    // The order is tracked as a shipment at once, so the trader follows it from the next screen.
    mutationFn: async (input: { id: string; quoteId: string }) => {
      const order = unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/award", { params: { path: { rfqId: input.id } }, body: { quoteId: input.quoteId } }));
      const shipment = unwrap(await api.POST("/api/v1/purchasing/shipments", { body: { orderId: order.id } }));
      return { id: order.id, number: order.number, shipmentId: shipment.id };
    },
    onSuccess: async (order) => {
      setProblem(null);
      setOrdered(order);
      await queryClient.invalidateQueries({ queryKey: ["orders"] });
      await queryClient.invalidateQueries({ queryKey: ["shipments"] });
      await refresh();
    },
    onError: fail,
  });
  const cancel = useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/cancel", { params: { path: { rfqId: id } } })),
    onSuccess: async () => { setProblem(null); setComparison(null); await refresh(); },
    onError: fail,
  });
  const saveOffer = useMutation({
    mutationFn: async (input: { id: string; form: OfferForm }) =>
      unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/quotes", {
        params: { path: { rfqId: input.id } },
        body: {
          partnerId: input.form.partnerId,
          currency: input.form.currency,
          leadTimeDays: num(input.form.leadTimeDays),
          freightAmount: num(input.form.freightAmount),
          otherCharges: num(input.form.otherCharges),
          lines: Object.entries(input.form.lines)
            .filter(([, line]) => line.price.trim())
            .map(([rfqLineId, line]) => ({ rfqLineId, unitPrice: num(line.price), moq: line.moq.trim() ? num(line.moq) : null, notes: line.notes.trim() || null })),
        },
      })),
    onSuccess: async (_result, input) => {
      setProblem(null);
      setOffer(null);
      await refresh();
      // The comparison runs again at once, so the ranking and the recommendation follow every offer entered.
      compare.mutate(input.id);
    },
    onError: fail,
  });

  // Opening a record that already holds offers compares them, so the recommendation is there without a click.
  const r = detail.data;
  const comparedFor = useRef<string | null>(null);
  const { mutate: runCompare } = compare;
  useEffect(() => {
    if (!r || r.quotes.length === 0 || (r.status !== "draft" && r.status !== "sent") || comparedFor.current === r.id) {
      return;
    }
    comparedFor.current = r.id;
    runCompare(r.id);
  }, [r, runCompare]);

  const columns = useMemo<ColumnDef<Rfq, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "title", accessorKey: "title", header: t("trader.offers.what"), size: 260, cell: ({ row }) => <span dir="auto">{row.original.title ?? ""}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 150, cell: ({ row }) => <OfferSetStatus status={row.original.status} /> },
      { id: "suppliers", accessorFn: (row) => row.suppliers.length, header: t("trader.offers.suppliers"), size: 110, cell: ({ row }) => formatNumber(row.original.suppliers.length) },
      { id: "offers", accessorFn: (row) => row.quotes.length, header: t("trader.offers.offers"), size: 90, cell: ({ row }) => formatNumber(row.original.quotes.length) },
      { id: "updated", accessorKey: "updatedAt", header: t("common.updated"), size: 130, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.updatedAt)}</span> },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ title: "", partnerIds: [], lines: [emptyLine()] }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const openOffer = (partnerId: string): void => {
    if (!r) {
      return;
    }
    const account = suppliers.data?.find((s) => s.partnerId === partnerId);
    const existing = r.quotes.find((q) => q.partnerId === partnerId);
    setProblem(null);
    setOffer({
      partnerId,
      currency: existing?.currency ?? account?.currency ?? "",
      leadTimeDays: String(existing?.leadTimeDays ?? account?.leadTimeDays ?? 0),
      freightAmount: existing ? String(existing.freightAmount) : "0",
      otherCharges: existing ? String(existing.otherCharges) : "0",
      lines: Object.fromEntries(r.lines.map((l) => {
        const quoted = existing?.lines.find((q) => q.rfqLineId === l.id);
        return [l.id, { price: quoted ? String(quoted.unitPrice) : "", moq: quoted?.moq != null ? String(quoted.moq) : "", notes: quoted?.notes ?? "" }];
      })),
    });
  };
  const collecting = r?.status === "draft" || r?.status === "sent";
  const recommendation = comparison?.rfqId === r?.id ? comparison?.recommendation ?? null : null;
  const rankingOf = (quoteId: string): Ranking | undefined => (comparison?.rfqId === r?.id ? comparison?.rankings.find((x) => x.quoteId === quoteId) : undefined);
  const lineLabel = (lineNo: number | string): string => {
    const line = r?.lines.find((l) => String(l.lineNo) === String(lineNo));
    return line ? `${String(lineNo)}. ${line.itemCode ?? line.description ?? ""}` : String(lineNo);
  };

  return (
    <>
      <PageHeader
        title={t("trader.offers.title")}
        description={t("trader.offers.description")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-offer-set">
            <Plus aria-hidden="true" />
            {t("trader.offers.newSet")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Rfq> label="trader.offers.title" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("trader.offers.empty")} emptyDescription={t("trader.offers.emptyDescription")} onOpen={(row) => { setProblem(null); setComparison(null); setOrdered(null); setOffer(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("trader.offers.newSet")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <Field label={t("trader.offers.what")} description={t("trader.offers.whatHint")}>
                <TextField value={form.title} onChange={(e) => { setForm({ ...form, title: e.target.value }); }} data-testid="offer-title" />
              </Field>
              <fieldset className="flex flex-col gap-1">
                <legend className="text-sm font-semibold">{t("trader.offers.suppliers")}</legend>
                {(suppliers.data ?? []).length === 0 ? <p className="text-sm text-fg-muted">{t("trader.offers.noSuppliers")}</p> : null}
                <div className="flex flex-wrap gap-3">
                  {(suppliers.data ?? []).map((s) => (
                    <label key={s.partnerId} className="flex items-center gap-2 text-sm">
                      <input type="checkbox" checked={form.partnerIds.includes(s.partnerId)} onChange={(e) => { setForm({ ...form, partnerIds: e.target.checked ? [...form.partnerIds, s.partnerId] : form.partnerIds.filter((id) => id !== s.partnerId) }); }} data-testid={`offer-supplier-${s.partnerCode}`} />
                      <span dir="auto">{s.partnerCode} · {localized(s.partnerName)}</span>
                    </label>
                  ))}
                </div>
              </fieldset>
              <LinesEditor lines={form.lines} onChange={(lines) => { setForm({ ...form, lines }); }} showPrice={false} showDescription />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-offer-set">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="offers-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="auto">{r.title ?? r.number}</span>
                  <OfferSetStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[[t("purchasing.number"), <span key="n" dir="ltr">{r.number}</span>], [t("trader.offers.suppliers"), formatNumber(r.suppliers.length)], [t("trader.offers.offers"), formatNumber(r.quotes.length)]]} />

              <section className="flex flex-col gap-2">
                <h3 className="text-sm font-semibold">{t("trader.offers.items")}</h3>
                <LinesTable lines={r.lines} testId="offer-items" />
              </section>

              {ordered ? (
                <p className="rounded-md border border-success/40 bg-success-soft p-3 text-sm" data-testid="ordered-note">
                  {t("trader.offers.ordered", { number: ordered.number })}{" "}
                  <Link to="/trader/shipments" search={{ open: ordered.shipmentId }} className="font-medium text-accent underline">{t("trader.offers.followShipment")}</Link>{" · "}
                  <Link to="/purchasing/orders" search={{ open: ordered.id }} className="font-medium text-accent underline">{t("trader.offers.openOrder")}</Link>
                </p>
              ) : null}

              {recommendation && collecting ? (
                <section className="flex flex-col gap-1 rounded-md border border-accent/40 bg-accent-soft/40 p-3" data-testid="recommendation">
                  <h3 className="text-base font-semibold">{t("trader.offers.recommendTitle", { supplier: `${recommendation.partnerCode} · ${localized(comparison?.rankings.find((x) => x.quoteId === recommendation.quoteId)?.partnerName ?? {})}` })}</h3>
                  {recommendation.reasons.length > 0 ? <p className="text-sm">{t("trader.offers.reasonsLead", { reasons: recommendation.reasons.map((code) => t(`trader.offers.reasons.${code}`, { defaultValue: code })).join(t("trader.offers.reasonJoin")) })}</p> : null}
                  {recommendation.savingVsNextRc !== null && comparison ? <p className="text-sm" data-testid="saving-vs-next">{t("trader.offers.savingVsNext", { amount: formatMoney(recommendation.savingVsNextRc, comparison.currency) })}</p> : null}
                  {recommendation.splitSavingRc !== null && comparison ? <p className="text-sm" data-testid="split-saving">{t("trader.offers.splitSaving", { amount: formatMoney(recommendation.splitSavingRc, comparison.currency) })}</p> : null}
                </section>
              ) : null}

              <section className="flex flex-col gap-2">
                <h3 className="text-sm font-semibold">{t("trader.offers.offers")}</h3>
                <Table data-testid="offers-table">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("trader.offers.supplier")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                      <TableHead className="text-end">{t("trader.offers.landedTotal")}</TableHead>
                      <TableHead className="text-end">{t("trader.offers.inYourCurrency")}</TableHead>
                      <TableHead className="text-end">{t("trader.offers.perUnit")}</TableHead>
                      <TableHead className="text-end">{t("trader.offers.leadTime")}</TableHead>
                      <TableHead>{t("trader.offers.moq")}</TableHead>
                      <TableHead className="text-end">{t("trader.offers.rank")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {r.suppliers.map((s) => {
                      const quote = r.quotes.find((q) => q.id === s.quoteId);
                      const ranking = quote ? rankingOf(quote.id) : undefined;
                      const stored: StoredScore | null = quote?.comparison ?? null;
                      const rank = ranking?.rank ?? stored?.rank;
                      const meetsMoq = ranking?.meetsMoq ?? stored?.meetsMoq;
                      const isRecommended = recommendation?.quoteId === quote?.id && collecting;
                      const isAwarded = r.awardedQuoteId !== null && r.awardedQuoteId === quote?.id;
                      const notes = ranking && rank !== 1 ? whyNot(ranking, t) : [];
                      return (
                        <TableRow key={s.id} data-testid="offer-row" className={isRecommended ? "bg-accent-soft/30" : undefined}>
                          <TableCell dir="auto">
                            <div className="flex flex-col">
                              <span>{s.partnerCode} · {localized(s.partnerName)}</span>
                              {notes.length > 0 ? <span className="text-xs text-fg-muted" data-testid="offer-why-not">{notes.join(t("trader.offers.reasonJoin"))}</span> : null}
                            </div>
                          </TableCell>
                          <TableCell>
                            {isAwarded ? <Badge tone="success">{t("trader.offers.statuses.awarded")}</Badge> : quote ? <Badge tone={isRecommended ? "accent" : "neutral"}>{isRecommended ? t("trader.offers.recommended") : t("trader.offers.offerStatus.received")}</Badge> : <Badge tone="neutral">{t("trader.offers.offerStatus.waiting")}</Badge>}
                          </TableCell>
                          <TableCell className="tabular text-end" dir="ltr">{quote ? formatMoney(quote.landedTotal, quote.currency) : ""}</TableCell>
                          <TableCell className="tabular text-end" dir="ltr" data-testid="offer-landed-rc">{ranking && comparison ? formatMoney(ranking.landedTotalRc, comparison.currency) : stored?.landedTotalRc !== undefined && comparison ? formatMoney(stored.landedTotalRc, comparison.currency) : ""}</TableCell>
                          <TableCell className="tabular text-end" dir="ltr">{ranking && comparison ? formatMoney(ranking.landedUnitAverageRc, comparison.currency) : ""}</TableCell>
                          <TableCell className="tabular text-end" dir="ltr">{quote ? formatNumber(quote.leadTimeDays) : ""}</TableCell>
                          <TableCell>{quote && meetsMoq !== undefined ? <Badge tone={meetsMoq ? "success" : "warning"} data-testid="offer-moq">{meetsMoq ? t("trader.offers.moqOk") : t("trader.offers.moqShort")}</Badge> : null}</TableCell>
                          <TableCell className="tabular text-end" dir="ltr" data-testid="offer-rank">{rank !== undefined ? formatNumber(rank) : ""}</TableCell>
                          <TableCell>
                            <div className="flex flex-wrap justify-end gap-1">
                              {collecting ? <Button type="button" variant="ghost" size="sm" onClick={() => { openOffer(s.partnerId); }} data-testid={`enter-offer-${s.partnerCode}`}>{quote ? t("trader.offers.changeOffer") : t("trader.offers.enterOffer")}</Button> : null}
                              {collecting && quote ? <Button type="button" variant={isRecommended ? "primary" : "secondary"} size="sm" onClick={() => { award.mutate({ id: r.id, quoteId: quote.id }); }} loading={award.isPending} data-testid={`order-from-${s.partnerCode}`}>{t("trader.offers.orderFrom")}</Button> : null}
                            </div>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
                {comparison?.rfqId === r.id ? <p className="text-xs text-fg-muted" data-testid="compared-note">{t("trader.offers.comparedNote", { currency: comparison.currency, date: formatDate(comparison.rateDate) })}</p> : null}
                {r.quotes.length === 0 ? <p className="text-sm text-fg-muted">{t("trader.offers.noOffersYet")}</p> : null}
              </section>

              {comparison?.rfqId === r.id && comparison.bestPerLine.length > 0 && r.quotes.length > 1 && collecting ? (
                <section className="flex flex-col gap-2">
                  <h3 className="text-sm font-semibold">{t("trader.offers.bestPerLine")}</h3>
                  <Table data-testid="best-per-line">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("trader.offers.line")}</TableHead>
                        <TableHead>{t("trader.offers.bestSupplier")}</TableHead>
                        <TableHead className="text-end">{t("trader.offers.unitPrice")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {comparison.bestPerLine.map((b) => (
                        <TableRow key={b.lineNo} data-testid="best-line-row">
                          <TableCell dir="auto">{lineLabel(b.lineNo)}</TableCell>
                          <TableCell dir="auto">{b.partnerCode}</TableCell>
                          <TableCell className="tabular text-end" dir="ltr">{formatMoney(b.landedUnitPriceRc, comparison.currency)}</TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </section>
              ) : null}

              {offer ? (
                <form onSubmit={(e) => { e.preventDefault(); saveOffer.mutate({ id: r.id, form: offer }); }} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="offer-form">
                  <h3 className="text-sm font-semibold">{t("trader.offers.offerFrom", { supplier: r.suppliers.find((s) => s.partnerId === offer.partnerId)?.partnerCode ?? "" })}</h3>
                  <div className="grid gap-3 sm:grid-cols-4">
                    <Field label={t("trader.offers.currency")} required>
                      <CurrencyField value={offer.currency} onChange={(code) => { setOffer({ ...offer, currency: code }); }} required data-testid="offer-currency" />
                    </Field>
                    <Field label={t("trader.offers.leadTime")}>
                      <TextField type="number" min={0} value={offer.leadTimeDays} onChange={(e) => { setOffer({ ...offer, leadTimeDays: e.target.value }); }} dir="ltr" data-testid="offer-lead-time" />
                    </Field>
                    <Field label={t("trader.offers.shipping")}>
                      <DecimalField value={offer.freightAmount} onChange={(e) => { setOffer({ ...offer, freightAmount: e.target.value }); }} dir="ltr" data-testid="offer-freight" />
                    </Field>
                    <Field label={t("trader.offers.otherCharges")}>
                      <DecimalField value={offer.otherCharges} onChange={(e) => { setOffer({ ...offer, otherCharges: e.target.value }); }} dir="ltr" data-testid="offer-other" />
                    </Field>
                  </div>
                  <Table data-testid="offer-lines">
                    <TableHeader>
                      <TableRow>
                        <TableHead>{t("trader.offers.line")}</TableHead>
                        <TableHead className="text-end">{t("purchasing.quantity")}</TableHead>
                        <TableHead>{t("trader.offers.price")}</TableHead>
                        <TableHead>{t("trader.offers.moq")}</TableHead>
                        <TableHead>{t("trader.offers.notes")}</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {r.lines.map((l, index) => {
                        const line = offer.lines[l.id] ?? { price: "", moq: "", notes: "" };
                        const patch = (change: Partial<OfferLine>): void => { setOffer({ ...offer, lines: { ...offer.lines, [l.id]: { ...line, ...change } } }); };
                        return (
                          <TableRow key={l.id}>
                            <TableCell dir="auto">{String(l.lineNo)}. {l.itemCode ?? l.description ?? ""}</TableCell>
                            <TableCell className="tabular text-end" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
                            <TableCell><DecimalField aria-label={`${t("trader.offers.price")} ${String(l.lineNo)}`} value={line.price} onChange={(e) => { patch({ price: e.target.value }); }} dir="ltr" className="w-28" data-testid={`offer-price-${String(index)}`} /></TableCell>
                            <TableCell><DecimalField aria-label={`${t("trader.offers.moq")} ${String(l.lineNo)}`} value={line.moq} onChange={(e) => { patch({ moq: e.target.value }); }} dir="ltr" className="w-24" data-testid={`offer-moq-${String(index)}`} /></TableCell>
                            <TableCell><TextField aria-label={`${t("trader.offers.notes")} ${String(l.lineNo)}`} value={line.notes} onChange={(e) => { patch({ notes: e.target.value }); }} dir="auto" data-testid={`offer-note-${String(index)}`} /></TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>
                  <div className="flex justify-end gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setOffer(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={saveOffer.isPending} data-testid="save-offer">{t("trader.offers.saveOffer")}</Button>
                  </div>
                </form>
              ) : null}

              <DialogFooter>
                {collecting && r.quotes.length > 0 ? <Button variant="secondary" onClick={() => { compare.mutate(r.id); }} loading={compare.isPending} data-testid="compare-offers">{t("trader.offers.compare")}</Button> : null}
                {collecting ? <Button variant="ghost" onClick={() => { cancel.mutate(r.id); }} loading={cancel.isPending} data-testid="cancel-offers">{t("trader.offers.cancel")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
