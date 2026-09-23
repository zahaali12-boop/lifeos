import { Badge, Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatMoney, formatNumber } from "../../lib/format";
import { SelectField, TextField } from "../common";

export function usePaymentTerms() {
  return useQuery({ queryKey: ["payment-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/payment-terms")) });
}

export function useDeliveryTerms() {
  return useQuery({ queryKey: ["delivery-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/delivery-terms")) });
}

export function useSupplierGroups() {
  return useQuery({ queryKey: ["supplier-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/supplier-groups")) });
}

export function useWhtCodes() {
  return useQuery({ queryKey: ["wht-codes"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/wht-codes")) });
}

export function useSupplierPostingGroups() {
  return useQuery({ queryKey: ["posting-groups", "partner_supplier"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups", { params: { query: { kind: "partner_supplier" } } })) });
}

export function HoldBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  if (status === "none") {
    return null;
  }
  return (
    <Badge tone="warning" data-testid="hold-badge">
      {t(`partners.holdStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** Number inputs travel as strings so nothing is ever a JavaScript float on the way to the API. */
export function num(value: string, fallback = 0): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

// ------------------------------------------------------------------ purchasing documents (roadmap 4.2)

export type Requisition = components["schemas"]["RequisitionSummary"];
export type Rfq = components["schemas"]["RfqSummary"];
export type PurchaseOrder = components["schemas"]["PurchaseOrderSummary"];
export type Agreement = components["schemas"]["BlanketAgreementSummary"];

export function useSuppliers(companyId: string) {
  return useQuery({
    queryKey: ["suppliers", companyId, "", ""],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/partners/suppliers", { params: { query: { companyId } } })),
  });
}

export function useAgreements(companyId: string) {
  return useQuery({
    queryKey: ["agreements", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/agreements", { params: { query: { companyId } } })),
  });
}

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", pending_approval: "warning", approved: "success", ordered: "success", sent: "info", active: "success", awarded: "success",
  partially_received: "info", received: "success", closed: "neutral", rejected: "danger", cancelled: "neutral", invited: "neutral", responded: "info", declined: "danger",
  open: "info", consumed: "neutral", released: "neutral",
};

export function PurchaseStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`purchasing.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** One editable document line: the item by code, a quantity in a unit, a price. Strings until the request is built. */
export interface LineForm {
  itemCode: string;
  description: string;
  quantity: string;
  uom: string;
  price: string;
  supplierId: string;
  blanketLineId: string;
}

export const emptyLine = (): LineForm => ({ itemCode: "", description: "", quantity: "1", uom: "", price: "", supplierId: "", blanketLineId: "" });

export function LinesEditor({ lines, onChange, showPrice = true, priceLabel, suppliers, showDescription = false }: { lines: LineForm[]; onChange: (lines: LineForm[]) => void; showPrice?: boolean; priceLabel?: string; suppliers?: { partnerId: string; partnerCode: string }[]; showDescription?: boolean }) {
  const { t } = useTranslation();
  const patch = (index: number, change: Partial<LineForm>): void => { onChange(lines.map((l, i) => (i === index ? { ...l, ...change } : l))); };
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">{t("purchasing.lines")}</h3>
        <Button type="button" variant="ghost" size="sm" onClick={() => { onChange([...lines, emptyLine()]); }} data-testid="add-line">
          {t("purchasing.addLine")}
        </Button>
      </div>
      {lines.length > 0 ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("purchasing.itemCode")}</TableHead>
              {showDescription ? <TableHead>{t("purchasing.description")}</TableHead> : null}
              <TableHead>{t("purchasing.quantity")}</TableHead>
              <TableHead>{t("purchasing.uom")}</TableHead>
              {showPrice ? <TableHead>{priceLabel ?? t("purchasing.unitPrice")}</TableHead> : null}
              {suppliers ? <TableHead>{t("purchasing.suggestedSupplier")}</TableHead> : null}
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {lines.map((line, index) => (
              <TableRow key={index}>
                <TableCell><TextField aria-label={t("purchasing.itemCode")} value={line.itemCode} onChange={(e) => { patch(index, { itemCode: e.target.value.toUpperCase() }); }} dir="ltr" className="w-28" data-testid={`line-item-${String(index)}`} /></TableCell>
                {showDescription ? <TableCell><TextField aria-label={t("purchasing.description")} value={line.description} onChange={(e) => { patch(index, { description: e.target.value }); }} className="w-40" /></TableCell> : null}
                <TableCell><TextField aria-label={t("purchasing.quantity")} inputMode="decimal" value={line.quantity} onChange={(e) => { patch(index, { quantity: e.target.value }); }} dir="ltr" className="w-20" data-testid={`line-qty-${String(index)}`} /></TableCell>
                <TableCell><TextField aria-label={t("purchasing.uom")} value={line.uom} onChange={(e) => { patch(index, { uom: e.target.value.toUpperCase() }); }} dir="ltr" className="w-20" placeholder={t("purchasing.baseUom")} data-testid={`line-uom-${String(index)}`} /></TableCell>
                {showPrice ? <TableCell><TextField aria-label={priceLabel ?? t("purchasing.unitPrice")} inputMode="decimal" value={line.price} onChange={(e) => { patch(index, { price: e.target.value }); }} dir="ltr" className="w-24" data-testid={`line-price-${String(index)}`} /></TableCell> : null}
                {suppliers ? (
                  <TableCell>
                    <SelectField aria-label={t("purchasing.suggestedSupplier")} value={line.supplierId} onChange={(e) => { patch(index, { supplierId: e.target.value }); }} data-testid={`line-supplier-${String(index)}`}>
                      <option value="">{t("purchasing.noSupplier")}</option>
                      {suppliers.map((s) => (
                        <option key={s.partnerId} value={s.partnerId}>{s.partnerCode}</option>
                      ))}
                    </SelectField>
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
        <p className="text-xs text-fg-muted">{t("purchasing.noLines")}</p>
      )}
    </div>
  );
}

/** The optional decimal fields travel as numbers only when filled, so an empty price stays "not given". */
export function optionalNum(value: string): number | null {
  return value.trim() ? num(value) : null;
}

export function orderLineBodies(lines: LineForm[]) {
  return lines.map((l) => ({ itemCode: l.itemCode, description: l.description || null, quantity: num(l.quantity), uom: l.uom || null, unitPrice: optionalNum(l.price), discountPct: 0, blanketLineId: l.blanketLineId || null }));
}

/** Read-only lines of a document, with the money in the document's currency. */
export function LinesTable({ lines, currency, testId = "doc-lines" }: { lines: { id: string; lineNo: string | number; itemCode?: string | null; description?: string | null; quantity: number | string; uomCode: string; unitPrice?: number | string | null; netAmount?: number | string; status?: string }[]; currency?: string; testId?: string }) {
  const { t } = useTranslation();
  return (
    <Table data-testid={testId}>
      <TableHeader>
        <TableRow>
          <TableHead>#</TableHead>
          <TableHead>{t("purchasing.item")}</TableHead>
          <TableHead>{t("purchasing.quantity")}</TableHead>
          {currency ? <TableHead>{t("purchasing.unitPrice")}</TableHead> : null}
          {currency ? <TableHead>{t("purchasing.net")}</TableHead> : null}
          <TableHead>{t("common.status")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((l) => (
          <TableRow key={l.id} data-testid="doc-line">
            <TableCell>{String(l.lineNo)}</TableCell>
            <TableCell dir="auto">{l.itemCode ?? ""}{l.description ? ` · ${l.description}` : ""}</TableCell>
            <TableCell className="tabular" dir="ltr">{formatNumber(l.quantity, { maximumFractionDigits: 3 })} {l.uomCode}</TableCell>
            {currency ? <TableCell className="tabular" dir="ltr">{l.unitPrice === null || l.unitPrice === undefined ? "" : formatNumber(l.unitPrice, { maximumFractionDigits: 4 })}</TableCell> : null}
            {currency ? <TableCell className="tabular" dir="ltr">{l.netAmount === undefined ? "" : formatMoney(l.netAmount, currency)}</TableCell> : null}
            <TableCell>{l.status ? <PurchaseStatus status={l.status} /> : null}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
