import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { setLanguage } from "../../i18n";
import { TrialBalancePage } from "./TrialBalancePage";

const company = { id: "00000000-0000-7000-8000-000000000001", code: "IQT", legalName: { en: "Al-Rafidain Trading", ar: "الرافدين للتجارة" }, functionalCurrency: "IQD", reportingCurrency: "USD" };
const costCentre = { id: "00000000-0000-7000-8000-000000000010", code: "COST_CENTER", name: { en: "Cost centre" } };
const project = { id: "00000000-0000-7000-8000-000000000011", code: "PROJECT", name: { en: "Project" } };
const cc1 = { id: "00000000-0000-7000-8000-000000000020", code: "CC-1", name: { en: "Head office" } };
const proj1 = { id: "00000000-0000-7000-8000-000000000030", code: "PJ-1", name: { en: "Warehouse fit-out" } };
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

/** The element at the index, or a loud failure: `noUncheckedIndexedAccess` leaves array indexing possibly-undefined. */
function at<T>(items: T[], index: number): T {
  const item = items[index];
  if (item === undefined) {
    throw new Error(`Expected an element at index ${String(index)} of ${String(items.length)}.`);
  }

  return item;
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

  it("filters on more than one dimension at once, ANDed, and stops offering dimensions already used", async () => {
    await setLanguage("en");
    const fetch = vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
      if (url.includes("/organization/companies")) {
        return Promise.resolve(json([company]));
      }
      if (url.includes(`/dimensions/${costCentre.id}/values`)) {
        return Promise.resolve(json([cc1]));
      }
      if (url.includes(`/dimensions/${project.id}/values`)) {
        return Promise.resolve(json([proj1]));
      }
      if (url.includes("/organization/dimensions")) {
        return Promise.resolve(json([costCentre, project]));
      }
      if (url.includes("/reports/trial-balance")) {
        return Promise.resolve(json(report));
      }
      return Promise.resolve(new Response("{}", { status: 404 }));
    });
    vi.stubGlobal("fetch", fetch);
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("tb-status")).toHaveTextContent("Balanced");
    });

    fireEvent.click(screen.getByTestId("tb-filter-add"));
    const firstDimension = at(screen.getAllByTestId("tb-filter-dimension"), 0);
    fireEvent.change(firstDimension, { target: { value: "COST_CENTER" } });
    // The value select renders before its options arrive; wait for the fetched option before picking it.
    await waitFor(() => {
      expect(within(at(screen.getAllByTestId("tb-filter-value"), 0)).getByRole("option", { name: "CC-1 · Head office" })).toBeInTheDocument();
    });
    const firstValue = at(screen.getAllByTestId("tb-filter-value"), 0);
    fireEvent.change(firstValue, { target: { value: cc1.id } });
    await waitFor(() => { expect(firstValue).toHaveValue(cc1.id); });

    fireEvent.click(screen.getByTestId("tb-filter-add"));
    await waitFor(() => { expect(screen.getAllByTestId("tb-filter-row")).toHaveLength(2); });
    const secondDimension = at(screen.getAllByTestId("tb-filter-dimension"), 1);
    // The first row's dimension is no longer offered in the second row.
    expect(within(secondDimension).queryByRole("option", { name: "Cost centre" })).not.toBeInTheDocument();
    fireEvent.change(secondDimension, { target: { value: "PROJECT" } });
    await waitFor(() => {
      expect(within(at(screen.getAllByTestId("tb-filter-value"), 1)).getByRole("option", { name: "PJ-1 · Warehouse fit-out" })).toBeInTheDocument();
    });
    const secondValue = at(screen.getAllByTestId("tb-filter-value"), 1);
    fireEvent.change(secondValue, { target: { value: proj1.id } });
    await waitFor(() => { expect(secondValue).toHaveValue(proj1.id); });

    // Both dimensions are now used: the add button is disabled.
    await waitFor(() => { expect(screen.getByTestId("tb-filter-add")).toBeDisabled(); });

    await waitFor(() => {
      const call = fetch.mock.calls.map((c) => (typeof c[0] === "string" ? c[0] : c[0] instanceof URL ? c[0].href : (c[0]).url)).findLast((u) => u.includes("/reports/trial-balance"));
      expect(call).toContain(`d.COST_CENTER=${cc1.id}`);
      expect(call).toContain(`d.PROJECT=${proj1.id}`);
    });

    // Removing a row frees its dimension again and drops it from the query.
    fireEvent.click(at(screen.getAllByTestId("tb-filter-remove"), 0));
    await waitFor(() => { expect(screen.getAllByTestId("tb-filter-row")).toHaveLength(1); });
    await waitFor(() => {
      const call = fetch.mock.calls.map((c) => (typeof c[0] === "string" ? c[0] : c[0] instanceof URL ? c[0].href : (c[0]).url)).findLast((u) => u.includes("/reports/trial-balance"));
      expect(call).not.toContain("COST_CENTER");
      expect(call).toContain(`d.PROJECT=${proj1.id}`);
    });
  });
});
