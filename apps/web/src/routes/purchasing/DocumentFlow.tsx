import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from "@quicker/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { ChevronDown, ClipboardList, FileMinus, FileQuestion, FileText, Handshake, PackageCheck, Ship, ShoppingCart, Undo2, type LucideIcon } from "lucide-react";
import { useTranslation } from "react-i18next";
import { api, unwrap } from "../../api";
import type { components } from "../../api/schema";
import { recordRoute } from "../../lib/documents";
import { formatDate, formatMoney, formatNumber } from "../../lib/format";
import { PurchaseStatus } from "./shared";

type FlowDocument = components["schemas"]["FlowDocument"];

/** The smart buttons in the order the chain runs; invoices split by kind so a debit note is not counted as a bill. */
const groups: { key: string; type: string; kind?: string; label: string; icon: LucideIcon }[] = [
  { key: "requisitions", type: "purchase_requisition", label: "nav.requisitions", icon: ClipboardList },
  { key: "rfqs", type: "purchase_rfq", label: "nav.rfqs", icon: FileQuestion },
  { key: "agreements", type: "purchase_agreement", label: "nav.agreements", icon: Handshake },
  { key: "orders", type: "purchase_order", label: "nav.purchaseOrders", icon: ShoppingCart },
  { key: "receipts", type: "purchase_receipt", label: "nav.receipts", icon: PackageCheck },
  { key: "returns", type: "purchase_return", label: "nav.returns", icon: Undo2 },
  { key: "landed-costs", type: "landed_cost_document", label: "nav.landedCosts", icon: Ship },
  { key: "invoices", type: "purchase_invoice", kind: "invoice", label: "nav.invoices", icon: FileText },
  { key: "debit-notes", type: "purchase_invoice", kind: "debit_note", label: "documentFlow.debitNotes", icon: FileMinus },
];

const inGroup = (group: (typeof groups)[number], doc: FlowDocument): boolean => doc.documentType === group.type && (!group.kind || doc.kind === group.kind);

function describe(doc: FlowDocument): string {
  const amount = doc.amount === null ? "" : doc.currency ? formatMoney(doc.amount, doc.currency) : formatNumber(doc.amount);
  return [formatDate(doc.date), amount].filter(Boolean).join(" · ");
}

/**
 * The documents linked to this one along the purchasing chain, as smart buttons with a count: the requisition and RFQ an
 * order came from, its receipts, returns, landed costs, invoices and debit notes. One document opens directly; several
 * open a list to choose from. Only documents the user may read are counted.
 */
export function DocumentFlowBar({ documentType, documentId }: { documentType: string; documentId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const flow = useQuery({
    queryKey: ["document-flow", documentType, documentId],
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/document-flow/{documentType}/{documentId}", { params: { path: { documentType, documentId } } })),
  });
  const others = (flow.data?.documents ?? []).filter((d) => !d.isCurrent);
  const shown = groups.map((group) => ({ group, docs: others.filter((d) => inGroup(group, d)) })).filter((g) => g.docs.length > 0);
  if (shown.length === 0) {
    return null;
  }

  return (
    <nav aria-label={t("documentFlow.label")} className="flex flex-wrap items-center gap-2" data-testid="document-flow">
      {shown.map(({ group, docs }) => {
        const Icon = group.icon;
        const content = (
          <>
            <Icon aria-hidden="true" />
            <span>{t(group.label)}</span>
            <span className="rounded-full bg-surface-sunken px-1.5 text-xs tabular" data-testid="flow-count">{docs.length}</span>
          </>
        );
        const only = docs.length === 1 ? docs[0] : undefined;
        const route = only ? recordRoute(only.documentType, only.id) : null;
        if (only && route) {
          return (
            <Button key={group.key} asChild variant="secondary" size="sm">
              <Link to={route.to} search={route.search} title={`${only.number} · ${describe(only)}`} data-testid={`flow-${group.key}`}>
                {content}
              </Link>
            </Button>
          );
        }
        return (
          <DropdownMenu key={group.key}>
            <DropdownMenuTrigger asChild>
              <Button variant="secondary" size="sm" data-testid={`flow-${group.key}`}>
                {content}
                <ChevronDown aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start" className="min-w-72">
              <DropdownMenuLabel>{t(group.label)}</DropdownMenuLabel>
              {docs.map((doc) => (
                <DropdownMenuItem
                  key={doc.id}
                  onSelect={() => { const to = recordRoute(doc.documentType, doc.id); if (to) { void navigate(to); } }}
                  className="flex items-center justify-between gap-3"
                  data-testid="flow-document"
                >
                  <span className="flex flex-col">
                    <span dir="ltr" className="font-medium">{doc.number}</span>
                    <span className="text-xs text-fg-muted" dir="ltr">{describe(doc)}</span>
                  </span>
                  <PurchaseStatus status={doc.status} />
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        );
      })}
      {flow.data?.truncated ? <span className="text-xs text-fg-muted" data-testid="flow-truncated">{t("documentFlow.truncated")}</span> : null}
    </nav>
  );
}
