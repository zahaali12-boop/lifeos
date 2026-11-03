import { useNavigate, useSearch } from "@tanstack/react-router";
import { useCallback, useEffect, useMemo, useRef } from "react";

/** The list screens that open one record from the URL (?open=id). */
export type RecordScreen =
  | "/trader/offers"
  | "/trader/shipments"
  | "/trader/purchases"
  | "/purchasing/requisitions"
  | "/hr/employees"
  | "/hr/leave"
  | "/hr/payroll"
  | "/hr/pay-components"
  | "/purchasing/rfqs"
  | "/purchasing/agreements"
  | "/purchasing/orders"
  | "/purchasing/receipts"
  | "/purchasing/invoices"
  | "/purchasing/landed-costs"
  | "/purchasing/returns"
  | "/purchasing/suppliers"
  | "/sales/quotations"
  | "/sales/orders"
  | "/sales/shipments"
  | "/sales/invoices"
  | "/sales/recurring-invoices"
  | "/sales/returns"
  | "/receivables/open-items"
  | "/inventory/adjustments"
  | "/inventory/revaluations"
  | "/inventory/assemblies"
  | "/inventory/transfers"
  | "/inventory/counts"
  | "/inventory/pick-lists"
  | "/inventory/items"
  | "/inventory/warehouses"
  | "/accounting/journals"
  | "/accounting/journal-entries"
  | "/banking/payments"
  | "/banking/receipts"
  | "/expenses"
  | "/expenses/funds"
  | "/fixed-assets"
  | "/fixed-assets/depreciation"
  | "/payables/proposals"
  | "/print-templates"
  | "/banking/cheques"
  | "/banking/reconciliation"
  | "/tax/einvoicing"
  | "/sales/pos"
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
  sales_quotation: "/sales/quotations",
  sales_order: "/sales/orders",
  sales_shipment: "/sales/shipments",
  sales_invoice: "/sales/invoices",
  sales_credit_note: "/sales/invoices",
  sales_return: "/sales/returns",
  stock_adjustment: "/inventory/adjustments",
  stock_revaluation: "/inventory/revaluations",
  stock_assembly: "/inventory/assemblies",
  stock_transfer: "/inventory/transfers",
  stock_count: "/inventory/counts",
  stock_pick_list: "/inventory/pick-lists",
  manual_journal: "/accounting/journals",
  journal_entry: "/accounting/journal-entries",
  bank_payment: "/banking/payments",
  bank_receipt: "/banking/receipts",
  expense: "/expenses",
  petty_cash_fund: "/expenses/funds",
  fund_replenishment: "/expenses/funds",
  fixed_asset: "/fixed-assets",
  fa_asset: "/fixed-assets",
  depreciation_run: "/fixed-assets/depreciation",
  fa_depreciation_run: "/fixed-assets/depreciation",
  payment_proposal: "/payables/proposals",
  item: "/inventory/items",
  partner: "/purchasing/suppliers",
  warehouse: "/inventory/warehouses",
  company: "/companies",
  print_template: "/print-templates",
  bank_cheque: "/banking/cheques",
  bank_statement: "/banking/reconciliation",
  bank_reconciliation: "/banking/reconciliation",
  einvoice_device: "/tax/einvoicing",
  einvoice_submission: "/tax/einvoicing",
  pos_register: "/sales/pos",
  pos_session: "/sales/pos",
  pos_order: "/sales/pos",
  employee: "/hr/employees",
  leave: "/hr/leave",
  payroll_run: "/hr/payroll",
  pay_component: "/hr/pay-components",
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

/**
 * A screen asked to open its create form from the URL (?new=1, as the "New" menu and the palette send): opens it once
 * the screen is ready (its company chosen) and clears the parameter so a reload does not open it again.
 */
export function useNewFromUrl(screen: string, open: () => void, ready = true): void {
  const search: { new?: string } = useSearch({ strict: false });
  const navigate = useNavigate();
  const wanted = search.new === "1";
  const opener = useRef(open);
  opener.current = open;
  useEffect(() => {
    if (wanted && ready) {
      opener.current();
      void navigate({ to: screen, search: {}, replace: true });
    }
  }, [wanted, ready, navigate, screen]);
}
