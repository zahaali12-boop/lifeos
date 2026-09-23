import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext } from "../inventory/shared";
import { emptyLine, LinesEditor, LinesTable, num, PurchaseStatus, useSuppliers, type LineForm, type Rfq } from "./shared";
import { RecordDiscussion, RecordHistory } from "../RecordDiscussion";

type Comparison = components["schemas"]["QuoteComparison"];

interface RfqForm {
  title: string;
  dueOn: string;
  partnerIds: string[];
  lines: LineForm[];
}

interface QuoteForm {
  partnerId: string;
  currency: string;
  leadTimeDays: string;
  freightAmount: string;
  prices: Record<string, string>;
}

/** Requests for quotation (roadmap 4.2): lines sent to invited suppliers, their quotes recorded, compared by landed price in the company's currency then lead time, and one awarded into a purchase order. */
export function RfqsPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [form, setForm] = useState<RfqForm | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [tab, setTab] = useState("lines");
  const [quote, setQuote] = useState<QuoteForm | null>(null);
  const [comparison, setComparison] = useState<Comparison | null>(null);
  const [awarded, setAwarded] = useState<string | null>(null);
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
    mutationFn: async (f: RfqForm) =>
      unwrap(await api.POST("/api/v1/purchasing/rfqs", {
        body: { companyId, title: f.title || null, dueOn: f.dueOn || null, partnerIds: f.partnerIds, lines: f.lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null })) },
      })),
    onSuccess: async (created) => { setProblem(null); setForm(null); setOpenId(created.id); await refresh(); },
    onError: fail,
  });
  const act = useMutation({
    mutationFn: async (input: { id: string; action: "send" | "close" | "cancel" | "compare" | "award"; quoteId?: string }) => {
      const params = { path: { rfqId: input.id } };
      switch (input.action) {
        case "send": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/send", { params, body: {} }));
        case "close": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/close", { params }));
        case "cancel": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/cancel", { params }));
        case "compare": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/compare", { params }));
        case "award": return unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/award", { params, body: { quoteId: input.quoteId ?? "" } }));
      }
    },
    onSuccess: async (result, input) => {
      setProblem(null);
      if (input.action === "compare" && "rankings" in result) {
        setComparison(result);
      }
      if (input.action === "award" && "number" in result) {
        setAwarded(result.number);
        await queryClient.invalidateQueries({ queryKey: ["orders"] });
      }
      await refresh();
    },
    onError: fail,
  });
  const recordQuote = useMutation({
    mutationFn: async (input: { id: string; form: QuoteForm }) =>
      unwrap(await api.POST("/api/v1/purchasing/rfqs/{rfqId}/quotes", {
        params: { path: { rfqId: input.id } },
        body: { partnerId: input.form.partnerId, currency: input.form.currency, leadTimeDays: num(input.form.leadTimeDays), freightAmount: num(input.form.freightAmount), otherCharges: 0, lines: Object.entries(input.form.prices).filter(([, price]) => price.trim()).map(([rfqLineId, price]) => ({ rfqLineId, unitPrice: num(price) })) },
      })),
    onSuccess: async () => { setProblem(null); setQuote(null); setComparison(null); await refresh(); },
    onError: fail,
  });

  const columns = useMemo<ColumnDef<Rfq, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("purchasing.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "title", accessorKey: "title", header: t("purchasing.title"), size: 220, cell: ({ row }) => <span dir="auto">{row.original.title ?? ""}</span> },
      { id: "status", accessorKey: "status", header: t("common.status"), size: 120, cell: ({ row }) => <PurchaseStatus status={row.original.status} /> },
      { id: "due", accessorKey: "dueOn", header: t("purchasing.dueOn"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.dueOn)}</span> },
      { id: "suppliers", accessorFn: (row) => row.suppliers.length, header: t("purchasing.suppliersInvited"), size: 110, cell: ({ row }) => String(row.original.suppliers.length) },
      { id: "quotes", accessorFn: (row) => row.quotes.length, header: t("purchasing.quotes"), size: 90, cell: ({ row }) => String(row.original.quotes.length) },
    ],
    [t],
  );

  const openNew = (): void => { setProblem(null); setForm({ title: "", dueOn: "", partnerIds: [], lines: [emptyLine()] }); };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { create.mutate(form); } };
  const r = detail.data;
  const openQuote = (partnerId: string): void => {
    if (!r) {
      return;
    }
    const account = suppliers.data?.find((s) => s.partnerId === partnerId);
    setProblem(null);
    setQuote({ partnerId, currency: account?.currency ?? "", leadTimeDays: String(account?.leadTimeDays ?? 0), freightAmount: "0", prices: Object.fromEntries(r.lines.map((l) => [l.id, ""])) });
  };

  return (
    <>
      <PageHeader
        title={t("nav.rfqs")}
        description={t("purchasing.rfqsDescription")}
        actions={
          <Button onClick={openNew} disabled={!companyId} data-testid="new-rfq">
            <Plus aria-hidden="true" />
            {t("purchasing.newRfq")}
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <DataGrid<Rfq> label="nav.rfqs" columns={columns} data={list.data ?? []} rowKey={(row) => row.id} loading={list.isPending && Boolean(companyId)} emptyTitle={t("purchasing.emptyRfqs")} emptyDescription={t("purchasing.emptyRfqsDescription")} onOpen={(row) => { setProblem(null); setTab("lines"); setComparison(null); setAwarded(null); setQuote(null); setOpenId(row.id); }} />

      <Dialog open={Boolean(form)} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {form ? (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <DialogHeader>
                <DialogTitle className="text-lg font-semibold">{t("purchasing.newRfq")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label={t("purchasing.title")}>
                  <TextField value={form.title} onChange={(e) => { setForm({ ...form, title: e.target.value }); }} data-testid="rfq-title" />
                </Field>
                <Field label={t("purchasing.dueOn")}>
                  <TextField type="date" value={form.dueOn} onChange={(e) => { setForm({ ...form, dueOn: e.target.value }); }} dir="ltr" data-testid="rfq-due" />
                </Field>
              </div>
              <fieldset className="flex flex-col gap-1">
                <legend className="text-sm font-semibold">{t("purchasing.inviteSuppliers")}</legend>
                <div className="flex flex-wrap gap-3">
                  {(suppliers.data ?? []).map((s) => (
                    <label key={s.partnerId} className="flex items-center gap-2 text-sm">
                      <input type="checkbox" checked={form.partnerIds.includes(s.partnerId)} onChange={(e) => { setForm({ ...form, partnerIds: e.target.checked ? [...form.partnerIds, s.partnerId] : form.partnerIds.filter((id) => id !== s.partnerId) }); }} data-testid={`invite-${s.partnerCode}`} />
                      <span dir="auto">{s.partnerCode} · {localized(s.partnerName)}</span>
                    </label>
                  ))}
                </div>
              </fieldset>
              <LinesEditor lines={form.lines} onChange={(lines) => { setForm({ ...form, lines }); }} showPrice={false} showDescription />
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={create.isPending} data-testid="save-rfq">{t("common.save")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-5xl">
          {r ? (
            <div className="flex flex-col gap-4" data-testid="rfq-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{r.number}</span>
                  <PurchaseStatus status={r.status} />
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[[t("purchasing.title"), r.title ?? "—"], [t("purchasing.dueOn"), formatDate(r.dueOn) || "—"]]} />
              <Tabs tabs={[{ id: "lines", label: t("purchasing.lines"), testId: "tab-lines" }, { id: "suppliers", label: t("purchasing.suppliersInvited"), testId: "tab-suppliers" }, { id: "quotes", label: t("purchasing.quotes"), testId: "tab-quotes" }, { id: "discussion", label: t("comments.tab"), testId: "tab-discussion" }, { id: "history", label: t("history.tab"), testId: "tab-history" }]} value={tab} onChange={setTab} />
              {tab === "discussion" ? <RecordDiscussion entityType="purchase_rfq" entityId={r.id} /> : null}
              {tab === "history" ? <RecordHistory entityType="purchase_rfq" entityId={r.id} /> : null}
              {tab === "lines" ? <LinesTable lines={r.lines} testId="rfq-lines" /> : null}
              {tab === "suppliers" ? (
                <Table data-testid="rfq-suppliers">
                  <TableHeader>
                    <TableRow>
                      <TableHead>{t("partners.supplier")}</TableHead>
                      <TableHead>{t("partners.email")}</TableHead>
                      <TableHead>{t("purchasing.sentAt")}</TableHead>
                      <TableHead>{t("common.status")}</TableHead>
                      <TableHead />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {r.suppliers.map((s) => (
                      <TableRow key={s.id} data-testid="rfq-supplier-row">
                        <TableCell dir="auto">{s.partnerCode} · {localized(s.partnerName)}</TableCell>
                        <TableCell dir="ltr">{s.contactEmail ?? "—"}</TableCell>
                        <TableCell dir="ltr">{formatDateTime(s.sentAt)}</TableCell>
                        <TableCell><PurchaseStatus status={s.status} /></TableCell>
                        <TableCell>
                          {(r.status === "sent" || r.status === "draft") && !s.quoteId ? (
                            <Button type="button" variant="ghost" size="sm" onClick={() => { openQuote(s.partnerId); }} data-testid={`record-quote-${s.partnerCode}`}>{t("purchasing.recordQuote")}</Button>
                          ) : null}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              ) : null}
              {tab === "quotes" ? (
                <div className="flex flex-col gap-3">
                  {r.quotes.length === 0 ? <p className="text-sm text-fg-muted">{t("purchasing.noQuotes")}</p> : (
                    <Table data-testid="rfq-quotes">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("partners.supplier")}</TableHead>
                          <TableHead>{t("partners.currency")}</TableHead>
                          <TableHead>{t("purchasing.goodsTotal")}</TableHead>
                          <TableHead>{t("purchasing.landedTotal")}</TableHead>
                          <TableHead>{t("purchasing.leadTimeDays")}</TableHead>
                          <TableHead>{t("purchasing.rank")}</TableHead>
                          <TableHead />
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {r.quotes.map((q) => {
                          const ranking = comparison?.rankings.find((x) => x.quoteId === q.id);
                          return (
                            <TableRow key={q.id} data-testid="quote-row">
                              <TableCell dir="auto">{q.partnerCode} · {localized(q.partnerName)}</TableCell>
                              <TableCell dir="ltr">{q.currency}</TableCell>
                              <TableCell className="tabular" dir="ltr">{formatMoney(q.goodsTotal, q.currency)}</TableCell>
                              <TableCell className="tabular" dir="ltr">{formatMoney(q.landedTotal, q.currency)}{ranking ? ` = ${formatMoney(ranking.landedTotalRc, comparison?.currency ?? q.currency)}` : ""}</TableCell>
                              <TableCell className="tabular" dir="ltr">{String(q.leadTimeDays)}</TableCell>
                              <TableCell className="tabular" dir="ltr" data-testid="quote-rank">{ranking ? String(ranking.rank) : ""}</TableCell>
                              <TableCell>
                                {r.status === "sent" ? <Button type="button" variant="ghost" size="sm" onClick={() => { act.mutate({ id: r.id, action: "award", quoteId: q.id }); }} loading={act.isPending} data-testid={`award-${q.partnerCode}`}>{t("purchasing.award")}</Button> : null}
                              </TableCell>
                            </TableRow>
                          );
                        })}
                      </TableBody>
                    </Table>
                  )}
                  {comparison ? <p className="text-xs text-fg-muted" data-testid="comparison-note">{t("purchasing.comparisonNote", { currency: comparison.currency, date: formatDate(comparison.rateDate) })}</p> : null}
                  {awarded ? <p className="text-sm" data-testid="awarded-order">{t("purchasing.awardedInto", { number: awarded })}</p> : null}
                </div>
              ) : null}
              {quote ? (
                <form onSubmit={(e) => { e.preventDefault(); recordQuote.mutate({ id: r.id, form: quote }); }} className="flex flex-col gap-3 rounded-md border border-border p-3" data-testid="quote-form">
                  <h3 className="text-sm font-semibold">{t("purchasing.recordQuoteFor", { supplier: r.suppliers.find((s) => s.partnerId === quote.partnerId)?.partnerCode ?? "" })}</h3>
                  <div className="grid gap-3 sm:grid-cols-3">
                    <Field label={t("partners.currency")} required>
                      <TextField value={quote.currency} onChange={(e) => { setQuote({ ...quote, currency: e.target.value.toUpperCase() }); }} dir="ltr" maxLength={3} required data-testid="quote-currency" />
                    </Field>
                    <Field label={t("purchasing.leadTimeDays")}>
                      <TextField type="number" min={0} value={quote.leadTimeDays} onChange={(e) => { setQuote({ ...quote, leadTimeDays: e.target.value }); }} dir="ltr" data-testid="quote-lead-time" />
                    </Field>
                    <Field label={t("purchasing.freight")}>
                      <TextField inputMode="decimal" value={quote.freightAmount} onChange={(e) => { setQuote({ ...quote, freightAmount: e.target.value }); }} dir="ltr" data-testid="quote-freight" />
                    </Field>
                    {r.lines.map((l, index) => (
                      <Field key={l.id} label={`${String(l.lineNo)}. ${l.itemCode ?? l.description ?? ""} × ${formatNumber(l.quantity, { maximumFractionDigits: 3 })} ${l.uomCode}`}>
                        <TextField inputMode="decimal" value={quote.prices[l.id] ?? ""} onChange={(e) => { setQuote({ ...quote, prices: { ...quote.prices, [l.id]: e.target.value } }); }} dir="ltr" data-testid={`quote-price-${String(index)}`} />
                      </Field>
                    ))}
                  </div>
                  <div className="flex justify-end gap-2">
                    <Button type="button" variant="secondary" onClick={() => { setQuote(null); }}>{t("common.cancel")}</Button>
                    <Button type="submit" loading={recordQuote.isPending} data-testid="save-quote">{t("purchasing.saveQuote")}</Button>
                  </div>
                </form>
              ) : null}
              <DialogFooter>
                {r.status === "draft" || (r.status === "sent" && r.suppliers.some((s) => !s.sentAt)) ? <Button onClick={() => { act.mutate({ id: r.id, action: "send" }); }} loading={act.isPending} data-testid="send-rfq">{t("purchasing.sendRfq")}</Button> : null}
                {r.quotes.length > 0 && r.status === "sent" ? <Button variant="secondary" onClick={() => { setTab("quotes"); act.mutate({ id: r.id, action: "compare" }); }} loading={act.isPending} data-testid="compare-quotes">{t("purchasing.compare")}</Button> : null}
                {r.status === "sent" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "close" }); }} loading={act.isPending} data-testid="close-rfq">{t("purchasing.close")}</Button> : null}
                {r.status === "draft" || r.status === "sent" ? <Button variant="secondary" onClick={() => { act.mutate({ id: r.id, action: "cancel" }); }} loading={act.isPending} data-testid="cancel-rfq">{t("purchasing.cancelDocument")}</Button> : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
