import { render, screen, waitFor } from "@testing-library/react";
import { App } from "./App";

describe("App", () => {
  it("renders the product name and reports API readiness", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(() =>
        Promise.resolve({ json: () => Promise.resolve({ status: "ready", migrations: 1 }) } as Response),
      ),
    );

    render(<App />);
    expect(screen.getByRole("heading", { name: "Quicker" })).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId("api-status")).toHaveTextContent("API: ready");
    });
  });
});
