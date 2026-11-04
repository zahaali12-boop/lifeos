import { Badge, Button, Checkbox, Dialog, DialogContent, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Link } from "@tanstack/react-router";
import { Plus, Store } from "lucide-react";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { useOpenRecord } from "../../lib/documents";
import { formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, KeyValues, Tabs, useCompanyContext, useWarehouses } from "../inventory/shared";
import { PrintButton } from "../print/PrintDialog";
import { RecordActivity } from "../RecordDiscussion";

type Register = components["schemas"]["PosRegisterSummary"];
type Session = components["schemas"]["PosSessionSummary"];
type Order = components["schemas"]["PosOrderSummary"];

interface RegisterForm {
  id: string | null;
  code: string;
  nameEn: string;
  nameAr: string;
  warehouseId: string;
  binId: string;
  cashAccountId: string;
  cardAccountId: string;
  priceListId: string;
  defaultPartnerId: string;
  allowCredit: boolean;
  allowPriceOverride: boolean;
  cashRoundingIncrement: string;
  footerEn: string;
  footerAr: string;
  isActive: boolean;
}

const emptyForm = (): RegisterForm => ({ id: null, code: "", nameEn: "", nameAr: "", warehouseId: "", binId: "", cashAccountId: "", cardAccountId: "", priceListId: "", defaultPartnerId: "", allowCredit: false, allowPriceOverride: false, cashRoundingIncrement: "", footerEn: "", footerAr: "", isActive: true });

function Status({ status, prefix }: { status: string; prefix: "sessionStatuses" | "kinds" | "einvoiceStatuses" }) {
  const { t } = useTranslation();
  const tone = status === "open" || status === "reported" || status === "cleared" ? "success" : status === "closed" || status === "sale" ? "neutral" : status === "refund" || status === "pending" ? "warning" : status === "rejected" || status === "failed" ? "danger" : "info";
  return <Badge tone={tone} data-testid="doc-status">{t(`pos.${prefix}.${status}`, { defaultValue: status })}</Badge>;
}

/** The QR of an e-invoiced receipt, fetched with the session so the image needs no public address. */
function QrImage({ submissionId, label }: { submissionId: string; label: string }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    let created: string | null = null;
    void api.GET("/api/v1/einvoicing/submissions/{submissionId}/qr.svg", { params: { path: { submissionId } }, parseAs: "blob" }).then((r) => {
      if (active && r.data) {
        created = URL.createObjectURL(r.data);
        setUrl(created);
      }
    });
    return () => { active = false; if (created) { URL.revokeObjectURL(created); } };
  }, [submissionId]);
  return url ? <img src={url} alt={label} width={160} height={160} className="rounded-md border border-border bg-white p-1" data-testid="pos-order-qr" /> : null;
}

/**
 * The back office of the point of sale (A-180): the registers of a company and what each needs (warehouse, cash box,
 * card account, price list, walk-in customer, rounding, what the cashier may do), the sessions with their takings and
 * cash count, and every order the tills synced with its lines, tenders, journal and e-invoice.
 */
