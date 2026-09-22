import { render, screen, waitFor } from "@testing-library/react";
import { App } from "./App";

describe("App", () => {
  it("renders the product name and reports API readiness through the typed client", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ status: "ready", migrations: 9 }), {
            status: 200,
            headers: { "content-type": "application/json" },
          }),
        ),
      ),
    );

    render(<App />);
    expect(screen.getByRole("heading", { name: "Quicker" })).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId("api-status")).toHaveTextContent("API: ready");
    });
    const request = vi.mocked(fetch).mock.calls[0]?.[0] as Request;
    expect(request.url).toContain("/health/ready");
  });
});
