import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { Play, ShoppingCart } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { recordRoute } from "../../lib/documents";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { useCan } from "../../lib/permissions";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { today } from "../accounting/shared";
import { Field, FormError, PageHeader, SelectField, TextField } from "../common";
import { useSuppliers } from "../purchasing/shared";
import { CompanyFilter, DocStatus, KeyValues, Qty, WarehouseSelect, plain, useCompanyContext, useWarehouses } from "./shared";

type Suggestion = components["schemas"]["ReplenishmentSuggestionSummary"];
type PurchaseOrder = components["schemas"]["PurchaseOrderSummary"];

/** Open, or accepted without an order yet: what can still become a purchase order. */
const orderable = (s: Suggestion): boolean => (s.status === "open" || s.status === "accepted") && !s.purchaseOrderLineId;
const supplierOf = (s: Suggestion): string | null => s.acceptedSupplierId ?? s.suggestedSupplierId;

const statuses = ["open", "accepted", "dismissed", "superseded", "all"];

/** Replenishment (roadmap 3.7): run the planner, read why each suggestion exists, turn suggestions into draft purchase orders, accept one with a different quantity or dismiss it with a reason. */
export function ReplenishmentPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = useSearch({ strict: false });
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [status, setStatus] = useState("open");
  const [asOf, setAsOf] = useState(today());
  const [quantity, setQuantity] = useState("");
  const [note, setNote] = useState("");
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [ordering, setOrdering] = useState<{ ids: string[]; supplierId: string; clear: (() => void) | undefined } | null>(null);
  const [ordered, setOrdered] = useState<PurchaseOrder[] | null>(null);
  const can = useCan();
  const canOrder = can("purchasing.order.manage");
  const suppliers = useSuppliers(canOrder ? companyId : "");
  const openId = search.open;

  const suggestions = useQuery({
    queryKey: ["suggestions", companyId, warehouseId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/suggestions", { params: { query: { companyId, status, ...(warehouseId ? { warehouseId } : {}) } } })),
  });
  const runs = useQuery({
    queryKey: ["replenishment-runs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/replenishment/runs", { params: { query: { companyId } } })),
  });
  const detail = suggestions.data?.find((s) => s.id === openId);
  const open = (id: string | null): void => { setQuantity(""); setNote(""); void navigate({ to: "/inventory/replenishment", search: id ? { open: id } : {} }); };
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ["suggestions"] });
    await queryClient.invalidateQueries({ queryKey: ["replenishment-runs"] });
  };

  const run = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/replenishment/run", { body: { companyId, warehouseId: warehouseId || null, asOf: asOf || null } })),
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const decide = useMutation({
    mutationFn: async (action: "accept" | "dismiss") => {
      const suggestionId = openId ?? "";
      return action === "accept"
        ? unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/accept", { params: { path: { suggestionId } }, body: { quantity: quantity || null, note: note || null } }))
        : unwrap(await api.POST("/api/v1/inventory/replenishment/suggestions/{suggestionId}/dismiss", { params: { path: { suggestionId } }, body: { reason: note } }));
    },
    onSuccess: async () => { setProblem(null); await refresh(); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });

  const order = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/purchasing/orders/from-suggestions", { body: { suggestionIds: ordering?.ids ?? [], supplierId: ordering && ordering.supplierId !== "" ? ordering.supplierId : null } })),
    onSuccess: async (result) => { setProblem(null); ordering?.clear?.(); setOrdered(result.orders); await refresh(); await queryClient.invalidateQueries({ queryKey: ["orders"] }); },
    onError: (error) => { setProblem(toFormProblem(error, t("common.saveFailed"))); },
  });
  const startOrdering = (ids: string[], clear?: () => void): void => {
    setProblem(null);
    setOrdered(null);
    const rows = suggestions.data ?? [];
    setOrdering({ ids: ids.filter((id) => rows.some((s) => s.id === id && orderable(s))), supplierId: "", clear });
  };
  const chosen = (suggestions.data ?? []).filter((s) => ordering?.ids.includes(s.id));
  const missingSupplier = chosen.filter((s) => !supplierOf(s)).length;
  const supplierCode = useMemo(() => new Map((suppliers.data ?? []).map((s) => [s.partnerId, s.partnerCode])), [suppliers.data]);

  const columns = useMemo<ColumnDef<Suggestion, unknown>[]>(
    () => [
      { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.itemCode}</span> },
      { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 220 },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
      { id: "qty", accessorKey: "suggestedQty", header: t("inventory.replenishment.suggested"), size: 120, cell: ({ row }) => <Qty value={row.original.suggestedQty} uom={row.original.baseUom} /> },
      { id: "neededBy", accessorKey: "neededBy", header: t("inventory.replenishment.neededBy"), size: 120, cell: ({ row }) => formatDate(row.original.neededBy) },
      { id: "supplier", accessorFn: (row) => { const id = supplierOf(row); return id ? (supplierCode.get(id) ?? "") : ""; }, header: t("partners.supplier"), size: 140, cell: ({ getValue }) => <span dir="ltr">{String(getValue()) || "—"}</span> },
      {
        id: "status",
        accessorKey: "status",
        header: t("common.status"),
        size: 150,
        cell: ({ row }) => (
          <span className="flex items-center gap-2">
            <DocStatus status={row.original.status} />
            {row.original.purchaseOrderLineId ? <span className="text-xs text-fg-muted" data-testid="on-order">{t("inventory.replenishment.onOrder")}</span> : null}
          </span>
        ),
      },
    ],
    [t, supplierCode],
  );

  return (
    <>
      <PageHeader
        title={t("nav.replenishment")}
        description={t("inventory.replenishment.description")}
        actions={
          <Button onClick={() => { run.mutate(); }} disabled={!companyId} loading={run.isPending} data-testid="run-planner">
            <Play aria-hidden="true" />
            {t("inventory.replenishment.runNow")}
          </Button>
        }
      />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll />
        <Field label={t("inventory.replenishment.asOf")}>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" />
        </Field>
        <Field label={t("common.status")}>
          <SelectField value={status} onChange={(e) => { setStatus(e.target.value); }}>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s === "all" ? t("accounting.anyStatus") : t(`inventory.statuses.${s}`)}
              </option>
            ))}
          </SelectField>
        </Field>
      </div>
      <FormError message={problem && !openId ? problem.message : null} />
      <DataGrid<Suggestion>
        label="nav.replenishment"
        columns={columns}
        data={suggestions.data ?? []}
        rowKey={(row) => row.id}
        loading={suggestions.isPending && Boolean(companyId)}
        emptyTitle={t("inventory.replenishment.emptyTitle")}
        emptyDescription={t("inventory.replenishment.emptyDescription")}
        onOpen={(row) => { open(row.id); }}
        height={400}
        selectable={canOrder}
        bulkActions={(selected, clear) => (
          <Button size="sm" onClick={() => { startOrdering(selected, clear); }} disabled={!(suggestions.data ?? []).some((s) => selected.includes(s.id) && orderable(s))} data-testid="order-suggestions">
            <ShoppingCart aria-hidden="true" />
            {t("inventory.replenishment.createOrders")}
          </Button>
        )}
      />
      <section className="mt-6">
        <h2 className="mb-2 text-base font-semibold">{t("inventory.replenishment.runs")}</h2>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("inventory.replenishment.ranAt")}</TableHead>
              <TableHead>{t("inventory.replenishment.asOf")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.checked")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.created")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.refreshed")}</TableHead>
              <TableHead className="text-end">{t("inventory.replenishment.closed")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {(runs.data ?? []).map((r) => (
              <TableRow key={r.id} data-testid="planner-run">
                <TableCell>{formatDateTime(r.ranAt)}</TableCell>
                <TableCell>{formatDate(r.asOf)}</TableCell>
                <TableNumberCell>{String(r.itemsChecked)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsCreated)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsRefreshed)}</TableNumberCell>
                <TableNumberCell>{String(r.suggestionsClosed)}</TableNumberCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </section>

      <Dialog open={Boolean(openId)} onOpenChange={(isOpen) => { if (!isOpen) { open(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold" dir="auto">
              {detail ? `${detail.itemCode} · ${localized(detail.itemName)} · ${detail.warehouseCode}` : t("common.loading")}
            </DialogTitle>
          </DialogHeader>
          {detail ? (
            <div className="flex flex-col gap-4" data-testid="suggestion-detail">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <DocStatus status={detail.status} />
                <span>
                  {t("inventory.replenishment.suggested")}: <Qty value={detail.suggestedQty} uom={detail.baseUom} />
                </span>
                {detail.neededBy ? <span className="text-fg-muted">{t("inventory.replenishment.neededBy")}: {formatDate(detail.neededBy)}</span> : null}
                {detail.decisionNote ? <span className="text-fg-muted">{detail.decisionNote}</span> : null}
              </div>
              <section>
                <h3 className="mb-1 text-sm font-semibold">{t("inventory.replenishment.explanation")}</h3>
                <KeyValues entries={Object.entries(detail.explanation as Record<string, unknown>).map(([key, value]) => [t(`inventory.replenishment.why.${key}`, { defaultValue: key }), key === "trigger" ? t(`inventory.replenishment.why.triggers.${String(value)}`, { defaultValue: String(value) }) : plain(value)])} />
              </section>
              <FormError message={problem?.message ?? null} />
              {detail.status === "open" ? (
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field label={t("inventory.replenishment.acceptedQty")}>
                    <TextField inputMode="decimal" value={quantity} onChange={(e) => { setQuantity(e.target.value); }} dir="ltr" placeholder={String(detail.suggestedQty)} />
                  </Field>
                  <Field label={t("inventory.replenishment.note")}>
                    <TextField value={note} onChange={(e) => { setNote(e.target.value); }} data-testid="decision-note" />
                  </Field>
                </div>
              ) : null}
              <DialogFooter>
                {canOrder && orderable(detail) ? (
                  <Button variant="secondary" onClick={() => { open(null); startOrdering([detail.id]); }} data-testid="order-suggestion">
                    <ShoppingCart aria-hidden="true" />
                    {t("inventory.replenishment.createOrder")}
                  </Button>
                ) : null}
                {detail.status === "open" ? (
                  <>
                    <Button variant="secondary" onClick={() => { decide.mutate("dismiss"); }} loading={decide.isPending} data-testid="dismiss-suggestion">
                      {t("inventory.replenishment.dismiss")}
                    </Button>
                    <Button onClick={() => { decide.mutate("accept"); }} loading={decide.isPending} data-testid="accept-suggestion">
                      {t("inventory.replenishment.accept")}
                    </Button>
                  </>
                ) : null}
              </DialogFooter>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(ordering)} onOpenChange={(isOpen) => { if (!isOpen) { setOrdering(null); setOrdered(null); setProblem(null); } }}>
        <DialogContent closeLabel={t("common.close")} className="max-w-xl">
          <DialogHeader>
            <DialogTitle className="text-lg font-semibold">{t("inventory.replenishment.orderTitle")}</DialogTitle>
          </DialogHeader>
          {ordered ? (
            <div className="flex flex-col gap-3" data-testid="ordered">
              <p className="text-sm">{t("inventory.replenishment.orderedHint")}</p>
              <ul className="flex flex-col gap-1 text-sm">
                {ordered.map((o) => {
                  const route = recordRoute("purchase_order", o.id);
                  return (
                    <li key={o.id} className="flex flex-wrap items-center gap-2">
                      {route ? <Link to={route.to} search={route.search} className="font-medium text-accent underline-offset-2 hover:underline" dir="ltr" data-testid="ordered-link">{o.number}</Link> : <span dir="ltr">{o.number}</span>}
                      <span dir="auto">{o.partnerCode} · {localized(o.partnerName)}</span>
                      <span className="text-fg-muted">{t("inventory.replenishment.lines", { count: o.lines.length })}</span>
                    </li>
                  );
                })}
              </ul>
              <DialogFooter>
                <Button onClick={() => { setOrdering(null); setOrdered(null); }}>{t("common.close")}</Button>
              </DialogFooter>
            </div>
          ) : ordering ? (
            <form onSubmit={(event) => { event.preventDefault(); order.mutate(); }} className="flex flex-col gap-3">
              <p className="text-sm">{t("inventory.replenishment.orderHint", { count: ordering.ids.length })}</p>
              <FormError message={problem?.message ?? null} />
              {missingSupplier > 0 ? (
                <Field label={t("inventory.replenishment.supplierFor")} description={t("inventory.replenishment.supplierForHint", { count: missingSupplier })} required>
                  <SelectField value={ordering.supplierId} onChange={(e) => { setOrdering({ ...ordering, supplierId: e.target.value }); }} required data-testid="order-supplier">
                    <option value="">—</option>
                    {(suppliers.data ?? []).map((s) => (
                      <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
                    ))}
                  </SelectField>
                </Field>
              ) : null}
              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => { setOrdering(null); }}>{t("common.cancel")}</Button>
                <Button type="submit" loading={order.isPending} disabled={ordering.ids.length === 0} data-testid="confirm-order">{t("inventory.replenishment.createOrders")}</Button>
              </DialogFooter>
            </form>
          ) : null}
        </DialogContent>
      </Dialog>
    </>
  );
}
