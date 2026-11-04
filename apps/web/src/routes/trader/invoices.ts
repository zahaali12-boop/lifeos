/**
 * What an invoice still asks for and the word for its state, shared by the trader's Sales and Supplier invoices
 * screens (ADR-0035 §6, §22): a posted invoice is paid, partly paid, unpaid or overdue by its open items.
 */
export interface OpenLike {
  remainingTc: number | string;
  dueDate: string;
}

export interface InvoiceLike {
  status: string;
  totalGross: number | string;
  openItems: OpenLike[];
}

export const n = (value: number | string | null | undefined): number => (value === null || value === undefined || value === "" ? 0 : Number(value));

/** What is still owed on an invoice: the remainders of its open items. */
export function remainingOf(invoice: InvoiceLike): number {
  return invoice.openItems.reduce((sum, item) => sum + n(item.remainingTc), 0);
}

/** What has been paid against an invoice so far: what its open items asked for less what they still ask for. */
export function paidOf(invoice: { openItems: { originalTc: number | string; remainingTc: number | string }[] }): number {
  return invoice.openItems.reduce((sum, item) => sum + n(item.originalTc) - n(item.remainingTc), 0);
}

export function paymentState(invoice: InvoiceLike, today: string): string {
  if (invoice.status !== "posted") {
    return invoice.status;
  }
  const remaining = remainingOf(invoice);
  if (remaining <= 0) {
    return "paid";
  }
  const due = invoice.openItems.map((i) => i.dueDate).sort()[0];
  if (due !== undefined && due < today) {
    return "overdue";
  }
  return remaining < n(invoice.totalGross) ? "partly" : "unpaid";
}

export const stateTones: Record<string, "success" | "warning" | "danger" | "info" | "neutral"> = { paid: "success", partly: "info", unpaid: "warning", overdue: "danger", draft: "neutral", cancelled: "neutral", reversed: "neutral", approved: "neutral", pending_approval: "neutral", blocked: "warning" };

/** The branded layout first, the plain one beside it (T12b). */
export const printVariants = ["brand", "standard"];
