import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { formatDate, formatDateTime, localized } from "../../lib/format";
import { Amount, today } from "../accounting/shared";
import { Field, FormError, PageHeader, TextField } from "../common";
import { CompanyFilter, DocStatus, Qty, WarehouseSelect, useCompanyContext, useWarehouses } from "./shared";

type Row = components["schemas"]["ValuationRow"];

/** Valuation (roadmap 3.3): what the stock is worth at any date, by item and warehouse, equal to the inventory accounts; and the cost adjustment runs the engine made. */
export function ValuationPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [asOf, setAsOf] = useState(today());
  const [includeZero, setIncludeZero] = useState(false);

  const report = useQuery({
    queryKey: ["valuation", companyId, warehouseId, asOf, includeZero],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/valuation", { params: { query: { companyId, asOf, includeZero, ...(warehouseId ? { warehouseId } : {}) } } })),
  });
  const runs = useQuery({
    queryKey: ["cost-runs", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/costing/runs", { params: { query: { companyId, limit: 50 } } })),
  });
  const runNow = useMutation({
    mutationFn: async (runId: string) => unwrap(await api.POST("/api/v1/inventory/costing/runs/{runId}/run", { params: { path: { runId } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["cost-runs"] });
      await queryClient.invalidateQueries({ queryKey: ["valuation"] });
    },
  });

  const columns = useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      { id: "item", accessorKey: "itemCode", header: t("inventory.item"), size: 130, cell: ({ row }) => <span dir="ltr">{row.original.itemCode}</span> },
      { id: "name", accessorFn: (row) => localized(row.itemName), header: t("inventory.items.name"), size: 220 },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("inventory.warehouse"), size: 110 },
      { id: "quantity", accessorKey: "quantity", header: t("inventory.quantity"), size: 120, cell: ({ row }) => <Qty value={row.original.quantity} uom={row.original.baseUom} /> },
      { id: "average", accessorKey: "averageUnitCost", header: t("inventory.valuation.averageCost"), size: 130, cell: ({ row }) => <Amount value={row.original.averageUnitCost} /> },
      { id: "actual", accessorKey: "actual", header: t("inventory.valuation.actual"), size: 130, cell: ({ row }) => <Amount value={row.original.actual} /> },
      { id: "expected", accessorKey: "expected", header: t("inventory.valuation.expected"), size: 130, cell: ({ row }) => <Amount value={row.original.expected} /> },
      { id: "value", accessorKey: "value", header: t("inventory.valuation.value"), size: 140, cell: ({ row }) => <Amount value={row.original.value} /> },
    ],
    [t],
  );

  return (
    <>
      <PageHeader title={t("nav.valuation")} description={t("inventory.valuation.description")} />
      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll />
        <Field label={t("inventory.valuation.asOf")}>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="valuation-date" />
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm">
          <input type="checkbox" checked={includeZero} onChange={(e) => { setIncludeZero(e.target.checked); }} />
          {t("inventory.valuation.includeZero")}
        </label>
      </div>
      {report.data ? (
        <div className="mb-3 flex flex-wrap gap-4 text-sm" data-testid="valuation-totals">
          <span>
            {t("inventory.valuation.totalValue")}: <strong><Amount value={report.data.totalValue} /></strong> {company?.functionalCurrency}
          </span>
          <span className="text-fg-muted">
            {t("inventory.valuation.totalActual")}: <Amount value={report.data.totalActual} />
          </span>
          <span className="text-fg-muted">
            {t("inventory.valuation.totalExpected")}: <Amount value={report.data.totalExpected} />
          </span>
          <span className="text-fg-muted">{formatDate(report.data.asOf)}</span>
        </div>
      ) : null}
      <FormError message={report.isError ? t("accounting.loadFailed") : null} />
      <DataGrid<Row> label="nav.valuation" columns={columns} data={report.data?.lines ?? []} rowKey={(row) => `${row.itemId}:${row.warehouseId}`} loading={report.isPending && Boolean(companyId)} emptyTitle={t("inventory.valuation.emptyTitle")} emptyDescription={t("inventory.valuation.emptyDescription")} height={420} />
      <section className="mt-6">
        <h2 className="mb-2 text-base font-semibold">{t("inventory.valuation.runs")}</h2>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("inventory.valuation.started")}</TableHead>
              <TableHead>{t("inventory.valuation.trigger")}</TableHead>
              <TableHead>{t("inventory.item")}</TableHead>
              <TableHead className="text-end">{t("inventory.valuation.walked")}</TableHead>
              <TableHead className="text-end">{t("inventory.valuation.reapplied")}</TableHead>
              <TableHead className="text-end">{t("inventory.valuation.adjusted")}</TableHead>
              <TableHead>{t("common.status")}</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {(runs.data?.items ?? []).map((r) => (
              <TableRow key={r.id}>
                <TableCell>{formatDateTime(r.startedAt)}</TableCell>
                <TableCell>{r.triggerKind} · {r.triggerDocumentType}</TableCell>
                <TableCell dir="ltr">{r.itemId}</TableCell>
                <TableNumberCell>{String(r.entriesWalked)}</TableNumberCell>
                <TableNumberCell>{String(r.entriesReapplied)}</TableNumberCell>
                <TableNumberCell><Amount value={r.amountAdjusted} /></TableNumberCell>
                <TableCell><DocStatus status={r.status} />{r.error ? <span className="ms-2 text-danger">{r.error}</span> : null}</TableCell>
                <TableCell>
                  {r.status === "queued" || r.status === "failed" ? (
                    <Button variant="secondary" size="sm" onClick={() => { runNow.mutate(r.id); }} loading={runNow.isPending}>
                      {t("inventory.valuation.run")}
                    </Button>
                  ) : null}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </section>
    </>
  );
}
