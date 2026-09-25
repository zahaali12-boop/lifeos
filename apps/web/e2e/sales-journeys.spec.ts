import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The customer and CRM journey of 5.1: a customer group, a sales rep paid under a tiered commission plan (tried
 * before it is used), a customer with its account, credit limit and contact, a deal moved through the pipeline from
 * its 360 and from the board, lost with its reason, a second one won, an activity planned and done, credit put on
 * hold; then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

test("English: sales set-up, a customer with credit, a deal through the pipeline, activities and a credit hold; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `sls-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Sales " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("SLS");
  await page.getByLabel(/Legal name \(English\)/).fill("Sales Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("SLS");

  // Set-up: a group on net 30, a commission plan tried before it is used, the owner as a rep, the default pipeline.
  await nav(page, "Sales set-up");
  await page.getByTestId("new-customer-group").click();
  await page.getByTestId("group-code").fill("RETAIL");
  await page.getByTestId("group-name").fill("Retail chains");
  await page.getByTestId("group-payment-terms").selectOption({ label: "NET30 · Net 30 days" });
  await page.getByTestId("save-customer-group").click();
  await expect(page.getByTestId("group-row")).toContainText("RETAIL");
  await expectAccessible(page);

  await page.getByTestId("tab-plans").click();
  await page.getByTestId("new-commission-plan").click();
  await page.getByTestId("plan-code").fill("STD");
  await page.getByTestId("plan-name").fill("Standard");
  await page.getByTestId("plan-currency").fill("IQD");
  await page.getByTestId("rule-rate-0").fill("2");
  await page.getByTestId("add-rule").click();
  await page.getByTestId("rule-from-1").fill("1000000");
  await page.getByTestId("rule-rate-1").fill("3");
  await page.getByTestId("save-commission-plan").click();
  await expect(page.getByTestId("plan-card")).toContainText("STD");
  await page.getByTestId("try-plan").click();
  await page.getByTestId("quote-amount").fill("2000000");
  await page.getByTestId("run-quote").click();
  // One million at 2% and one at 3%.
  await expect(page.getByTestId("quote-total")).toContainText("50,000");
  await expectAccessible(page);
  await closeDialog(page);

  await page.getByTestId("tab-reps").click();
  await page.getByTestId("new-sales-rep").click();
  await page.getByTestId("rep-code").fill("OWN");
  await page.getByTestId("rep-name").fill("Owner the rep");
  await page.getByTestId("rep-member").selectOption({ index: 1 });
  await page.getByTestId("rep-plan").selectOption({ label: "STD · Standard" });
  await page.getByTestId("save-sales-rep").click();
  await expect(page.getByTestId("rep-row")).toContainText("OWN");
  await page.getByTestId("tab-stages").click();
  await expect(page.getByTestId("stage-row")).toHaveCount(6);
  await expectAccessible(page);

  // A customer with its account: group, rep, a credit limit; its 360 opens.
  await nav(page, "Customers");
  await page.getByTestId("new-customer").click();
  await page.getByTestId("partner-code").fill("BAGHDAD-MALL");
  await page.getByTestId("partner-legal-name-en").fill("Baghdad Mall LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة بغداد مول");
  await page.getByTestId("partner-email").fill("buying@baghdad-mall.example");
  await page.getByTestId("customer-group").selectOption({ label: "RETAIL · Retail chains" });
  await page.getByTestId("customer-rep").selectOption({ label: "OWN · Owner the rep" });
  await page.getByTestId("credit-limit").fill("250000000");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Baghdad Mall LLC");
  await expectAccessible(page);

  await page.getByTestId("tab-accounts").click();
  await expect(page.getByTestId("effective-terms")).toContainText("NET30");
  await page.getByTestId("tab-contacts").click();
  await page.getByTestId("contact-name").fill("Ahmed Jassim");
  await page.getByTestId("contact-email").fill("ahmed@baghdad-mall.example");
  await page.getByTestId("add-contact").click();
  await expect(page.getByTestId("contact-row")).toContainText("Ahmed Jassim");

  // A deal from the 360: it opens in the first stage and moves to a proposal.
  await page.getByTestId("new-opportunity").click();
  await page.getByTestId("opportunity-title").fill("Ramadan promotion, 14 stores");
  await page.getByTestId("opportunity-amount").fill("85000000");
  await page.getByTestId("save-opportunity").click();
  await expect(page.getByTestId("opportunity-detail")).toBeVisible();
  await expect(page.getByTestId("opportunity-stage-badge")).toHaveText("Lead");
  await page.getByTestId("move-PROPOSAL").click();
  await expect(page.getByTestId("opportunity-stage-badge")).toHaveText("Proposal");
  await expect(page.getByTestId("opportunity-weighted")).toContainText("42,500,000");
  const deal = page.getByRole("dialog");
  await deal.getByTestId("tab-activities").click();
  await deal.getByTestId("activity-subject").fill("Call the buyer about quantities");
  await deal.getByTestId("save-activity").click();
  await expect(deal.getByTestId("activity-row")).toContainText("Call the buyer about quantities");
  await deal.getByTestId("tab-stage-history").click();
  await expect(deal.getByTestId("stage-change")).toHaveCount(2);
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByTestId("kpi-pipeline")).toContainText("42,500,000");

  // A second deal for the board, won there.
  await page.getByTestId("new-opportunity").click();
  await page.getByTestId("opportunity-title").fill("Winter dairy contract");
  await page.getByTestId("opportunity-amount").fill("12000000");
  await page.getByTestId("save-opportunity").click();
  await expect(page.getByTestId("opportunity-detail")).toBeVisible();
  await closeDialog(page);

  // The board: the promotion moves to negotiation, then is lost with its reason; the dairy deal is won.
  await nav(page, "Pipeline");
  const proposal = page.getByTestId("column-PROPOSAL");
  await expect(proposal.getByTestId("deal-card")).toContainText("Ramadan promotion");
  await proposal.getByTestId("deal-card").filter({ hasText: "Ramadan promotion" }).getByTestId("move-deal").selectOption({ label: "Negotiation" });
  await expect(page.getByTestId("column-NEGOTIATION").getByTestId("deal-card")).toContainText("Ramadan promotion");
  await expectAccessible(page);
  await page.getByTestId("column-NEGOTIATION").getByTestId("deal-card").getByTestId("move-deal").selectOption({ label: "Lost" });
  await page.getByTestId("lost-reason").fill("Chose a local supplier on price");
  await page.getByTestId("confirm-lost").click();
  await expect(page.getByTestId("column-LOST").getByTestId("deal-card")).toContainText("Ramadan promotion");
  await page.getByTestId("column-LEAD").getByTestId("deal-card").filter({ hasText: "Winter dairy" }).getByTestId("move-deal").selectOption({ label: "Won" });
  await expect(page.getByTestId("column-WON").getByTestId("deal-card")).toContainText("Winter dairy");

  // The activity is on the owner's list; done with its outcome.
  await nav(page, "Sales activities");
  await expect(page.getByTestId("activities")).toContainText("Call the buyer about quantities");
  await page.getByTestId("complete-activity").first().click();
  await page.getByTestId("activity-outcome").fill("Wants 14 stores, samples next week");
  await page.getByTestId("confirm-done").click();
  await expect(page.getByTestId("activities")).toContainText("Nothing here.");
  await expectAccessible(page);

  // Back on the 360: a lost and a won deal, a 50% win rate, and credit control puts the account on hold.
  await nav(page, "Customers");
  await page.getByRole("grid").getByText("BAGHDAD-MALL").dblclick();
  await expect(page.getByTestId("kpi-win-rate")).toContainText("50%");
  await page.getByTestId("tab-accounts").click();
  await page.getByTestId("credit-reason").fill("Two cheques returned");
  await page.getByTestId("apply-credit-status").click();
  await expect(page.getByTestId("release-credit")).toBeVisible();
  await expect(page.getByTestId("credit-badge").first()).toContainText("On credit hold");
  await page.getByTestId("tab-transactions").click();
  await expect(page.getByTestId("no-transactions")).toBeVisible();
  await expectAccessible(page);

  // Arabic: the customers, the 360, the board and the set-up read right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByTestId("tab-overview").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("شركة بغداد مول");
  await expect(page.getByTestId("credit-badge").first()).toContainText("معلّق ائتمانيًا");
  await expectAccessible(page);
  await nav(page, "العملاء");
  await expect(page.getByRole("grid")).toContainText("BAGHDAD-MALL");
  await expectAccessible(page);
  await nav(page, "الفرص");
  await expect(page.getByTestId("column-WON")).toContainText("Winter dairy");
  await expectAccessible(page);
  await nav(page, "إعداد المبيعات");
  await page.getByTestId("tab-plans").click();
  await expect(page.getByTestId("plan-card")).toContainText("STD");
  await expectAccessible(page);
});
