import { jsx as _jsx } from "react/jsx-runtime";
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
import { AdjustmentsPage } from "./inventory/AdjustmentsPage";
import { AssembliesPage } from "./inventory/AssembliesPage";
import { CountsPage } from "./inventory/CountsPage";
import { ItemsPage } from "./inventory/ItemsPage";
import { ReplenishmentPage } from "./inventory/ReplenishmentPage";
import { StockPage } from "./inventory/StockPage";
import { TrackingPage } from "./inventory/TrackingPage";
import { TransfersPage } from "./inventory/TransfersPage";
import { ValuationPage } from "./inventory/ValuationPage";
import { WarehousesPage } from "./inventory/WarehousesPage";
import { AgreementsPage } from "./purchasing/AgreementsPage";
import { InvoicesPage } from "./purchasing/InvoicesPage";
import { LandedCostsPage } from "./purchasing/LandedCostsPage";
import { ReturnsPage } from "./purchasing/ReturnsPage";
import { SupplierIntelligencePage } from "./purchasing/SupplierIntelligencePage";
import { OpenItemsPage } from "./payables/OpenItemsPage";
import { ProposalsPage } from "./payables/ProposalsPage";
import { BankAccountsPage } from "./banking/BankAccountsPage";
import { PaymentsPage } from "./banking/PaymentsPage";
import { PurchaseOrdersPage } from "./purchasing/PurchaseOrdersPage";
import { PurchasingSettingsPage } from "./purchasing/PurchasingSettingsPage";
import { ReceiptsPage } from "./purchasing/ReceiptsPage";
import { RequisitionsPage } from "./purchasing/RequisitionsPage";
import { RfqsPage } from "./purchasing/RfqsPage";
import { SuppliersPage } from "./purchasing/SuppliersPage";
import { ApprovalsPage } from "./workflow/ApprovalsPage";
import { WorkflowsPage } from "./workflow/WorkflowsPage";
import { MobileCountPage } from "./mobile/MobileCountPage";
import { MobileHomePage } from "./mobile/MobileHomePage";
import { MobileQueuePage } from "./mobile/MobileQueuePage";
import { MobileShell } from "./mobile/MobileShell";
import { MobileTransferPage } from "./mobile/MobileTransferPage";
import { MobileReceivePage } from "./mobile/MobileReceivePage";
/** Typed routes (ADR-0013): anonymous auth screens, the shell whose children require a session, and the mobile scanner under /m. */
const rootRoute = createRootRoute({ component: () => _jsx(Outlet, {}) });
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: "/login", component: LoginPage, beforeLoad: () => { if (getSession()) {
        throw redirect({ to: "/" });
    } } });
