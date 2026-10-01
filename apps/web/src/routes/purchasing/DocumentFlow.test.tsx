import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter, useSearch } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { components } from "../../api/schema";
import { setLanguage } from "../../i18n";
import { DocumentFlowBar } from "./DocumentFlow";

type FlowDocument = components["schemas"]["FlowDocument"];

const company = "00000000-0000-7000-8000-000000000001";
const order = "00000000-0000-7000-8000-00000000000a";

function doc(documentType: string, id: string, number: string, change: Partial<FlowDocument> = {}): FlowDocument {
  return { documentType, id, number, status: "posted", date: "2026-09-20", amount: "1200.00", currency: "IQD", companyId: company, kind: null, isCurrent: false, ...change };
}

const flow: components["schemas"]["DocumentFlow"] = {
  documentType: "purchase_order",
  documentId: order,
  truncated: false,
  documents: [
    doc("purchase_requisition", "00000000-0000-7000-8000-000000000011", "REQ-2026-00001", { status: "ordered", amount: null, currency: null }),
    doc("purchase_order", order, "PO-2026-00001", { status: "partially_received", isCurrent: true }),
    doc("purchase_receipt", "00000000-0000-7000-8000-000000000021", "GRN-2026-00001"),
    doc("purchase_receipt", "00000000-0000-7000-8000-000000000022", "GRN-2026-00002"),
    doc("purchase_invoice", "00000000-0000-7000-8000-000000000031", "PINV-2026-00001", { kind: "invoice" }),
    doc("purchase_invoice", "00000000-0000-7000-8000-000000000032", "PINV-2026-00002", { kind: "debit_note", amount: "-300.00" }),
  ],
};

function Opened({ screenName }: { screenName: string }) {
  const search: { open?: string } = useSearch({ strict: false });
  return <p data-testid="opened">{screenName} {search.open}</p>;
}

function renderBar() {
  const rootRoute = createRootRoute();
  const orders = createRoute({ getParentRoute: () => rootRoute, path: "/purchasing/orders", component: () => <DocumentFlowBar documentType="purchase_order" documentId={order} /> });
  const targets = ["requisitions", "receipts", "invoices"].map((name) =>
    createRoute({ getParentRoute: () => rootRoute, path: `/purchasing/${name}`, validateSearch: (search: Record<string, unknown>) => ({ open: typeof search.open === "string" ? search.open : undefined }), component: () => <Opened screenName={name} /> }),
  );
  const router = createRouter({ routeTree: rootRoute.addChildren([orders, ...targets]), history: createMemoryHistory({ initialEntries: ["/purchasing/orders"] }) });
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe("DocumentFlowBar", () => {
  beforeEach(() => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: Request) => {
        const path = new URL(input.url).pathname;
        return Promise.resolve(path === `/api/v1/purchasing/document-flow/purchase_order/${order}`
          ? new Response(JSON.stringify(flow), { status: 200, headers: { "content-type": "application/json" } })
          : new Response("{}", { status: 404 }));
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("counts the linked documents by kind, leaves the current one out and opens a single one directly", async () => {
    await setLanguage("en");
    renderBar();
    await waitFor(() => {
      expect(screen.getByTestId("document-flow")).toBeInTheDocument();
    });
    expect(screen.getByTestId("flow-requisitions")).toHaveTextContent("Requisitions1");
    expect(screen.getByTestId("flow-receipts")).toHaveTextContent("Goods receipts2");
    expect(screen.getByTestId("flow-invoices")).toHaveTextContent("Supplier invoices1");
    expect(screen.getByTestId("flow-debit-notes")).toHaveTextContent("Debit notes1");
    expect(screen.queryByTestId("flow-orders")).not.toBeInTheDocument();
    expect(screen.queryByTestId("flow-truncated")).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId("flow-invoices"));
    await waitFor(() => {
      expect(screen.getByTestId("opened")).toHaveTextContent("invoices 00000000-0000-7000-8000-000000000031");
    });
  });

  it("lists several documents of a kind to choose from, in Arabic too", async () => {
    await setLanguage("ar");
    renderBar();
    await waitFor(() => {
      expect(screen.getByTestId("flow-receipts")).toHaveTextContent("استلام البضائع2");
    });
    expect(screen.getByRole("navigation", { name: "المستندات المرتبطة" })).toBeInTheDocument();
    fireEvent.keyDown(screen.getByTestId("flow-receipts"), { key: "Enter" });
    const items = await screen.findAllByTestId("flow-document");
    expect(items).toHaveLength(2);
    expect(items[1]).toHaveTextContent("GRN-2026-00002");
    fireEvent.click(screen.getByText("GRN-2026-00002"));
    await waitFor(() => {
      expect(screen.getByTestId("opened")).toHaveTextContent("receipts 00000000-0000-7000-8000-000000000022");
    });
    await setLanguage("en");
  });
});
