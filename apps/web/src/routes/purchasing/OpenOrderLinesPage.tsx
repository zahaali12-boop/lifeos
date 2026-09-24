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
import { CompanyFilter, useCompanyContext, useWarehouses, WarehouseSelect } from "../inventory/shared";
import { PurchaseStatus, useSuppliers } from "./shared";

type OpenLine = components["schemas"]["OpenOrderLine"];

/**
 * What is still to arrive: the stock lines of approved, sent and partly received purchase orders with goods open,
 * valued at the net order price, the late ones flagged by how many days they are overdue. A line opens its order.
 */
export function OpenOrderLinesPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { companies, companyId, setCompanyId } = useCompanyContext();
  const suppliers = useSuppliers(companyId);
  const warehouses = useWarehouses(companyId);
  const [partnerId, setPartnerId] = useState("");
  const [warehouseId, setWarehouseId] = useState("");
  const [asOf, setAsOf] = useState(today());
  const [lateOnly, setLateOnly] = useState(false);

  const report = useQuery({
    queryKey: ["open-order-lines", companyId, partnerId, warehouseId, asOf, lateOnly],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/reports/open-order-lines", {
      params: { query: { companyId, lateOnly, ...(asOf ? { asOf } : {}), ...(partnerId ? { partnerId } : {}), ...(warehouseId ? { warehouseId } : {}) } },
    })),
  });

  const columns = useMemo<ColumnDef<OpenLine, unknown>[]>(
    () => [
      { id: "order", accessorKey: "orderNumber", header: t("purchasing.number"), size: 140, cell: ({ row }) => <span dir="ltr">{row.original.orderNumber}</span> },
      { id: "status", accessorKey: "orderStatus", header: t("common.status"), size: 140, cell: ({ row }) => <PurchaseStatus status={row.original.orderStatus} /> },
      { id: "supplier", accessorFn: (row) => `${row.partnerCode} · ${localized(row.partnerName)}`, header: t("partners.supplier"), size: 200, cell: ({ getValue }) => <span dir="auto">{String(getValue())}</span> },
      { id: "item", accessorFn: (row) => `${row.itemCode} · ${localized(row.itemName)}`, header: t("purchasing.item"), size: 220, cell: ({ getValue }) => <span dir="auto">{String(getValue())}</span> },
      { id: "warehouse", accessorKey: "warehouseCode", header: t("purchasing.warehouse"), size: 110, cell: ({ row }) => <span dir="ltr">{row.original.warehouseCode ?? "—"}</span> },
      { id: "expected", accessorKey: "expectedDate", header: t("purchasing.expectedDate"), size: 120, cell: ({ row }) => <span dir="ltr">{formatDate(row.original.expectedDate) || "—"}</span> },
      {
        id: "late",
        accessorKey: "daysLate",
        header: t("purchasing.openLines.late"),
        size: 120,
        cell: ({ row }) => (row.original.daysLate === null ? null : <Badge tone="danger" data-testid="days-late">{t("purchasing.openLines.daysLate", { count: row.original.daysLate })}</Badge>),
      },
      { id: "ordered", accessorKey: "ordered", header: t("purchasing.ordered"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.ordered, { maximumFractionDigits: 3 })} {row.original.uomCode}</span> },
      { id: "received", accessorKey: "received", header: t("purchasing.received"), size: 100, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.received, { maximumFractionDigits: 3 })}</span> },
      { id: "open", accessorKey: "open", header: t("purchasing.openLines.open"), size: 100, cell: ({ row }) => <span className="tabular font-medium" dir="ltr">{formatNumber(row.original.open, { maximumFractionDigits: 3 })}</span> },
      { id: "price", accessorKey: "netUnitPrice", header: t("purchasing.unitPrice"), size: 110, cell: ({ row }) => <span className="tabular" dir="ltr">{formatNumber(row.original.netUnitPrice, { maximumFractionDigits: 4 })}</span> },
      { id: "value", accessorKey: "openValue", header: t("purchasing.openLines.openValue"), size: 150, cell: ({ row }) => <span className="tabular" dir="ltr">{formatMoney(row.original.openValue, row.original.currency)}</span> },
    ],
    [t],
  );
  const data = report.data;

  return (
    <>
      <PageHeader title={t("nav.openOrderLines")} description={t("purchasing.openLines.description")} />
      <div className="mb-3 flex flex-wrap items-end gap-3">
        <CompanyFilter companies={companies} value={companyId} onChange={setCompanyId} />
        <Field label={t("partners.supplier")}>
          <SelectField value={partnerId} onChange={(e) => { setPartnerId(e.target.value); }} data-testid="open-lines-supplier">
            <option value="">{t("common.all")}</option>
            {(suppliers.data ?? []).map((s) => (
              <option key={s.partnerId} value={s.partnerId}>{s.partnerCode} · {localized(s.partnerName)}</option>
            ))}
          </SelectField>
        </Field>
        <WarehouseSelect warehouses={warehouses.data ?? []} value={warehouseId} onChange={setWarehouseId} allowAll />
        <Field label={t("inventory.replenishment.asOf")}>
          <TextField type="date" value={asOf} onChange={(e) => { setAsOf(e.target.value); }} dir="ltr" data-testid="open-lines-as-of" />
        </Field>
        <label className="flex items-center gap-2 self-end pb-2 text-sm">
          <input type="checkbox" checked={lateOnly} onChange={(e) => { setLateOnly(e.target.checked); }} data-testid="open-lines-late-only" />
          {t("purchasing.openLines.lateOnly")}
        </label>
      </div>
      {data ? (
        <p className="mb-3 flex flex-wrap items-center gap-3 text-sm" data-testid="open-lines-summary">
          <span>{t("purchasing.openLines.summary", { lines: data.lines.length, late: data.lateLines })}</span>
          {data.totals.map((total) => (
            <span key={total.currency} className="tabular font-medium" dir="ltr">{formatMoney(total.amount, total.currency)}</span>
          ))}
        </p>
      ) : null}
      <DataGrid<OpenLine>
        label="nav.openOrderLines"
        columns={columns}
        data={data?.lines ?? []}
        rowKey={(row) => `${row.orderId}:${String(row.lineNo)}`}
        loading={report.isPending && Boolean(companyId)}
        emptyTitle={t("purchasing.openLines.emptyTitle")}
        emptyDescription={t("purchasing.openLines.emptyDescription")}
        onOpen={(row) => { const route = recordRoute("purchase_order", row.orderId); if (route) { void navigate(route); } }}
      />
    </>
  );
}
