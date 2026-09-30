import { describe, expect, it } from "vitest";
import { add } from "./decimal";
import { journalBalance } from "./journalBalance";

describe("journalBalance", () => {
  it("is balanced when debits equal credits, in exact decimals", () => {
    // 0.1 + 0.2 is 0.30000000000000004 in floating point; the journal must still read as balanced.
    expect(journalBalance("manual", add("0.1", "0.2"), "0.3").state).toBe("balanced");
    expect(journalBalance("manual", 100, "100.00").state).toBe("balanced");
  });

  it("refuses a manual, accrual or allocation journal whose sides differ, either way", () => {
    expect(journalBalance("manual", "100", "60")).toEqual({ state: "unbalanced", difference: "40" });
    expect(journalBalance("accrual", "60", "100")).toEqual({ state: "unbalanced", difference: "-40" });
    expect(journalBalance("allocation", "0", "5").state).toBe("unbalanced");
  });

  it("tells an opening journal where its difference goes instead of refusing it", () => {
    expect(journalBalance("opening", "1000", "0")).toEqual({ state: "opening", difference: "1000" });
  });

  it("is empty with no amounts", () => {
    expect(journalBalance("manual", "0", "0").state).toBe("empty");
    expect(journalBalance("opening", 0, 0).state).toBe("empty");
  });
});
