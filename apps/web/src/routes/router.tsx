import { Outlet, createRootRoute, createRoute, createRouter, redirect } from "@tanstack/react-router";
import { getSession } from "../session/session";
import { AppShell } from "../shell/AppShell";
import { AuditPage } from "./AuditPage";
import { CompaniesPage } from "./CompaniesPage";
import { CustomFieldsPage } from "./CustomFieldsPage";
import { DashboardPage } from "./DashboardPage";
import { JobsPage } from "./JobsPage";
import { LoginPage } from "./LoginPage";
import { MembersPage } from "./MembersPage";
import { NotificationsPage } from "./NotificationsPage";
import { RatesPage } from "./RatesPage";
import { RolesPage } from "./RolesPage";
import { SignupPage } from "./SignupPage";
import { WebhooksPage } from "./WebhooksPage";
import { ChartPage } from "./accounting/ChartPage";
import { JournalBrowserPage } from "./accounting/JournalBrowserPage";
import { JournalsPage } from "./accounting/JournalsPage";
import { LedgerPage } from "./accounting/LedgerPage";
import { PeriodsPage } from "./accounting/PeriodsPage";
import { TrialBalancePage } from "./accounting/TrialBalancePage";

/** Typed routes (ADR-0013): anonymous auth screens, and the shell whose children require a session. */
const rootRoute = createRootRoute({ component: () => <Outlet /> });

const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: "/login", component: LoginPage, beforeLoad: () => { if (getSession()) { throw redirect({ to: "/" }); } } });
const signupRoute = createRoute({ getParentRoute: () => rootRoute, path: "/signup", component: SignupPage, beforeLoad: () => { if (getSession()) { throw redirect({ to: "/" }); } } });

const shellRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: "shell",
  component: AppShell,
  beforeLoad: () => {
    if (!getSession()) {
      throw redirect({ to: "/login" });
    }
  },
});

const searchRecord = (search: Record<string, unknown>): Record<string, string> =>
  Object.fromEntries(Object.entries(search).filter(([, value]) => typeof value === "string").map(([key, value]) => [key, value as string]));

const dashboardRoute = createRoute({ getParentRoute: () => shellRoute, path: "/", component: DashboardPage });
const companiesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/companies", component: CompaniesPage, validateSearch: searchRecord });
const ratesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/rates", component: RatesPage });
const membersRoute = createRoute({ getParentRoute: () => shellRoute, path: "/members", component: MembersPage });
const rolesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/roles", component: RolesPage });
const customFieldsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/custom-fields", component: CustomFieldsPage });
const notificationsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/notifications", component: NotificationsPage, validateSearch: searchRecord });
const auditRoute = createRoute({ getParentRoute: () => shellRoute, path: "/audit", component: AuditPage });
const jobsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/jobs", component: JobsPage });
const webhooksRoute = createRoute({ getParentRoute: () => shellRoute, path: "/webhooks", component: WebhooksPage });
const chartRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/chart", component: ChartPage });
const journalsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/journals", component: JournalsPage, validateSearch: searchRecord });
const trialBalanceRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/trial-balance", component: TrialBalancePage });
const ledgerRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/ledger", component: LedgerPage, validateSearch: searchRecord });
const journalEntriesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/journal-entries", component: JournalBrowserPage, validateSearch: searchRecord });
const periodsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/accounting/periods", component: PeriodsPage });

const routeTree = rootRoute.addChildren([
  loginRoute,
  signupRoute,
  shellRoute.addChildren([dashboardRoute, companiesRoute, ratesRoute, membersRoute, rolesRoute, customFieldsRoute, notificationsRoute, auditRoute, jobsRoute, webhooksRoute, chartRoute, journalsRoute, trialBalanceRoute, ledgerRoute, journalEntriesRoute, periodsRoute]),
]);

export const router = createRouter({ routeTree, defaultPreload: "intent" });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
