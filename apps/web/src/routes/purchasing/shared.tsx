import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";

export function usePaymentTerms() {
  return useQuery({ queryKey: ["payment-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/payment-terms")) });
}

export function useDeliveryTerms() {
  return useQuery({ queryKey: ["delivery-terms"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/delivery-terms")) });
}

export function useSupplierGroups() {
  return useQuery({ queryKey: ["supplier-groups"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/supplier-groups")) });
}

export function useWhtCodes() {
  return useQuery({ queryKey: ["wht-codes"], queryFn: async () => unwrap(await api.GET("/api/v1/partners/wht-codes")) });
}

export function useSupplierPostingGroups() {
  return useQuery({ queryKey: ["posting-groups", "partner_supplier"], queryFn: async () => unwrap(await api.GET("/api/v1/accounting/posting-groups", { params: { query: { kind: "partner_supplier" } } })) });
}

export function HoldBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  if (status === "none") {
    return null;
  }
  return (
    <Badge tone="warning" data-testid="hold-badge">
      {t(`partners.holdStatuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** Number inputs travel as strings so nothing is ever a JavaScript float on the way to the API. */
export function num(value: string, fallback = 0): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}
