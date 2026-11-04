import { describe, expect, it } from "vitest";
import { todayIn } from "./dates";

describe("todayIn", () => {
  const lateEvening = new Date("2026-09-24T21:31:00Z");

  it("gives the calendar date in the zone, not the UTC date", () => {
    expect(todayIn("Asia/Baghdad", lateEvening)).toBe("2026-09-25");
    expect(todayIn("UTC", lateEvening)).toBe("2026-09-24");
    expect(todayIn("America/New_York", new Date("2026-01-01T03:00:00Z"))).toBe("2025-12-31");
  });

  it("falls back to the browser's date for an unknown zone", () => {
    expect(todayIn("Not/AZone", lateEvening)).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});
