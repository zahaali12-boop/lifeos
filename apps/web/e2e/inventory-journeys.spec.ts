import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The inventory journey of M3 (ADR-0029): a company with a chart, two warehouses (one with bins), an item, a reason
 * code, a positive adjustment posted from the desktop, the stock screen showing it, a one-step transfer shipped, a
 * count frozen, entered with a variance, reasoned, approved and posted, and the valuation equal to what was booked;
 * then the same screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

test("English: warehouses with bins, an item, a posted adjustment, stock, a transfer, a count with a reasoned variance, and the valuation", async ({ page }) => {
  const slug = `inv-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Inventory " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // A company with a chart, so stock can be valued and booked.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("INV");
  await page.getByLabel(/Legal name \(English\)/).fill("Inventory Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("INV");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // Warehouses: MAIN without bins, COLD with bins and a bin A-01.
  await nav(page, "Warehouses");
  await page.getByTestId("new-warehouse").click();
  await page.getByTestId("warehouse-code").fill("MAIN");
  await page.getByTestId("warehouse-name-en").fill("Main warehouse");
  await page.getByTestId("save-warehouse").click();
  await expect(page.getByTestId("warehouse-detail")).toBeVisible();
  await closeDialog(page);
  await page.getByTestId("new-warehouse").click();
  await page.getByTestId("warehouse-code").fill("COLD");
  await page.getByTestId("warehouse-name-en").fill("Cold store");
  await page.getByTestId("warehouse-bins").check();
  await page.getByTestId("save-warehouse").click();
  await expect(page.getByTestId("warehouse-detail")).toBeVisible();
  await page.getByTestId("bin-code").fill("A-01");
  await page.getByTestId("add-bin").click();
  await expect(page.getByTestId("bin-row")).toHaveCount(1);
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("COLD");

  // An item.
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("WATER");
  await page.getByTestId("item-name-en").fill("Water 1.5L");
  await page.getByTestId("item-name-ar").fill("ماء ١٫٥ لتر");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await expect(page.getByRole("dialog")).toContainText("Water 1.5L");
  await expectAccessible(page);
  await closeDialog(page);

  // Reason codes, then a positive adjustment of 100 at 250 posted straight away (no approval configured).
  await nav(page, "Adjustments");
  await page.getByTestId("reason-codes").click();
  await page.getByTestId("reason-code").fill("INIT");
  await page.getByTestId("reason-name-en").fill("Opening stock");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(1);
  await page.getByTestId("reason-code").fill("MISCOUNT");
  await page.getByLabel(/Applies to/).selectOption("count");
  await page.getByTestId("reason-name-en").fill("Counting difference");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(2);
  await closeDialog(page);
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("adjustment-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("WATER");
  await page.getByTestId("line-qty-0").fill("100");
  await page.getByTestId("line-cost-0").fill("250");
  await page.getByTestId("line-reason-0").selectOption({ label: "INIT · Opening stock" });
  await page.getByTestId("save-adjustment").click();
  await expect(page.getByTestId("adjustment-detail")).toBeVisible();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await expectAccessible(page);
  await closeDialog(page);

  // Stock shows 100 on hand in MAIN.
  await nav(page, "Stock");
  await page.getByTestId("stock-search").fill("WATER");
  await expect(page.getByRole("grid")).toContainText("WATER");
  await expect(page.getByRole("grid")).toContainText("100");
  await expectAccessible(page);

  // A one-step transfer of 30 to COLD (bin A-01 is the only bin, so receipt lands there).
  await nav(page, "Transfers");
  await page.getByTestId("new-transfer").click();
  await page.getByTestId("transfer-from").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("transfer-to").selectOption({ label: "COLD · Cold store" });
  await page.getByTestId("transfer-kind").selectOption("one_step");
  await page.getByTestId("line-item-0").fill("WATER");
  await page.getByTestId("line-qty-0").fill("30");
  await page.getByTestId("line-to-bin-0").selectOption({ label: "A-01" });
  await page.getByTestId("save-transfer").click();
  await expect(page.getByTestId("transfer-detail")).toBeVisible();
  await page.getByTestId("ship-transfer").click();
  await expect(page.getByTestId("transfer-detail").getByTestId("doc-status")).toContainText("Received");
  await closeDialog(page);

  // A count of MAIN: frozen at 70, counted 68, the variance reasoned, approved and posted.
  await nav(page, "Counts");
  await page.getByTestId("new-count").click();
  await page.getByTestId("count-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("save-count").click();
  await expect(page.getByTestId("count-detail")).toBeVisible();
  await page.getByTestId("freeze-count").click();
  await expect(page.getByTestId("count-line")).toHaveCount(1);
  await expect(page.getByTestId("count-line")).toContainText("70");
  await page.getByTestId("counted-1").fill("68");
  await page.getByTestId("save-entries").click();
  await page.getByTestId("review-count").click();
  await expect(page.getByTestId("count-detail").getByTestId("doc-status")).toContainText("In review");
  await page.getByTestId("reason-1").selectOption("MISCOUNT");
  await expect(page.getByTestId("count-line")).toContainText("MISCOUNT");
  await page.getByTestId("approve-count").click();
  await expect(page.getByTestId("count-detail").getByTestId("doc-status")).toContainText("Approved");
  await page.getByTestId("post-count").click();
  await expect(page.getByTestId("count-detail").getByTestId("doc-status")).toContainText("Posted");
  await expectAccessible(page);
  await closeDialog(page);

  // Valuation: 98 units on hand across both warehouses, worth 98 × 250 = 24,500.
  await nav(page, "Valuation");
  await expect(page.getByRole("grid")).toContainText("WATER");
  await expect(page.getByTestId("valuation-totals")).toContainText("24,500");
  await expectAccessible(page);

  // Why a movement cost what it did: the opening adjustment of 100 at 250 explains itself as 25,000 of direct cost.
  await nav(page, "Stock");
  await page.getByTestId("tab-ledger").click();
  await page.getByRole("grid").getByRole("row").filter({ hasText: "Positive adjustment" }).first().dblclick();
  await expect(page.getByTestId("cost-explanation")).toBeVisible();
  await expect(page.getByTestId("explained-cost")).toContainText("25,000");
  await expect(page.getByTestId("value-entry-row").first()).toContainText("Direct cost");
  await expectAccessible(page);
  await closeDialog(page);

  // Lots and serials, and replenishment, open and pass axe even when empty.
  await nav(page, "Lots & serials");
  await expectAccessible(page);
  await nav(page, "Replenishment");
  await page.getByTestId("run-planner").click();
  await expect(page.getByTestId("planner-run")).toHaveCount(1);
  await expectAccessible(page);

  // Arabic: the stock and counts screens render right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "المخزون");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("المخزون");
  await expect(page.getByRole("grid")).toContainText("WATER");
  await expectAccessible(page);
  await nav(page, "الجرد");
  await expect(page.getByRole("grid")).toContainText("مرحّل");
  await expectAccessible(page);
});
