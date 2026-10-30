import { Anchor, Undo2, ArrowRightLeft, BadgePercent, ChartColumn, Banknote, CircleDollarSign, Barcode, Bell, BookMarked, BookOpen, Boxes, Building2, Calculator, CalendarClock, CalendarDays, CalendarRange, ClipboardCheck, ClipboardList, Coins, Contact, Diff, FileSignature, Gauge, FileText, GitBranch, Hammer, HandCoins, Handshake, Hash, Hourglass, Inbox, Kanban, KeyRound, Landmark, Layers, LayoutDashboard, ListChecks, ListTodo, MessageSquareQuote, NotebookPen, Package, PackageCheck, PackageX, Percent, ReceiptText, Repeat, Route, Ruler, Scale, ScanLine, SearchCheck, Settings, Settings2, ShieldCheck, Shapes, Ship, ShoppingCart, SlidersHorizontal, Gift, Stamp, Tags, TrendingDown, Truck, Users, Warehouse, Webhook, type LucideIcon } from "lucide-react";

/** The modules, in order: each is a menu of the top bar (labelled `nav.modules.<id>`) and a section of the small-screen drawer (`nav.groups.<id>`). */
export const navigationGroups = ["accounting", "sales", "purchasing", "inventory", "finance", "organization", "administration"] as const;
export type NavigationGroup = (typeof navigationGroups)[number];

/** The sections within a module, in the order the menus show them; each is labelled `nav.sections.<id>`. */
export const navigationSections = ["customers", "orders", "invoicing", "operations", "products", "receivables", "payables", "bank", "tax", "planning", "pricing", "reporting", "setup", "users", "automation", "configuration"] as const;
export type NavigationSection = (typeof navigationSections)[number];

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
  /** The section of the module the entry is listed under in the top menu. */
  section?: NavigationSection;
}

