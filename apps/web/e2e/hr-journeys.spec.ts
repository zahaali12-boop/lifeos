import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The payroll journey (founder programme item 8): a housing allowance set up as a pay component, an employee hired
 * from the screen (partner, supplier account and employee in one go), unpaid leave recorded, and the month's run
 * calculated with the days prorated, submitted, posted to the books and paid from the bank, with the payslip to print.
 * Then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

test("English: a component, an employee, unpaid leave and a month's run calculated, posted and paid; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `hr-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Payroll " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("HRC");
  await page.getByLabel(/Legal name \(English\)/).fill("Payroll Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("HRC");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The bank account the salaries are paid from, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "HRC")?.id ?? "";
  expect((await page.request.post(`${apiUrl}/api/v1/banking/bank-accounts`, { headers, data: { companyId, code: "MAIN", name: { en: "Main account", ar: "الحساب الرئيسي" }, kind: "bank", currency: "IQD" } })).ok()).toBeTruthy();

  // A housing allowance of a quarter of the basic, for everyone; BASIC is already there.
  await nav(page, "Pay components");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Pay components");
  await expect(page.getByRole("grid")).toContainText("BASIC");
  await expectAccessible(page);
  await page.getByTestId("new-component").click();
  await page.getByTestId("component-name-en").fill("Housing allowance");
  await page.getByTestId("component-name-ar").fill("بدل سكن");
  await page.getByTestId("component-kind").selectOption("earning");
  await page.getByTestId("component-calculation").selectOption("percent_of_basic");
  await page.getByTestId("component-rate").fill("25");
  await page.getByTestId("component-expense-account").fill("6100");
  await page.getByTestId("component-all").check();
  await expectAccessible(page);
  await page.getByTestId("save-component").click();
  await expect(page.getByRole("grid")).toContainText("HOUSING-ALLOWANCE");

  // An employee hired from the screen: the partner, the supplier account and the employee in one save.
  await nav(page, "Employees");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Employees");
  await page.getByTestId("new-employee").click();
  await page.getByTestId("employee-name-en").fill("Noura Al-Harbi");
  await page.getByTestId("employee-name-ar").fill("نورة الحربي");
  await page.getByTestId("employee-hire-date").fill("2026-01-01");
  await page.getByTestId("employee-basic").fill("1500000");
  await page.getByTestId("employee-job-en").fill("Accountant");
  await expectAccessible(page);
  await page.getByTestId("save-employee").click();
  await expect(page.getByTestId("employee-detail")).toContainText("EMP-0001");
  await expect(page.getByTestId("employee-basic-salary")).toContainText("1,500,000");
  await expect(page.getByTestId("employee-leave-balance")).toContainText("21 / 21");
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Noura Al-Harbi");

  // Three days of unpaid leave in September.
  await nav(page, "Leave");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Leave");
  await page.getByTestId("new-leave").click();
  await page.getByTestId("leave-employee").selectOption({ label: "EMP-0001 · Noura Al-Harbi" });
  await page.getByTestId("leave-kind").selectOption("unpaid");
  await page.getByTestId("leave-from").fill("2026-09-07");
  await page.getByTestId("leave-to").fill("2026-09-09");
  await expectAccessible(page);
  await page.getByTestId("save-leave").click();
  await expect(page.getByRole("grid")).toContainText("Unpaid");
  await expectAccessible(page);

  // September's run: 27 of 30 days, the basic prorated, the allowance on it; submitted, posted and paid from the bank.
  await nav(page, "Payroll");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Payroll");
  await page.getByTestId("new-run").click();
  await page.getByTestId("run-year").fill("2026");
  await page.getByTestId("run-month").selectOption("9");
  await expectAccessible(page);
  await page.getByTestId("save-run").click();
  const detail = page.getByTestId("run-detail");
  await expect(detail).toContainText("PRL-2026-001");
  await expect(detail.getByTestId("doc-status").first()).toContainText("Calculated");
  await expect(page.getByTestId("line-days-1")).toContainText("27 / 30");
  await expect(page.getByTestId("line-basic-1")).toContainText("1,350,000");
  await expect(page.getByTestId("line-net-1")).toContainText("1,687,500");
  await page.getByTestId("line-toggle-1").click();
  await expect(page.getByTestId("line-items-1")).toContainText("HOUSING-ALLOWANCE");
  await expectAccessible(page);
  await page.getByTestId("submit-run").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("post-run").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("run-journal")).toBeVisible();
  await page.getByTestId("pay-run").click();
  await page.getByTestId("pay-bank").selectOption({ label: "MAIN · IQD" });
  await expectAccessible(page);
  await page.getByTestId("confirm-pay").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Paid");
  await expect(page.getByTestId("run-line").first()).toContainText("PAY-2026-");
  await expect(page.getByTestId("print-payslip-1")).toBeVisible();
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Paid");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الرواتب");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الرواتب");
  await expect(page.getByRole("grid")).toContainText("مدفوع");
  await expectAccessible(page);
  await nav(page, "الموظفون");
  await expect(page.getByRole("grid")).toContainText("نورة الحربي");
  await expectAccessible(page);
});
