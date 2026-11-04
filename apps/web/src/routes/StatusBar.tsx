import { cn } from "@quicker/ui";
import { Check, ChevronRight } from "lucide-react";
import type { TFunction } from "i18next";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

export interface FlowStep {
  id: string;
  label: string;
}

/** Where the document stands: on one of the steps (with the label to show there), or off the path (cancelled, rejected, on hold) with a tone. */
export interface FlowState {
  step: string | null;
  label: string;
  tone?: "warning" | "danger" | "neutral" | undefined;
}

export interface Flow {
  steps: FlowStep[];
  state: FlowState;
}

/**
 * The bar at the top of a document (Odoo's status bar): the actions on one side, only those the document can take
 * next, and the document's path on the other, every step named, the current one highlighted, the ones behind it
 * ticked. A document off its path (cancelled, rejected, on hold) shows that in its own colour at the end.
 */
export function StatusBar({ steps, state, actions }: Flow & { actions?: ReactNode }) {
  const { t } = useTranslation();
  const at = steps.findIndex((s) => s.id === state.step);
  const toneClass = state.tone === "danger" ? "bg-danger text-danger-fg" : state.tone === "warning" ? "bg-warning text-warning-fg" : state.tone === "neutral" ? "bg-fg-muted text-surface" : "bg-accent text-accent-fg";
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface px-3 py-2" data-testid="status-bar">
      <div className="flex flex-wrap items-center gap-2">{actions}</div>
      <ol className="flex max-w-full items-center gap-0.5 overflow-x-auto text-xs" aria-label={t("document.progress")}>
        {steps.map((s, i) => {
          const current = at === i;
          const done = at > i;
          return (
            <li key={s.id} className="flex items-center gap-0.5">
              <span
                aria-current={current ? "step" : undefined}
                data-testid={current ? "doc-status" : undefined}
                className={cn("flex items-center gap-1 whitespace-nowrap rounded-full px-2.5 py-1", current ? `${toneClass} font-medium` : done ? "text-fg-muted" : "text-fg-subtle")}
              >
                {done ? <Check className="size-3" aria-hidden="true" /> : null}
                {current ? state.label : s.label}
              </span>
              {i < steps.length - 1 ? <ChevronRight className="size-3 shrink-0 text-fg-subtle rtl:-scale-x-100" aria-hidden="true" /> : null}
            </li>
          );
        })}
        {at < 0 ? (
          <li className="ms-2 flex items-center">
            <span aria-current="step" data-testid="doc-status" className={cn("whitespace-nowrap rounded-full px-2.5 py-1 font-medium", toneClass)}>{state.label}</span>
          </li>
        ) : null}
      </ol>
    </div>
  );
}

const offPath: Record<string, FlowState["tone"]> = { rejected: "danger", on_hold: "danger", blocked: "danger", expired: "warning", cancelled: "neutral", reversed: "neutral", closed: "neutral" };

function flow(prefix: "sales" | "purchasing", stepIds: string[], status: string, t: TFunction, override?: Partial<FlowState>): Flow {
  const label = (id: string): string => t(`${prefix}.statuses.${id}`, { defaultValue: id });
  const steps = stepIds.map((id) => ({ id, label: label(id) }));
  const state: FlowState = steps.some((s) => s.id === status)
    ? { step: status, label: label(status) }
    : { step: null, label: label(status), tone: offPath[status] ?? "neutral" };
  return { steps, state: { ...state, ...override } };
}

/** The paths of the sales documents; a partly shipped order stands on the "shipped" step in amber, a settled invoice on "paid". */
export function salesFlow(kind: "quotation" | "order" | "shipment" | "invoice", status: string, t: TFunction, options?: { paid?: boolean; creditNote?: boolean }): Flow {
  switch (kind) {
    case "quotation":
      return flow("sales", ["draft", "sent", "accepted", "converted"], status, t);
    case "order":
      return status === "partially_shipped"
        ? flow("sales", ["draft", "confirmed", "shipped", "invoiced"], "shipped", t, { label: t("sales.statuses.partially_shipped"), tone: "warning" })
        : flow("sales", ["draft", "confirmed", "shipped", "invoiced"], status, t);
    case "shipment":
      return flow("sales", ["draft", "picking", "posted"], status, t);
    default:
      if (options?.creditNote) {
        return flow("sales", ["draft", "posted"], status, t);
      }
      return status === "posted" && options?.paid
        ? flow("sales", ["draft", "posted", "paid"], "paid", t)
        : flow("sales", ["draft", "posted", "paid"], status, t);
  }
}

/** The paths of the purchasing documents; a partly received order stands on "received" in amber, a settled invoice on "paid". */
export function purchaseFlow(kind: "order" | "receipt" | "invoice", status: string, t: TFunction, options?: { paid?: boolean }): Flow {
  switch (kind) {
    case "order":
      return status === "partially_received"
        ? flow("purchasing", ["draft", "pending_approval", "approved", "sent", "received"], "received", t, { label: t("purchasing.statuses.partially_received"), tone: "warning" })
        : flow("purchasing", ["draft", "pending_approval", "approved", "sent", "received"], status, t);
    case "receipt":
      return flow("purchasing", ["draft", "posted"], status, t);
    default:
      return status === "posted" && options?.paid
        ? flow("purchasing", ["draft", "pending_approval", "approved", "posted", "paid"], "paid", t)
        : flow("purchasing", ["draft", "pending_approval", "approved", "posted", "paid"], status, t);
  }
}
