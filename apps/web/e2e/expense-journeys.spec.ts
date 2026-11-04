import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The expenses journey (founder programme item 2, roadmap 6.4): a supplier's bill recorded with its date and lines,
 * confirmed into an accrual and paid on its payment date; a petty cash fund replenished from the bank and a voucher
 * spending it. Then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

test("English: a supplier bill confirmed and paid, a petty cash fund replenished and spent; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `exp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Expenses " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("EXP");
  await page.getByLabel(/Legal name \(English\)/).fill("Expenses Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("EXP");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The supplier, the bank account, the cash box and the categories, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "EXP")?.id ?? "";
  const supplier = (await (await page.request.post(`${apiUrl}/api/v1/partners`, { headers, data: { code: "STATION", legalName: { en: "Station Supplies", ar: "ستيشن للتوريدات" }, isSupplier: true } })).json()) as { id: string };
  expect((await page.request.put(`${apiUrl}/api/v1/partners/${supplier.id}/supplier-accounts/${companyId}`, { headers, data: { currency: "IQD" } })).ok()).toBeTruthy();
  const main = (await (await page.request.post(`${apiUrl}/api/v1/banking/bank-accounts`, { headers, data: { companyId, code: "MAIN", name: { en: "Main account", ar: "الحساب الرئيسي" }, kind: "bank", currency: "IQD" } })).json()) as { id: string };
  const box = (await (await page.request.post(`${apiUrl}/api/v1/banking/bank-accounts`, { headers, data: { companyId, code: "BOX", name: { en: "Office cash box", ar: "صندوق المكتب" }, kind: "cash", currency: "IQD" } })).json()) as { id: string };
  expect((await page.request.post(`${apiUrl}/api/v1/expenses/categories`, { headers, data: { code: "OFFICE", name: { en: "Office supplies", ar: "مستلزمات مكتبية" }, accountCode: "6150" } })).ok()).toBeTruthy();
  expect((await page.request.post(`${apiUrl}/api/v1/expenses/categories`, { headers, data: { code: "TRAVEL", name: { en: "Travel", ar: "سفر" }, accountCode: "6140", maxAmount: 500000 } })).ok()).toBeTruthy();

  // A supplier bill: two lines, saved as a draft with its date.
  await nav(page, "Expenses");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Expenses");
  await expectAccessible(page);
  await page.getByTestId("new-expense").click();
  await page.getByTestId("expense-kind").selectOption("supplier");
  await page.getByTestId("expense-supplier").selectOption(supplier.id);
  await page.getByTestId("expense-date").fill("2026-10-05");
  await page.getByTestId("expense-due-date").fill("2026-11-04");
  await page.getByTestId("expense-reference").fill("INV-7781");
  await page.getByTestId("expense-description").fill("October stationery and the Basra trip");
  await page.getByTestId("line-category-0").selectOption("OFFICE");
  await page.getByTestId("line-description-0").fill("Stationery");
  await page.getByTestId("line-amount-0").fill("150000");
  await page.getByTestId("line-category-1").selectOption("TRAVEL");
  await page.getByTestId("line-description-1").fill("Basra trip");
  await page.getByTestId("line-amount-1").fill("90000");
  await expectAccessible(page);
  await page.getByTestId("save-expense").click();
  await expect(page.getByTestId("expense-detail")).toBeVisible();
  await expect(page.getByTestId("doc-status").first()).toContainText("Draft");
  await expect(page.getByTestId("expense-total")).toContainText("240,000");
  await expectAccessible(page);

  // Confirming posts the accrual; the status bar moves to "Confirmed" and the payable waits on the due date.
  await page.getByTestId("confirm-expense").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Confirmed");
  await expect(page.getByTestId("expense-remaining")).toContainText("240,000");
  await expect(page.getByTestId("expense-detail")).toContainText("EXP-2026-00001");

  // Paid on its own date from the bank account.
  await page.getByTestId("pay-expense").click();
  await page.getByTestId("pay-bank").selectOption(main.id);
  await page.getByTestId("pay-date").fill("2026-11-03");
  await page.getByTestId("confirm-pay").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Paid");
  await expect(page.getByTestId("expense-remaining")).toContainText("0");
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("EXP-2026-00001");

  // A petty cash fund on the cash box, replenished from the bank.
  await nav(page, "Petty cash funds");
  await page.getByTestId("new-fund").click();
  await page.getByTestId("fund-code").fill("PC-HQ");
  await page.getByTestId("fund-box").selectOption(box.id);
  await page.getByTestId("fund-name-en").fill("Head office petty cash");
  await page.getByTestId("fund-name-ar").fill("الصندوق النثري للمركز الرئيسي");
  await page.getByTestId("fund-float").fill("500000");
  await expectAccessible(page);
  await page.getByTestId("save-fund").click();
  await expect(page.getByTestId("fund-detail")).toBeVisible();
  await expect(page.getByTestId("fund-to-replenish")).toContainText("500,000");
  await page.getByTestId("replenish-fund").click();
  await page.getByTestId("replenish-from").selectOption(main.id);
  await page.getByTestId("replenish-date").fill("2026-10-01");
  await page.getByTestId("confirm-replenish").click();
  await expect(page.getByTestId("fund-balance")).toContainText("500,000");
  await expect(page.getByTestId("replenishment-row")).toContainText("PCR-2026-00001");
  await expectAccessible(page);
  await closeDialog(page);

  // A voucher spends the fund the moment it is confirmed.
  await nav(page, "Expenses");
  await page.getByTestId("new-expense").click();
  await page.getByTestId("expense-kind").selectOption("petty_cash");
  await page.getByTestId("expense-fund").selectOption({ index: 1 });
  await page.getByTestId("expense-date").fill("2026-10-07");
  await page.getByTestId("line-category-0").selectOption("OFFICE");
  await page.getByTestId("line-description-0").fill("Printer paper");
  await page.getByTestId("line-amount-0").fill("35000");
  await page.getByTestId("save-expense").click();
  await expect(page.getByTestId("expense-detail")).toBeVisible();
  await page.getByTestId("confirm-expense").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Confirmed");
  await expect(page.getByTestId("pay-expense")).toHaveCount(0);
  await closeDialog(page);
  await nav(page, "Petty cash funds");
  await expect(page.getByRole("grid")).toContainText("465,000");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "المصروفات");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("المصروفات");
  await expect(page.getByRole("grid")).toContainText("سند صندوق نثري");
  await expectAccessible(page);
  await nav(page, "فئات المصروفات");
  await expect(page.getByRole("grid")).toContainText("OFFICE");
  await expectAccessible(page);
});
