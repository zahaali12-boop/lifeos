import { describe, expect, it } from "vitest";
import { periodRange, rangeParams, type Period } from "./period";

const period = (preset: Period["preset"], from = "", to = ""): Period => ({ preset, from, to });

describe("the period a trader's list shows", () => {
  it("covers the presets from the day it is, month and year boundaries included", () => {
    expect(periodRange(period("today"), "2026-11-03")).toEqual({ from: "2026-11-03", to: "2026-11-03" });
    expect(periodRange(period("last7"), "2026-11-03")).toEqual({ from: "2026-10-28", to: "2026-11-03" });
    expect(periodRange(period("month"), "2026-11-03")).toEqual({ from: "2026-11-01", to: "2026-11-03" });
    expect(periodRange(period("lastMonth"), "2026-11-03")).toEqual({ from: "2026-10-01", to: "2026-10-31" });
    expect(periodRange(period("lastMonth"), "2026-01-05")).toEqual({ from: "2025-12-01", to: "2025-12-31" });
    expect(periodRange(period("lastMonth"), "2026-03-15")).toEqual({ from: "2026-02-01", to: "2026-02-28" });
    expect(periodRange(period("quarter"), "2026-11-03")).toEqual({ from: "2026-10-01", to: "2026-11-03" });
    expect(periodRange(period("quarter"), "2026-05-20")).toEqual({ from: "2026-04-01", to: "2026-05-20" });
    expect(periodRange(period("quarter"), "2026-03-01")).toEqual({ from: "2026-01-01", to: "2026-03-01" });
    expect(periodRange(period("year"), "2026-11-03")).toEqual({ from: "2026-01-01", to: "2026-11-03" });
    expect(periodRange(period("all"), "2026-11-03")).toEqual({});
  });

  it("takes the typed bounds for a custom range, either side open", () => {
    expect(periodRange(period("custom", "2026-10-10", "2026-10-20"), "2026-11-03")).toEqual({ from: "2026-10-10", to: "2026-10-20" });
    expect(periodRange(period("custom", "2026-10-10"), "2026-11-03")).toEqual({ from: "2026-10-10" });
    expect(periodRange(period("custom", "", "2026-10-20"), "2026-11-03")).toEqual({ to: "2026-10-20" });
    expect(rangeParams({})).toEqual({});
    expect(rangeParams({ from: "2026-10-01", to: "2026-10-31" })).toEqual({ from: "2026-10-01", to: "2026-10-31" });
  });
});
