import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The receivables journey of 5.7: an invoice dated months ago is collected in part from the receipts screen with the
 * bank's charge, the customer's promise to pay recorded on what is left, a small balance written off, the reminder
 * ladder set up and a dunning run issued that charges its fee. Then the screens in Arabic, right-to-left. Every screen
 * passes axe with no serious or critical violation.
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

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: an old invoice collected in part with a bank charge, a promise to pay, a write-off and a dunning run with its fee; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `rcv-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Receivables " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("RCV");
  await page.getByLabel(/Legal name \(English\)/).fill("Receivables Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("RCV");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // A customer, and the bank account receipts come into.
  await nav(page, "Customers");
  await page.getByTestId("new-customer").click();
  await page.getByTestId("partner-code").fill("CUST");
  await page.getByTestId("partner-legal-name-en").fill("Collected Customer LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة العميل المحصّل");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();
  await closeDialog(page);
  await nav(page, "Bank & cash accounts");
  await page.getByTestId("new-bank-account").click();
  await page.getByTestId("bank-code").fill("MAIN");
  await page.getByTestId("bank-name-en").fill("Main account");
  await page.getByTestId("bank-name-ar").fill("الحساب الرئيسي");
  await page.getByTestId("save-bank-account").click();
  await expect(page.getByTestId("bank-account-detail")).toBeVisible();
  await closeDialog(page);

  // A service invoice of 100,000 dated five months ago, posted: long past due.
  await nav(page, "Sales invoices");
  await page.getByTestId("new-sales-invoice").click();
  await page.getByTestId("invoice-customer").selectOption({ label: "CUST · Collected Customer LLC" });
  await page.getByTestId("invoice-date").fill("2026-05-01");
  await page.getByTestId("add-service-line").click();
  await page.getByTestId("invoice-description-0").fill("Consulting, May");
  await page.getByTestId("invoice-price-0").fill("100000");
  await page.getByTestId("save-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail")).toBeVisible();
  await page.getByTestId("post-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await closeDialog(page);

  // 60,000 collected by transfer, the bank keeping 500: the receipt posts, the invoice stays open for 40,000.
  await nav(page, "Customer receipts");
  await page.getByTestId("new-receipt").click();
  await page.getByTestId("receipt-customer").selectOption({ label: "CUST · Collected Customer LLC" });
  await page.getByTestId("receipt-bank").selectOption({ index: 1 });
  await page.getByTestId("receipt-reference").fill("TT-0001");
  await page.getByTestId("receipt-charges").fill("500");
  await page.getByTestId(/add-item-INV-/u).click();
  await page.getByTestId("collect-amount-0").fill("60000");
  await expectAccessible(page);
  await page.getByTestId("save-receipt").click();
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await expect(page.getByTestId("receipt-amount")).toContainText("60,000");
  await page.getByTestId("post-receipt").click();
  await expect(page.getByTestId("receipt-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("receipt-bank-amount-value")).toContainText("59,500");
  await expectAccessible(page);
  await closeDialog(page);

  // The home page shows the money that came in and the customer still past due.
  await page.goto("/");
  await expect(page.getByTestId("work-cash-value")).toContainText("59,500");
  await expect(page.getByTestId("work-cash")).toContainText("59,500.00 in this month");
  await expect(page.getByTestId("work-receivables-value")).toContainText("40,000");
  await expect(page.getByTestId("work-receivables")).toContainText("1 customer");

  // On the receivables screen: the invoice shows 40,000 open with the receipt's settlement; the customer promises to pay, then 40 of it is written off as a small balance.
  await nav(page, "Receivables");
  await page.getByRole("grid").getByText(/INV-\d{4}-00001/u).dblclick();
  await expect(page.getByTestId("open-item-detail")).toBeVisible();
  await expect(page.getByTestId("item-remaining")).toContainText("40,000");
  await expect(page.getByTestId("settlement-row")).toHaveCount(1);
  await page.getByTestId("promise-item").click();
  await page.getByTestId("promise-date").fill("2027-01-15");
  await page.getByTestId("promise-note").fill("Will pay with the January run");
  await page.getByTestId("confirm-promise").click();
  await expect(page.getByTestId("item-promise")).toBeVisible();
  // A promised item is left alone by dunning: clear the promise so the run below has something to issue.
  await page.getByTestId("promise-item").click();
  await page.getByTestId("promise-date").fill("");
  await page.getByTestId("confirm-promise").click();
  await expect(page.getByTestId("item-promise")).toHaveCount(0);
  await page.getByTestId("write-off-item").click();
  await page.getByTestId("write-off-amount").fill("40");
  await page.getByTestId("write-off-kind").selectOption("small_balance");
  await page.getByTestId("write-off-reason").fill("Bank rounding, not worth chasing");
  await expectAccessible(page);
  await page.getByTestId("confirm-write-off").click();
  await expect(page.getByTestId("item-remaining")).toContainText("39,960");
  await expect(page.getByTestId("settlement-row")).toHaveCount(2);
  await closeDialog(page);

  // The reminder ladder: a reminder after a week, a second notice with a fee after a month; the run today charges the fee.
  await page.getByTestId("tab-dunning").click();
  await expect(page.getByTestId("dunning-panel")).toBeVisible();
  await page.getByTestId("new-dunning-level").click();
  await page.getByTestId("level-days").fill("7");
  await page.getByTestId("level-name-en").fill("Reminder");
  await page.getByTestId("level-name-ar").fill("تذكير");
  await page.getByTestId("save-dunning-level").click();
  await expect(page.getByTestId("dunning-level-row")).toHaveCount(1);
  await page.getByTestId("new-dunning-level").click();
  await page.getByTestId("level-days").fill("30");
  await page.getByTestId("level-name-en").fill("Second notice");
  await page.getByTestId("level-name-ar").fill("إشعار ثانٍ");
  await page.getByTestId("level-fee").fill("1000");
  await page.getByTestId("save-dunning-level").click();
  await expect(page.getByTestId("dunning-level-row")).toHaveCount(2);
  await expect(page.getByTestId("dunning-preview-row")).toHaveCount(1);
  await expect(page.getByTestId("dunning-preview-row")).toContainText("2 · Second notice");
  await expectAccessible(page);
  await page.getByTestId("issue-dunning").click();
  await expect(page.getByTestId("dunning-run-detail")).toBeVisible();
  await expect(page.getByTestId("dunning-letter-row")).toHaveCount(1);
  await page.getByTestId("open-dunning-letter").click();
  await expect(page.getByTestId("dunning-letter")).toContainText("1,000");
  await expect(page.getByTestId("dunning-letter-item")).toHaveCount(1);
  await page.getByTestId("mark-letter-sent").click();
  await expect(page.getByTestId("dunning-letter")).not.toContainText("Not yet");
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByTestId("dunning-nothing-due")).toBeVisible();
  await expect(page.getByTestId("dunning-run-row")).toHaveCount(1);

  // The fee is owed too: the items tab lists the letter's fee beside the invoice, and the aging carries both.
  await page.getByTestId("tab-items").click();
  await expect(page.getByRole("grid")).toContainText("DUN-");
  await page.getByTestId("tab-aging").click();
  await expect(page.getByTestId("aging-total")).toContainText("40,960");
  await expectAccessible(page);

  // Arabic: receipts, receivables and the dunning ladder read right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByTestId("aging-total")).toContainText("40,960");
  await expectAccessible(page);
  await page.getByTestId("tab-dunning").click();
  await expect(page.getByTestId("dunning-level-row")).toHaveCount(2);
  await expectAccessible(page);
  await nav(page, "مقبوضات العملاء");
  await expect(page.getByRole("grid")).toContainText("RCT-");
  await expectAccessible(page);
});
