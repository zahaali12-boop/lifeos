import { Anchor, ArrowRightLeft, BadgePercent, ChartColumn, Banknote, Barcode, Bell, BookMarked, BookOpen, Boxes, Building2, Calculator, CalendarClock, CalendarDays, CalendarRange, ClipboardCheck, ClipboardList, Coins, Contact, Diff, FileSignature, Gauge, FileText, GitBranch, Hammer, HandCoins, Handshake, Hash, Hourglass, Inbox, Kanban, KeyRound, Landmark, Layers, LayoutDashboard, ListChecks, ListTodo, MessageSquareQuote, NotebookPen, Package, PackageCheck, PackageX, ReceiptText, Repeat, Route, Ruler, Scale, ScanLine, Settings, Settings2, ShieldCheck, Shapes, ShoppingCart, SlidersHorizontal, TrendingDown, Truck, Users, Warehouse, Webhook, type LucideIcon } from "lucide-react";

/** The sections of the sidebar, in order; each is labelled `nav.groups.<id>` and can be collapsed. */
export const navigationGroups = ["organization", "accounting", "inventory", "purchasing", "sales", "finance", "administration"] as const;
export type NavigationGroup = (typeof navigationGroups)[number];

/**
 * The primary navigation: one entry per screen, each with a "g <key>" shortcut, grouped by module. Entries without a
 * group (dashboard, approvals, notifications) sit above the sections because everyone uses them.
 */
export interface NavigationItem {
  to: string;
  /** Translation key of the label. */
  label: string;
  icon: LucideIcon;
  shortcut: string;
  /** Permission a member needs to see it; owners see everything. */
  permission?: string;
  group?: NavigationGroup;
}

