import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@quicker/ui";
import { useNavigate } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { useTranslation } from "react-i18next";
import { covers } from "../lib/permissions";

/** What can be started from anywhere, in the order people reach for it; each opens its screen with the form already open. */
export const quickNew: { id: string; to: string; permission: string; group: "sales" | "purchasing" | "masters" | "money" }[] = [
  { id: "quotation", to: "/sales/quotations", permission: "sales.quote.manage", group: "sales" },
  { id: "salesOrder", to: "/sales/orders", permission: "sales.order.manage", group: "sales" },
  { id: "salesInvoice", to: "/sales/invoices", permission: "sales.invoice.manage", group: "sales" },
  { id: "customerReceipt", to: "/banking/receipts", permission: "banking.receipt.manage", group: "money" },
  { id: "purchaseOrder", to: "/purchasing/orders", permission: "purchasing.order.manage", group: "purchasing" },
  { id: "supplierInvoice", to: "/purchasing/invoices", permission: "purchasing.invoice.manage", group: "purchasing" },
  { id: "supplierPayment", to: "/banking/payments", permission: "banking.payment.manage", group: "money" },
  { id: "journal", to: "/accounting/journals", permission: "accounting.journal.manage", group: "money" },
  { id: "customer", to: "/sales/customers", permission: "partners.customer.manage", group: "masters" },
  { id: "supplier", to: "/purchasing/suppliers", permission: "partners.supplier.manage", group: "masters" },
  { id: "item", to: "/inventory/items", permission: "inventory.item.manage", group: "masters" },
];

const groups: ("sales" | "purchasing" | "money" | "masters")[] = ["sales", "purchasing", "money", "masters"];

/** The "New" button in the top bar: a document or a master record started from anywhere, only those the member may create. */
export function NewMenu({ permissions }: { permissions: ReadonlySet<string> }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const allowed = quickNew.filter((entry) => covers(permissions, entry.permission));
  if (allowed.length === 0) {
    return null;
  }
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="primary" size="sm" className="h-8 gap-1 px-2.5" data-testid="new-menu">
          <Plus aria-hidden="true" />
          <span className="hidden sm:inline">{t("quick.new")}</span>
          <span className="sr-only sm:hidden">{t("quick.new")}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-56">
        {groups.map((group) => {
          const entries = allowed.filter((entry) => entry.group === group);
          if (entries.length === 0) {
            return null;
          }
          return (
            <div key={group}>
              <DropdownMenuLabel>{t(`quick.groups.${group}`)}</DropdownMenuLabel>
              {entries.map((entry) => (
                <DropdownMenuItem key={entry.id} onSelect={() => { void navigate({ to: entry.to, search: { new: "1" } }); }} data-testid={`new-menu-${entry.id}`}>
                  {t(`quick.entries.${entry.id}`)}
                </DropdownMenuItem>
              ))}
              <DropdownMenuSeparator />
            </div>
          );
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
