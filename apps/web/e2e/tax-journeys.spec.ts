import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The tax journey of 5.3: install Iraq's sales-tax template, see its codes, rates and determination matrix, add a
 * partner tax group and a company registration, then preview and file an (empty, so trivially reconciled) return
 * period; then the screens in Arabic. Every screen passes axe with no serious or critical violation.
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

test("English: install a tax template, extend its codes and matrix, register a company and file an empty return; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `tax-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Tax " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // A company: the default country (IQ) matches the Iraq sales-tax template.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("TAX");
  await page.getByLabel(/Legal name \(English\)/).fill("Tax Demo Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("TAX");

  // Install the Iraq sales-tax template.
  await nav(page, "Tax setup");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Tax setup");
  const iqRow = page.getByTestId("template-row").filter({ hasText: "IQ-ST" });
  await expect(iqRow).toBeVisible();
  await iqRow.getByTestId("install-template").click();
  await expect(iqRow).toContainText("Installed", { timeout: 10_000 });
  await expectAccessible(page);

  // The regime came with codes, rates and a determination matrix; add one more code.
  await page.getByTestId("tab-regimes").click();
  await page.getByTestId("regime-row").filter({ hasText: "IQ-ST" }).getByTestId("select-regime").click();
  await expect(page.getByTestId("code-row").first()).toBeVisible();
  await expect(page.getByTestId("code-row")).not.toHaveCount(0);
  await expect(page.getByTestId("rule-row")).not.toHaveCount(0);
  await page.getByTestId("new-code").click();
  const codeDialog = page.getByRole("dialog");
  await codeDialog.getByTestId("code-code").fill("IQ-CUSTOM10");
  await codeDialog.getByLabel(/Name \(English\)/).fill("Custom ten percent");
  await codeDialog.getByLabel(/Name \(Arabic\)/).fill("عشرة بالمئة مخصصة");
  await codeDialog.getByTestId("rate-from").fill("2026-01-01");
  await codeDialog.getByTestId("rate-pct").fill("10");
  await codeDialog.getByTestId("save-code").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("code-row").filter({ hasText: "IQ-CUSTOM10" })).toBeVisible();
  await expectAccessible(page);

  // A new determination rule taking the custom code for sales of standard-rated goods (the template's other rows
  // already claim the plain sales default and the hospitality and telecom item groups).
  await page.getByTestId("new-rule").click();
  const ruleDialog = page.getByRole("dialog");
  await ruleDialog.getByTestId("rule-direction").selectOption({ label: "Sales" });
  await ruleDialog.getByTestId("rule-code").selectOption({ label: "IQ-CUSTOM10" });
  await ruleDialog.getByLabel(/Item tax group/).selectOption({ label: "STANDARD · Standard-rated goods and services" });
  await ruleDialog.getByTestId("save-rule").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("rule-row").filter({ hasText: "IQ-CUSTOM10" })).toBeVisible();

  // The template's item and partner tax groups; add one more partner group.
  await page.getByTestId("tab-groups").click();
  await expect(page.getByTestId("group-row")).not.toHaveCount(0);
  await page.getByTestId("group-kind-filter").selectOption({ label: "Partner tax group" });
  await expect(page.getByTestId("group-row").filter({ hasText: "DOMESTIC" }).first()).toBeVisible();
  await page.getByTestId("new-group").click();
  const groupDialog = page.getByRole("dialog");
  await groupDialog.getByTestId("group-code").fill("DIPLOMATIC");
  await groupDialog.getByLabel(/Name \(English\)/).fill("Diplomatic missions");
  await groupDialog.getByLabel(/Name \(Arabic\)/).fill("البعثات الدبلوماسية");
  await groupDialog.getByTestId("save-group").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("group-row").filter({ hasText: "DIPLOMATIC" })).toBeVisible();
  await expectAccessible(page);

  // Register the company in the regime.
  await page.getByTestId("tab-registrations").click();
  await page.getByTestId("new-registration").click();
  const registrationDialog = page.getByRole("dialog");
  await registrationDialog.getByTestId("registration-regime").selectOption({ label: "IQ-ST" });
  await registrationDialog.getByTestId("registration-number").fill("IQ-999000111");
  await registrationDialog.getByTestId("save-registration").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("registration-row")).toContainText("IQ-999000111");
  await expectAccessible(page);

  // The return: nothing posted yet, so every box is empty and the ledger trivially reconciles; file it.
  await nav(page, "Tax returns");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Tax returns");
  await expect(page.getByText("Reconciled to the ledger")).toBeVisible({ timeout: 10_000 });
  await expectAccessible(page);
  await page.getByTestId("return-reference").fill("IQ-2026-DEMO");
  await page.getByTestId("file-return").click();
  await expect(page.getByText("Period filed")).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId("period-row")).toContainText("IQ-2026-DEMO");
  await expect(page.getByTestId("file-return")).toBeDisabled();
  await expectAccessible(page);

  // Arabic: the setup and returns screens read right to left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الإقرارات الضريبية");
  await expect(page.getByTestId("period-row")).toContainText("IQ-2026-DEMO");
  await expectAccessible(page);
  await nav(page, "إعداد الضريبة");
  await expect(page.getByTestId("tab-templates")).toBeVisible();
  await expectAccessible(page);
});
