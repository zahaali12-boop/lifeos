import { Badge, Button } from "@quicker/ui";
import { Printer } from "lucide-react";
import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { formatDateTime, formatNumber, localized } from "../../lib/format";
import { lineAmount, type Catalogue, type LocalOrder, type Register } from "./store";

/**
 * The receipt as the till shows and prints it from what it has on the device: the server's number once synced (the
 * device reference before), the lines, the tenders and the change. The server's receipt, with the e-invoice QR, prints
 * from the back office once the order is posted.
 */
export function Receipt({ order, register, catalogue, companyName, onPrint }: { order: LocalOrder; register: Register; catalogue: Catalogue; companyName: string; onPrint?: () => void }) {
  const { t } = useTranslation();
  const minor = Number(catalogue.minorUnits);
  const money = (value: string | number): string => formatNumber(value, { minimumFractionDigits: minor, maximumFractionDigits: minor });
  const reference = order.server?.number ?? t("pos.till.deviceReference", { reference: order.deviceOrderId.slice(0, 8).toUpperCase() });
  return (
    <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4 print:border-0 print:p-0" data-testid="till-receipt">
      <div className="text-center">
        <p className="text-base font-semibold">{companyName}</p>
        <p className="text-sm text-fg-muted">{localized(register.name)}</p>
        <p className="mt-1 text-sm font-medium">{order.kind === "refund" ? t("pos.till.refundReceipt") : t("pos.till.receipt")}</p>
        <p className="text-sm" dir="ltr" data-testid="till-receipt-number">{reference}</p>
        <p className="text-xs text-fg-muted" dir="ltr">{formatDateTime(order.orderedAt)}</p>
        <div className="mt-1 flex justify-center gap-2 print:hidden">
          <Badge tone={order.status === "synced" ? "success" : order.status === "failed" ? "danger" : "warning"} data-testid="till-receipt-status">{t(`pos.till.orderStatuses.${order.status}`)}</Badge>
          {order.server?.einvoiceStatus ? <Badge tone="info">{t(`einvoicing.submissionStatuses.${order.server.einvoiceStatus}`, { defaultValue: order.server.einvoiceStatus })}</Badge> : null}
        </div>
      </div>
      {order.partnerName ? <p className="text-sm">{t("pos.till.customer")}: {order.partnerName}</p> : null}
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-border text-start text-xs text-fg-muted">
            <th className="py-1 text-start font-medium">{t("pos.till.item")}</th>
            <th className="py-1 text-end font-medium">{t("pos.till.quantity")}</th>
            <th className="py-1 text-end font-medium">{t("pos.till.amount")}</th>
          </tr>
        </thead>
        <tbody>
          {order.lines.map((line, index) => (
            <tr key={`${line.itemId}-${index}`} className="border-b border-border/60">
              <td className="py-1">
                <span dir="auto">{localized(line.name)}</span>
                <span className="block text-xs text-fg-muted" dir="ltr">{line.code} · {money(line.unitPrice)}{line.discountPct !== "0" ? ` · -${line.discountPct}%` : ""}</span>
              </td>
              <td className="py-1 text-end tabular-nums"><bdi dir="ltr">{formatNumber(line.quantity)}</bdi></td>
              <td className="py-1 text-end tabular-nums"><bdi dir="ltr">{money(lineAmount(line, minor))}</bdi></td>
            </tr>
          ))}
        </tbody>
      </table>
      <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-0.5 text-sm">
        <dt className="text-fg-muted">{t("pos.till.net")}</dt>
        <dd className="text-end tabular-nums" dir="ltr">{money(order.totals.net)}</dd>
        <dt className="text-fg-muted">{t("pos.till.tax")}</dt>
        <dd className="text-end tabular-nums" dir="ltr">{money(order.totals.tax)}</dd>
        {order.totals.rounding !== "0" ? (
          <>
            <dt className="text-fg-muted">{t("pos.till.rounding")}</dt>
            <dd className="text-end tabular-nums" dir="ltr">{money(order.totals.rounding)}</dd>
          </>
        ) : null}
        <dt className="text-base font-semibold">{t("pos.till.totalDue")}</dt>
        <dd className="text-end text-base font-semibold tabular-nums" dir="ltr" data-testid="till-receipt-total">{money(order.totals.due)} {catalogue.currency}</dd>
        {order.payments.map((payment, index) => (
          <Fragment key={`${payment.method}-${index}`}>
            <dt className="text-fg-muted">{t(`pos.paymentMethods.${payment.method}`)}{payment.reference ? <span dir="ltr"> {payment.reference}</span> : null}</dt>
            <dd className="text-end tabular-nums" dir="ltr">{money(payment.amount)}</dd>
          </Fragment>
        ))}
        {order.changeGiven !== "0" ? (
          <>
            <dt className="text-fg-muted">{t("pos.till.change")}</dt>
            <dd className="text-end tabular-nums" dir="ltr" data-testid="till-receipt-change">{money(order.changeGiven)}</dd>
          </>
        ) : null}
      </dl>
      {order.error ? <p role="alert" className="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-xs text-danger" data-testid="till-receipt-error">{order.error}</p> : null}
      <p className="text-center text-xs text-fg-muted">{Object.keys(register.receiptFooter).length > 0 ? localized(register.receiptFooter) : t("pos.till.thankYou")}</p>
      {onPrint ? (
        <Button variant="secondary" className="gap-2 print:hidden" onClick={onPrint} data-testid="till-print">
          <Printer aria-hidden="true" />
          {t("pos.till.print")}
        </Button>
      ) : null}
    </div>
  );
}
