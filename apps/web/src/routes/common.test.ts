import { describe, expect, it } from "vitest";
import { groupDecimal, normalizeDecimalInput } from "./common";

describe("decimal fields", () => {
  it("groups the integer part with thousands separators and keeps the fraction as typed", () => {
    expect(groupDecimal("1234567.50")).toBe("1,234,567.50");
    expect(groupDecimal("1000")).toBe("1,000");
    expect(groupDecimal("999")).toBe("999");
    expect(groupDecimal("-2500.125")).toBe("-2,500.125");
    expect(groupDecimal("1000.")).toBe("1,000.");
    expect(groupDecimal("")).toBe("");
    expect(groupDecimal("12abc")).toBe("12abc");
  });

  it("turns what was typed into a plain decimal: Arabic-Indic digits, separators and stray characters", () => {
    expect(normalizeDecimalInput("1,500,000")).toBe("1500000");
    expect(normalizeDecimalInput("١٢٣٤٫٥")).toBe("1234.5");
    expect(normalizeDecimalInput("۱۰۰")).toBe("100");
    expect(normalizeDecimalInput("1.2.3")).toBe("1.23");
    expect(normalizeDecimalInput("-12-3")).toBe("-123");
    expect(normalizeDecimalInput("12 000 IQD")).toBe("12000");
  });
});