const signupRoute = createRoute({ getParentRoute: () => rootRoute, path: "/signup", component: SignupPage, beforeLoad: () => { if (getSession()) {
        throw redirect({ to: "/" });
    } } });
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
const searchRecord = (search) => Object.fromEntries(Object.entries(search).filter(([, value]) => typeof value === "string").map(([key, value]) => [key, value]));
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
const itemsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/items", component: ItemsPage, validateSearch: searchRecord });
const warehousesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/warehouses", component: WarehousesPage, validateSearch: searchRecord });
const stockRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/stock", component: StockPage });
const adjustmentsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/adjustments", component: AdjustmentsPage, validateSearch: searchRecord });
const transfersRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/transfers", component: TransfersPage, validateSearch: searchRecord });
const assembliesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/assemblies", component: AssembliesPage, validateSearch: searchRecord });
const trackingRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/tracking", component: TrackingPage, validateSearch: searchRecord });
const countsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/counts", component: CountsPage, validateSearch: searchRecord });
const replenishmentRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/replenishment", component: ReplenishmentPage, validateSearch: searchRecord });
const valuationRoute = createRoute({ getParentRoute: () => shellRoute, path: "/inventory/valuation", component: ValuationPage });
const suppliersRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/suppliers", component: SuppliersPage, validateSearch: searchRecord });
const purchasingSettingsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/settings", component: PurchasingSettingsPage });
const requisitionsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/requisitions", component: RequisitionsPage, validateSearch: searchRecord });
const rfqsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/rfqs", component: RfqsPage, validateSearch: searchRecord });
const purchaseOrdersRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/orders", component: PurchaseOrdersPage, validateSearch: searchRecord });
const agreementsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/agreements", component: AgreementsPage, validateSearch: searchRecord });
const receiptsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/receipts", component: ReceiptsPage, validateSearch: searchRecord });
const invoicesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/invoices", component: InvoicesPage, validateSearch: searchRecord });
const landedCostsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/landed-costs", component: LandedCostsPage, validateSearch: searchRecord });
const returnsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/returns", component: ReturnsPage, validateSearch: searchRecord });
const intelligenceRoute = createRoute({ getParentRoute: () => shellRoute, path: "/purchasing/intelligence", component: SupplierIntelligencePage, validateSearch: searchRecord });
const payablesRoute = createRoute({ getParentRoute: () => shellRoute, path: "/payables/open-items", component: OpenItemsPage, validateSearch: searchRecord });
const proposalsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/payables/proposals", component: ProposalsPage, validateSearch: searchRecord });
const bankAccountsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/banking/bank-accounts", component: BankAccountsPage, validateSearch: searchRecord });
const paymentsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/banking/payments", component: PaymentsPage, validateSearch: searchRecord });
const approvalsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/approvals", component: ApprovalsPage, validateSearch: searchRecord });
const workflowsRoute = createRoute({ getParentRoute: () => shellRoute, path: "/workflows", component: WorkflowsPage, validateSearch: searchRecord });
// The scanner (roadmap 3.8): its own thumb-first shell, the same session.
const mobileRoute = createRoute({
    getParentRoute: () => rootRoute,
    id: "mobile",
    component: MobileShell,
    beforeLoad: () => {
        if (!getSession()) {
            throw redirect({ to: "/login" });
        }
    },
});
const mobileHomeRoute = createRoute({ getParentRoute: () => mobileRoute, path: "/m", component: MobileHomePage });
const mobileCountRoute = createRoute({ getParentRoute: () => mobileRoute, path: "/m/count", component: MobileCountPage });
const mobileTransferRoute = createRoute({ getParentRoute: () => mobileRoute, path: "/m/transfer", component: MobileTransferPage });
const mobileQueueRoute = createRoute({ getParentRoute: () => mobileRoute, path: "/m/queue", component: MobileQueuePage });
const mobileReceiveRoute = createRoute({ getParentRoute: () => mobileRoute, path: "/m/receive", component: MobileReceivePage });
const routeTree = rootRoute.addChildren([
    loginRoute,
    signupRoute,
    shellRoute.addChildren([
        dashboardRoute, companiesRoute, ratesRoute, membersRoute, rolesRoute, customFieldsRoute, notificationsRoute, auditRoute, jobsRoute, webhooksRoute,
        chartRoute, journalsRoute, trialBalanceRoute, ledgerRoute, journalEntriesRoute, periodsRoute,
        itemsRoute, warehousesRoute, stockRoute, adjustmentsRoute, transfersRoute, assembliesRoute, trackingRoute, countsRoute, replenishmentRoute, valuationRoute,
        approvalsRoute, workflowsRoute, suppliersRoute, purchasingSettingsRoute, requisitionsRoute, rfqsRoute, purchaseOrdersRoute, agreementsRoute, receiptsRoute, invoicesRoute, landedCostsRoute, returnsRoute, intelligenceRoute, payablesRoute, proposalsRoute, bankAccountsRoute, paymentsRoute,
    ]),
    mobileRoute.addChildren([mobileHomeRoute, mobileCountRoute, mobileTransferRoute, mobileQueueRoute, mobileReceiveRoute]),
]);
export const router = createRouter({ routeTree, defaultPreload: "intent" });
