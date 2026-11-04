import { describe, expect, it, vi } from "vitest";
import { lineKeys, parseRows, pastedNumber } from "./lineKeys";

interface Line {
  itemCode: string;
  quantity: string;
  price: string;
}

const empty = (): Line => ({ itemCode: "", quantity: "", price: "" });

function keys(lines: Line[], onChange: (lines: Line[]) => void) {
  return lineKeys<Line>({ lines, onChange, empty, columns: ["itemCode", "quantity", "price"], parse: { itemCode: (raw) => raw.trim().toUpperCase(), quantity: pastedNumber, price: pastedNumber } });
}

function cellElement(index: number, column: string): HTMLInputElement {
  const input = document.createElement("input");
  input.dataset.line = String(index);
  input.dataset.col = column;
  return input;
}

describe("spreadsheet numbers", () => {
  it("drops thousands separators and keeps the decimal point", () => {
    expect(pastedNumber("1,250.50")).toBe("1250.50");
    expect(pastedNumber(" 12 ")).toBe("12");
    expect(pastedNumber("1.250,50")).toBe("1250.50");
    expect(pastedNumber("-3")).toBe("-3");
    expect(pastedNumber("abc")).toBe("abc");
  });

  it("reads rows and cells; a single value is not a paste of rows", () => {
    expect(parseRows("TEA\t5\t100\r\nMILK\t2\t40\r\n")).toEqual([["TEA", "5", "100"], ["MILK", "2", "40"]]);
    expect(parseRows("TEA")).toBeNull();
  });
});

describe("line keys", () => {
  it("fills lines from the cell the paste lands in, adding lines as needed", () => {
    const onChange = vi.fn();
    const handlers = keys([{ itemCode: "SUGAR", quantity: "1", price: "9" }], onChange);
    const target = cellElement(0, "quantity");
    const event = { target, clipboardData: { getData: () => "5\t1,000\n2\t40\n" }, preventDefault: vi.fn() };
    handlers.onPaste(event as never);
    expect(event.preventDefault).toHaveBeenCalled();
    expect(onChange).toHaveBeenCalledWith([
      { itemCode: "SUGAR", quantity: "5", price: "1000" },
      { itemCode: "", quantity: "2", price: "40" },
    ]);
  });

  it("leaves a plain paste alone", () => {
    const onChange = vi.fn();
    const handlers = keys([empty()], onChange);
    const event = { target: cellElement(0, "itemCode"), clipboardData: { getData: () => "TEA" }, preventDefault: vi.fn() };
    handlers.onPaste(event as never);
    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("adds a line on Enter in the last line and moves down otherwise", () => {
    const onChange = vi.fn();
    const handlers = keys([empty(), empty()], onChange);
    const tbody = document.createElement("tbody");
    const first = cellElement(0, "price");
    const second = cellElement(1, "price");
    tbody.append(first, second);
    document.body.append(tbody);
    const down = { key: "Enter", target: first, defaultPrevented: false, preventDefault: vi.fn() };
    handlers.onKeyDown(down as never);
    expect(down.preventDefault).toHaveBeenCalled();
    expect(document.activeElement).toBe(second);
    expect(onChange).not.toHaveBeenCalled();
    const add = { key: "Enter", target: second, defaultPrevented: false, preventDefault: vi.fn() };
    handlers.onKeyDown(add as never);
    expect(onChange).toHaveBeenCalledWith([empty(), empty(), empty()]);
    // A key a picker already used is left alone.
    const used = { key: "Enter", target: second, defaultPrevented: true, preventDefault: vi.fn() };
    handlers.onKeyDown(used as never);
    expect(used.preventDefault).not.toHaveBeenCalled();
    tbody.remove();
  });
});
