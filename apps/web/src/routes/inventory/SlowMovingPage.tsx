import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import type { ColumnDef } from "@tanstack/react-table";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { DataGrid } from "../../grid/DataGrid";
import { recordRoute } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { today } from "../accounting/shared";
import { Field, PageHeader, SelectField, TextField } from "../common";
import { CompanyFilter, useCompanyContext, useWarehouses, WarehouseSelect } from "./shared";

type Row = components["schemas"]["SlowMovingRow"];

const thresholds = [30, 60, 90, 180, 365];

/**
 * Stock that is not moving: what is on hand at a date and has not been sold or consumed for at least the days chosen
 * (never used: since it first came in), the longest idle first, with its value for those who may see costs. A move
 * between warehouses, an adjustment or a count does not make stock used. A line opens its item.
 */
export function SlowMovingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { companies, companyId, setCompanyId, company } = useCompanyContext();
  const warehouses = useWarehouses(companyId);
  const [warehouseId, setWarehouseId] = useState("");
  const [asOf, setAsOf] = useState(today());
  const [idleDays, setIdleDays] = useState(90);

  const report = useQuery({
    queryKey: ["slow-moving", companyId, warehouseId, asOf, idleDays],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/slow-moving", { params: { query: { companyId, idleDays, ...(asOf ? { asOf } : {}), ...(warehouseId ? { warehouseId } : {}) } } })),
  });
  const currency = company?.functionalCurrency ?? "";
  const data = report.data;
  const valued = data?.totalValue !== null && data?.totalValue !== undefined;

  const columns = useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      { id: "item", accessorFn: (row) => `${row.itemCode} · ${localized(row.itemName)}`, header: t("purchasing.item"), size: 240, cell: ({ getValue }) => <span dir="auto">{String(getValue())}</span> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("purchasing.warehouse"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.warehouseCode}</span> },
      { id: "onHand", accessorKey: "onHand", header: t("inventory.slowMoving.onHand"), size: 120, meta: { exportType: "number" }, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.onHand, { maximumFractionDigits: 3 })} {row.original.baseUom}</span> },
      { id: "firstReceived", accessorKey: "firstReceived", header: t("inventory.slowMoving.firstReceived"), size: 130, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.firstReceived) || "—"}</span> },
      { id: "lastReceived", accessorKey: "lastReceived", header: t("inventory.slowMoving.lastReceived"), size: 130, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.lastReceived) || "—"}</span> },
      { id: "lastUsed", accessorKey: "lastUsed", header: t("inventory.slowMoving.lastUsed"), size: 130, cell: ({ row }) => (row.original.lastUsed ? <span dir="ltr">{formatDate(row.original.lastUsed)}</span> : <span className="text-fg-muted">{t("inventory.slowMoving.never")}</span>) },
      {
        id: "idle",
        accessorKey: "idleDays",
        header: t("inventory.slowMoving.idle"),
        size: 130,
        meta: { exportType: "number" },
        cell: ({ row }) => <Badge tone={Number(row.original.idleDays) >= 180 ? "danger" : "warning"} data-testid="idle-days">{t("inventory.slowMoving.idleDays", { count: Number(row.original.idleDays) })}</Badge>,
      },
      ...(valued
        ? [{ id: "value", accessorKey: "value", header: t("inventory.slowMoving.value"), size: 150, meta: { exportType: "number" as const }, cell: ({ row }: { row: { original: Row } }) => <span className="tabular" dir="ltr">{row.original.value === null ? "" : formatMoney(row.original.value, currency)}</span> } satisfies ColumnDef<Row, unknown>]
        : []),
    ],
    [t, valued, currency],
  );

  return (
    <>
      <PageHeader title={t("nav.slowMoving")} description={t("inventory.slowMoving.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll />
        <Field label={t("inventory.replenishment.asOf")}>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="slow-as-of" />
        </Field>
        <Field label={t("inventory.slowMoving.idleAtLeast")}>
          <SelectField value={String(idleDays)} onChange={(e) => { setIdleDays(Number(e.target.value)); }} data-testid="slow-idle">
            {thresholds.map((days) => (
              <option key={days} value={days}>{t("inventory.slowMoving.days", { count: days })}</option>
            ))}
          </SelectField>
        </Field>
      </div>
      {data ? (
        <p className="mb-3 flex flex-wrap items-center gap-3 text-sm" data-testid="slow-summary">
          <span>{t("inventory.slowMoving.summary", { lines: data.rows.length })}</span>
          {data.totalValue === null ? null : <span className="tabular font-medium" dir="ltr" data-testid="slow-total">{formatMoney(data.totalValue, currency)}</span>}
        </p>
      ) : null}
      <DataGrid<Row>
        label="nav.slowMoving"
        columns={columns}
        data={data?.rows ?? []}
        rowKey={(row) => `${row.itemId}:${row.warehouseId}`}
        loading={report.isPending && Boolean(companyId)}
        emptyTitle={t("inventory.slowMoving.emptyTitle")}
        emptyDescription={t("inventory.slowMoving.emptyDescription")}
        onOpen={(row) => { const route = recordRoute("item", row.itemId); if (route) { void navigate(route); } }}
      />
    </>
  );
}
