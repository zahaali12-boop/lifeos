import { jsx as _jsx } from "react/jsx-runtime";
import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
export function useOpenItems(companyId, partnerId, status) {
    return useQuery({
        queryKey: ["open-items", companyId, partnerId, status],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/payables/open-items", { params: { query: { companyId, ...(partnerId ? { partnerId } : {}), ...(status ? { status } : {}) } } })),
    });
}
export function useBankAccounts(companyId) {
    return useQuery({
        queryKey: ["bank-accounts", companyId],
        enabled: Boolean(companyId),
        queryFn: async () => unwrap(await api.GET("/api/v1/banking/bank-accounts", { params: { query: { companyId } } })),
    });
}
/** Status badge for open items, settlements, proposals and payments (their statuses share the purchasing vocabulary). */
export function ItemStatus({ status }) {
    const { t } = useTranslation();
    const tones = { open: "info", partially_settled: "warning", settled: "success", reversed: "neutral", draft: "neutral", approved: "info", executed: "success", cancelled: "neutral", posted: "success" };
    return (_jsx(Badge, { tone: tones[status] ?? "neutral", "data-testid": "doc-status", children: t(`purchasing.statuses.${status}`) }));
}
export function kindLabel(t, kind) {
    return t(`payables.kinds.${kind}`);
}
