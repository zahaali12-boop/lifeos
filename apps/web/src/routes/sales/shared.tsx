import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { CalendarClock, Mail, NotebookPen, Phone, SquareCheck, Users } from "lucide-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatNumber } from "../../lib/format";
import { TextField } from "../common";
import { ItemCodeField } from "../inventory/ItemCodeField";

export type CustomerAccount = components["schemas"]["CustomerAccountSummary"];
export type CustomerGroup = components["schemas"]["CustomerGroupSummary"];
export type SalesRep = components["schemas"]["SalesRepSummary"];
export type CommissionPlan = components["schemas"]["CommissionPlanSummary"];
export type PipelineStage = components["schemas"]["PipelineStageSummary"];
export type Opportunity = components["schemas"]["OpportunitySummary"];
export type PipelineTotal = components["schemas"]["PipelineTotal"];
export type CrmActivity = components["schemas"]["CrmActivitySummary"];
export type Customer360 = components["schemas"]["Customer360"];
export type Quotation = components["schemas"]["QuotationSummary"];
export type QuotationLine = components["schemas"]["QuotationLineSummary"];
export type SalesOrder = components["schemas"]["OrderSummary"];
export type SalesOrderLine = components["schemas"]["OrderLineSummary"];
export type Shipment = components["schemas"]["ShipmentSummary"];
export type ShipmentLine = components["schemas"]["ShipmentLineSummary"];

export function useCustomerGroups() {
  return useQuery({ queryKey: ["customer-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/customer-groups")) });
}