export const navigation: NavigationItem[] = [
  { to: "/", label: "nav.dashboard", icon: LayoutDashboard, shortcut: "g d" },
  { to: "/companies", label: "nav.companies", icon: Building2, shortcut: "g c", permission: "organization.company.read", group: "organization", section: "setup" },
  { to: "/rates", label: "nav.rates", icon: Coins, shortcut: "g r", permission: "organization.company.read", group: "organization", section: "setup" },
  { to: "/numbering", label: "nav.numbering", icon: Hash, shortcut: "g '", permission: "numbering.series.read", group: "organization", section: "configuration" },
  { to: "/dimensions", label: "nav.dimensions", icon: Shapes, shortcut: "g @", permission: "organization.company.read", group: "organization", section: "setup" },
  { to: "/units", label: "nav.units", icon: Ruler, shortcut: "g #", permission: "organization.company.read", group: "organization", section: "setup" },
  { to: "/calendars", label: "nav.calendars", icon: CalendarDays, shortcut: "g %", permission: "organization.company.read", group: "organization", section: "setup" },
  { to: "/settings", label: "nav.settings", icon: Settings, shortcut: "g ^", permission: "organization.settings.manage", group: "organization", section: "configuration" },
  { to: "/accounting/chart", label: "nav.chart", icon: BookOpen, shortcut: "g h", permission: "accounting.chart.read", group: "accounting", section: "configuration" },
  { to: "/accounting/journals", label: "nav.journals", icon: NotebookPen, shortcut: "g u", permission: "accounting.journal.read", group: "accounting", section: "operations" },
  { to: "/accounting/trial-balance", label: "nav.trialBalance", icon: Scale, shortcut: "g t", permission: "accounting.journal.read", group: "accounting", section: "reporting" },
  { to: "/accounting/ledger", label: "nav.ledger", icon: BookMarked, shortcut: "g l", permission: "accounting.journal.read", group: "accounting", section: "reporting" },
  { to: "/accounting/journal-entries", label: "nav.journalEntries", icon: Layers, shortcut: "g e", permission: "accounting.journal.read", group: "accounting", section: "operations" },
  { to: "/accounting/periods", label: "nav.periods", icon: CalendarRange, shortcut: "g p", permission: "organization.company.read", group: "accounting", section: "configuration" },
  { to: "/accounting/routines", label: "nav.routines", icon: Repeat, shortcut: "g !", permission: "accounting.journal.read", group: "accounting", section: "operations" },
  { to: "/accounting/posting-rules", label: "nav.postingRules", icon: Route, shortcut: "g $", permission: "accounting.chart.read", group: "accounting", section: "configuration" },
  { to: "/inventory/items", label: "nav.items", icon: Package, shortcut: "g i", permission: "inventory.item.read", group: "inventory", section: "products" },
  { to: "/inventory/warehouses", label: "nav.warehouses", icon: Warehouse, shortcut: "g b", permission: "inventory.warehouse.read", group: "inventory", section: "products" },
  { to: "/inventory/stock", label: "nav.stock", icon: Boxes, shortcut: "g k", permission: "inventory.stock.read", group: "inventory", section: "reporting" },
  { to: "/inventory/adjustments", label: "nav.adjustments", icon: Diff, shortcut: "g x", permission: "inventory.adjustment.read", group: "inventory", section: "operations" },
  { to: "/inventory/transfers", label: "nav.transfers", icon: ArrowRightLeft, shortcut: "g v", permission: "inventory.transfer.read", group: "inventory", section: "operations" },
  { to: "/inventory/assemblies", label: "nav.assemblies", icon: Hammer, shortcut: "g y", permission: "inventory.assembly.read", group: "inventory", section: "operations" },
  { to: "/inventory/tracking", label: "nav.tracking", icon: Barcode, shortcut: "g z", permission: "inventory.stock.read", group: "inventory", section: "products" },
  { to: "/inventory/counts", label: "nav.counts", icon: ClipboardCheck, shortcut: "g q", permission: "inventory.count.read", group: "inventory", section: "operations" },
  { to: "/inventory/pick-lists", label: "nav.pickLists", icon: ListChecks, shortcut: "g +", permission: "inventory.pick.read", group: "inventory", section: "operations" },
  { to: "/inventory/replenishment", label: "nav.replenishment", icon: ShoppingCart, shortcut: "g g", permission: "inventory.replenishment.read", group: "inventory", section: "planning" },
  { to: "/inventory/slow-moving", label: "nav.slowMoving", icon: Hourglass, shortcut: "g )", permission: "inventory.stock.read", group: "inventory", section: "reporting" },
  { to: "/inventory/valuation", label: "nav.valuation", icon: Calculator, shortcut: "g 9", permission: "inventory.costing.read", group: "inventory", section: "reporting" },
  { to: "/inventory/revaluations", label: "nav.revaluations", icon: TrendingDown, shortcut: "g `", permission: "inventory.costing.read", group: "inventory", section: "planning" },
  { to: "/m", label: "nav.mobile", icon: ScanLine, shortcut: "g s", permission: "inventory.count.enter", group: "inventory", section: "operations" },
  { to: "/purchasing/suppliers", label: "nav.suppliers", icon: Handshake, shortcut: "g 3", permission: "partners.supplier.read", group: "purchasing", section: "orders" },
  { to: "/purchasing/settings", label: "nav.purchasingSettings", icon: Settings2, shortcut: "g 4", permission: "partners.terms.manage", group: "purchasing", section: "configuration" },
  { to: "/purchasing/requisitions", label: "nav.requisitions", icon: ClipboardList, shortcut: "g 5", permission: "purchasing.requisition.read", group: "purchasing", section: "orders" },
  { to: "/purchasing/rfqs", label: "nav.rfqs", icon: MessageSquareQuote, shortcut: "g 6", permission: "purchasing.rfq.read", group: "purchasing", section: "orders" },
  { to: "/purchasing/orders", label: "nav.purchaseOrders", icon: FileText, shortcut: "g 7", permission: "purchasing.order.read", group: "purchasing", section: "orders" },
  { to: "/purchasing/agreements", label: "nav.agreements", icon: FileSignature, shortcut: "g 8", permission: "purchasing.agreement.read", group: "purchasing", section: "orders" },
  { to: "/purchasing/receipts", label: "nav.receipts", icon: PackageCheck, shortcut: "g 0", permission: "purchasing.receipt.read", group: "purchasing", section: "operations" },
  { to: "/purchasing/invoices", label: "nav.invoices", icon: ReceiptText, shortcut: "g .", permission: "purchasing.invoice.read", group: "purchasing", section: "operations" },
  { to: "/purchasing/landed-costs", label: "nav.landedCosts", icon: Anchor, shortcut: "g ,", permission: "purchasing.landed_cost.read", group: "purchasing", section: "operations" },
  { to: "/purchasing/returns", label: "nav.returns", icon: PackageX, shortcut: "g ;", permission: "purchasing.return.read", group: "purchasing", section: "operations" },
  { to: "/purchasing/open-lines", label: "nav.openOrderLines", icon: Truck, shortcut: "g *", permission: "purchasing.order.read", group: "purchasing", section: "reporting" },
  { to: "/purchasing/analysis", label: "nav.purchaseAnalysis", icon: ChartColumn, shortcut: "g (", permission: "purchasing.order.read", group: "purchasing", section: "reporting" },
  { to: "/purchasing/intelligence", label: "nav.supplierIntelligence", icon: Gauge, shortcut: "g ]", permission: "purchasing.intelligence.read", group: "purchasing", section: "reporting" },
  { to: "/sales/customers", label: "nav.customers", icon: Contact, shortcut: "s c", permission: "partners.customer.read", group: "sales", section: "customers" },
  { to: "/sales/quotations", label: "nav.quotations", icon: FileSignature, shortcut: "s q", permission: "sales.quote.read", group: "sales", section: "orders" },
  { to: "/sales/orders", label: "nav.salesOrders", icon: ShoppingCart, shortcut: "s o", permission: "sales.order.read", group: "sales", section: "orders" },
  { to: "/sales/shipments", label: "nav.shipments", icon: Ship, shortcut: "s h", permission: "sales.shipment.read", group: "sales", section: "orders" },
  { to: "/sales/invoices", label: "nav.salesInvoices", icon: ReceiptText, shortcut: "s i", permission: "sales.invoice.read", group: "sales", section: "invoicing" },
  { to: "/sales/recurring-invoices", label: "nav.recurringInvoices", icon: Repeat, shortcut: "s v", permission: "sales.invoice.read", group: "sales", section: "invoicing" },
  { to: "/sales/returns", label: "nav.salesReturns", icon: Undo2, shortcut: "s u", permission: "sales.return.read", group: "sales", section: "orders" },
  { to: "/sales/pipeline", label: "nav.pipeline", icon: Kanban, shortcut: "s p", permission: "partners.customer.read", group: "sales", section: "customers" },
  { to: "/sales/activities", label: "nav.crmActivities", icon: ListTodo, shortcut: "s a", permission: "partners.customer.read", group: "sales", section: "customers" },
  { to: "/sales/price-lists", label: "nav.priceLists", icon: Tags, shortcut: "s l", permission: "pricing.price.read", group: "sales", section: "pricing" },
  { to: "/sales/pricing-rules", label: "nav.pricingRules", icon: Gift, shortcut: "s r", permission: "pricing.price.read", group: "sales", section: "pricing" },
  { to: "/sales/price-check", label: "nav.priceCheck", icon: SearchCheck, shortcut: "s k", permission: "pricing.price.read", group: "sales", section: "pricing" },
  { to: "/sales/setup", label: "nav.salesSetup", icon: BadgePercent, shortcut: "s t", permission: "partners.customer.read", group: "sales", section: "configuration" },
  { to: "/tax/setup", label: "nav.taxSetup", icon: Percent, shortcut: "g {", permission: "tax.setup.read", group: "finance", section: "tax" },
  { to: "/tax/returns", label: "nav.taxReturns", icon: Stamp, shortcut: "g }", permission: "tax.return.read", group: "finance", section: "tax" },
  { to: "/receivables/open-items", label: "nav.receivables", icon: Coins, shortcut: "r o", permission: "receivables.open_item.read", group: "finance", section: "receivables" },
  { to: "/banking/receipts", label: "nav.customerReceipts", icon: CircleDollarSign, shortcut: "r c", permission: "banking.receipt.read", group: "finance", section: "receivables" },
  { to: "/payables/open-items", label: "nav.payables", icon: HandCoins, shortcut: "g /", permission: "payables.open_item.read", group: "finance", section: "payables" },
  { to: "/payables/proposals", label: "nav.paymentProposals", icon: CalendarClock, shortcut: "g -", permission: "payables.proposal.read", group: "finance", section: "payables" },
  { to: "/banking/bank-accounts", label: "nav.bankAccounts", icon: Landmark, shortcut: "g =", permission: "banking.bank_account.read", group: "finance", section: "bank" },
  { to: "/banking/payments", label: "nav.payments", icon: Banknote, shortcut: "g [", permission: "banking.payment.read", group: "finance", section: "payables" },
  { to: "/approvals", label: "nav.approvals", icon: Inbox, shortcut: "g 1" },
  { to: "/workflows", label: "nav.workflows", icon: GitBranch, shortcut: "g 2", permission: "workflow.definition.read", group: "administration", section: "automation" },
  { to: "/members", label: "nav.members", icon: Users, shortcut: "g m", permission: "identity.user.read", group: "administration", section: "users" },
  { to: "/roles", label: "nav.roles", icon: ShieldCheck, shortcut: "g o", permission: "identity.role.read", group: "administration", section: "users" },
  { to: "/security", label: "nav.security", icon: KeyRound, shortcut: "g &", permission: "identity.sod.read", group: "administration", section: "users" },
  { to: "/custom-fields", label: "nav.customFields", icon: SlidersHorizontal, shortcut: "g f", group: "organization", section: "configuration" },
  { to: "/notifications", label: "nav.notifications", icon: Bell, shortcut: "g n" },
  { to: "/audit", label: "nav.audit", icon: ClipboardList, shortcut: "g a", permission: "audit.event.read", group: "administration", section: "automation" },
  { to: "/jobs", label: "nav.jobs", icon: ListChecks, shortcut: "g j", permission: "platform.job.read", group: "administration", section: "automation" },
  { to: "/webhooks", label: "nav.webhooks", icon: Webhook, shortcut: "g w", permission: "integration.webhook.read", group: "administration", section: "automation" },
];
