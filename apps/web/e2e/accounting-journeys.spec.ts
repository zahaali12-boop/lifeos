import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The accounting journey of slice 2.5 (ADR-0029): a company gets a chart from a template, a manual journal is
 * drafted and posted, the trial balance balances and drills to the ledger, the period is closed and reopened with a
 * reason; every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation").getByRole("link", { name }).click();
}

test("English: chart from a template, a journal posted, the trial balance drills to the ledger, the period closes and reopens", async ({ page }) => {
  const slug = `acc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Accounting " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // A company to post in.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("ACC");
  await page.getByLabel(/Legal name \(English\)/).fill("Accounting Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("ACC");

  // The chart of accounts from the IFRS template, then an account's details.
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();
  await expect(page.getByRole("table")).toContainText("6110");
  await page.getByRole("button", { name: "6110" }).click();
  await expect(page.getByTestId("dimension-rules")).toBeVisible();
  await expectAccessible(page);

  // A manual journal: rent against accrued expenses, saved then posted.
  await nav(page, "Journals");
  await page.getByTestId("new-journal").click();
  await page.getByTestId("journal-description").fill("September rent");
  await page.getByTestId("line-account-0").fill("6110");
  await page.getByTestId("line-debit-0").fill("1500000");
  await page.getByTestId("line-account-1").fill("2170");
  await page.getByTestId("line-credit-1").fill("1500000");
  await page.getByTestId("save-journal").click();
  await expect(page.getByTestId("journal-detail")).toContainText("Draft");
  await page.getByTestId("post-journal").click();
  await expect(page.getByTestId("journal-detail")).toContainText("Posted");
  await expectAccessible(page);
  await page.keyboard.press("Escape");

  // The trial balance balances and every figure drills to its ledger.
  await nav(page, "Trial balance");
  await expect(page.getByTestId("tb-status")).toContainText("Balanced");
  await expect(page.getByRole("table")).toContainText("6110");
  await expectAccessible(page);
  await page.getByRole("button", { name: "6110" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("6110");
  await expect(page.getByTestId("ledger-line")).toHaveCount(1);
  await expect(page.getByTestId("ledger-line")).toContainText("September rent");
  await expectAccessible(page);

  // Period control: the current period is hard-closed, then reopened with a reason.
  await nav(page, "Period control");
  await expect(page.getByTestId("period-row").first()).toBeVisible();
  const today = new Date();
  const currentRow = page.getByTestId("period-row").nth(today.getUTCMonth());
  await currentRow.getByTestId("hard-close-period").click();
  await expect(currentRow).toContainText("Hard closed");
  await page.getByTestId("period-reason").fill("Auditor adjustment");
  await currentRow.getByTestId("reopen-period").click();
  await expect(currentRow.getByTestId("reopen-period")).toHaveCount(0);
  await expectAccessible(page);
});
