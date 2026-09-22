import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { render, screen, waitFor } from "@testing-library/react";
import { setLanguage } from "../../i18n";
import { TrialBalancePage } from "./TrialBalancePage";

const company = { id: "00000000-0000-7000-8000-000000000001", code: "IQT", legalName: { en: "Al-Rafidain Trading", ar: "الرافدين للتجارة" }, functionalCurrency: "IQD", reportingCurrency: "USD" };
const report = {
  companyId: company.id,
  currency: "IQD",
  basis: "fc",
  asOf: "2026-09-22",
  from: null,
  compareAsOf: null,
  compareFrom: null,
  groupBy: null,
  filters: {},
  includeClosing: false,
  rows: [
    { accountId: "a1", accountCode: "6110", accountName: { en: "Rent", ar: "الإيجار" }, accountType: "expense", isControl: false, dimensionValueId: null, dimensionValueCode: null, dimensionValueName: null, opening: "0", debit: "1500000.000000", credit: "0", closing: "1500000.000000", compare: null, drill: { accountId: "a1", from: null, to: "2026-09-22", dimensions: {} } },
    { accountId: "a2", accountCode: "2170", accountName: { en: "Accrued expenses", ar: "مصروفات مستحقة" }, accountType: "liability", isControl: false, dimensionValueId: null, dimensionValueCode: null, dimensionValueName: null, opening: "0", debit: "0", credit: "1500000.000000", closing: "-1500000.000000", compare: null, drill: { accountId: "a2", from: null, to: "2026-09-22", dimensions: {} } },
  ],
  totals: { opening: "0", debit: "1500000.000000", credit: "1500000.000000", closing: "0" },
  compareTotals: null,
  balanced: true,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
}

function renderPage() {
  const rootRoute = createRootRoute();
  const route = createRoute({ getParentRoute: () => rootRoute, path: "/accounting/trial-balance", component: TrialBalancePage });
  const ledger = createRoute({ getParentRoute: () => rootRoute, path: "/accounting/ledger", component: () => null });
  const router = createRouter({ routeTree: rootRoute.addChildren([route, ledger]), history: createMemoryHistory({ initialEntries: ["/accounting/trial-balance"] }) });
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe("TrialBalancePage", () => {
  beforeEach(() => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
        if (url.includes("/organization/companies")) {
          return Promise.resolve(json([company]));
        }
        if (url.includes("/organization/dimensions")) {
          return Promise.resolve(json([]));
        }
        if (url.includes("/reports/trial-balance")) {
          return Promise.resolve(json(report));
        }
        return Promise.resolve(new Response("{}", { status: 404 }));
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("shows the balanced report with its rows and totals, in English then in Arabic", async () => {
    await setLanguage("en");
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("tb-status")).toHaveTextContent("Balanced");
    });
    expect(screen.getAllByTestId("tb-row")).toHaveLength(2);
    expect(screen.getByText("Rent")).toBeInTheDocument();
    expect(screen.getByTestId("tb-status")).toHaveTextContent("2 accounts");
    expect(screen.getByRole("button", { name: "6110" })).toBeInTheDocument();

    await setLanguage("ar");
    await waitFor(() => {
      expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("ميزان المراجعة");
    });
    expect(screen.getByText("الإيجار")).toBeInTheDocument();
    expect(document.documentElement.dir).toBe("rtl");
  });
});
