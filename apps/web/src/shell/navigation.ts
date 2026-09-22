import { Bell, BookMarked, BookOpen, Building2, CalendarRange, ClipboardList, Coins, Layers, LayoutDashboard, ListChecks, NotebookPen, Scale, ScanLine, ShieldCheck, SlidersHorizontal, Users, Webhook, type LucideIcon } from "lucide-react";

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
  { to: "/m", label: "nav.mobile", icon: ScanLine, shortcut: "g s", permission: "inventory.count.enter" },
  { to: "/members", label: "nav.members", icon: Users, shortcut: "g m", permission: "identity.user.read" },
  { to: "/roles", label: "nav.roles", icon: ShieldCheck, shortcut: "g o", permission: "identity.role.read" },
  { to: "/custom-fields", label: "nav.customFields", icon: SlidersHorizontal, shortcut: "g f" },
  { to: "/notifications", label: "nav.notifications", icon: Bell, shortcut: "g n" },
  { to: "/audit", label: "nav.audit", icon: ClipboardList, shortcut: "g a", permission: "audit.event.read" },
  { to: "/jobs", label: "nav.jobs", icon: ListChecks, shortcut: "g j", permission: "platform.job.read" },
  { to: "/webhooks", label: "nav.webhooks", icon: Webhook, shortcut: "g w", permission: "integration.webhook.read" },
];
