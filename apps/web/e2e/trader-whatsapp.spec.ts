import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * WhatsApp to a customer (trader edition, ADR-0035 §25, T15): a workspace made as the trader program with a customer
 * who owes two invoices, one past due; from Money a reminder is written for him with the total, the part past due and
 * every open invoice, the statement attached as a link, and WhatsApp opened on his number; from Sales the same for one
 * invoice, in Arabic too, the text edited and restored; after a payment its confirmation names the receipt, what the
 * invoice got and the balance after, with the receipt and the invoice attached; the company's own wording from
 * Settings takes over; the send is logged as a note on the customer; the phone's Customers screen offers the same;
 * then the dialog in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

/** The message WhatsApp would have opened with, as the journey's stand-in for the new window recorded it. */
async function opened(page: Page): Promise<{ href: string; text: string }> {
  const href = await page.evaluate(() => (window as unknown as { __opened?: string }).__opened ?? "");
  const text = decodeURIComponent(href.split("?text=")[1] ?? "").replace(/\u00a0/gu, " ");
  return { href, text };
}

test("WhatsApp: a reminder of what is owed with the statement, one invoice in Arabic, a payment confirmed with the receipt and the invoice, the company's own wording, the phone, then Arabic", async ({ page }) => {
  test.setTimeout(240_000);
  const slug = `twa-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;
  const today = new Date();
  const iso = (d: Date): string => d.toISOString().slice(0, 10);
  const daysAgo = (days: number): string => iso(new Date(today.getTime() - days * 86_400_000));

  const signup = await page.request.post(`${apiUrl}/api/v1/auth/signup`, { data: { tenantName: "WhatsApp " + slug, slug, ownerName: "Owner", ownerEmail: email, password, edition: "trader" } });
  expect(signup.ok(), await signup.text()).toBeTruthy();
  const tokens = (await signup.json()) as { accessToken: string };
  const headers = { Authorization: `Bearer ${tokens.accessToken}`, "Content-Type": "application/json" };
  const call = async (method: "post" | "put", path: string, data: unknown): Promise<{ id: string; number: string }> => {
    const response = await page.request[method](`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string; number: string };
  };
  const company = await call("post", "/api/v1/organization/companies", { code: "TRD", legalName: { en: "Parts Trading Co.", ar: "شركة تجارة القطع" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  await call("post", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const ali = await call("post", "/api/v1/partners", { code: "ALI", legalName: { en: "Ali Auto Repair", ar: "ورشة علي" }, isCustomer: true, phone: "+964 770 123 4567", defaultLanguage: "en" });
  await call("put", `/api/v1/partners/${ali.id}/customer-accounts/${company.id}`, { currency: "IQD" });
  await call("post", "/api/v1/banking/bank-accounts", { companyId: company.id, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency: "IQD", isActive: true });
  // Two invoices: one from 45 days ago, past due, and one of today.
  const late = await call("post", "/api/v1/sales/invoices", { companyId: company.id, partnerId: ali.id, documentDate: daysAgo(45), lines: [{ kind: "service", quantity: 1, unitPrice: 300_000, description: "Gearbox repair" }] });
  await call("post", `/api/v1/sales/invoices/${late.id}/post`, {});
  const fresh = await call("post", "/api/v1/sales/invoices", { companyId: company.id, partnerId: ali.id, documentDate: iso(today), lines: [{ kind: "service", quantity: 1, unitPrice: 200_000, description: "Brake pads" }] });
  await call("post", `/api/v1/sales/invoices/${fresh.id}/post`, {});
  await call("put", "/api/v1/organization/settings/trader.rates.prompt_hour", { value: -1, valueType: "number" });

  await page.addInitScript(() => {
    window.localStorage.setItem("quicker.language", "en");
    // WhatsApp opens in a new window; the journey records what would have opened instead of leaving the app.
    window.open = (url?: string | URL): null => { (window as unknown as { __opened?: string }).__opened = String(url); return null; };
  });
  await page.goto("/login");
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByTestId("flat-nav")).toBeVisible({ timeout: 20_000 });

  // Money: the reminder written for Ali, his number already there, the statement attached; WhatsApp opens with it.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Money" }).click();
  await page.getByTestId("whatsapp-ALI").click();
  const dialog = page.getByTestId("whatsapp-dialog");
  await expect(dialog).toBeVisible();
  await expect(page.getByTestId("whatsapp-to")).toHaveValue("+964 770 123 4567");
  const text = page.getByTestId("whatsapp-text");
  // Amounts are written with a no-break space after the currency; the journey reads them with a plain one.
  const message = async (): Promise<string> => (await text.inputValue()).replace(/\u00a0/gu, " ");
  await expect(text).toHaveValue(/Dear Ali Auto Repair,/);
  await expect.poll(message).toMatch(/A reminder from Parts Trading Co\.: your balance with us is IQD 500,000\.00\. Of this, IQD 300,000\.00 is past due\./);
  expect(await message()).toMatch(new RegExp(`• ${late.number} of .*: IQD 300,000\\.00 open\\n• ${fresh.number} of .*: IQD 200,000\\.00 open \\(due .*\\)`));
  await expect(page.getByTestId("whatsapp-attach-statement")).toHaveAttribute("data-state", "checked");
  await expect(page.getByTestId("whatsapp-save-phone")).toHaveCount(0);
  await expectAccessible(page);
  await page.getByTestId("whatsapp-send").click();
  await expect(page.getByTestId("whatsapp-sent")).toBeVisible({ timeout: 60_000 });
  const reminder = await opened(page);
  expect(reminder.href).toContain("https://wa.me/9647701234567?text=");
  expect(reminder.text).toContain("Dear Ali Auto Repair,");
  expect(reminder.text).toMatch(/\nAccount statement: http.*\/api\/v1\/public\/prints\//);
  await expect(page.getByTestId("whatsapp-open-again")).toHaveAttribute("href", reminder.href);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  // The send is on the customer's record as a note.
  const notes = await page.request.get(`${apiUrl}/api/v1/partners/crm-activities?partnerId=${ali.id}`, { headers });
  const activities = (await notes.json()) as { kind: string; subject: string; body: string | null }[];
  expect(activities.filter((a) => a.kind === "note" && a.subject === "WhatsApp reminder sent to +964 770 123 4567")).toHaveLength(1);
  expect(activities[0]?.body).toContain("Account statement: http");

  // Sales (this month's invoices): the reminder from an invoice's row carries that invoice; in Arabic for an Arabic reader; edited and restored.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Sales" }).click();
  await page.getByTestId(`remind-${fresh.number}`).click();
  await expect(dialog).toBeVisible();
  await expect(page.getByTestId(`whatsapp-attach-invoice-${fresh.id}`)).toHaveAttribute("data-state", "checked");
  await expect(page.getByTestId("whatsapp-attach-statement")).toHaveAttribute("data-state", "unchecked");
  await page.getByTestId("whatsapp-language").selectOption("ar");
  await expect.poll(message).toMatch(/السيد\/ة ورشة علي المحترم\/ة،\nتذكير من شركة تجارة القطع: رصيدكم المستحق لدينا IQD 500,000\.00\. منها IQD 300,000\.00 متأخرة عن موعدها\./);
  await text.fill("Custom words.");
  await page.getByTestId("whatsapp-reset-text").click();
  await expect(text).toHaveValue(/تذكير من شركة تجارة القطع/);
  await page.keyboard.press("Escape");

  // The fresh invoice is paid in full; the confirmation names the receipt, what the invoice got and the balance after, with the receipt and the invoice attached.
  await page.getByTestId(`pay-${fresh.number}`).click();
  await expect(page.getByTestId("payment-amount")).toHaveValue("200,000");
  await page.getByTestId("save-payment").click();
  await page.getByTestId("next-receipt-whatsapp").click();
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("Payment confirmation by WhatsApp");
  await expect.poll(message).toMatch(/Parts Trading Co\. confirms receiving IQD 200,000\.00 from you on .* \(receipt RCT-\d{4}-\d{5}\)\./);
  await expect.poll(message).toMatch(new RegExp(`• ${fresh.number}: IQD 200,000\\.00 paid \\(settled\\)`));
  await expect.poll(message).toMatch(/Your remaining balance is IQD 300,000\.00\./);
  await expect(page.getByTestId("whatsapp-attach-receipt")).toHaveAttribute("data-state", "checked");
  await page.getByTestId(`whatsapp-attach-invoice-${fresh.id}`).click();
  await expectAccessible(page);
  await page.getByTestId("whatsapp-send").click();
  await expect(page.getByTestId("whatsapp-sent")).toBeVisible({ timeout: 90_000 });
  const confirmation = await opened(page);
  expect(confirmation.text).toMatch(/\nReceipt RCT-\d{4}-\d{5}: http/);
  expect(confirmation.text).toMatch(new RegExp(`\\nInvoice ${fresh.number}: http`));
  expect(confirmation.text.match(/\/api\/v1\/public\/prints\//gu)).toHaveLength(2);
  await page.keyboard.press("Escape");

  // Settings: the company's own English wording of the reminder takes over; the standard text comes back on reset.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Settings" }).click();
  await expect(page.getByTestId("whatsapp-messages")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("whatsapp-template-reminder-en").fill("Hello {name}, you owe {amount}. {nope}");
  await page.getByTestId("whatsapp-template-save-reminder-en").click();
  await expect(page.getByTestId("whatsapp-template-save-reminder-en")).toBeDisabled();
  await page.getByTestId("flat-nav").getByRole("link", { name: "Money" }).click();
  await page.getByTestId("whatsapp-ALI").click();
  await expect.poll(message).toBe("Hello Ali Auto Repair, you owe IQD 300,000.00. {nope}");
  await page.keyboard.press("Escape");
  await page.getByTestId("flat-nav").getByRole("link", { name: "Settings" }).click();
  await page.getByTestId("whatsapp-template-reset-reminder-en").click();
  await expect(page.getByTestId("whatsapp-template-reminder-en")).toHaveValue(/^Dear \{name\},/);

  // The phone's Customers screen offers the same reminder.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/p/customers");
  await page.getByTestId("phone-whatsapp-ALI").click();
  await expect(dialog).toBeVisible();
  await expect(text).toHaveValue(/Dear Ali Auto Repair,/);
  await expectAccessible(page);
  await page.keyboard.press("Escape");
  await page.setViewportSize({ width: 1280, height: 800 });

  // The same dialog in Arabic.
  await page.goto("/trader/money");
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByTestId("whatsapp-ALI").click();
  await expect(dialog).toContainText("تذكير عبر واتساب");
  await expect(dialog).toContainText("كشف الحساب");
  await expectAccessible(page);
});
