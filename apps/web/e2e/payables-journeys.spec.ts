import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The payables journey of 4.7: a supplier's expense invoice is posted, a bank account opened, the invoice paid in
 * part with a remainder on account, the open items and their aging read, the on-account remainder applied to the
 * invoice, a proposal for the rest drafted into a payment. Then the screens in Arabic, right-to-left. Every screen
 * passes axe with no serious or critical violation.
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

test("English: an invoice paid in part, the remainder on account applied, the rest proposed and paid; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `pay-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Payables " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("PAY");
  await page.getByLabel(/Legal name \(English\)/).fill("Payables Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("PAY");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  await nav(page, "Suppliers");
  await page.getByTestId("new-supplier").click();
  await page.getByTestId("partner-code").fill("ALPHA");
  await page.getByTestId("partner-legal-name-en").fill("Alpha Supplies");
  await page.getByTestId("partner-legal-name-ar").fill("ألفا للتوريدات");
  await page.getByTestId("partner-email").fill("alpha@example.test");
  await page.getByTestId("save-supplier").click();
  await expect(page.getByTestId("supplier-detail")).toBeVisible();
  await closeDialog(page);

  // The command palette finds the supplier by code and opens it.
  await page.keyboard.press("ControlOrMeta+k");
  await page.getByRole("dialog").getByPlaceholder(/Search/).fill("ALPHA");
  await expect(page.getByRole("dialog")).toContainText("Alpha Supplies");
  await page.getByRole("option", { name: /ALPHA/ }).click();
  await expect(page.getByTestId("supplier-detail")).toBeVisible();
  await closeDialog(page);

  // An expense invoice of 10 000 posted.
  await nav(page, "Supplier invoices");
  await page.getByTestId("new-invoice").click();
  await page.getByTestId("invoice-supplier").selectOption({ label: "ALPHA · Alpha Supplies" });
  await page.getByTestId("invoice-kind").selectOption("expense");
  await page.getByTestId("invoice-reference").fill("R-1");
  await page.getByTestId("add-expense-line").click();
  await page.getByTestId("invoice-description-0").fill("Office rent");
  await page.getByTestId("invoice-price-0").fill("10000");
  await page.getByTestId("save-invoice").click();
  await expect(page.getByTestId("invoice-detail")).toBeVisible();
  await page.getByTestId("submit-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("post-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await closeDialog(page);

  // The bank account payments go out of.
  await nav(page, "Bank & cash accounts");
  await expect(page.getByText("No bank or cash accounts yet")).toBeVisible();
  await page.getByTestId("new-bank-account").click();
  await page.getByTestId("bank-code").fill("MAIN");
  await page.getByTestId("bank-name-en").fill("Main account");
  await page.getByTestId("bank-name-ar").fill("الحساب الرئيسي");
  await page.getByTestId("bank-bank-name").fill("Trade Bank");
  await expectAccessible(page);
  await page.getByTestId("save-bank-account").click();
  await expect(page.getByTestId("bank-account-detail")).toBeVisible();
  await expect(page.getByTestId("bank-balance")).toContainText("0");
  await closeDialog(page);

  // 6 000 paid against the invoice and 1 000 on account.
  await nav(page, "Supplier payments");
  await expect(page.getByText("No payments yet")).toBeVisible();
  await page.getByTestId("new-payment").click();
  await page.getByTestId("payment-supplier").selectOption({ label: "ALPHA · Alpha Supplies" });
  await page.getByTestId("payment-bank").selectOption({ index: 1 });
  await page.getByTestId("payment-reference").fill("TT-1");
  await page.getByTestId("add-item-PI-2026-00001").click();
  await page.getByTestId("pay-amount-0").fill("6000");
  await page.getByTestId("payment-on-account").fill("1000");
  await expectAccessible(page);
  await page.getByTestId("save-payment").click();
  await expect(page.getByTestId("payment-detail")).toBeVisible();
  await expect(page.getByTestId("payment-amount")).toContainText("7,000");
  await page.getByTestId("post-payment").click();
  await expect(page.getByTestId("payment-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expectAccessible(page);
  await closeDialog(page);
  await nav(page, "Bank & cash accounts");
  await expect(page.getByRole("grid")).toContainText("7,000");

  // The open items: the invoice with 4 000 left, the payment's 1 000 on account applied to it; aging shows 3 000.
  await nav(page, "Payables");
  await expect(page.getByRole("grid")).toContainText("PI-2026-00001");
  await page.getByRole("grid").getByText("PAY-2026-00001").dblclick();
  await expect(page.getByTestId("open-item-detail")).toBeVisible();
  await page.getByTestId("apply-item").click();
  await page.getByTestId("apply-target").selectOption({ index: 1 });
  await expectAccessible(page);
  await page.getByTestId("confirm-apply").click();
  await expect(page.getByTestId("item-remaining")).toContainText("0");
  await expect(page.getByTestId("settlement-row")).toHaveCount(2);
  await closeDialog(page);
  await page.getByTestId("tab-aging").click();
  await expect(page.getByTestId("aging-total")).toContainText("3,000");
  await expectAccessible(page);

  // Alpha's statement ends at the same 3 000: the invoice, the payment and the payment on account on the way.
  await page.getByTestId("tab-statement").click();
  await expect(page.getByTestId("statement-choose")).toBeVisible();
  await page.getByTestId("supplier-filter").selectOption({ label: "ALPHA · Alpha Supplies" });
  await page.getByTestId("statement-from").fill("2026-01-01");
  await expect(page.getByTestId("statement-closing")).toContainText("3,000");
  await expect(page.getByTestId("statement-line").first()).toContainText("PI-2026-00001");
  await expectAccessible(page);

  // The home page counts the day's work: what is owed to suppliers and the money in the bank among it.
  await page.goto("/");
  await expect(page.getByTestId("work-today")).toBeVisible();
  await expect(page.getByTestId("work-overdue")).toContainText("3,000");
  await expect(page.getByTestId("work-cash")).toBeVisible();
  await expect(page.getByTestId("work-approvals-value")).toHaveText("0");
  await expectAccessible(page);

  // A proposal for what is left, approved and drafted into a payment.
  await nav(page, "Payment proposals");
  await expect(page.getByText("No proposals yet")).toBeVisible();
  await page.getByTestId("new-proposal").click();
  await page.getByTestId("proposal-pay-through").fill("2026-12-31");
  await expectAccessible(page);
  await page.getByTestId("save-proposal").click();
  await expect(page.getByTestId("proposal-detail")).toBeVisible();
  await expect(page.getByTestId("proposal-total")).toContainText("3,000");
  await page.getByTestId("approve-proposal").click();
  await expect(page.getByTestId("proposal-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("pay-proposal").click();
  await page.getByTestId("pay-bank").selectOption({ index: 1 });
  await page.getByTestId("confirm-pay-proposal").click();
  await expect(page.getByTestId("proposal-detail").getByTestId("doc-status").first()).toContainText("Executed");
  await closeDialog(page);
  await nav(page, "Supplier payments");
  await expect(page.getByRole("grid")).toContainText("PAY-2026-00002");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الذمم الدائنة");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الذمم الدائنة");
  await expect(page.getByRole("grid")).toContainText("PI-");
  await expectAccessible(page);
  await nav(page, "مدفوعات الموردين");
  await expect(page.getByRole("grid")).toContainText("PAY-");
  await expectAccessible(page);
});