export const navigation: NavigationItem[] = [
  { to: "/", label: "nav.dashboard", icon: LayoutDashboard, shortcut: "g d" },
  { to: "/companies", label: "nav.companies", icon: Building2, shortcut: "g c", permission: "organization.company.read", group: "organization" },
  { to: "/rates", label: "nav.rates", icon: Coins, shortcut: "g r", permission: "organization.company.read", group: "organization" },
  { to: "/numbering", label: "nav.numbering", icon: Hash, shortcut: "g '", permission: "numbering.series.read", group: "organization" },
  { to: "/dimensions", label: "nav.dimensions", icon: Shapes, shortcut: "g @", permission: "organization.company.read", group: "organization" },
  { to: "/units", label: "nav.units", icon: Ruler, shortcut: "g #", permission: "organization.company.read", group: "organization" },
  { to: "/calendars", label: "nav.calendars", icon: CalendarDays, shortcut: "g %", permission: "organization.company.read", group: "organization" },
  { to: "/settings", label: "nav.settings", icon: Settings, shortcut: "g ^", permission: "organization.settings.manage", group: "organization" },
  { to: "/accounting/chart", label: "nav.chart", icon: BookOpen, shortcut: "g h", permission: "accounting.chart.read", group: "accounting" },
  { to: "/accounting/journals", label: "nav.journals", icon: NotebookPen, shortcut: "g u", permission: "accounting.journal.read", group: "accounting" },
  { to: "/accounting/trial-balance", label: "nav.trialBalance", icon: Scale, shortcut: "g t", permission: "accounting.journal.read", group: "accounting" },
  { to: "/accounting/ledger", label: "nav.ledger", icon: BookMarked, shortcut: "g l", permission: "accounting.journal.read", group: "accounting" },
  { to: "/accounting/journal-entries", label: "nav.journalEntries", icon: Layers, shortcut: "g e", permission: "accounting.journal.read", group: "accounting" },
  { to: "/accounting/periods", label: "nav.periods", icon: CalendarRange, shortcut: "g p", permission: "organization.company.read", group: "accounting" },
  { to: "/accounting/routines", label: "nav.routines", icon: Repeat, shortcut: "g !", permission: "accounting.journal.read", group: "accounting" },
  { to: "/accounting/posting-rules", label: "nav.postingRules", icon: Route, shortcut: "g $", permission: "accounting.chart.read", group: "accounting" },
  { to: "/inventory/items", label: "nav.items", icon: Package, shortcut: "g i", permission: "inventory.item.read", group: "inventory" },
  { to: "/inventory/warehouses", label: "nav.warehouses", icon: Warehouse, shortcut: "g b", permission: "inventory.warehouse.read", group: "inventory" },
  { to: "/inventory/stock", label: "nav.stock", icon: Boxes, shortcut: "g k", permission: "inventory.stock.read", group: "inventory" },
  { to: "/inventory/adjustments", label: "nav.adjustments", icon: Diff, shortcut: "g x", permission: "inventory.adjustment.read", group: "inventory" },
  { to: "/inventory/transfers", label: "nav.transfers", icon: ArrowRightLeft, shortcut: "g v", permission: "inventory.transfer.read", group: "inventory" },
  { to: "/inventory/assemblies", label: "nav.assemblies", icon: Hammer, shortcut: "g y", permission: "inventory.assembly.read", group: "inventory" },
  { to: "/inventory/tracking", label: "nav.tracking", icon: Barcode, shortcut: "g z", permission: "inventory.stock.read", group: "inventory" },
  { to: "/inventory/counts", label: "nav.counts", icon: ClipboardCheck, shortcut: "g q", permission: "inventory.count.read", group: "inventory" },
  { to: "/inventory/replenishment", label: "nav.replenishment", icon: ShoppingCart, shortcut: "g g", permission: "inventory.replenishment.read", group: "inventory" },
  { to: "/inventory/slow-moving", label: "nav.slowMoving", icon: Hourglass, shortcut: "g )", permission: "inventory.stock.read", group: "inventory" },
  { to: "/inventory/valuation", label: "nav.valuation", icon: Calculator, shortcut: "g 9", permission: "inventory.costing.read", group: "inventory" },
  { to: "/inventory/revaluations", label: "nav.revaluations", icon: TrendingDown, shortcut: "g `", permission: "inventory.costing.read", group: "inventory" },
  { to: "/m", label: "nav.mobile", icon: ScanLine, shortcut: "g s", permission: "inventory.count.enter", group: "inventory" },
  { to: "/purchasing/suppliers", label: "nav.suppliers", icon: Handshake, shortcut: "g 3", permission: "partners.supplier.read", group: "purchasing" },
  { to: "/purchasing/settings", label: "nav.purchasingSettings", icon: Settings2, shortcut: "g 4", permission: "partners.terms.manage", group: "purchasing" },
  { to: "/purchasing/requisitions", label: "nav.requisitions", icon: ClipboardList, shortcut: "g 5", permission: "purchasing.requisition.read", group: "purchasing" },
  { to: "/purchasing/rfqs", label: "nav.rfqs", icon: MessageSquareQuote, shortcut: "g 6", permission: "purchasing.rfq.read", group: "purchasing" },
  { to: "/purchasing/orders", label: "nav.purchaseOrders", icon: FileText, shortcut: "g 7", permission: "purchasing.order.read", group: "purchasing" },
  { to: "/purchasing/agreements", label: "nav.agreements", icon: FileSignature, shortcut: "g 8", permission: "purchasing.agreement.read", group: "purchasing" },
  { to: "/purchasing/receipts", label: "nav.receipts", icon: PackageCheck, shortcut: "g 0", permission: "purchasing.receipt.read", group: "purchasing" },
  { to: "/purchasing/invoices", label: "nav.invoices", icon: ReceiptText, shortcut: "g .", permission: "purchasing.invoice.read", group: "purchasing" },
  { to: "/purchasing/landed-costs", label: "nav.landedCosts", icon: Anchor, shortcut: "g ,", permission: "purchasing.landed_cost.read", group: "purchasing" },
  { to: "/purchasing/returns", label: "nav.returns", icon: PackageX, shortcut: "g ;", permission: "purchasing.return.read", group: "purchasing" },
  { to: "/purchasing/open-lines", label: "nav.openOrderLines", icon: Truck, shortcut: "g *", permission: "purchasing.order.read", group: "purchasing" },
  { to: "/purchasing/analysis", label: "nav.purchaseAnalysis", icon: ChartColumn, shortcut: "g (", permission: "purchasing.order.read", group: "purchasing" },
  { to: "/purchasing/intelligence", label: "nav.supplierIntelligence", icon: Gauge, shortcut: "g ]", permission: "purchasing.intelligence.read", group: "purchasing" },
  { to: "/sales/customers", label: "nav.customers", icon: Contact, shortcut: "s c", permission: "partners.customer.read", group: "sales" },
  { to: "/sales/pipeline", label: "nav.pipeline", icon: Kanban, shortcut: "s p", permission: "partners.customer.read", group: "sales" },
  { to: "/sales/activities", label: "nav.crmActivities", icon: ListTodo, shortcut: "s a", permission: "partners.customer.read", group: "sales" },
  { to: "/sales/setup", label: "nav.salesSetup", icon: BadgePercent, shortcut: "s t", permission: "partners.customer.read", group: "sales" },
  { to: "/payables/open-items", label: "nav.payables", icon: HandCoins, shortcut: "g /", permission: "payables.open_item.read", group: "finance" },
  { to: "/payables/proposals", label: "nav.paymentProposals", icon: CalendarClock, shortcut: "g -", permission: "payables.proposal.read", group: "finance" },
  { to: "/banking/bank-accounts", label: "nav.bankAccounts", icon: Landmark, shortcut: "g =", permission: "banking.bank_account.read", group: "finance" },
  { to: "/banking/payments", label: "nav.payments", icon: Banknote, shortcut: "g [", permission: "banking.payment.read", group: "finance" },
  { to: "/approvals", label: "nav.approvals", icon: Inbox, shortcut: "g 1" },
  { to: "/workflows", label: "nav.workflows", icon: GitBranch, shortcut: "g 2", permission: "workflow.definition.read", group: "administration" },
  { to: "/members", label: "nav.members", icon: Users, shortcut: "g m", permission: "identity.user.read", group: "administration" },
  { to: "/roles", label: "nav.roles", icon: ShieldCheck, shortcut: "g o", permission: "identity.role.read", group: "administration" },
  { to: "/security", label: "nav.security", icon: KeyRound, shortcut: "g &", permission: "identity.sod.read", group: "administration" },
  { to: "/custom-fields", label: "nav.customFields", icon: SlidersHorizontal, shortcut: "g f", group: "organization" },
  { to: "/notifications", label: "nav.notifications", icon: Bell, shortcut: "g n" },
  { to: "/audit", label: "nav.audit", icon: ClipboardList, shortcut: "g a", permission: "audit.event.read", group: "administration" },
  { to: "/jobs", label: "nav.jobs", icon: ListChecks, shortcut: "g j", permission: "platform.job.read", group: "administration" },
  { to: "/webhooks", label: "nav.webhooks", icon: Webhook, shortcut: "g w", permission: "integration.webhook.read", group: "administration" },
];
