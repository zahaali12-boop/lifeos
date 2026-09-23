import { ArrowRightLeft, Barcode, Bell, BookMarked, BookOpen, Boxes, Building2, Calculator, CalendarRange, ClipboardCheck, ClipboardList, Coins, Diff, FileSignature, FileText, GitBranch, Hammer, Handshake, Inbox, Layers, LayoutDashboard, ListChecks, MessageSquareQuote, NotebookPen, Package, PackageCheck, Scale, ScanLine, Settings2, ShieldCheck, ShoppingCart, SlidersHorizontal, Users, Warehouse, Webhook, type LucideIcon } from "lucide-react";

/** The primary navigation: one entry per admin area shipped in 1.4–1.8, the accounting screens of M2 and the mobile scanner of M3, each with a "g <key>" shortcut. */
export interface NavigationItem {
  to: string;
  /** Translation key of the label. */
  label: string;
  icon: LucideIcon;
  shortcut: string;
  /** Permission a member needs to see it; owners see everything. */
  permission?: string;
}

export const navigation: NavigationItem[] = [
  { to: "/", label: "nav.dashboard", icon: LayoutDashboard, shortcut: "g d" },
  { to: "/companies", label: "nav.companies", icon: Building2, shortcut: "g c", permission: "organization.company.read" },
  { to: "/rates", label: "nav.rates", icon: Coins, shortcut: "g r", permission: "organization.company.read" },
  { to: "/accounting/chart", label: "nav.chart", icon: BookOpen, shortcut: "g h", permission: "accounting.chart.read" },
  { to: "/accounting/journals", label: "nav.journals", icon: NotebookPen, shortcut: "g u", permission: "accounting.journal.read" },
  { to: "/accounting/trial-balance", label: "nav.trialBalance", icon: Scale, shortcut: "g t", permission: "accounting.journal.read" },
  { to: "/accounting/ledger", label: "nav.ledger", icon: BookMarked, shortcut: "g l", permission: "accounting.journal.read" },
  { to: "/accounting/journal-entries", label: "nav.journalEntries", icon: Layers, shortcut: "g e", permission: "accounting.journal.read" },
  { to: "/accounting/periods", label: "nav.periods", icon: CalendarRange, shortcut: "g p", permission: "organization.company.read" },
  { to: "/inventory/items", label: "nav.items", icon: Package, shortcut: "g i", permission: "inventory.item.read" },
  { to: "/inventory/warehouses", label: "nav.warehouses", icon: Warehouse, shortcut: "g b", permission: "inventory.warehouse.read" },
  { to: "/inventory/stock", label: "nav.stock", icon: Boxes, shortcut: "g k", permission: "inventory.stock.read" },
  { to: "/inventory/adjustments", label: "nav.adjustments", icon: Diff, shortcut: "g x", permission: "inventory.adjustment.read" },
  { to: "/inventory/transfers", label: "nav.transfers", icon: ArrowRightLeft, shortcut: "g v", permission: "inventory.transfer.read" },
  { to: "/inventory/assemblies", label: "nav.assemblies", icon: Hammer, shortcut: "g y", permission: "inventory.assembly.read" },
  { to: "/inventory/tracking", label: "nav.tracking", icon: Barcode, shortcut: "g z", permission: "inventory.stock.read" },
  { to: "/inventory/counts", label: "nav.counts", icon: ClipboardCheck, shortcut: "g q", permission: "inventory.count.read" },
  { to: "/inventory/replenishment", label: "nav.replenishment", icon: ShoppingCart, shortcut: "g g", permission: "inventory.replenishment.read" },
  { to: "/inventory/valuation", label: "nav.valuation", icon: Calculator, shortcut: "g 9", permission: "inventory.costing.read" },
  { to: "/m", label: "nav.mobile", icon: ScanLine, shortcut: "g s", permission: "inventory.count.enter" },
  { to: "/purchasing/suppliers", label: "nav.suppliers", icon: Handshake, shortcut: "g 3", permission: "partners.supplier.read" },
  { to: "/purchasing/settings", label: "nav.purchasingSettings", icon: Settings2, shortcut: "g 4", permission: "partners.terms.manage" },
  { to: "/purchasing/requisitions", label: "nav.requisitions", icon: ClipboardList, shortcut: "g 5", permission: "purchasing.requisition.read" },
  { to: "/purchasing/rfqs", label: "nav.rfqs", icon: MessageSquareQuote, shortcut: "g 6", permission: "purchasing.rfq.read" },
  { to: "/purchasing/orders", label: "nav.purchaseOrders", icon: FileText, shortcut: "g 7", permission: "purchasing.order.read" },
  { to: "/purchasing/agreements", label: "nav.agreements", icon: FileSignature, shortcut: "g 8", permission: "purchasing.agreement.read" },
  { to: "/purchasing/receipts", label: "nav.receipts", icon: PackageCheck, shortcut: "g 0", permission: "purchasing.receipt.read" },
  { to: "/approvals", label: "nav.approvals", icon: Inbox, shortcut: "g 1" },
  { to: "/workflows", label: "nav.workflows", icon: GitBranch, shortcut: "g 2", permission: "workflow.definition.read" },
  { to: "/members", label: "nav.members", icon: Users, shortcut: "g m", permission: "identity.user.read" },
  { to: "/roles", label: "nav.roles", icon: ShieldCheck, shortcut: "g o", permission: "identity.role.read" },
  { to: "/custom-fields", label: "nav.customFields", icon: SlidersHorizontal, shortcut: "g f" },
  { to: "/notifications", label: "nav.notifications", icon: Bell, shortcut: "g n" },
  { to: "/audit", label: "nav.audit", icon: ClipboardList, shortcut: "g a", permission: "audit.event.read" },
  { to: "/jobs", label: "nav.jobs", icon: ListChecks, shortcut: "g j", permission: "platform.job.read" },
  { to: "/webhooks", label: "nav.webhooks", icon: Webhook, shortcut: "g w", permission: "integration.webhook.read" },
];