export function useSalesReps() {
  return useQuery({ queryKey: ["sales-reps"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/sales-reps")) });
}

export function useCommissionPlans() {
  return useQuery({ queryKey: ["commission-plans"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/commission-plans")) });
}

export function usePipelineStages() {
  return useQuery({ queryKey: ["pipeline-stages"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/pipeline-stages")) });
}

export function useCustomerPostingGroups() {
  return useQuery({ queryKey: ["posting-groups", "partner_customer"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups", { params: { query: { kind: "partner_customer" } } })) });
}

/** Minor units of the currencies ISO 4217 gives other than two; amounts show them only when they are there. */
const minorUnits: Record<string, number> = { IQD: 3, KWD: 3, BHD: 3, OMR: 3, JOD: 3, TND: 3, LYD: 3, JPY: 0, KRW: 0, CLP: 0, ISK: 0, VND: 0 };

/** An amount with its currency code, digits as the currency has them (a dinar's fils only when there are any). */
export function money(amount: number | string | null | undefined, currency: string): string {
  if (amount === null || amount === undefined || amount === "") {
    return "";
  }
  const digits = minorUnits[currency] ?? 2;
  return `${formatNumber(amount, { minimumFractionDigits: digits === 2 ? 2 : 0, maximumFractionDigits: digits })} ${currency}`;
}

export function Money({ amount, currency, testId }: { amount: number | string | null | undefined; currency: string; testId?: string }) {
  return (
    <span className="tabular" dir="ltr" data-testid={testId}>
      {money(amount, currency)}
    </span>
  );
}

/** Totals per currency on one line each: never summed across currencies. */
export function Totals({ totals, weighted = true }: { totals: PipelineTotal[]; weighted?: boolean }) {
  const { t } = useTranslation();
  if (totals.length === 0) {
    return <span className="text-fg-muted">—</span>;
  }
  return (
    <span className="flex flex-col gap-0.5">
      {totals.map((total) => (
        <span key={total.currency} className="tabular" dir="ltr">
          {money(weighted ? total.weighted : total.amount, total.currency)}
          <span className="text-fg-muted" dir="auto">
            {" "}
            · {t("sales.dealsCount", { count: Number(total.count) })}
          </span>
        </span>
      ))}
    </span>
  );
}

const creditTones: Record<string, "success" | "warning" | "danger"> = { ok: "success", on_hold: "warning", blocked: "danger" };

export function CreditBadge({ status, hideOk = false }: { status: string; hideOk?: boolean }) {
  const { t } = useTranslation();
  if (hideOk && status === "ok") {
    return null;
  }
  return (
    <Badge tone={creditTones[status] ?? "neutral"} data-testid="credit-badge">
      {t(`sales.creditStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

const outcomeTones: Record<string, "info" | "success" | "danger"> = { open: "info", won: "success", lost: "danger" };

export function OutcomeBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={outcomeTones[status] ?? "neutral"} data-testid="outcome-badge">
      {t(`sales.outcomes.${status}`, { defaultValue: status })}
    </Badge>
  );
}

const activityIcons = { call: Phone, meeting: Users, email: Mail, task: SquareCheck, note: NotebookPen } as const;

export function ActivityIcon({ kind }: { kind: string }) {
  const Icon = kind in activityIcons ? activityIcons[kind as keyof typeof activityIcons] : CalendarClock;
  return <Icon className="size-4 shrink-0 text-fg-muted" aria-hidden="true" />;
}

/** Whole days since an instant, for "in this stage for 12 days". */
export function daysSince(iso: string, now: Date = new Date()): number {
  return Math.max(0, Math.floor((now.getTime() - new Date(iso).getTime()) / 86_400_000));
}

// ------------------------------------------------------------------ quotations and orders (roadmap 5.4)

const docTones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", sent: "info", accepted: "success", rejected: "danger", converted: "neutral",
  confirmed: "success", on_hold: "danger", cancelled: "neutral",
  open: "info", backordered: "warning",
  partially_shipped: "warning", shipped: "success", fulfilled: "success",
  posted: "success", reversed: "neutral", picking: "info",
};

export function SalesStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={docTones[status] ?? "neutral"} data-testid="doc-status">
      {t(`sales.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** Number inputs travel as strings so nothing is ever a JavaScript float on the way to the API. */
export function num(value: string, fallback = 0): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

export function optionalNum(value: string): number | null {
  return value.trim() ? num(value) : null;
}

/** One editable quotation or order line: the item by code, a quantity in a unit, a price; drop-ship only on orders. */
export interface SalesLineForm {
  itemCode: string;
  quantity: string;
  uom: string;
  price: string;
  discountPct: string;
  dropShip: boolean;
}

export const emptySalesLine = (): SalesLineForm => ({ itemCode: "", quantity: "1", uom: "", price: "", discountPct: "0", dropShip: false });

export function salesLineBodies(lines: SalesLineForm[]) {
  return lines.map((l) => ({ itemCode: l.itemCode, quantity: num(l.quantity), uom: l.uom || null, unitPrice: optionalNum(l.price), discountPct: num(l.discountPct) }));
}

export function orderLineBodies(lines: SalesLineForm[]) {
  return lines.map((l) => ({ itemCode: l.itemCode, quantity: num(l.quantity), uom: l.uom || null, unitPrice: optionalNum(l.price), discountPct: num(l.discountPct), dropShip: l.dropShip }));
}

/** The lines editor shared by quotations and orders: item, quantity, unit, price and discount; drop-ship only for orders. */
export function SalesLinesEditor({ lines, onChange, showDropShip = false }: { lines: SalesLineForm[]; onChange: (lines: SalesLineForm[]) => void; showDropShip?: boolean }) {
  const { t } = useTranslation();
  const patch = (index: number, change: Partial<SalesLineForm>): void => { onChange(lines.map((l, i) => (i === index ? { ...l, ...change } : l))); };
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">{t("sales.lines")}</h3>
        <Button type="button" variant="ghost" size="sm" onClick={() => { onChange([...lines, emptySalesLine()]); }} data-testid="add-line">
          {t("sales.addLine")}
        </Button>
      </div>
      {lines.length > 0 ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("sales.itemCode")}</TableHead>
              <TableHead>{t("sales.quantity")}</TableHead>
              <TableHead>{t("sales.uom")}</TableHead>
              <TableHead>{t("sales.unitPrice")}</TableHead>
              <TableHead>{t("sales.discountPct")}</TableHead>
              {showDropShip ? <TableHead>{t("sales.dropShip")}</TableHead> : null}
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {lines.map((line, index) => (
              <TableRow key={index}>
                <TableCell><ItemCodeField aria-label={t("sales.itemCode")} value={line.itemCode} onChange={(code) => { patch(index, { itemCode: code }); }} className="w-28" data-testid={`line-item-${String(index)}`} /></TableCell>
                <TableCell><TextField aria-label={t("sales.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { patch(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`line-qty-${String(index)}`} /></TableCell>
                <TableCell><TextField aria-label={t("sales.uom")} value={line.uom} onChange={(e) => { patch(index, { uom: e.target.value.toUpperCase() }); }} dir="ltr" className="w-20" placeholder={t("sales.baseUom")} data-testid={`line-uom-${String(index)}`} /></TableCell>
                <TableCell><TextField aria-label={t("sales.unitPrice")} inputMode="decimal" value={line.price} onChange={(e) => { patch(index, { price: e.target.value }); }} dir="ltr" className="w-24" placeholder={t("sales.autoPrice")} data-testid={`line-price-${String(index)}`} /></TableCell>
                <TableCell><TextField aria-label={t("sales.discountPct")} inputMode="decimal" value={line.discountPct} onChange={(e) => { patch(index, { discountPct: e.target.value }); }} dir="ltr" className="w-20" data-testid={`line-discount-${String(index)}`} /></TableCell>
                {showDropShip ? (
                  <TableCell>
                    <input type="checkbox" aria-label={t("sales.dropShip")} checked={line.dropShip} onChange={(e) => { patch(index, { dropShip: e.target.checked }); }} data-testid={`line-drop-ship-${String(index)}`} />
                  </TableCell>
                ) : null}
                <TableCell>
                  <Button type="button" variant="ghost" size="sm" onClick={() => { onChange(lines.filter((_, i) => i !== index)); }}>
                    {t("workflow.remove")}
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <p className="text-xs text-fg-muted">{t("sales.noLines")}</p>
      )}
    </div>
  );
}

/** Read-only lines of a priced, taxed document: quotation or confirmed order lines, in the document's currency. */
export function SalesLinesTable({ lines, currency, testId = "doc-lines", dropShipActions }: { lines: (QuotationLine | SalesOrderLine)[]; currency: string; testId?: string; dropShipActions?: (line: SalesOrderLine) => ReactNode }) {
  const { t } = useTranslation();
  const hasStatus = Boolean(lines[0] && "status" in lines[0]);
  return (
    <Table data-testid={testId}>
      <TableHeader>
        <TableRow>
          <TableHead>#</TableHead>
          <TableHead>{t("sales.item")}</TableHead>
          <TableHead>{t("sales.quantity")}</TableHead>
          <TableHead>{t("sales.unitPrice")}</TableHead>
          <TableHead>{t("sales.discountPct")}</TableHead>
          <TableHead>{t("sales.net")}</TableHead>
          <TableHead>{t("tax.code")}</TableHead>
          <TableHead>{t("sales.taxAmount")}</TableHead>
          {hasStatus ? <TableHead>{t("common.status")}</TableHead> : null}
          {dropShipActions ? <TableHead /> : null}
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((l) => (
          <TableRow key={l.id} data-testid="doc-line">
            <TableCell>{String(l.lineNo)}</TableCell>
            <TableCell dir="auto">{l.itemCode}{l.description ? ` · ${l.description}` : ""}</TableCell>
            <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
            <TableCell className="tabular" dir="ltr">{formatNumber(l.unitPrice, { maximumFractionDigits: 4 })}</TableCell>
            <TableCell className="tabular" dir="ltr">{formatNumber(l.discountPct, { maximumFractionDigits: 2 })}%</TableCell>
            <TableCell className="tabular" dir="ltr">{money(l.netAmount, currency)}</TableCell>
            <TableCell dir="ltr">{l.taxCode ?? "—"}</TableCell>
            <TableCell className="tabular" dir="ltr">{money(l.taxAmount, currency)}</TableCell>
            {hasStatus ? (
              <TableCell>
                <span className="flex flex-wrap items-center gap-1">
                  <SalesStatus status={(l as SalesOrderLine).status} />
                  {(l as SalesOrderLine).dropShip ? <Badge tone="accent" data-testid="drop-ship-badge">{t("sales.dropShip")}</Badge> : null}
                </span>
              </TableCell>
            ) : null}
            {dropShipActions ? <TableCell>{(l as SalesOrderLine).dropShip ? dropShipActions(l as SalesOrderLine) : null}</TableCell> : null}
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
