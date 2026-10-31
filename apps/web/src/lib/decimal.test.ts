import { add, compare, isDecimal, subtract, sumDecimals, toDecimalString } from "./decimal";

describe("decimal", () => {
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
