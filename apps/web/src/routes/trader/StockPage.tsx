import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatDate, formatDateTime, formatMoney, formatNumber, localized } from "../../lib/format";
import { toFormProblem, type FormProblem } from "../../lib/problem";
import { DecimalField, Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, WarehouseSelect, useCompanyContext, useWarehouses } from "../inventory/shared";
import { setScanContext } from "../mobile/context";

type Count = components["schemas"]["CountSummary"];
type CountLine = components["schemas"]["CountLineSummary"];

const openStatuses = new Set(["frozen", "counting", "review", "approved"]);

const n = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/**
 * Stock (trader edition, ADR-0035 §9): what the store holds, what it is worth at cost, and the count of the store in
 * three plain steps: start it (the sheet is frozen), count it on the phone with the camera or type the counts here,
 * finish it (the differences are posted, the items not counted stay as they are). The stock, the costs and the count
 * are the inventory module's own; this screen only words them.
 */
export function StockPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [search, setSearch] = useState("");
  const [counted, setCounted] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<FormProblem | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const warehouse = warehouses.data?.find((w) => w.id === warehouseId) ?? warehouses.data?.[0];
  const storeId = warehouse?.id ?? "";

  useEffect(() => {
    if (!warehouseId && warehouses.data?.[0]) {
      setWarehouseId(warehouses.data[0].id);
    }
  }, [warehouseId, warehouses.data]);

  const stock = useQuery({
    queryKey: ["trader-stock", companyId, storeId, search],
    enabled: Boolean(companyId) && Boolean(storeId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock", { params: { query: { companyId, warehouseId: storeId, limit: 200, ...(search.trim() ? { q: search.trim() } : {}) } } })),
  });
  const valuation = useQuery({
    queryKey: ["trader-valuation", companyId, storeId],
    enabled: Boolean(companyId) && Boolean(storeId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/valuation", { params: { query: { companyId, warehouseId: storeId } } })),
  });
  const counts = useQuery({
    queryKey: ["trader-counts", companyId, storeId],
    enabled: Boolean(companyId) && Boolean(storeId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: { companyId, warehouseId: storeId } } })),
  });
  const open: Count | undefined = (counts.data ?? []).filter((c) => openStatuses.has(c.status)).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))[0];
  const openId = open?.id ?? "";
  const sheet = useQuery({
    queryKey: ["trader-count-sheet", openId],
    enabled: Boolean(openId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{countId}/sheet", { params: { path: { countId: openId } } })),
  });

  const fail = (error: unknown): void => { setNotice(null); setProblem(toFormProblem(error, t("common.saveFailed"))); };
  const refresh = async (): Promise<void> => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["trader-counts"] }),
      queryClient.invalidateQueries({ queryKey: ["trader-count-sheet"] }),
      queryClient.invalidateQueries({ queryKey: ["trader-stock"] }),
      queryClient.invalidateQueries({ queryKey: ["trader-valuation"] }),
    ]);
  };
  const start = useMutation({
    mutationFn: async () => {
      const created = unwrap(await api.POST("/api/v1/inventory/counts", { body: { companyId, warehouseId: storeId, scope: "full", blind: false, blockMovements: false } }));
      return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/freeze", { params: { path: { countId: created.id } } }));
    },
    onSuccess: async (frozen) => {
      setProblem(null);
      setCounted({});
      setNotice(t("trader.stock.started", { number: frozen.number, lines: formatNumber(frozen.lineCount) }));
      await refresh();
    },
    onError: fail,
  });
  const save = useMutation({
    mutationFn: async () => {
      const entries = Object.entries(counted).filter(([, value]) => value.trim() !== "").map(([lineId, value]) => ({ lineId, countedQty: Number(value) }));
      return unwrap(await api.POST("/api/v1/inventory/counts/{countId}/entries", { params: { path: { countId: openId } }, body: { entries } }));
    },
    onSuccess: async (result) => {
      setProblem(null);
      setCounted({});
      setNotice(t("trader.stock.saved", { counted: formatNumber(result.count.countedLines), lines: formatNumber(result.count.lineCount) }));
      await refresh();
    },
    onError: fail,
  });
  const finish = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/counts/{countId}/finish", { params: { path: { countId: openId } }, body: { uncountedAsUnchanged: true } })),
    onSuccess: async (posted) => {
      setProblem(null);
      setNotice(t("trader.stock.finished", { number: posted.number, differences: formatNumber(posted.varianceLines), value: formatMoney(posted.varianceValue, currency) }));
      await refresh();
    },
    onError: fail,
  });
  const cancel = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/inventory/counts/{countId}/cancel", { params: { path: { countId: openId } } })),
    onSuccess: async () => { setProblem(null); setNotice(t("trader.stock.cancelled")); await refresh(); },
    onError: fail,
  });

  const currency = companies.find((c) => c.id === companyId)?.functionalCurrency ?? "";
  const costOf = new Map((valuation.data?.lines ?? []).map((l) => [l.itemId, l]));
  const rows = (stock.data?.items ?? []).filter((r) => n(r.onHand) !== 0 || costOf.has(r.itemId));
  const units = rows.reduce((sum, r) => sum + n(r.onHand), 0);
  const lines: CountLine[] = sheet.data?.lines ?? [];
  const uncounted = lines.filter((l) => l.countedQty === null).length;
  const countedOf = (line: CountLine): string => counted[line.id] ?? (line.countedQty === null ? "" : String(line.countedQty));
  const dirty = Object.values(counted).some((v) => v.trim() !== "");
  const phoneUrl = typeof window === "undefined" ? "/m" : `${window.location.origin}/m`;
  const goToPhone = (): void => { setScanContext({ companyId, warehouseId: storeId }); };

  return (
    <>
      <PageHeader title={t("trader.stock.title")} description={t("trader.stock.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={(id) => { setCompanyId(id); setWarehouseId(""); }} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={storeId} onChange={setWarehouseId} label={t("trader.stock.store")} testId="stock-store" />
        <Field label={t("trader.stock.search")}>
          <TextField type="search" value={search} onChange={(e) => { setSearch(e.target.value); }} placeholder={t("trader.stock.searchPlaceholder")} data-testid="stock-search" />
        </Field>
      </div>
      <FormError message={problem?.message ?? null} />
      {notice ? <p role="status" className="mb-3 rounded-md border border-success/40 bg-success-soft p-2 text-sm" data-testid="stock-notice">{notice}</p> : null}

      {warehouse ? (
        <div className="flex flex-col gap-4">
          <section className="grid gap-3 sm:grid-cols-3">
            <div className="rounded-lg border border-border bg-surface p-4">
              <div className="text-sm text-fg-muted">{t("trader.stock.items")}</div>
              <div className="text-2xl font-semibold tabular" dir="ltr" data-testid="stock-items">{formatNumber(rows.length)}</div>
            </div>
            <div className="rounded-lg border border-border bg-surface p-4">
              <div className="text-sm text-fg-muted">{t("trader.stock.units")}</div>
              <div className="text-2xl font-semibold tabular" dir="ltr" data-testid="stock-units">{formatNumber(units, { maximumFractionDigits: 3 })}</div>
            </div>
            <div className="rounded-lg border border-border bg-surface p-4">
              <div className="text-sm text-fg-muted">{t("trader.stock.value")}</div>
              <div className="text-2xl font-semibold tabular" dir="ltr" data-testid="stock-value">{valuation.data ? formatMoney(valuation.data.totalValue, currency) : "—"}</div>
              <div className="text-xs text-fg-muted">{t("trader.stock.valueHint")}</div>
            </div>
          </section>

          <section className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4" data-testid="count-card">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-base font-semibold">{t("trader.stock.count.title")}</h2>
              {open ? <Badge tone={open.status === "frozen" ? "info" : "warning"} data-testid="count-status">{t(`trader.stock.count.statuses.${open.status}`, { defaultValue: open.status })}</Badge> : null}
            </div>
            {!open ? (
              <>
                <p className="text-sm text-fg-muted">{t("trader.stock.count.hint")}</p>
                <div>
                  <Button type="button" onClick={() => { start.mutate(); }} loading={start.isPending} data-testid="start-count">{t("trader.stock.count.start")}</Button>
                </div>
              </>
            ) : (
              <>
                <p className="text-sm" dir="auto" data-testid="count-progress">
                  {t("trader.stock.count.progress", { number: open.number, counted: formatNumber(open.countedLines), lines: formatNumber(open.lineCount), date: formatDate(open.postingDate) })}
                </p>
                <div className="flex flex-wrap gap-2">
                  <Button asChild variant="secondary">
                    <Link to="/m/count" onClick={goToPhone} data-testid="count-on-phone">{t("trader.stock.count.onPhone")}</Link>
                  </Button>
                  <Button type="button" onClick={() => { finish.mutate(); }} loading={finish.isPending} data-testid="finish-count">{t("trader.stock.count.finish")}</Button>
                  <Button type="button" variant="ghost" onClick={() => { cancel.mutate(); }} loading={cancel.isPending} data-testid="cancel-count">{t("trader.stock.count.cancel")}</Button>
                </div>
                <p className="text-xs text-fg-muted" dir="auto">{uncounted > 0 ? t("trader.stock.count.uncountedHint", { count: formatNumber(uncounted) }) : t("trader.stock.count.allCountedHint")}</p>
                <p className="text-xs text-fg-muted" dir="auto">{t("trader.stock.count.phoneHint")} <span className="font-mono" dir="ltr" data-testid="phone-url">{phoneUrl}</span></p>
                {lines.length > 0 ? (
                  <div className="flex flex-col gap-2">
                    <h3 className="text-sm font-semibold">{t("trader.stock.count.typeHere")}</h3>
                    <Table data-testid="count-sheet">
                      <TableHeader>
                        <TableRow>
                          <TableHead>{t("trader.stock.item")}</TableHead>
                          <TableHead className="text-end">{t("trader.stock.count.expected")}</TableHead>
                          <TableHead>{t("trader.stock.count.counted")}</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {lines.map((line) => (
                          <TableRow key={line.id} data-testid="count-line">
                            <TableCell dir="auto">{line.itemCode} · {localized(line.itemName)}{line.binCode ? ` · ${line.binCode}` : ""}{line.lotNumber ? ` · ${line.lotNumber}` : ""}{line.serialNumber ? ` · ${line.serialNumber}` : ""}</TableCell>
                            <TableCell className="tabular text-end" dir="ltr">{line.expectedQty === null ? "—" : formatNumber(line.expectedQty, { maximumFractionDigits: 3 })}</TableCell>
                            <TableCell>
                              <DecimalField aria-label={`${t("trader.stock.count.counted")} ${line.itemCode}`} value={countedOf(line)} onChange={(e) => { setCounted({ ...counted, [line.id]: e.target.value }); }} data-testid={`counted-${line.itemCode}`} className="max-w-32" />
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                    <div>
                      <Button type="button" variant="secondary" onClick={() => { save.mutate(); }} loading={save.isPending} disabled={!dirty} data-testid="save-counts">{t("trader.stock.count.save")}</Button>
                    </div>
                  </div>
                ) : null}
              </>
            )}
          </section>

          {rows.length === 0 ? (
            <div className="rounded-md border border-dashed border-border p-6 text-center text-sm text-fg-muted">
              <p className="font-medium text-fg">{t("trader.stock.empty")}</p>
              <p>{t("trader.stock.emptyDescription")}</p>
            </div>
          ) : (
            <Table data-testid="stock-table">
              <TableHeader>
                <TableRow>
                  <TableHead>{t("trader.stock.item")}</TableHead>
                  <TableHead className="text-end">{t("trader.stock.onHand")}</TableHead>
                  <TableHead className="text-end">{t("trader.stock.unitCost")}</TableHead>
                  <TableHead className="text-end">{t("trader.stock.worth")}</TableHead>
                  <TableHead>{t("trader.stock.lastMoved")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => {
                  const cost = costOf.get(row.itemId);
                  return (
                    <TableRow key={`${row.itemId}-${row.warehouseId}`} data-testid="stock-row">
                      <TableCell dir="auto">{row.itemCode} · {localized(row.itemName)}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr" data-testid="stock-on-hand">{formatNumber(row.onHand, { maximumFractionDigits: 3 })} {row.baseUom}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{cost ? formatMoney(cost.averageUnitCost, currency) : "—"}</TableCell>
                      <TableCell className="tabular text-end" dir="ltr">{cost ? formatMoney(cost.value, currency) : "—"}</TableCell>
                      <TableCell dir="ltr">{row.lastMovementAt ? formatDateTime(row.lastMovementAt) : "—"}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
          <p className="text-sm text-fg-muted">
            <Link to="/inventory/stock" className="text-accent underline">{t("trader.stock.fullStock")}</Link>
            {" · "}
            <Link to="/inventory/items" className="text-accent underline">{t("trader.stock.itemsLink")}</Link>
          </p>
        </div>
      ) : warehouses.isSuccess && warehouses.data.length === 0 ? (
        <div className="rounded-md border border-dashed border-border p-6 text-center text-sm text-fg-muted">
          <p className="font-medium text-fg">{t("trader.stock.noStore")}</p>
          <p><Link to="/inventory/warehouses" className="text-accent underline">{t("trader.stock.createStore")}</Link></p>
        </div>
      ) : null}
    </>
  );
}
