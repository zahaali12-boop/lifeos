import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The pricing journey of 5.2: an item sold by the piece and by the carton, a customer, a standard list with quantity
 * breaks, a wholesale list derived from it for that customer, a buy-ten-get-a-glass promotion and a price floor; then
 * the price check shows each line's price and why, the free glass as its own line, and a typed discount breaking the
 * floor; then the screens in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation").getByRole("link", { name, exact: true }).click();
}

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

async function item(page: Page, code: string, en: string, ar: string): Promise<void> {
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill(code);
  await page.getByTestId("item-name-en").fill(en);
  await page.getByTestId("item-name-ar").fill(ar);
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
}

async function addPrice(page: Page, code: string, uom: string, min: string, value: string): Promise<void> {
  await page.getByTestId("new-price").click();
  const dialog = page.getByRole("dialog");
  await dialog.getByTestId("price-item").fill(code);
  await dialog.getByTestId("price-item").press("Tab");
  await dialog.getByTestId("price-uom").selectOption({ label: uom });
  await dialog.getByTestId("price-min").fill(min);
  await dialog.getByTestId("price-value").fill(value);
  await dialog.getByTestId("save-price").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: price lists with breaks and a derived wholesale list, a promotion, a floor, and the price check explaining each price; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `prc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Pricing " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("PRC");
  await page.getByLabel(/Legal name \(English\)/).fill("Pricing Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("PRC");

  // Tea by the piece and by the carton of twelve; tea glasses.
  await item(page, "TEA", "Black tea", "شاي أسود");
  await page.getByTestId("add-unit").click();
  await page.getByTestId("unit-uom").selectOption("CTN");
  await page.getByTestId("unit-numerator").fill("12");
  await page.getByTestId("save-unit").click();
  await expect(page.getByTestId("item-units")).toContainText("1 CTN = 12 PCS");
  await closeDialog(page);
  await item(page, "GLASS", "Tea glass", "استكان");
  await closeDialog(page);

  // A customer.
  await nav(page, "Customers");
  await page.getByTestId("new-customer").click();
  await page.getByTestId("partner-code").fill("BAGHDAD-MALL");
  await page.getByTestId("partner-legal-name-en").fill("Baghdad Mall LLC");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();

  // The standard list: 1,000 a piece, 11,000 a carton from ten cartons, 2,000 a glass.
  await nav(page, "Price lists");
  await page.getByTestId("new-price-list").click();
  await page.getByTestId("list-code").fill("STD");
  await page.getByTestId("list-name").fill("Standard");
  await page.getByTestId("list-default").check();
  await page.getByTestId("save-price-list").click();
  await expect(page.getByTestId("price-list-summary")).toBeVisible();
  await addPrice(page, "TEA", "PCS", "0", "1000");
  await addPrice(page, "TEA", "CTN", "10", "11000");
  await addPrice(page, "GLASS", "PCS", "0", "2000");
  await expect(page.getByTestId("price-row")).toHaveCount(3);
  await expectAccessible(page);

  // Wholesale for Baghdad Mall: 8% under the standard list, rounded to the nearest 10.
  await nav(page, "Price lists");
  await page.getByTestId("new-price-list").click();
  await page.getByTestId("list-code").fill("WHOLESALE");
  await page.getByTestId("list-name").fill("Wholesale");
  await page.getByTestId("list-parent").selectOption({ label: "STD · Standard" });
  await page.getByTestId("list-adjustment").fill("-8");
  await page.getByTestId("list-rounding").fill("10");
  await page.getByTestId("list-customer").selectOption({ label: "BAGHDAD-MALL · Baghdad Mall LLC" });
  await expectAccessible(page);
  await page.getByTestId("save-price-list").click();
  await expect(page.getByTestId("price-list-summary")).toContainText("BAGHDAD-MALL");
  await expect(page.getByTestId("price-list-summary")).toContainText("STD -8%");
  await nav(page, "Price lists");
  await expect(page.getByRole("grid")).toContainText("WHOLESALE");
  await expectAccessible(page);

  // Buy ten tea, get a glass free; tea never under 900 a piece.
  await nav(page, "Discounts and promotions");
  await page.getByTestId("tab-promotions").click();
  await page.getByTestId("new-promotion").click();
  await page.getByTestId("promotion-code").fill("TEA10-GLASS");
  await page.getByTestId("promotion-name").fill("Ten tea, a glass free");
  await page.getByTestId("scope-item").fill("TEA");
  await page.getByTestId("promotion-buy").fill("10");
  await page.getByTestId("promotion-get-item").fill("GLASS");
  await expectAccessible(page);
  await page.getByTestId("save-promotion").click();
  await expect(page.getByTestId("promotion-row")).toContainText("TEA10-GLASS");
  await page.getByTestId("tab-floors").click();
  await page.getByTestId("new-floor").click();
  await page.getByTestId("floor-item").fill("TEA");
  await page.getByTestId("floor-min-price").fill("900");
  await page.getByTestId("save-floor").click();
  await expect(page.getByTestId("floor-row")).toContainText("TEA");
  await expectAccessible(page);

  // The price check: twelve tea and a glass for Baghdad Mall.
  await nav(page, "Price check");
  await page.getByTestId("check-customer").selectOption({ label: "BAGHDAD-MALL · Baghdad Mall LLC" });
  await page.getByTestId("basket-item-0").fill("TEA");
  await page.getByTestId("basket-item-0").press("Tab");
  await page.getByTestId("basket-uom-0").selectOption({ label: "PCS" });
  await page.getByTestId("basket-qty-0").fill("12");
  await page.getByTestId("add-basket-line").click();
  await page.getByTestId("basket-item-1").fill("GLASS");
  await page.getByTestId("basket-item-1").press("Tab");
  await page.getByTestId("basket-qty-1").fill("1");
  await page.getByTestId("calculate").click();

  // Tea from the wholesale list: 1,000 less 8% is 920; twelve are 11,040. The free glass is its own line.
  const lines = page.getByTestId("priced-line");
  await expect(lines).toHaveCount(3);
  await expect(lines.nth(0)).toContainText("The customer's list");
  await expect(lines.nth(0).getByTestId("line-net")).toContainText("11,040");
  await expect(lines.filter({ hasText: "Free with TEA10-GLASS" })).toHaveCount(1);
  const why = page.getByTestId("price-explanation");
  await expect(why.getByTestId("price-step-base_price")).toContainText("WHOLESALE");
  await expect(why.getByTestId("price-step-derivation")).toContainText("STD");
  await expect(why.getByTestId("price-step-floor")).toBeVisible();
  await expectAccessible(page);
  await lines.filter({ hasText: "Free with TEA10-GLASS" }).getByTestId("why-price").click();
  await expect(page.getByTestId("price-explanation").getByTestId("price-step-promotion")).toContainText("TEA10-GLASS");
  await expect(page.getByTestId("total-net")).toContainText("12,880");

  // A typed 5% off the tea takes it to 874 a piece, under its floor of 900: the check says it needs approval.
  await page.getByTestId("basket-discount-0").fill("5");
  await page.getByTestId("calculate").click();
  await expect(page.getByTestId("price-result")).toContainText("A price is under its floor");
  await expect(lines.nth(0)).toContainText("Under its floor: needs approval");
  await expectAccessible(page);

  // Arabic: the price check, the lists and the rules read right to left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("فحص السعر");
  await page.getByTestId("calculate").click();
  await expect(page.getByTestId("price-result")).toContainText("تحت حده الأدنى");
  await expectAccessible(page);
  await nav(page, "قوائم الأسعار");
  await expect(page.getByRole("grid")).toContainText("WHOLESALE");
  await expectAccessible(page);
  await nav(page, "الخصومات والعروض");
  await page.getByTestId("tab-promotions").click();
  await expect(page.getByTestId("promotion-row")).toContainText("TEA10-GLASS");
  await expectAccessible(page);
});
