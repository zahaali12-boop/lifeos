import { add, compare, isDecimal, multiply, roundTo, roundToIncrement, subtract, sumDecimals, toDecimalString } from "./decimal";

describe("decimal", () => {
  it("multiplies exactly and rounds halves away from zero like the server", () => {
    expect(multiply("3", "2.30")).toBe("6.9");
    expect(multiply("0.1", "0.2")).toBe("0.02");
    expect(multiply("6.9", "0.15")).toBe("1.035");
    expect(roundTo("1.035", 2)).toBe("1.04");
    expect(roundTo("1.034", 2)).toBe("1.03");
    expect(roundTo("-1.035", 2)).toBe("-1.04");
    expect(roundTo("2.5", 0)).toBe("3");
    expect(roundTo("12", 2)).toBe("12");
    expect(roundTo("1.025", 2, "half_even")).toBe("1.02");
    expect(roundTo("1.035", 2, "half_even")).toBe("1.04");
    expect(roundTo("1.0351", 2, "half_even")).toBe("1.04");
  });

  it("rounds cash to an increment", () => {
    expect(roundToIncrement("35.54", "0.05")).toBe("35.55");
    expect(roundToIncrement("35.52", "0.05")).toBe("35.5");
    expect(roundToIncrement("35.525", "0.05")).toBe("35.55");
    expect(roundToIncrement("12375", "250")).toBe("12500");
    expect(roundToIncrement("12374", "250")).toBe("12250");
    expect(roundToIncrement("-2.63", "0.05")).toBe("-2.65");
    expect(roundToIncrement("9.99", "0")).toBe("9.99");
  });

  it("subtracts exactly where floating point would drift", () => {
    expect(subtract("0.3", "0.1", "0.2")).toBe("0");
    expect(subtract("12.500000", "2.5")).toBe("10");
    expect(subtract("10", "2.125", "0.000001")).toBe("7.874999");
    expect(subtract("1", "3")).toBe("-2");
    expect(subtract("0.5", "0.75")).toBe("-0.25");
  });

  it("adds and compares at the larger scale", () => {
    expect(add("0.1", "0.2")).toBe("0.3");
    expect(add("99999999999999.999999", "0.000001")).toBe("100000000000000");
    expect(compare("2.50", "2.5")).toBe(0);
    expect(compare("2.499999", "2.5")).toBe(-1);
    expect(compare("-1", "-1.5")).toBe(1);
  });

  it("recognises plain decimals and treats anything else as zero", () => {
    expect(isDecimal("12")).toBe(true);
    expect(isDecimal(".5")).toBe(true);
    expect(isDecimal("-3.25")).toBe(true);
    expect(isDecimal("")).toBe(false);
    expect(isDecimal("1e3")).toBe(false);
    expect(isDecimal("1,5")).toBe(false);
    expect(add("abc", "2")).toBe("2");
  });
});

describe("sumDecimals", () => {
  it("adds the figures exactly, netting negatives, and trims trailing zeros", () => {
    expect(sumDecimals(["0.1", "0.2"])).toBe("0.3");
    expect(sumDecimals(["100000.000000", "15000", "-20000.5"])).toBe("94999.5");
    expect(sumDecimals([3, "-1", -2.5])).toBe("-0.5");
    expect(sumDecimals(["-0.25", "0.25"])).toBe("0");
  });

  it("ignores what is not a figure and sums nothing to zero", () => {
    expect(sumDecimals([null, undefined, "abc", { en: "x" }, "7"])).toBe("7");
    expect(sumDecimals([])).toBe("0");
  });

  it("prints numbers plainly, never in exponent form", () => {
    expect(toDecimalString(" 12.50 ")).toBe("12.50");
    expect(toDecimalString(1e21)).toBe("1000000000000000000000");
    expect(toDecimalString(1e-7)).toBe("0.0000001");
    expect(toDecimalString(Number.NaN)).toBeNull();
    expect(toDecimalString("1,000")).toBeNull();
  });
});
