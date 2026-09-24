import { useNavigate, useSearch } from "@tanstack/react-router";
import { useCallback, useMemo } from "react";

/** The list screens that open one record from the URL (?open=id). */
export type RecordScreen =
  | "/purchasing/requisitions"
  | "/purchasing/rfqs"
  | "/purchasing/agreements"
  | "/purchasing/orders"
  | "/purchasing/receipts"
  | "/purchasing/invoices"
  | "/purchasing/landed-costs"
  | "/purchasing/returns"
  | "/purchasing/suppliers"
  | "/inventory/adjustments"
  | "/inventory/revaluations"
  | "/inventory/assemblies"
  | "/inventory/transfers"
  | "/inventory/counts"
  | "/inventory/items"
  | "/inventory/warehouses"
  | "/accounting/journals"
  | "/accounting/journal-entries"
  | "/banking/payments"
  | "/payables/proposals"
  | "/companies";

/** Where each kind of record opens: documents by the type their number was issued under, masters by their audit entity type. */
const screens: Record<string, RecordScreen> = {
  purchase_requisition: "/purchasing/requisitions",
  purchase_rfq: "/purchasing/rfqs",
  purchase_agreement: "/purchasing/agreements",
  purchase_order: "/purchasing/orders",
  purchase_receipt: "/purchasing/receipts",
  purchase_invoice: "/purchasing/invoices",
  landed_cost_document: "/purchasing/landed-costs",
  purchase_return: "/purchasing/returns",
  stock_adjustment: "/inventory/adjustments",
  stock_revaluation: "/inventory/revaluations",
  stock_assembly: "/inventory/assemblies",
  stock_transfer: "/inventory/transfers",
  stock_count: "/inventory/counts",
  manual_journal: "/accounting/journals",
  journal_entry: "/accounting/journal-entries",
  bank_payment: "/banking/payments",
  payment_proposal: "/payables/proposals",
  item: "/inventory/items",
  partner: "/purchasing/suppliers",
  warehouse: "/inventory/warehouses",
  company: "/companies",
};

/** The screen a record opens on, with the record open; null for kinds that have no screen of their own. */
export function recordRoute(type: string, id: string): { to: RecordScreen; search: { open: string } } | null {
  const to = screens[type];
  return to ? { to, search: { open: id } } : null;
}

/**
 * The record a list screen has open, kept in the URL (?open=id) so a link, the command palette, a ledger line or the
 * back button can reach it; closing clears it.
 */
export function useOpenRecord(screen: RecordScreen): [string | null, (id: string | null) => void] {
  const search: { open?: string } = useSearch({ strict: false });
  const navigate = useNavigate();
  const setOpen = useCallback((id: string | null) => { void navigate({ to: screen, search: id ? { open: id } : {} }); }, [navigate, screen]);
  return [search.open ?? null, setOpen];
}

/** A document started from another one: a receipt from its order, an invoice from an order or receipt, a return from a receipt, a debit note from a return. */
export interface FollowOnSource {
  type: string;
  id: string;
}

/** The search that opens a new document filled from its source (?from=type:id). */
export function followOn(type: string, id: string): { from: string } {
  return { from: `${type}:${id}` };
}

/** The source a list screen was asked to start a new document from, and a way to clear it once the form is filled. */
export function useFollowOnSource(screen: RecordScreen): [FollowOnSource | null, () => void] {
  const search: { from?: string } = useSearch({ strict: false });
  const navigate = useNavigate();
  const raw = search.from;
  const source = useMemo(() => {
    const at = raw?.indexOf(":") ?? -1;
    return raw && at > 0 && at < raw.length - 1 ? { type: raw.slice(0, at), id: raw.slice(at + 1) } : null;
  }, [raw]);
  const clear = useCallback(() => { void navigate({ to: screen, search: {}, replace: true }); }, [navigate, screen]);
  return [source, clear];
}
