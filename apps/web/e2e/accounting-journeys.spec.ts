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
  // A long journey (a chart, a posted journal, drill-downs and a period closed and reopened): under a minute alone, more on a shared CI runner.
  test.setTimeout(120_000);
  const slug = `acc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Accounting " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

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

  // Posting rules: the chart's profile is in use; a new version adds a narrower rule (purchase-invoice expenses to rent) and takes over.
  await nav(page, "Posting rules");
  await expect(page.getByTestId("posting-profile")).toBeVisible();
  await expect(page.getByTestId("rule-row").first()).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("new-profile-version").click();
  await page.getByTestId("create-version").click();
  await expect(page.getByTestId("profile-select")).toContainText("v2");
  await page.getByTestId("edit-rules").click();
  await page.getByTestId("add-rule").click();
  const newRule = page.getByTestId("rule-edit-row").last();
  await newRule.getByLabel("Account role").selectOption("PurchaseExpense");
  await newRule.getByLabel("Account", { exact: true }).fill("6110");
  await newRule.getByLabel("Document type").fill("purchase_invoice");
  await expectAccessible(page);
  await page.getByTestId("save-rules").click();
  await expect(page.getByTestId("rule-row").filter({ hasText: "purchase_invoice" })).toHaveCount(1);
  await page.getByTestId("activate-profile").click();
  await expect(page.getByTestId("posting-profile")).toContainText("in use");
  await page.getByTestId("tab-groups").click();
  await page.getByTestId("new-posting-group").click();
  await page.getByTestId("group-code").fill("IMPORTED");
  await page.getByTestId("group-name-en").fill("Imported goods");
  await page.getByTestId("save-posting-group").click();
  await expect(page.getByTestId("posting-group-row")).toContainText("IMPORTED");
  await expectAccessible(page);

  // A recurring rent journal, generated once by hand; a year of insurance paid up front, previewed and scheduled over 12 periods.
  await nav(page, "Recurring & deferrals");
  await page.getByTestId("new-template").click();
  await page.getByTestId("template-code").fill("RENT");
  await page.getByTestId("template-name-en").fill("Monthly rent");
  await page.getByTestId("template-account-0").fill("6110");
  await page.getByTestId("template-debit-0").fill("1000");
  await page.getByTestId("template-account-1").fill("2170");
  await page.getByTestId("template-credit-1").fill("1000");
  await expectAccessible(page);
  await page.getByTestId("save-template").click();
  await expect(page.getByTestId("template-detail")).toBeVisible();
  await page.getByTestId("generate-journal").click();
  await expect(page.getByTestId("generated")).toContainText("generated");
  await expectAccessible(page);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("grid")).toContainText("RENT");
  await page.getByTestId("tab-deferrals").click();
  await page.getByTestId("new-deferral").click();
  await page.getByTestId("deferral-balance").fill("1410");
  await page.getByTestId("deferral-target").fill("6110");
  await page.getByTestId("deferral-total").fill("12000");
  await page.getByTestId("deferral-description").fill("Insurance year");
  await page.getByTestId("preview-deferral").click();
  await expect(page.getByTestId("deferral-preview").getByRole("row")).toHaveCount(13);
  await expectAccessible(page);
  await page.getByTestId("save-deferral").click();
  await expect(page.getByTestId("deferral-line")).toHaveCount(12);
  await expectAccessible(page);
  await page.keyboard.press("Escape");

  // Running the routines now is logged for the company: who ran them, for which date, and what went out.
  await page.getByTestId("run-routines").click();
  await expect(page.getByTestId("routines-result")).toBeVisible();
  await page.getByTestId("tab-runs").click();
  const run = page.getByTestId("routine-run");
  await expect(run).toHaveCount(1);
  await expect(run).toContainText("run now");
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
