import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The approvals journey of 4.0 (ADR-0020): an approval definition for stock adjustments written in the safe grammar
 * and checked as it is typed, activated; an adjustment submitted through it (no rule holds, so it is recorded as
 * auto-approved and posted); the request shown in the approvals screen with its why panel; then the screens in Arabic,
 * right-to-left. Every screen passes axe with no serious or critical violation. Routing to another member's inbox,
 * decisions, blocks, overrides, delegation and escalation are covered by the workflow API tests.
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

test("English: an approval definition checked and activated, an adjustment routed through it, the request and its why panel", async ({ page }) => {
  const slug = `wf-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Workflow " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // A company with a chart, a warehouse, an item and a reason code, so an adjustment can be submitted.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("WFC");
  await page.getByLabel(/Legal name \(English\)/).fill("Workflow Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("WFC");
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
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("PUMP");
  await page.getByTestId("item-name-en").fill("Pump");
  await page.getByTestId("item-name-ar").fill("مضخة");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await closeDialog(page);
  await nav(page, "Adjustments");
  await page.getByTestId("reason-codes").click();
  await page.getByTestId("reason-code").fill("FOUND");
  await page.getByTestId("reason-name-en").fill("Found");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(1);
  await closeDialog(page);

  // The approvals screen is empty and accessible before anything is routed.
  await nav(page, "Approvals");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Approvals");
  await expect(page.getByText("Nothing to decide")).toBeVisible();
  await expectAccessible(page);

  // A definition for stock adjustments: the condition is checked as typed (an unknown field is refused with its position), then saved and activated.
  await nav(page, "Approval rules");
  await page.getByTestId("new-definition").click();
  await page.getByTestId("definition-entity-type").selectOption({ label: "Stock adjustment" });
  await page.getByTestId("definition-name").fill("Large adjustments");
  await page.getByTestId("rule-name").fill("Over a billion");
  await page.getByTestId("rule-condition").fill("amoun >= 1000000000");
  await page.getByTestId("check-condition").click();
  await expect(page.getByTestId("condition-check")).toContainText("Unknown field 'amoun'");
  await page.getByTestId("rule-condition").fill("amount_in('IQD') >= 1000000000 and kind = 'positive'");
  await page.getByTestId("check-condition").click();
  await expect(page.getByTestId("condition-check")).toContainText("valid");
  await page.getByTestId("step-name").fill("Owner");
  await page.getByTestId("step-role").selectOption("owner");
  await expectAccessible(page);
  await page.getByTestId("save-definition").click();
  await expect(page.getByTestId("activate-definition")).toBeVisible();
  await page.getByTestId("activate-definition").click();
  await expect(page.getByTestId("retire-definition")).toBeVisible();
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Large adjustments");
  await expect(page.getByRole("grid")).toContainText("Active");
  await expectAccessible(page);

  // An adjustment below the rule: submitted through the definition, recorded as auto-approved and posted.
  await nav(page, "Adjustments");
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("adjustment-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("PUMP");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-cost-0").fill("250");
  await page.getByTestId("line-reason-0").selectOption({ label: "FOUND · Found" });
  await page.getByTestId("save-adjustment").click();
  await expect(page.getByTestId("adjustment-detail")).toBeVisible();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await closeDialog(page);

  // The request is listed for readers with its why panel: the rule did not hold, the values are shown.
  await nav(page, "Approvals");
  await page.getByTestId("tab-all").click();
  await page.getByTestId("status-filter").selectOption("auto_approved");
  await expect(page.getByRole("grid")).toContainText("positive · MAIN");
  await expect(page.getByRole("grid")).toContainText("Auto-approved");
  await page.getByRole("grid").getByText("positive · MAIN").first().dblclick();
  await expect(page.getByTestId("request-detail")).toBeVisible();
  await expect(page.getByTestId("why-panel")).toContainText("Over a billion");
  await expect(page.getByTestId("why-panel")).toContainText("not matched");
  await expect(page.getByTestId("request-detail")).toContainText("2,500");
  await expect(page.getByTestId("request-history")).toContainText("Submitted");
  await expectAccessible(page);
  await closeDialog(page);

  // Delegations: the tab opens and the form is accessible (no second member here to delegate to).
  await page.getByTestId("tab-delegations").click();
  await expect(page.getByText("No delegations")).toBeVisible();
  await page.getByTestId("new-delegation").click();
  await expectAccessible(page);
  await closeDialog(page);

  // Arabic: both screens render right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "قواعد الاعتماد");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("قواعد الاعتماد");
  await expect(page.getByRole("grid")).toContainText("نشط");
  await expectAccessible(page);
  await nav(page, "الاعتمادات");
  await page.getByTestId("tab-all").click();
  await page.getByTestId("status-filter").selectOption("auto_approved");
  await expect(page.getByRole("grid")).toContainText("معتمد تلقائيًا");
  await expectAccessible(page);
});
