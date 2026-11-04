import { Table, TableBody, TableCell, TableHead, TableHeader, TableNumberCell, TableRow } from "@quicker/ui";
import type { ReactNode } from "react";
import { formatDate, formatMoney, formatNumber, localized } from "../../lib/format";
import { n } from "./invoices";

/**
 * The parts of an invoice opened from the trader's Sales and Supplier invoices screens (ADR-0035 §22): its facts, its
 * items and its payments, each lined up under its heading in Arabic and in English. A code, a number or a date reads
 * left to right inside its cell (an isolated run), never by turning the cell itself, so it stays under its heading.
 */
export function InvoiceFacts({ rows }: { rows: [string, ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[max-content_minmax(0,1fr)] items-center gap-x-6 gap-y-1.5 text-sm" data-testid="invoice-facts">
      {rows.map(([label, value]) => (
        <div key={label} className="contents">
          <dt className="text-fg-muted">{label}</dt>
          <dd className="min-w-0 break-words">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

interface LineLike {
  id: string;
  itemCode: string | null;
  itemName: Record<string, string> | null;
  description: string | null;
  quantity: number | string;
  uomCode: string | null;
  unitPrice: number | string;
  netAmount: number | string;
}

export interface InvoiceLinesLabels {
  item: string;
  quantity: string;
  unitPrice: string;
  net: string;
  total: string;
  paid: string;
  remaining: string;
}

/** The items with their quantities, prices and amounts; then the total, what was paid and what is left, each under the amount column. */
export function InvoiceLines({ lines, currency, total, paid, remaining, labels }: { lines: LineLike[]; currency: string; total: number | string; paid: number; remaining: number; labels: InvoiceLinesLabels }) {
  const summary: [string, number | string, string][] = [[labels.total, total, "font-semibold"], [labels.paid, paid, ""], [labels.remaining, remaining, "font-semibold"]];
  return (
    <Table data-testid="invoice-lines">
      <TableHeader>
        <TableRow>
          <TableHead>{labels.item}</TableHead>
          <TableHead className="text-end">{labels.quantity}</TableHead>
          <TableHead className="text-end">{labels.unitPrice}</TableHead>
          <TableHead className="text-end">{labels.net}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((line) => (
          <TableRow key={line.id} data-testid="invoice-line">
            <TableCell>
              {line.itemCode ? (
                <span className="flex flex-col">
                  <bdi dir="ltr" className="font-medium">{line.itemCode}</bdi>
                  <span className="text-xs text-fg-muted" dir="auto">{localized(line.itemName ?? {})}</span>
                </span>
              ) : <span dir="auto">{line.description ?? ""}</span>}
            </TableCell>
            <TableNumberCell dir="ltr">{formatNumber(n(line.quantity))} {line.uomCode ?? ""}</TableNumberCell>
            <TableNumberCell dir="ltr">{formatMoney(line.unitPrice, currency)}</TableNumberCell>
            <TableNumberCell dir="ltr">{formatMoney(line.netAmount, currency)}</TableNumberCell>
          </TableRow>
        ))}
        {summary.map(([label, amount, weight]) => (
          <TableRow key={label} className="bg-surface-sunken/40 hover:bg-surface-sunken/40">
            <TableCell colSpan={3} className={weight}>{label}</TableCell>
            <TableNumberCell dir="ltr" className={weight} data-testid={label === labels.remaining ? "dialog-remaining" : label === labels.paid ? "dialog-paid" : "dialog-total"}>{formatMoney(amount, currency)}</TableNumberCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

interface PaymentLike {
  id: string;
  settlementDate: string;
  settlingDocumentNumber: string;
  amountTc: number | string;
  currency: string;
}

/** Every payment recorded against the invoice, newest first: the day, the receipt or payment, the amount. */
export function InvoicePayments({ payments, labels, empty }: { payments: PaymentLike[]; labels: { date: string; document: string; amount: string }; empty: string }) {
  if (payments.length === 0) {
    return <p className="text-sm text-fg-muted" data-testid="no-payments">{empty}</p>;
  }
  return (
    <div data-testid="payments-list">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{labels.date}</TableHead>
            <TableHead>{labels.document}</TableHead>
            <TableHead className="text-end">{labels.amount}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {payments.map((p) => (
            <TableRow key={p.id} data-testid="payment-row">
              <TableCell dir="ltr">{formatDate(p.settlementDate)}</TableCell>
              <TableCell dir="ltr">{p.settlingDocumentNumber}</TableCell>
              <TableNumberCell dir="ltr">{formatMoney(p.amountTc, p.currency)}</TableNumberCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
