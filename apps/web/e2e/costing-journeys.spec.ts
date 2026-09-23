import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The costing journey of the post-M4 screens: a company created on standard cost (its costing policy chosen on the
 * company form), an item whose first standard is set from its Costing tab before any stock arrives, ten units taken in
 * at that standard, then a new standard set from the valuation's item-cost dialog, which revalues the stock on hand;
 * the item's costing method is locked once it holds stock. Then the valuation in Arabic, right-to-left. Every screen
 * passes axe with no serious or critical violation.
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

test("English: a standard-cost company, a first standard before stock, stock at standard, a new standard that revalues it; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `std-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const newYear = `${String(new Date().getFullYear())}-01-01`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Costing " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // A company on standard cost; the costing policy is locked once it exists.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("STD");
  await page.getByLabel(/Legal name \(English\)/).fill("Standard Co.");
  await page.getByTestId("policy-costingMethod").selectOption("standard");
  await expectAccessible(page);
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("STD");
  await page.getByRole("grid").getByText("STD", { exact: true }).dblclick();
  await expect(page.getByTestId("policy-costingMethod")).toBeDisabled();
  await expect(page.getByTestId("policy-costingMethod")).toHaveValue("standard");
  await closeDialog(page);
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();
  await nav(page, "Warehouses");
  await page.getByTestId("new-warehouse").click();
  await page.getByTestId("warehouse-code").fill("MAIN");
  await page.getByTestId("warehouse-name-en").fill("Main warehouse");
  await page.getByTestId("save-warehouse").click();
  await expect(page.getByTestId("warehouse-detail")).toBeVisible();
  await closeDialog(page);

  // An item whose first standard (100) is set on its Costing tab before any stock arrives.
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("BOLT");
  await page.getByTestId("item-name-en").fill("Steel bolt");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await page.getByTestId("item-tab-costing").click();
  await expect(page.getByTestId("item-costing")).toContainText("Standard cost");
  await expect(page.getByTestId("costing-override")).toBeEnabled();
  await page.getByTestId("new-standard-cost").fill("100");
  await page.getByTestId("new-standard-date").fill(newYear);
  await page.getByTestId("new-standard-reason").fill("Opening standard");
  await page.getByTestId("save-standard-cost").click();
  await expect(page.getByTestId("standard-version")).toHaveCount(1);
  await expect(page.getByTestId("item-cost-standard")).toContainText("100");
  await expectAccessible(page);
  await closeDialog(page);

  // Ten bolts taken in at the standard.
  await nav(page, "Adjustments");
  await page.getByTestId("reason-codes").click();
  await page.getByTestId("reason-code").fill("INIT");
  await page.getByTestId("reason-name-en").fill("Opening stock");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(1);
  await closeDialog(page);
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("line-item-0").fill("BOLT");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-cost-0").fill("100");
  await page.getByTestId("line-reason-0").selectOption({ label: "INIT · Opening stock" });
  await page.getByTestId("save-adjustment").click();
  await expect(page.getByTestId("adjustment-detail")).toBeVisible();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await closeDialog(page);

  // The valuation: 10 × 100 = 1,000. A new standard of 120 from the item-cost dialog revalues the stock to 1,200.
  await nav(page, "Valuation");
  await expect(page.getByTestId("valuation-totals")).toContainText("1,000");
  await page.getByRole("grid").getByText("BOLT", { exact: true }).dblclick();
  await expect(page.getByTestId("item-cost")).toBeVisible();
  await page.getByTestId("new-standard-cost").fill("120");
  await page.getByTestId("new-standard-reason").fill("Annual review");
  await expectAccessible(page);
  await page.getByTestId("save-standard-cost").click();
  await expect(page.getByTestId("standard-version")).toHaveCount(2);
  await expect(page.getByTestId("item-cost-standard")).toContainText("120");
  await closeDialog(page);
  await expect(page.getByTestId("valuation-totals")).toContainText("1,200");

  // The item now holds stock, so its costing method can no longer change.
  await nav(page, "Items");
  await page.getByRole("grid").getByText("BOLT", { exact: true }).dblclick();
  await page.getByTestId("item-tab-costing").click();
  await expect(page.getByTestId("costing-override")).toBeDisabled();
  await closeDialog(page);

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "التقييم");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("التقييم");
  await expectAccessible(page);
});
