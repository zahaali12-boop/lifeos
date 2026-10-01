import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";

export type OpenItem = components["schemas"]["OpenItemInfo"];
export type OpenItemRow = components["schemas"]["OpenItemSummary"];
export type Settlement = components["schemas"]["SettlementInfo"];
export type BankAccount = components["schemas"]["CompanyBankAccountSummary"];

export function useOpenItems(companyId: string, partnerId: string, status: string) {
  return useQuery({
    queryKey: ["open-items", companyId, partnerId, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, ...(partnerId ? { partnerId } : {}), ...(status ? { status } : {}) } } })),
  });
}

export function useBankAccounts(companyId: string) {
  return useQuery({
    queryKey: ["bank-accounts", companyId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })),
  });
}

/** Status badge for open items, settlements, proposals and payments (their statuses share the purchasing vocabulary). */
export function ItemStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  const tones: Record<string, "success" | "warning" | "info" | "neutral" | "danger"> = { open: "info", partially_settled: "warning", settled: "success", reversed: "neutral", draft: "neutral", approved: "info", executed: "success", cancelled: "neutral", posted: "success" };
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`purchasing.statuses.${status}`)}
    </Badge>
  );
}

export function kindLabel(t: (key: string) => string, kind: string): string {
  return t(`payables.kinds.${kind}`);
}
