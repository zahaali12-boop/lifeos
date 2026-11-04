import { exchangeRate, owedOn, spreadOldestFirst } from "./cashMovements";

const items = [
  { id: "late", remaining: "250000", dueDate: "2026-11-20", documentDate: "2026-10-20" },
  { id: "first", remaining: "100000.5", dueDate: "2026-11-01", documentDate: "2026-10-01" },
  { id: "paid", remaining: "0", dueDate: "2026-10-01", documentDate: "2026-09-01" },
  { id: "second", remaining: 0.1, dueDate: "2026-11-01", documentDate: "2026-10-02" },
];

describe("a payment spread over open invoices", () => {
  it("settles the oldest due first, each up to what it asks, and keeps nothing on account when it falls short", () => {
    expect(spreadOldestFirst(items, 150000)).toEqual({ lines: [{ openItemId: "first", amount: 100000.5 }, { openItemId: "second", amount: 0.1 }, { openItemId: "late", amount: 49999.4 }], onAccount: 0 });
  });

  it("leaves what is beyond every invoice on account, without float drift", () => {
    expect(spreadOldestFirst(items, "400000.7")).toEqual({ lines: [{ openItemId: "first", amount: 100000.5 }, { openItemId: "second", amount: 0.1 }, { openItemId: "late", amount: 250000 }], onAccount: 50000.1 });
  });

  it("puts everything on account when nothing is open", () => {
    expect(spreadOldestFirst([], 500)).toEqual({ lines: [], onAccount: 500 });
  });

  it("adds up what is still owed exactly", () => {
    expect(owedOn(items)).toBe(350000.6);
  });
});

describe("the rate of an exchange", () => {
  it("is dinars per dollar whichever way the money went", () => {
    expect(exchangeRate(1320000, "IQD", 1000, "USD", "IQD")).toEqual({ foreign: "USD", rate: 1320 });
    expect(exchangeRate(400, "USD", 530000, "IQD", "IQD")).toEqual({ foreign: "USD", rate: 1325 });
  });

  it("is not asked within one currency, between two foreign ones, or before both amounts are known", () => {
    expect(exchangeRate(100, "IQD", 100, "IQD", "IQD")).toBeNull();
    expect(exchangeRate(100, "USD", 92, "EUR", "IQD")).toBeNull();
    expect(exchangeRate(1320000, "IQD", "", "USD", "IQD")).toBeNull();
  });
});
