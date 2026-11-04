import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { formatMoney, formatNumber } from "../../lib/format";

/**
 * The phone site of the trader edition (ADR-0035 §21): the few things the trader does away from the desk, each on
 * one thumb-sized screen, over the same records and the same API as the full program.
 */
export type Invoice = components["schemas"]["SalesInvoiceSummary"];
export type CashAccount = components["schemas"]["TraderCashReportAccount"];

export interface AgingLike {
  partnerId: string;
  partnerCode: string;
  partnerName: Record<string, string>;
  notDue: number | string;
  days1To30: number | string;
  days31To60: number | string;
  days61To90: number | string;
  over90: number | string;
  totalFc: number | string;
}

export const n = (value: number | string | null | undefined): number => (value === null || value === undefined || value === "" ? 0 : Number(value));

export const overdueOf = (row: AgingLike): number => n(row.days1To30) + n(row.days31To60) + n(row.days61To90) + n(row.over90);

/** The branded layout first, the plain one beside it (T12b). */
export const printVariants = ["brand", "standard"];

/** Where the trader chose the full program on this phone for the session; the home screen then stays the desk's. */
export const FULL_PROGRAM_KEY = "quicker.phone.full";

export function isPhone(): boolean {
  return typeof window !== "undefined" && window.matchMedia("(max-width: 640px)").matches;
}

export function fullProgramChosen(): boolean {
  try {
    return sessionStorage.getItem(FULL_PROGRAM_KEY) === "1";
  } catch {
    return false;
  }
}

export function chooseFullProgram(): void {
  try {
    sessionStorage.setItem(FULL_PROGRAM_KEY, "1");
  } catch {
    // ignore
  }
}

export function useReceivablesAging(companyId: string) {
  return useQuery({ queryKey: ["ar-aging", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/receivables/open-items/aging", { params: { query: { companyId } } })) });
}

export function usePayablesAging(companyId: string) {
  return useQuery({ queryKey: ["ap-aging", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items/aging", { params: { query: { companyId } } })) });
}

export function useCash(companyId: string) {
  return useQuery({ queryKey: ["trader-cash", companyId, "phone"], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/trader/cash", { params: { query: { companyId } } })) });
}

/** Posted invoices, newest first (the server orders them so, T11b). */
export function usePostedInvoices(companyId: string) {
  return useQuery({ queryKey: ["invoices", companyId, "posted", "phone"], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/sales/invoices", { params: { query: { companyId, status: "posted" } } })) });
}

export function useCustomers(companyId: string) {
  return useQuery({ queryKey: ["customers", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/partners/customers", { params: { query: { companyId } } })) });
}

export interface SellableItem {
  itemId: string;
  itemCode: string;
  itemName: Record<string, string>;
  /** What the store holds, all warehouses together. */
  onHand: number;
  /** The price list's current price, when the item has one. */
  price: number | null;
}

/**
 * What the trader can sell from the phone: every item the store holds (the stock balances, all warehouses together)
 * and every item the price board knows, with the board's current price when there is one; the price is typed otherwise.
 */
export function useSellableItems(companyId: string) {
  const balances = useQuery({ queryKey: ["stock-balances", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/balances", { params: { query: { companyId, includeZero: false } } })) });
  const board = useQuery({ queryKey: ["trader-pricing", companyId], enabled: Boolean(companyId), queryFn: async () => unwrap(await api.GET("/api/v1/trader/pricing/board", { params: { query: { companyId } } })) });
  const items = useMemo(() => {
    const onHand = new Map<string, number>();
    for (const balance of balances.data ?? []) {
      onHand.set(balance.itemId, (onHand.get(balance.itemId) ?? 0) + n(balance.onHand));
    }
    const byId = new Map<string, SellableItem>();
    for (const row of board.data?.rows ?? []) {
      byId.set(row.itemId, { itemId: row.itemId, itemCode: row.itemCode, itemName: row.itemName, onHand: onHand.get(row.itemId) ?? n(row.onHand), price: row.currentPrice === null ? null : n(row.currentPrice) });
    }
    for (const balance of balances.data ?? []) {
      if (!byId.has(balance.itemId)) {
        byId.set(balance.itemId, { itemId: balance.itemId, itemCode: balance.itemCode, itemName: balance.itemName, onHand: onHand.get(balance.itemId) ?? 0, price: null });
      }
    }
    return [...byId.values()].sort((a, b) => a.itemCode.localeCompare(b.itemCode));
  }, [balances.data, board.data]);
  return { items, isPending: balances.isPending || board.isPending };
}

/** Money with its currency, or the bare number while the company and its currency are still loading. */
export const money = (amount: number, currency: string): string => (currency ? formatMoney(amount, currency) : formatNumber(amount));

export const remainingOf = (invoice: Invoice): number => invoice.openItems.reduce((sum, item) => sum + n(item.remainingTc), 0);
