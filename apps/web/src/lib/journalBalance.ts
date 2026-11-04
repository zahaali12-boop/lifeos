import { compare, subtract } from "./decimal";

export type JournalBalanceState = "balanced" | "empty" | "unbalanced" | "opening";

/**
 * Whether a manual journal can go further, mirroring the server's rule: debits must equal credits, and not both be
 * nil, before it is submitted or posted. An opening journal is the exception: the server posts its difference to
 * opening balance equity, so the screen says where the difference goes instead of refusing.
 */
export function journalBalance(kind: string, debit: string | number, credit: string | number): { state: JournalBalanceState; difference: string } {
  const difference = subtract(debit, credit);
  if (compare(debit, 0) <= 0 && compare(credit, 0) <= 0) {
    return { state: "empty", difference };
  }
  if (compare(difference, 0) === 0) {
    return { state: "balanced", difference };
  }
  return { state: kind === "opening" ? "opening" : "unbalanced", difference };
}
