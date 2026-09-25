import { add, compare, isDecimal, subtract } from "./decimal";

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
