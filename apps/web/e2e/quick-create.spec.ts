import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Starting work from anywhere (founder programme item 1c): the "New" button in the top bar opens a quotation's form
 * on its screen; the customer the quotation needs is made from the "+" beside the customer field, and the item it
 * sells is made from the line's own picker, both without leaving the form.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByTestId("apps-menu").click();
  await page.getByTestId("apps-panel").getByRole("link", { name, exact: true }).click();
}

test("English: a quotation started from the New menu, with its customer and its item made on the spot", async ({ page }) => {
  const slug = `qc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Quick " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("QCK");
  await page.getByLabel(/Legal name \(English\)/).fill("Quick Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("QCK");

  // "New" in the top bar lists what the owner may create; the quotation opens with its form already up.
  await page.getByTestId("new-menu").click();
  await expect(page.getByTestId("new-menu-item")).toBeVisible();
  await page.getByTestId("new-menu-quotation").click();
  await expect(page).toHaveURL(/\/sales\/quotations$/u);
  await expect(page.getByTestId("quotation-customer")).toBeVisible();
  // The rarely needed fields are folded away until asked for.
  await expect(page.getByTestId("quotation-valid-until")).toHaveCount(0);
  await page.getByTestId("more-fields").click();
  await expect(page.getByTestId("quotation-valid-until")).toBeVisible();

  // The customer is made from the "+" beside the field, with the next code filled in, and comes back selected.
  await page.getByTestId("quick-create-customer").click();
  await expect(page.getByTestId("quick-create")).toBeVisible();
  await expect(page.getByTestId("quick-code")).toHaveValue("CUS-0001");
  await page.getByTestId("quick-name-en").fill("Quick Customer LLC");
  await page.getByTestId("quick-name-ar").fill("عميل سريع");
  await expectAccessible(page);
  await page.getByTestId("quick-save").click();
  await expect(page.getByTestId("quick-create")).toHaveCount(0);
  await expect(page.getByTestId("quotation-customer").locator("option:checked")).toHaveText("CUS-0001 · Quick Customer LLC");

  // An item code that matches nothing offers to create it; the new item lands on the line.
  await page.getByTestId("line-item-0").click();
  await page.getByTestId("line-item-0").pressSequentially("KETTLE");
  await expect(page.getByTestId("line-item-0-create")).toContainText("Create item “KETTLE”");
  await page.getByTestId("line-item-0-create").click();
  await expect(page.getByTestId("quick-create")).toBeVisible();
  await expect(page.getByTestId("quick-code")).toHaveValue("KETTLE");
  await page.getByTestId("quick-name-en").fill("Kettle");
  await page.getByTestId("quick-price").fill("25000");
  await page.getByTestId("quick-save").click();
  await expect(page.getByTestId("quick-create")).toHaveCount(0);
  await expect(page.getByTestId("line-item-0")).toHaveValue("KETTLE");
  await page.getByTestId("line-qty-0").fill("2");
  await page.getByTestId("line-price-0").fill("25000");
  await page.getByTestId("save-quotation").click();
  await expect(page.getByTestId("quotation-detail")).toBeVisible();
  await expect(page.getByTestId("quotation-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expect(page.getByTestId("quotation-total")).toContainText("50,000");
  await expectAccessible(page);

  // The item and the customer exist for good, on their own screens.
  await nav(page, "Items");
  await expect(page.getByRole("grid")).toContainText("KETTLE");
  await nav(page, "Customers");
  await expect(page.getByRole("grid")).toContainText("Quick Customer LLC");
});
