import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The fixed-assets journey (founder programme item 3, roadmap 6.6): a category, an asset paid from the bank, activated
 * into the books, depreciated through a month from the depreciation screen, verified; the register and the plan read;
 * then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: a category, an asset activated from the bank, a depreciation run, a verification; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `fa-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Assets " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("FAC");
  await page.getByLabel(/Legal name \(English\)/).fill("Assets Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("FAC");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The bank account through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "FAC")?.id ?? "";
  const main = (await (await page.request.post(`${apiUrl}/api/v1/banking/bank-accounts`, { headers, data: { companyId, code: "MAIN", name: { en: "Main account", ar: "الحساب الرئيسي" }, kind: "bank", currency: "IQD" } })).json()) as { id: string };

  // A category: vehicles, straight line over five years, a tenth kept.
  await nav(page, "Asset categories");
  await expectAccessible(page);
  await page.getByTestId("new-asset-category").click();
  await page.getByTestId("category-code").fill("VEH");
  await page.getByTestId("category-name-en").fill("Vehicles");
  await page.getByTestId("category-name-ar").fill("مركبات");
  await page.getByTestId("category-life").fill("60");
  await page.getByTestId("category-salvage").fill("10");
  await expectAccessible(page);
  await page.getByTestId("save-asset-category").click();
  await expect(page.getByRole("grid")).toContainText("VEH");

  // The asset: a van paid from the bank, drafted then activated.
  await nav(page, "Fixed assets");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Fixed assets");
  await page.getByTestId("new-asset").click();
  await page.getByTestId("asset-name-en").fill("Delivery van");
  await page.getByTestId("asset-name-ar").fill("شاحنة توصيل");
  await page.getByTestId("asset-category").selectOption({ index: 1 });
  await page.getByTestId("asset-acquisition-date").fill("2026-01-15");
  await page.getByTestId("asset-cost").fill("120000000");
  await page.getByTestId("asset-source").selectOption("bank");
  await page.getByTestId("asset-bank").selectOption(main.id);
  await page.getByTestId("asset-serial").fill("VIN-123");
  await expectAccessible(page);
  await page.getByTestId("save-asset").click();
  await expect(page.getByTestId("asset-detail")).toBeVisible();
  await expect(page.getByTestId("asset-detail")).toContainText("FA-000001");
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Draft");
  await page.getByTestId("activate-asset").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Active");
  await expect(page.getByTestId("asset-books")).toContainText("MAIN");
  await expect(page.getByTestId("asset-transactions")).toContainText("Acquisition");
  await expectAccessible(page);

  // Verified in place.
  await page.getByTestId("verify-asset").click();
  await page.getByTestId("action-date").fill("2026-02-01");
  await page.getByTestId("action-location").fill("Yard B");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("asset-verifications")).toContainText("Yard B");
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("FA-000001");

  // Depreciation through March: three months of 1,800,000 (108,000,000 over 60 months) posted as one run.
  await nav(page, "Depreciation");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Depreciation");
  await page.getByTestId("run-through").fill("2026-03-31");
  await page.getByTestId("post-run").click();
  await expect(page.getByRole("grid")).toContainText("DEP-2026-0001");
  await expect(page.getByRole("grid")).toContainText("5,400,000");
  await expect(page.getByTestId("reconciliation-status")).toContainText("Ledger agrees");
  await expect(page.getByTestId("forecast")).toContainText("1,800,000");
  await expectAccessible(page);

  await nav(page, "Fixed assets");
  await expect(page.getByRole("grid")).toContainText("5,400,000");
  await page.getByRole("grid").getByText("FA-000001", { exact: true }).dblclick();
  await expect(page.getByTestId("asset-nbv-value")).toContainText("114,600,000");
  await closeDialog(page);

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الموجودات الثابتة");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الموجودات الثابتة");
  await expect(page.getByRole("grid")).toContainText("فعّال");
  await expectAccessible(page);
  await nav(page, "الإهلاك");
  await expect(page.getByRole("grid")).toContainText("DEP-2026-0001");
  await expectAccessible(page);
});
