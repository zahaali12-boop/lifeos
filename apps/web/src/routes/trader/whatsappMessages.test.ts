import { defaultTemplates, fillTemplate, phoneDigits, receiptValues, reminderValues, sumByCurrency, whatsappHref, withLinks, type Formatters } from "./whatsappMessages";

const fmt: Formatters = {
  money: (amount, currency) => `${currency} ${amount.toLocaleString("en-US", { minimumFractionDigits: 2 })}`,
  date: (date) => date.split("-").reverse().join("/"),
};

describe("a reminder of what is owed", () => {
  const facts = {
    name: "Ali Auto Repair",
    company: "SINO AUTO",
    today: "2026-11-04",
    invoices: [
      { number: "INV-2", date: "2026-11-01", dueDate: "2026-11-30", remaining: 200000, currency: "IQD" },
      { number: "INV-1", date: "2026-09-01", dueDate: "2026-09-01", remaining: 300000, currency: "IQD" },
      { number: "INV-0", date: "2026-08-01", dueDate: "2026-08-01", remaining: 0, currency: "IQD" },
    ],
  };

  it("names the total, the part past due and every open invoice, the oldest due first", () => {
    const values = reminderValues(facts, "en", fmt);
    expect(values.amount).toBe("IQD 500,000.00");
    expect(values.overdue).toBe("Of this, IQD 300,000.00 is past due.");
    expect(values.invoices).toBe("• INV-1 of 01/09/2026: IQD 300,000.00 open\n• INV-2 of 01/11/2026: IQD 200,000.00 open (due 30/11/2026)");
    const text = fillTemplate(defaultTemplates.reminder.en, values);
    expect(text).toContain("Dear Ali Auto Repair,");
    expect(text).toContain("A reminder from SINO AUTO: your balance with us is IQD 500,000.00. Of this, IQD 300,000.00 is past due.");
  });

  it("says nothing about lateness when nothing is late, and leaves no blank line behind", () => {
    const values = reminderValues({ ...facts, invoices: facts.invoices.filter((i) => i.number === "INV-2") }, "en", fmt);
    expect(values.overdue).toBe("");
    expect(fillTemplate(defaultTemplates.reminder.en, values)).toContain("is IQD 200,000.00.\n• INV-2");
  });

  it("reads in Arabic when the customer does", () => {
    const text = fillTemplate(defaultTemplates.reminder.ar, reminderValues(facts, "ar", fmt));
    expect(text).toContain("تذكير من SINO AUTO: رصيدكم المستحق لدينا IQD 500,000.00. منها IQD 300,000.00 متأخرة عن موعدها.");
    expect(text).toContain("• INV-1 بتاريخ 01/09/2026: المتبقي IQD 300,000.00");
  });

  it("adds up each currency on its own", () => {
    expect(sumByCurrency([{ amount: 100, currency: "IQD" }, { amount: 5, currency: "USD" }, { amount: 50, currency: "IQD" }], "en", fmt)).toBe("IQD 150.00 + USD 5.00");
    expect(sumByCurrency([], "ar", fmt)).toBe("لا شيء");
  });
});

describe("a payment confirmed", () => {
  it("names the amount, the receipt, what each invoice got and what is left, and the balance after", () => {
    const values = receiptValues({
      name: "Ali Auto Repair",
      company: "SINO AUTO",
      number: "RCT-2026-00003",
      date: "2026-11-04",
      amount: 250000,
      currency: "IQD",
      settled: [{ number: "INV-1", paid: 200000, remaining: 0, currency: "IQD" }, { number: "INV-2", paid: 50000, remaining: 150000, currency: "IQD" }],
      balance: [{ amount: 150000, currency: "IQD" }],
    }, "en", fmt);
    const text = fillTemplate(defaultTemplates.receipt.en, values);
    expect(text).toContain("SINO AUTO confirms receiving IQD 250,000.00 from you on 04/11/2026 (receipt RCT-2026-00003).");
    expect(text).toContain("• INV-1: IQD 200,000.00 paid (settled)\n• INV-2: IQD 50,000.00 paid (IQD 150,000.00 still open)");
    expect(text).toContain("Your remaining balance is IQD 150,000.00.");
  });
});

describe("the message as sent", () => {
  it("keeps an unknown placeholder visible so a typo in a template shows itself", () => {
    expect(fillTemplate("Hello {name}, {nope}.", { name: "Ali" })).toBe("Hello Ali, {nope}.");
  });

  it("puts each document's link on its own line and opens WhatsApp on the digits alone", () => {
    const text = withLinks("Hello.\n", [{ label: "Statement", url: "https://x/1" }, { label: "Invoice INV-1", url: "https://x/2" }]);
    expect(text).toBe("Hello.\nStatement: https://x/1\nInvoice INV-1: https://x/2");
    expect(phoneDigits("+964 770 123 4567")).toBe("9647701234567");
    expect(phoneDigits("00964 770 123 4567")).toBe("9647701234567");
    expect(whatsappHref("+964 770 123 4567", "a b")).toBe("https://wa.me/9647701234567?text=a%20b");
  });
});
