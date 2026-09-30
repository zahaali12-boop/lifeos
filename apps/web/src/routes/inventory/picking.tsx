import { Badge } from "@quicker/ui";
import { useTranslation } from "react-i18next";
import type { components } from "../../api/schema";
import { formatDate, formatNumber } from "../../lib/format";

export type PickList = components["schemas"]["PickListInfo"];
export type PickListLine = components["schemas"]["PickListLineInfo"];
export type PickListRow = components["schemas"]["PickListRow"];
export type Allocation = components["schemas"]["ShipmentAllocation"];

/** Pick lists that are still with the warehouse. */
export const livePickStatuses = ["released", "in_progress", "picked"] as const;

const tones: Record<string, "neutral" | "accent" | "success" | "warning" | "danger" | "info"> = {
  released: "info",
  in_progress: "accent",
  picked: "success",
  closed: "neutral",
  cancelled: "neutral",
  open: "info",
  short: "warning",
};

/** A pick list's or a pick line's status. */
export function PickStatus({ status, testId = "pick-status" }: { status: string; testId?: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid={testId}>
      {t(`picking.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** Lines done out of lines to do, as a bar with the numbers beside it (the bar is never the only signal). */
export function PickProgress({ done, total, testId }: { done: number | string; total: number | string; testId?: string }) {
  const { t } = useTranslation();
  const d = Number(done);
  const n = Number(total);
  const percent = n > 0 ? Math.round((d / n) * 100) : 0;
  return (
    <span className="flex items-center gap-2" data-testid={testId}>
      <span className="h-2 w-20 overflow-hidden rounded-full bg-surface-sunken" role="progressbar" aria-valuemin={0} aria-valuemax={n} aria-valuenow={d} aria-label={t("picking.progress", { done: d, total: n })}>
        <span className="block h-full rounded-full bg-accent" style={{ inlineSize: `${String(percent)}%` }} />
      </span>
      <span className="tabular text-xs text-fg-muted" dir="ltr">
        {formatNumber(d)}/{formatNumber(n)}
      </span>
    </span>
  );
}

/** Where a shipment line's quantity comes from: one row per bin and lot, with the serials. */
export function Allocations({ parts, uom }: { parts: Allocation[]; uom?: string }) {
  const { t } = useTranslation();
  if (parts.length === 0) {
    return <span className="text-fg-muted">—</span>;
  }
  return (
    <ul className="flex flex-col gap-0.5 text-xs" data-testid="allocations">
      {parts.map((part, index) => (
        <li key={`${part.binId ?? ""}-${part.lotId ?? ""}-${String(index)}`} className="flex flex-wrap items-baseline gap-x-1.5" data-testid="allocation">
          {part.binCode ? <span className="font-medium" dir="ltr">{part.binCode}</span> : null}
          {part.lotNumber ? (
            <span className="text-fg-muted" dir="ltr">
              {t("picking.lotShort", { lot: part.lotNumber })}
              {part.expiresOn ? ` · ${formatDate(part.expiresOn)}` : ""}
            </span>
          ) : null}
          <span className="tabular" dir="ltr">
            × {formatNumber(part.quantity, { maximumFractionDigits: 3 })}
            {uom ? ` ${uom}` : ""}
          </span>
          {part.serialNumbers.length > 0 ? (
            <span className="basis-full text-fg-muted" dir="ltr">
              {part.serialNumbers.join(", ")}
            </span>
          ) : null}
        </li>
      ))}
    </ul>
  );
}
