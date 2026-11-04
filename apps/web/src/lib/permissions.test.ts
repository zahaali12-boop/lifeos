import { describe, expect, it } from "vitest";
import { covers } from "./permissions";

describe("covers", () => {
  it("honours exact keys, whole areas and everything, as the server does", () => {
    expect(covers(["partners.customer.*"], "partners.customer.manage")).toBe(true);
    expect(covers(["partners.*"], "partners.credit.manage")).toBe(true);
    expect(covers(["*"], "anything.at.all")).toBe(true);
    expect(covers(["partners.customer.read"], "partners.customer.read")).toBe(true);
    expect(covers(["partners.customer.read"], "partners.customer.manage")).toBe(false);
    expect(covers(["partners.customer.*"], "partners.credit.manage")).toBe(false);
    expect(covers(["partners.customer.*"], "partners.customers.read")).toBe(false);
    expect(covers([], "partners.customer.read")).toBe(false);
  });
});
