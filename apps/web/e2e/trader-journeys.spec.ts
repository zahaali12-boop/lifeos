import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The trader edition journey (ADR-0035, T1): a workspace switched to the trader edition from Settings shows the flat
 * bar of plain-language screens; on Offers a comparison is started for two car parts from two suppliers in China, the
 * offers entered (one cheaper but with a minimum quantity the order does not reach), compared landed in the company's
 * currency, the recommendation names the usable supplier with its reason and the saving a split would bring, and
 * "order from this supplier" makes the purchase order; the order is followed as a shipment to arrival, priced, sold
 * and paid for, and the Money and Health screens say what is owed and whether the project carries itself. Then the
 * same screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByTestId("apps-menu").click();
  await page.getByTestId("apps-panel").getByRole("link", { name, exact: true }).click();
}

test("English: the trader edition switched on, offers compared with minimum quantities, the recommended supplier ordered from; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `trd-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Trader " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("TRD");
  await page.getByLabel(/Legal name \(English\)/).fill("Parts Trading Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("TRD");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // Two parts, a warehouse and two suppliers through the API (the screens for them are covered by other journeys).
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "TRD")?.id ?? "";
  const call = async (method: "post" | "put", path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request[method](`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  await call("post", "/api/v1/items", { code: "PADS", name: { en: "Brake pads", ar: "فحمات فرامل" }, baseUom: "PCS" });
  await call("post", "/api/v1/items", { code: "FILTER", name: { en: "Oil filter", ar: "فلتر زيت" }, baseUom: "PCS" });
  await call("post", "/api/v1/inventory/warehouses", { companyId, code: "MAIN", name: { en: "Main store", ar: "المخزن الرئيسي" } });
  for (const [code, en, ar] of [["SHENZ", "Shenzhen Auto Parts", "شنجن لقطع السيارات"], ["GUANG", "Guangzhou Motors", "غوانزو موتورز"]]) {
    const supplier = await call("post", "/api/v1/partners", { code, legalName: { en, ar }, isSupplier: true });
    await call("put", `/api/v1/partners/${supplier.id}/supplier-accounts/${companyId}`, { currency: "IQD", leadTimeDays: 30 });
  }

  // Settings: the workspace switched to the trader edition; the bar shows the flat links at once.
  await nav(page, "Settings");
  await expect(page.getByTestId("edition-card")).toBeVisible();
  await page.getByTestId("edition-trader").check();
  await expect(page.getByTestId("flat-nav")).toBeVisible();
  await expect(page.getByTestId("flat-nav").getByRole("link", { name: "Offers" })).toBeVisible();
  await expectAccessible(page);

  // Offers: 100 pads and 50 filters from the two suppliers.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Offers" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Purchase offers");
  await page.getByTestId("new-offer-set").click();
  await page.getByTestId("offer-title").fill("Brake pads and filters, autumn");
  await page.getByTestId("offer-supplier-SHENZ").check();
  await page.getByTestId("offer-supplier-GUANG").check();
  await page.getByTestId("line-item-0").fill("PADS");
  await page.getByTestId("line-qty-0").fill("100");
  await page.getByTestId("add-line").click();
  await page.getByTestId("line-item-1").fill("FILTER");
  await page.getByTestId("line-qty-1").fill("50");
  await page.getByTestId("save-offer-set").click();
  await expect(page.getByTestId("offers-detail")).toBeVisible();
  await expect(page.getByTestId("offer-row")).toHaveCount(2);

  // Shenzhen: pads 14,000 but only from 500, filters 9,000; the only offer so far is recommended as such.
  await page.getByTestId("enter-offer-SHENZ").click();
  await page.getByTestId("offer-lead-time").fill("30");
  await page.getByTestId("offer-price-0").fill("14000");
  await page.getByTestId("offer-moq-0").fill("500");
  await page.getByTestId("offer-price-1").fill("9000");
  await page.getByTestId("offer-note-1").fill("OEM quality, boxed");
  await page.getByTestId("save-offer").click();
  await expect(page.getByTestId("offer-form")).toHaveCount(0);
  await expect(page.getByTestId("recommendation")).toContainText("SHENZ");
  await expect(page.getByTestId("recommendation")).toContainText("the only offer so far");
  await expect(page.getByTestId("offer-row").filter({ hasText: "SHENZ" }).getByTestId("offer-moq")).toHaveText("Below minimum");

  // Guangzhou: pads 15,000 and filters 9,500, slower; it is the one to order from, Shenzhen's minimum rules it out.
  await page.getByTestId("enter-offer-GUANG").click();
  await page.getByTestId("offer-lead-time").fill("45");
  await page.getByTestId("offer-price-0").fill("15000");
  await page.getByTestId("offer-price-1").fill("9500");
  await page.getByTestId("save-offer").click();
  await expect(page.getByTestId("offer-form")).toHaveCount(0);
  await expect(page.getByTestId("recommendation")).toContainText("GUANG");
  await expect(page.getByTestId("recommendation")).toContainText("minimum quantities");
  await expect(page.getByTestId("split-saving")).toContainText("25,000");
  const rows = page.getByTestId("offer-row");
  await expect(rows.filter({ hasText: "GUANG" }).getByTestId("offer-rank")).toHaveText("1");
  await expect(rows.filter({ hasText: "SHENZ" }).getByTestId("offer-rank")).toHaveText("2");
  await expect(rows.filter({ hasText: "GUANG" }).getByTestId("offer-landed-rc")).toContainText("1,975,000");
  await expect(rows.filter({ hasText: "SHENZ" }).getByTestId("offer-why-not")).toContainText("below its minimum on item 1");
  await expect(page.getByTestId("best-line-row")).toHaveCount(2);
  await expect(page.getByTestId("best-line-row").nth(1)).toContainText("SHENZ");
  await expectAccessible(page);

  await page.getByTestId("order-from-GUANG").click();
  await expect(page.getByTestId("ordered-note")).toContainText("PO-");
  await expect(page.getByTestId("offers-detail").getByTestId("offer-status")).toHaveText("Ordered");

  // The shipment: ordered at once; shipped with a date; sea freight added; what was paid recorded; received into the
  // store, which posts the receipt and lands the freight on the stock; the supplier's invoice recorded into the payable.
  await page.getByTestId("ordered-note").getByRole("link", { name: "Follow the shipment" }).click();
  await expect(page.getByTestId("shipment-detail")).toBeVisible();
  await expect(page.getByTestId("shipment-stage")).toHaveText("Ordered");
  await expect(page.getByTestId("stage-ordered")).toContainText("Ordered");
  await page.getByTestId("next-stage").selectOption("shipped");
  await page.getByTestId("next-stage-note").fill("MSC Gulsun, container MSCU1234567");
  await page.getByTestId("advance-shipment").click();
  await expect(page.getByTestId("shipment-stage")).toHaveText("Shipped");
  await expect(page.getByTestId("shipment-hint")).toHaveCount(2);
  await page.getByTestId("add-cost").click();
  await page.getByTestId("cost-type").selectOption({ label: "FREIGHT · Freight" });
  await page.getByTestId("cost-amount").fill("200000");
  await page.getByTestId("save-cost").click();
  await expect(page.getByTestId("cost-row")).toHaveCount(1);
  await expect(page.getByTestId("cost-row")).toContainText("200,000");
  await page.getByTestId("record-payment").click();
  await page.getByTestId("paid-amount").fill("2000000");
  await page.getByTestId("save-payment").click();
  await expect(page.getByTestId("landed-total")).toContainText("2,200,000");
  await expect(page.getByTestId("shipment-hints")).toContainText("above the order's");
  await expect(page.getByTestId("shipment-line").first().getByTestId("landed-unit")).toContainText("16,708");
  await expectAccessible(page);
  await page.getByTestId("receive-warehouse").selectOption({ label: "MAIN · Main store" });
  await page.getByTestId("receive-shipment").click();
  await expect(page.getByTestId("shipment-stage")).toHaveText("Received", { timeout: 20_000 });
  await expect(page.getByTestId("received-note")).toContainText("GRN-");
  await expect(page.getByTestId("received-note")).toContainText("landed on the stock");
  await page.getByTestId("invoice-number").fill("GZ-2026-18");
  await page.getByTestId("record-invoice").click();
  await expect(page.getByTestId("invoice-note")).toContainText("recorded in what you owe", { timeout: 20_000 });
  await expect(page.getByTestId("shipment-hints")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("grid")).toContainText("Received");
  await page.getByTestId("flat-nav").getByRole("link", { name: "Offers" }).click();
  await expect(page.getByRole("grid")).toContainText("Ordered");

  // Prices: the landed cost per unit from the shipment, a 25 % target rounded up to 250, the suggestion written to a new sales list.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Prices" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Selling prices");
  await page.getByTestId("create-price-list").click();
  await expect(page.getByTestId("no-price-list")).toHaveCount(0);
  await page.getByTestId("edit-pricing-settings").click();
  await page.getByTestId("target-margin").fill("25");
  await page.getByTestId("round-to").fill("250");
  await page.getByTestId("save-pricing-settings").click();
  await expect(page.getByTestId("target-margin-value")).toContainText("25");
  const padsRow = page.getByTestId("price-row").filter({ hasText: "PADS" });
  await expect(padsRow.getByTestId("landed-cost")).toContainText("16,708");
  await expect(padsRow.locator("[data-testid^='suggested-']")).toHaveValue("21,000");
  await expect(padsRow.getByTestId("alert-no_price")).toBeVisible();
  await expect(page.getByTestId("unpriced-count")).toContainText("2");
  await page.getByTestId("apply-prices").click();
  await expect(page.getByTestId("pricing-notice")).toContainText("written");
  await expect(padsRow.getByTestId("current-price")).toContainText("21,000");
  await expect(padsRow.getByTestId("alert-no_price")).toHaveCount(0);
  await expect(page.getByTestId("unpriced-count")).toHaveCount(0);
  await expectAccessible(page);

  // Sales and money: a customer and a cash box through the API; ten pads sold in one step at the list price; 100,000
  // received against the invoice; the Money page shows what is still owed and the supplier invoice to pay.
  const ali = await call("post", "/api/v1/partners", { code: "ALI", legalName: { en: "Ali Auto Repair", ar: "ورشة علي" }, isCustomer: true });
  await call("put", `/api/v1/partners/${ali.id}/customer-accounts/${companyId}`, { currency: "IQD" });
  await call("post", "/api/v1/banking/bank-accounts", { companyId, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency: "IQD", isActive: true });
  await page.getByTestId("flat-nav").getByRole("link", { name: "Sales" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Sales");
  await page.getByTestId("new-sale").click();
  await page.getByTestId("sale-customer").selectOption({ label: "ALI · Ali Auto Repair" });
  await page.getByTestId("line-item-0").fill("PADS");
  await page.getByTestId("line-qty-0").fill("10");
  await expect(page.getByTestId("line-price-0")).toHaveValue("21,000");
  await page.getByTestId("sell").click();
  await expect(page.getByTestId("sales-notice")).toContainText("210,000", { timeout: 20_000 });
  await expect(page.getByTestId("invoice-state").first()).toHaveText("Unpaid");
  await page.getByRole("button", { name: "Record a payment" }).first().click();
  await page.getByTestId("payment-amount").fill("100000");
  await page.getByTestId("save-payment").click();
  await expect(page.getByTestId("sales-notice")).toContainText("Payment recorded", { timeout: 20_000 });
  await expect(page.getByTestId("invoice-state").first()).toHaveText("Partly paid");
  await expect(page.getByTestId("invoice-remaining").first()).toContainText("110,000");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "Money" }).click();
  await expect(page.getByTestId("owed-to-you-total")).toContainText("110,000");
  await expect(page.getByTestId("you-owe-total")).toContainText("1,975,000");
  await expect(page.getByTestId("nothing-to-chase")).toBeVisible();
  await expect(page.getByTestId("pay-row")).toHaveCount(1);
  await expect(page.getByTestId("sales-month")).toHaveCount(6);
  await expectAccessible(page);

  // Health: the month made a profit on the ten pads; the fixed costs entered give the break-even and how long the
  // 100,000 in the cash box lasts; nothing is on the way, the pads sold to Ali, and the stock covers the coming days.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Health" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Health");
  await expect(page.getByTestId("verdict")).toContainText("a profit of");
  await expect(page.getByTestId("warning-row").filter({ hasText: "fixed costs" })).toBeVisible();
  await expect(page.getByTestId("break-even")).toHaveText("—");
  await page.getByTestId("edit-fixed-costs").click();
  await page.getByTestId("fixed-costs").fill("300000");
  await page.getByTestId("save-fixed-costs").click();
  await expect(page.getByTestId("fixed-costs-value")).toContainText("300,000", { timeout: 20_000 });
  await expect(page.getByTestId("break-even")).not.toHaveText("—");
  await expect(page.getByTestId("cash-total")).toContainText("100,000");
  await expect(page.getByTestId("cash-runway")).toContainText("0.3");
  await expect(page.getByTestId("warning-row").filter({ hasText: "covers only" })).toBeVisible();
  await expect(page.getByTestId("nothing-on-the-way")).toBeVisible();
  await expect(page.getByTestId("by-item-row")).toContainText("PADS");
  await expect(page.getByTestId("by-customer-row")).toContainText("ALI");
  await expect(page.getByTestId("nothing-to-reorder")).toBeVisible();
  await expect(page.getByTestId("month-row")).toHaveCount(6);
  await expectAccessible(page);

  // Stock: ninety pads in the store; a count is started, 88 typed (two missing), finished: the stock is set to 88.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Stock" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Stock");
  await expect(page.getByTestId("stock-row").filter({ hasText: "PADS" }).getByTestId("stock-on-hand")).toContainText("90");
  await page.getByTestId("start-count").click();
  await expect(page.getByTestId("stock-notice")).toContainText("started", { timeout: 20_000 });
  await expect(page.getByTestId("count-status")).toHaveText("Ready to count");
  await page.getByTestId("counted-PADS").fill("88");
  await page.getByTestId("save-counts").click();
  await expect(page.getByTestId("stock-notice")).toContainText("Counts saved", { timeout: 20_000 });
  await expect(page.getByTestId("count-progress")).toContainText("1 of 2");
  await page.getByTestId("finish-count").click();
  await expect(page.getByTestId("stock-notice")).toContainText("finished", { timeout: 20_000 });
  await expect(page.getByTestId("stock-row").filter({ hasText: "PADS" }).getByTestId("stock-on-hand")).toContainText("88");
  await expect(page.getByTestId("start-count")).toBeVisible();
  await expectAccessible(page);

  // Home: the trader's day in plain words: today's sale, the month's profit, the cash, what to do, and the shortcuts.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Home" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Hello");
  await expect(page.getByTestId("home-sold-today-value")).toContainText("210,000", { timeout: 20_000 });
  await expect(page.getByTestId("home-month-value")).toContainText("A profit of");
  await expect(page.getByTestId("home-cash-value")).toContainText("100,000");
  await expect(page.getByTestId("home-owed-value")).toContainText("110,000");
  await expect(page.getByTestId("todo-row").filter({ hasText: "covers only" })).toBeVisible();
  await expect(page.getByTestId("home-actions").getByRole("link", { name: "Sell" })).toBeVisible();
  await expect(page.getByTestId("home-month-row")).toHaveCount(6);
  await expectAccessible(page);

  // Arabic, right-to-left: the flat bar, the list and the record in Arabic.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("مرحباً");
  await expect(page.getByTestId("home-month-value")).toContainText("ربح قدره");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "المخزون" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("المخزون");
  await expect(page.getByTestId("stock-row").filter({ hasText: "PADS" }).getByTestId("stock-on-hand")).toContainText("88");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "صحة المشروع" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("صحة المشروع");
  await expect(page.getByTestId("verdict")).toContainText("ربح قدره");
  await expect(page.getByTestId("cash-runway")).toContainText("0.3");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "المال" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("المال");
  await expect(page.getByTestId("owed-to-you-total")).toContainText("110,000");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "الأسعار" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("أسعار البيع");
  await expect(page.getByTestId("price-row").filter({ hasText: "PADS" }).getByTestId("current-price")).toContainText("21,000");
  await expectAccessible(page);
  await page.getByTestId("flat-nav").getByRole("link", { name: "العروض" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("عروض الشراء");
  await expect(page.getByTestId("flat-nav").getByRole("link", { name: "العروض" })).toBeVisible();
  await expect(page.getByRole("grid")).toContainText("تم الطلب");
  await page.getByRole("grid").getByText("Brake pads and filters, autumn", { exact: true }).dblclick();
  await expect(page.getByTestId("offers-detail")).toBeVisible();
  await expect(page.getByTestId("offer-row").filter({ hasText: "GUANG" })).toContainText("تم الطلب");
  await expectAccessible(page);
  await page.keyboard.press("Escape");
  await page.getByTestId("flat-nav").getByRole("link", { name: "الشحنات" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الشحنات");
  await expect(page.getByRole("grid")).toContainText("وصلت");
  await page.getByRole("grid").getByText("GUANG", { exact: false }).first().dblclick();
  await expect(page.getByTestId("shipment-detail")).toBeVisible();
  await expect(page.getByTestId("shipment-stage")).toHaveText("وصلت");
  await expectAccessible(page);
});