export function PosPage() {
  const { t } = useTranslation();
  const can = useCan();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const canManage = can("pos.register.manage");
  const [tab, setTab] = useState("registers");
  const [openId, setOpenId] = useOpenRecord("/sales/pos");
  const [form, setForm] = useState<RegisterForm | null>(null);
  const [problem, setProblem] = useState<FormProblem | null>(null);

  const registers = useQuery({ queryKey: ["pos-registers", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/pos/registers", { params: { query: { companyId } } })) });
  const sessions = useQuery({ queryKey: ["pos-sessions", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/pos/sessions", { params: { query: { companyId } } })) });
  const orders = useQuery({ queryKey: ["pos-orders", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/pos/orders", { params: { query: { companyId } } })) });
  const warehouses = useWarehouses(companyId);
  const bankAccounts = useQuery({ queryKey: ["bank-accounts", companyId], enabled: Boolean(companyId) && form !== null, queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })) });
  const priceLists = useQuery({ queryKey: ["price-lists", companyId], enabled: Boolean(companyId) && form !== null, queryFn: async () => unwrap(await api.GET("/api/v1/pricing/price-lists", { params: { query: { companyId } } })) });
  const customers = useQuery({ queryKey: ["partners", "customers"], enabled: form !== null, queryFn: async () => unwrap(await api.GET("/api/v1/partners", { params: { query: { role: "customer", isActive: true, limit: 200 } } })) });
  const bins = useQuery({ queryKey: ["bins", form?.warehouseId], enabled: Boolean(form?.warehouseId) && (warehouses.data ?? []).some((w) => w.id === form?.warehouseId && w.binsEnabled), queryFn: async () => unwrap(await api.GET("/api/v1/inventory/warehouses/{warehouseId}/bins", { params: { path: { warehouseId: form?.warehouseId ?? "" } } })) });

  const register = (registers.data ?? []).find((r) => r.id === openId) ?? null;
  const session = (sessions.data ?? []).find((s) => s.id === openId) ?? null;
  const order = (orders.data ?? []).find((o) => o.id === openId) ?? null;
  const openOrder = useQuery({ queryKey: ["pos-order", openId], enabled: Boolean(openId) && !register && !session && !order, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/pos/orders/{orderId}", { params: { path: { orderId: openId ?? "" } } })) });
  const shownOrder = order ?? (register || session ? null : openOrder.data ?? null);

  const refresh = async (): Promise<void> => {
    await Promise.all([["pos-registers"], ["pos-sessions"], ["pos-orders"], ["pos-order"], ["activities"]].map((key) => queryClient.invalidateQueries({ queryKey: key })));
  };
  const fail = (error: unknown): void => { setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const save = useMutation({
    mutationFn: async (f: RegisterForm) => {
      const body = {
        companyId,
        code: f.code || null,
        name: { en: f.nameEn, ar: f.nameAr },
        warehouseId: f.warehouseId,
        binId: f.binId || null,
        cashAccountId: f.cashAccountId,
        cardAccountId: f.cardAccountId || null,
        priceListId: f.priceListId || null,
        defaultPartnerId: f.defaultPartnerId || null,
        allowCredit: f.allowCredit,
        allowPriceOverride: f.allowPriceOverride,
        cashRoundingIncrement: f.cashRoundingIncrement.trim() === "" ? null : f.cashRoundingIncrement.trim(),
        receiptFooter: { en: f.footerEn, ar: f.footerAr },
        isActive: f.isActive,
      };
      return unwrap(f.id ? await api.PUT("/api/v1/pos/registers/{registerId}", { params: { path: { registerId: f.id } }, body }) : await api.POST("/api/v1/pos/registers", { body }));
    },
    onSuccess: async (saved) => { setProblem(null); setForm(null); await refresh(); setOpenId(saved.id); },
    onError: fail,
  });
  const submitEinvoice = useMutation({
    mutationFn: async (orderId: string) => unwrap(await api.POST("/api/v1/pos/orders/{orderId}/einvoice", { params: { path: { orderId } } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: fail,
  });

  const money = (value: string | number, currency: string): string => formatMoney(value, currency);
  const registerColumns = useMemo<ColumnDef<Register, unknown>[]>(
    () => [
      { id: "code", accessorKey: "code", header: t("pos.code"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.code}</span> },
      { id: "name", accessorFn: (r) => localized(r.name), header: t("common.name"), size: 200, cell: ({ row }) => <span dir="auto">{localized(row.original.name)}</span> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("pos.warehouse"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.warehouseCode}</span> },
      { id: "cash", accessorKey: "cashAccountCode", header: t("pos.cashAccount"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.cashAccountCode}</span> },
      { id: "card", accessorKey: "cardAccountCode", header: t("pos.cardAccount"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.cardAccountCode ?? "—"}</span> },
      { id: "rounding", accessorKey: "cashRoundingIncrement", header: t("pos.cashRounding"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.cashRoundingIncrement)}</span> },
      { id: "open", accessorFn: (r) => (r.openSessionId ? t("pos.sessionStatuses.open") : ""), header: t("pos.session"), size: 120, cell: ({ row }) => (row.original.openSessionId ? <Badge tone="success">{t("pos.sessionStatuses.open")}</Badge> : <span className="text-fg-muted">—</span>) },
      { id: "active", accessorFn: (r) => (r.isActive ? t("common.active") : t("common.inactive")), header: t("common.status"), size: 100, cell: ({ row }) => (row.original.isActive ? t("common.active") : t("common.inactive")) },
    ],
    [t],
  );
  const sessionColumns = useMemo<ColumnDef<Session, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("pos.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "register", accessorKey: "registerCode", header: t("pos.register"), size: 120, cell: ({ row }) => <span dir="ltr">{row.original.registerCode}</span> },
      { id: "status", accessorFn: (r) => t(`pos.sessionStatuses.${r.status}`), header: t("common.status"), size: 110, cell: ({ row }) => <Status status={row.original.status} prefix="sessionStatuses" /> },
      { id: "cashier", accessorKey: "cashierName", header: t("pos.cashier"), size: 150, cell: ({ row }) => <span dir="auto">{row.original.cashierName}</span> },
      { id: "opened", accessorKey: "openedAt", header: t("pos.openedAt"), size: 160, cell: ({ row }) => <span dir="ltr">{formatDateTime(row.original.openedAt)}</span> },
      { id: "orders", accessorKey: "ordersCount", header: t("pos.ordersCount"), size: 90, cell: ({ row }) => <span className="tabular" dir="ltr">{String(row.original.ordersCount)}</span> },
      { id: "sales", accessorKey: "salesTotal", header: t("pos.salesTotal"), size: 140, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.salesTotal, row.original.currency)}</span> },
      { id: "cash", accessorKey: "cashTaken", header: t("pos.cashTaken"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.cashTaken, row.original.currency)}</span> },
      { id: "difference", accessorKey: "difference", header: t("pos.difference"), size: 120, cell: ({ row }) => <span className="tabular" dir="ltr">{row.original.difference === null ? "—" : money(row.original.difference, row.original.currency)}</span> },
    ],
    [t],
  );
  const orderColumns = useMemo<ColumnDef<Order, unknown>[]>(
    () => [
      { id: "number", accessorKey: "number", header: t("pos.number"), size: 150, cell: ({ row }) => <span dir="ltr">{row.original.number}</span> },
      { id: "kind", accessorFn: (r) => t(`pos.kinds.${r.kind}`), header: t("pos.kind"), size: 100, cell: ({ row }) => <Status status={row.original.kind} prefix="kinds" /> },
      { id: "register", accessorKey: "registerCode", header: t("pos.register"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.registerCode}</span> },
      { id: "session", accessorKey: "sessionNumber", header: t("pos.session"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.sessionNumber}</span> },
      { id: "ordered", accessorKey: "orderedAt", header: t("pos.orderedAt"), size: 160, cell: ({ row }) => <span dir="ltr">{formatDateTime(row.original.orderedAt)}</span> },
      { id: "partner", accessorFn: (r) => localized(r.partnerName), header: t("pos.till.customer"), size: 160, cell: ({ row }) => <span dir="auto">{localized(row.original.partnerName) || "—"}</span> },
      { id: "net", accessorKey: "totalNet", header: t("pos.till.net"), size: 120, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.totalNet, row.original.currency)}</span> },
      { id: "tax", accessorKey: "totalTax", header: t("pos.till.tax"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.totalTax, row.original.currency)}</span> },
      { id: "due", accessorKey: "totalDue", header: t("pos.till.totalDue"), size: 130, cell: ({ row }) => <span className="tabular" dir="ltr">{money(row.original.totalDue, row.original.currency)}</span> },
      { id: "einvoice", accessorFn: (r) => r.einvoiceStatus ?? "", header: t("pos.einvoice"), size: 110, cell: ({ row }) => (row.original.einvoiceStatus ? <Status status={row.original.einvoiceStatus} prefix="einvoiceStatuses" /> : <span className="text-fg-muted">—</span>) },
    ],
    [t],
  );

  const edit = (r: Register | null): void => {
    setProblem(null);
    setForm(r ? {
      id: r.id, code: r.code, nameEn: r.name.en ?? "", nameAr: r.name.ar ?? "", warehouseId: r.warehouseId, binId: r.binId ?? "", cashAccountId: r.cashAccountId, cardAccountId: r.cardAccountId ?? "", priceListId: r.priceListId ?? "", defaultPartnerId: r.defaultPartnerId ?? "",
      allowCredit: r.allowCredit, allowPriceOverride: r.allowPriceOverride, cashRoundingIncrement: "", footerEn: r.receiptFooter.en ?? "", footerAr: r.receiptFooter.ar ?? "", isActive: r.isActive,
    } : emptyForm());
  };
  const submit = (event: FormEvent): void => { event.preventDefault(); if (form) { save.mutate(form); } };
  const warehouse = (warehouses.data ?? []).find((w) => w.id === form?.warehouseId);

  return (
    <>
      <PageHeader
        title={t("nav.pos")}
        description={t("pos.description")}
        actions={(
          <>
            <Button variant="secondary" asChild>
              <Link to="/pos" data-testid="open-till"><Store aria-hidden="true" />{t("nav.till")}</Link>
            </Button>
            {canManage && tab === "registers" ? (
              <Button onClick={() => { edit(null); }} disabled={!companyId} data-testid="new-register">
                <Plus aria-hidden="true" />
                {t("pos.newRegister")}
              </Button>
            ) : null}
          </>
        )}
      />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
      </div>
      <Tabs tabs={[{ id: "registers", label: t("pos.tabs.registers"), testId: "tab-registers" }, { id: "sessions", label: t("pos.tabs.sessions"), testId: "tab-sessions" }, { id: "orders", label: t("pos.tabs.orders"), testId: "tab-orders" }]} value={tab} onChange={(id) => { setTab(id); setProblem(null); }} />
      <FormError message={!openId && !form ? problem?.message ?? null : null} />
      {tab === "registers" ? (
        <DataGrid<Register> label="pos.tabs.registers" columns={registerColumns} data={registers.data ?? []} rowKey={(row) => row.id} loading={registers.isPending && Boolean(companyId)} emptyTitle={t("pos.emptyRegisters")} emptyDescription={t("pos.emptyRegistersDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} exportable={false} />
      ) : tab === "sessions" ? (
        <DataGrid<Session> label="pos.tabs.sessions" columns={sessionColumns} data={sessions.data ?? []} rowKey={(row) => row.id} loading={sessions.isPending && Boolean(companyId)} emptyTitle={t("pos.emptySessions")} emptyDescription={t("pos.emptySessionsDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} exportable={false} />
      ) : (
        <DataGrid<Order> label="pos.tabs.orders" columns={orderColumns} data={orders.data ?? []} rowKey={(row) => row.id} loading={orders.isPending && Boolean(companyId)} emptyTitle={t("pos.emptyOrders")} emptyDescription={t("pos.emptyOrdersDescription")} onOpen={(row) => { setProblem(null); setOpenId(row.id); }} documentType="pos_order" />
      )}

      <Dialog open={Boolean(register)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {register ? (
            <div className="flex flex-col gap-4" data-testid="register-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{register.code}</span>
                  <span dir="auto">{localized(register.name)}</span>
                  {register.openSessionId ? <Badge tone="success">{t("pos.sessionStatuses.open")}</Badge> : null}
                  {!register.isActive ? <Badge tone="neutral">{t("common.inactive")}</Badge> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <KeyValues entries={[
                [t("pos.warehouse"), <span key="w" dir="ltr">{register.warehouseCode}</span>],
                [t("pos.cashAccount"), <span key="c" dir="ltr">{register.cashAccountCode}</span>],
                [t("pos.cardAccount"), <span key="k" dir="ltr">{register.cardAccountCode ?? "—"}</span>],
                [t("pos.till.walkIn"), <span key="p" dir="auto">{localized(register.defaultPartnerName) || "—"}</span>],
                [t("pos.cashRounding"), <span key="r" dir="ltr">{formatNumber(register.cashRoundingIncrement)} {register.currency}</span>],
                [t("pos.allowCredit"), register.allowCredit ? t("common.yes") : t("common.no")],
                [t("pos.allowPriceOverride"), register.allowPriceOverride ? t("common.yes") : t("common.no")],
              ]} />
              <div className="flex flex-wrap gap-2">
                {canManage ? <Button variant="secondary" onClick={() => { edit(register); }} data-testid="edit-register">{t("common.edit")}</Button> : null}
                <Button asChild variant="ghost"><Link to="/pos">{t("pos.openTill")}</Link></Button>
              </div>
              <RecordActivity entityType="pos_register" entityId={register.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(session)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-3xl">
          {session ? (
            <div className="flex flex-col gap-4" data-testid="session-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{session.number}</span>
                  <Status status={session.status} prefix="sessionStatuses" />
                </DialogTitle>
              </DialogHeader>
              <KeyValues entries={[
                [t("pos.register"), <span key="r" dir="ltr">{session.registerCode} · {localized(session.registerName)}</span>],
                [t("pos.cashier"), <span key="c" dir="auto">{session.cashierName}</span>],
                [t("pos.openedAt"), <span key="o" dir="ltr">{formatDateTime(session.openedAt)}</span>],
                [t("pos.closedAt"), <span key="x" dir="ltr">{session.closedAt ? formatDateTime(session.closedAt) : "—"}</span>],
                [t("pos.openingFloat"), <span key="f" dir="ltr">{money(session.openingFloat, session.currency)}</span>],
                [t("pos.cashTaken"), <span key="ct" dir="ltr">{money(session.cashTaken, session.currency)}</span>],
                [t("pos.cardTaken"), <span key="cd" dir="ltr">{money(session.cardTaken, session.currency)}</span>],
                [t("pos.creditTaken"), <span key="cr" dir="ltr">{money(session.creditTaken, session.currency)}</span>],
                [t("pos.ordersCount"), <span key="n" dir="ltr">{String(session.ordersCount)}</span>],
                [t("pos.salesTotal"), <span key="s" dir="ltr">{money(session.salesTotal, session.currency)}</span>],
                [t("pos.expectedCash"), <span key="e" dir="ltr">{session.expectedCash === null ? "—" : money(session.expectedCash, session.currency)}</span>],
                [t("pos.countedCash"), <span key="cc" dir="ltr">{session.countedCash === null ? "—" : money(session.countedCash, session.currency)}</span>],
                [t("pos.difference"), <span key="d" dir="ltr" data-testid="session-difference">{session.difference === null ? "—" : money(session.difference, session.currency)}</span>],
              ]} />
              {session.notes ? <p className="whitespace-pre-wrap text-sm">{session.notes}</p> : null}
              <RecordActivity entityType="pos_session" entityId={session.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(shownOrder)} onOpenChange={(isOpen) => { if (!isOpen) { setOpenId(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-4xl">
          {shownOrder ? (
            <div className="flex flex-col gap-4" data-testid="pos-order-detail">
              <DialogHeader>
                <DialogTitle className="flex items-center gap-3 text-lg font-semibold">
                  <span dir="ltr">{shownOrder.number}</span>
                  <Status status={shownOrder.kind} prefix="kinds" />
                  {shownOrder.einvoiceStatus ? <Status status={shownOrder.einvoiceStatus} prefix="einvoiceStatuses" /> : null}
                </DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="flex flex-wrap gap-2">
                <PrintButton subject={{ documentType: "pos_order", documentId: shownOrder.id, number: shownOrder.number, partnerId: shownOrder.partnerId }} />
                {shownOrder.einvoiceScheme && shownOrder.einvoiceStatus !== "reported" && shownOrder.einvoiceStatus !== "cleared" ? <Button variant="secondary" loading={submitEinvoice.isPending} onClick={() => { submitEinvoice.mutate(shownOrder.id); }} data-testid="submit-pos-einvoice">{t("pos.submitEinvoice")}</Button> : null}
                {shownOrder.einvoiceSubmissionId ? <Button asChild variant="ghost"><Link to="/tax/einvoicing" search={{ open: shownOrder.einvoiceSubmissionId }}>{t("sales.invoicing.openSubmission")}</Link></Button> : null}
              </div>
              <div className="grid gap-4 md:grid-cols-[1fr_auto]">
                <KeyValues entries={[
                  [t("pos.register"), <span key="r" dir="ltr">{shownOrder.registerCode}</span>],
                  [t("pos.session"), <span key="s" dir="ltr">{shownOrder.sessionNumber}</span>],
                  [t("pos.orderedAt"), <span key="o" dir="ltr">{formatDateTime(shownOrder.orderedAt)}</span>],
                  [t("pos.till.customer"), <span key="c" dir="auto">{localized(shownOrder.partnerName) || t("pos.till.walkIn")}</span>],
                  [t("pos.refundsOrder"), <span key="f" dir="ltr">{shownOrder.refundsOrderNumber ?? "—"}</span>],
                  [t("pos.journal"), <span key="j" dir="ltr">{shownOrder.journalEntryId ? <Link to="/accounting/journal-entries" search={{ open: shownOrder.journalEntryId }} className="text-accent underline">{t("pos.openJournal")}</Link> : "—"}</span>],
                ]} />
                {shownOrder.einvoiceQr && shownOrder.einvoiceSubmissionId ? <QrImage submissionId={shownOrder.einvoiceSubmissionId} label={t("sales.invoicing.einvoiceQr")} /> : null}
              </div>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t("pos.till.item")}</TableHead>
                    <TableHead className="text-end">{t("pos.till.quantity")}</TableHead>
                    <TableHead className="text-end">{t("pos.till.unitPrice")}</TableHead>
                    <TableHead className="text-end">{t("pos.discount")}</TableHead>
                    <TableHead className="text-end">{t("pos.till.net")}</TableHead>
                    <TableHead className="text-end">{t("pos.till.tax")}</TableHead>
                    <TableHead className="text-end">{t("pos.gross")}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {shownOrder.lines.map((line) => (
                    <TableRow key={line.id} data-testid="pos-order-line">
                      <TableCell><span dir="ltr">{line.itemCode}</span> <span dir="auto">{localized(line.description)}</span></TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.quantity)} {line.uomCode}</TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.unitPrice)}</TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.discountPct)}%</TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.netAmount)}</TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.taxAmount)} ({formatNumber(line.taxRatePct)}%)</TableCell>
                      <TableCell className="text-end tabular-nums" dir="ltr">{formatNumber(line.grossAmount)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <div className="grid gap-4 md:grid-cols-2">
                <KeyValues entries={shownOrder.payments.map((p) => [t(`pos.paymentMethods.${p.method}`) + (p.reference ? ` (${p.reference})` : ""), <span key={p.id} dir="ltr">{money(p.amount, shownOrder.currency)}</span>] as [string, React.ReactNode])} />
                <KeyValues entries={[
                  [t("pos.till.net"), <span key="n" dir="ltr">{money(shownOrder.totalNet, shownOrder.currency)}</span>],
                  [t("pos.till.tax"), <span key="t" dir="ltr">{money(shownOrder.totalTax, shownOrder.currency)}</span>],
                  [t("pos.gross"), <span key="g" dir="ltr">{money(shownOrder.totalGross, shownOrder.currency)}</span>],
                  [t("pos.till.rounding"), <span key="r" dir="ltr">{money(shownOrder.rounding, shownOrder.currency)}</span>],
                  [t("pos.till.totalDue"), <span key="d" dir="ltr" className="font-semibold" data-testid="pos-order-total">{money(shownOrder.totalDue, shownOrder.currency)}</span>],
                  [t("pos.till.change"), <span key="c" dir="ltr">{money(shownOrder.changeGiven, shownOrder.currency)}</span>],
                ]} />
              </div>
              {shownOrder.einvoiceMessages.length > 0 ? <ul className="list-disc ps-5 text-sm">{shownOrder.einvoiceMessages.map((m) => <li key={m}>{m}</li>)}</ul> : null}
              <RecordActivity entityType="pos_order" entityId={shownOrder.id} />
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={form !== null} onOpenChange={(isOpen) => { if (!isOpen) { setForm(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          {form ? (
            <form className="flex flex-col gap-4" onSubmit={submit} data-testid="register-form">
              <DialogHeader>
                <DialogTitle>{form.id ? t("pos.editRegister") : t("pos.newRegister")}</DialogTitle>
              </DialogHeader>
              <FormError message={problem?.message ?? null} />
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t("common.nameEn")} required>
                  <TextField value={form.nameEn} dir="ltr" required onChange={(e) => { setForm({ ...form, nameEn: e.target.value }); }} data-testid="register-name-en" />
                </Field>
                <Field label={t("common.nameAr")}>
                  <TextField value={form.nameAr} dir="rtl" onChange={(e) => { setForm({ ...form, nameAr: e.target.value }); }} data-testid="register-name-ar" />
                </Field>
                <Field label={t("pos.code")} description={t("pos.codeHelp")}>
                  <TextField value={form.code} dir="ltr" onChange={(e) => { setForm({ ...form, code: e.target.value.toUpperCase() }); }} data-testid="register-code" />
                </Field>
                <Field label={t("pos.warehouse")} required>
                  <SelectField value={form.warehouseId} required onChange={(e) => { setForm({ ...form, warehouseId: e.target.value, binId: "" }); }} data-testid="register-warehouse">
                    <option value="">{t("common.typeToNarrow")}</option>
                    {(warehouses.data ?? []).map((w) => <option key={w.id} value={w.id}>{w.code} · {localized(w.name)}</option>)}
                  </SelectField>
                </Field>
                {warehouse?.binsEnabled ? (
                  <Field label={t("pos.bin")} required>
                    <SelectField value={form.binId} required onChange={(e) => { setForm({ ...form, binId: e.target.value }); }} data-testid="register-bin">
                      <option value="">{t("common.typeToNarrow")}</option>
                      {(bins.data ?? []).map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
                    </SelectField>
                  </Field>
                ) : null}
                <Field label={t("pos.cashAccount")} required description={t("pos.cashAccountHelp")}>
                  <SelectField value={form.cashAccountId} required onChange={(e) => { setForm({ ...form, cashAccountId: e.target.value }); }} data-testid="register-cash-account">
                    <option value="">{t("common.typeToNarrow")}</option>
                    {(bankAccounts.data ?? []).filter((a) => a.kind !== "bank").map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("pos.cardAccount")} description={t("pos.cardAccountHelp")}>
                  <SelectField value={form.cardAccountId} onChange={(e) => { setForm({ ...form, cardAccountId: e.target.value }); }} data-testid="register-card-account">
                    <option value="">{t("pos.noCard")}</option>
                    {(bankAccounts.data ?? []).filter((a) => a.kind === "bank").map((a) => <option key={a.id} value={a.id}>{a.code} · {localized(a.name)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("pos.priceList")}>
                  <SelectField value={form.priceListId} onChange={(e) => { setForm({ ...form, priceListId: e.target.value }); }} data-testid="register-price-list">
                    <option value="">{t("pos.defaultPrices")}</option>
                    {(priceLists.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.code} · {localized(p.name)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("pos.till.walkIn")} description={t("pos.walkInHelp")}>
                  <SelectField value={form.defaultPartnerId} onChange={(e) => { setForm({ ...form, defaultPartnerId: e.target.value }); }} data-testid="register-walk-in">
                    <option value="">{t("pos.noWalkIn")}</option>
                    {(customers.data?.items ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {localized(c.legalName)}</option>)}
                  </SelectField>
                </Field>
                <Field label={t("pos.cashRounding")} description={t("pos.cashRoundingHelp")}>
                  <DecimalField dir="ltr" value={form.cashRoundingIncrement} onChange={(e) => { setForm({ ...form, cashRoundingIncrement: e.target.value }); }} data-testid="register-rounding" />
                </Field>
                <Field label={t("pos.receiptFooterEn")}>
                  <TextField value={form.footerEn} dir="ltr" onChange={(e) => { setForm({ ...form, footerEn: e.target.value }); }} />
                </Field>
                <Field label={t("pos.receiptFooterAr")}>
                  <TextField value={form.footerAr} dir="rtl" onChange={(e) => { setForm({ ...form, footerAr: e.target.value }); }} />
                </Field>
              </div>
              <div className="flex flex-wrap gap-4">
                <label className="flex items-center gap-2 text-sm"><Checkbox checked={form.allowCredit} onCheckedChange={(v) => { setForm({ ...form, allowCredit: v === true }); }} data-testid="register-allow-credit" />{t("pos.allowCredit")}</label>
                <label className="flex items-center gap-2 text-sm"><Checkbox checked={form.allowPriceOverride} onCheckedChange={(v) => { setForm({ ...form, allowPriceOverride: v === true }); }} data-testid="register-allow-override" />{t("pos.allowPriceOverride")}</label>
                <label className="flex items-center gap-2 text-sm"><Checkbox checked={form.isActive} onCheckedChange={(v) => { setForm({ ...form, isActive: v === true }); }} />{t("common.active")}</label>
              </div>
              <div className="flex justify-end gap-2">
                <Button type="button" variant="ghost" onClick={() => { setForm(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={save.isPending} data-testid="save-register">{t("common.save")}</Button>
              </div>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
