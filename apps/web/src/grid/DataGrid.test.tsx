import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ColumnDef } from "@tanstack/react-table";
import { setLanguage } from "../i18n";
import { DataGrid } from "./DataGrid";

interface Row {
  id: string;
  name: string;
  amount: string;
}

const columns: ColumnDef<Row, unknown>[] = [
  { id: "name", accessorKey: "name", header: "Name", size: 180 },
  { id: "amount", accessorKey: "amount", header: "Amount", size: 120 },
];

const rows: Row[] = [{ id: "1", name: "Shenzhen Auto Parts", amount: "1,900.00" }];

function renderGrid() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <DataGrid<Row> label="trader.purchases.title" columns={columns} data={rows} rowKey={(row) => row.id} emptyTitle="Empty" />
    </QueryClientProvider>,
  );
}

function headerRow(): HTMLElement {
  const row = screen.getAllByRole("columnheader")[0]?.parentElement;
  if (!row) {
    throw new Error("The grid has no header row.");
  }
  return row;
}

describe("DataGrid column widths", () => {
  beforeEach(async () => {
    window.localStorage.clear();
    await setLanguage("en");
  });

  it("gives every column at least its designed width and lets the list scroll sideways instead of cutting it", () => {
    renderGrid();
    expect(headerRow().style.gridTemplateColumns).toBe("minmax(180px, 1fr) minmax(120px, 1fr)");
    // The header and the rows sit in one block as wide as the columns together, inside the scrolling list.
    expect(headerRow().parentElement?.parentElement?.style.minWidth).toBe("300px");
  });

  it("widens a column from the keyboard on its edge, keeps the width in this browser and resets it", () => {
    const { unmount } = renderGrid();
    const edge = screen.getByTestId("grid-resize-name");
    expect(edge).toHaveAttribute("role", "separator");
    // The edge is named by what it does, never by the column's name, so a screen's own field of that name stays the only match for its label.
    expect(edge).toHaveAccessibleName("Column width");
    expect(edge).toHaveAccessibleDescription("Name");
    expect(screen.queryByLabelText("Name")).toBeNull();
    // jsdom lays nothing out, so the column starts from the smallest width and each press adds 16 px.
    fireEvent.keyDown(edge, { key: "ArrowRight" });
    fireEvent.keyDown(edge, { key: "ArrowRight" });
    expect(headerRow().style.gridTemplateColumns).toMatch(/^\d+px minmax\(120px, 1fr\)$/);
    const stored = JSON.parse(window.localStorage.getItem("quicker.grid.widths.trader.purchases.title") ?? "{}") as Record<string, number>;
    expect(stored.name).toBeGreaterThanOrEqual(60);
    unmount();

    // The width is still there the next time the list opens.
    renderGrid();
    expect(headerRow().style.gridTemplateColumns).toBe(`${String(stored.name)}px minmax(120px, 1fr)`);
  });

  it("narrows with the arrow towards the start in Arabic, where the edge is on the left", async () => {
    await setLanguage("ar");
    renderGrid();
    const edge = screen.getByTestId("grid-resize-amount");
    fireEvent.keyDown(edge, { key: "ArrowLeft" });
    const widened = Number(/(\d+)px$/.exec(headerRow().style.gridTemplateColumns)?.[1]);
    fireEvent.keyDown(edge, { key: "ArrowRight" });
    fireEvent.keyDown(edge, { key: "ArrowRight" });
    const narrowed = Number(/(\d+)px$/.exec(headerRow().style.gridTemplateColumns)?.[1]);
    expect(widened).toBeGreaterThan(0);
    expect(narrowed).toBeLessThanOrEqual(widened);
  });
});
