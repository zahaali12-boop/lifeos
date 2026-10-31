import { Badge } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import type { Flow } from "../StatusBar";

export type Cheque = components["schemas"]["ChequeSummary"];
export type ChequeDeposit = components["schemas"]["ChequeDepositSummary"];
export type ChequeBook = components["schemas"]["ChequeBookSummary"];
export type ChequeForecastRow = components["schemas"]["ChequeForecastRow"];

export function useCheques(companyId: string, direction: string, status = "") {
  return useQuery({
    queryKey: ["cheques", companyId, direction, status],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheques", { params: { query: { companyId, direction, ...(status ? { status } : {}) } } })),
  });
}

export function useChequeBooks(companyId: string, bankAccountId = "") {
  return useQuery({
    queryKey: ["cheque-books", companyId, bankAccountId],
    enabled: Boolean(companyId),
    queryFn: async () => unwrap(await api.GET("/api/v1/banking/cheque-books", { params: { query: { companyId, ...(bankAccountId ? { bankAccountId } : {}) } } })),
  });
}

const tones: Record<string, "neutral" | "info" | "success" | "warning" | "danger"> = {
  draft: "neutral", in_hand: "info", deposited: "warning", cleared: "success", bounced: "danger", returned: "neutral", issued: "info", paid: "success", cancelled: "neutral", partially_bounced: "danger",
};

export function ChequeStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  return (
    <Badge tone={tones[status] ?? "neutral"} data-testid="doc-status">
      {t(`banking.cheques.statuses.${status}`, { defaultValue: status })}
    </Badge>
  );
}

/** A received cheque goes from in hand to deposited to cleared; an issued one from issued to paid; bounced, returned and cancelled sit off the path. */
export function chequeFlow(cheque: Pick<Cheque, "direction" | "status">, t: TFunction): Flow {
  const label = (id: string): string => t(`banking.cheques.statuses.${id}`, { defaultValue: id });
  const ids = cheque.direction === "received" ? ["in_hand", "deposited", "cleared"] : ["issued", "paid"];
  const steps = ids.map((id) => ({ id, label: label(id) }));
  return steps.some((s) => s.id === cheque.status)
    ? { steps, state: { step: cheque.status, label: label(cheque.status) } }
    : { steps, state: { step: null, label: label(cheque.status), tone: cheque.status === "bounced" ? "danger" : "neutral" } };
}
