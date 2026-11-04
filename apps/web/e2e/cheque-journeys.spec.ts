import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The cheques journey (roadmap 6.3, hard scenario 10): a receipt by cheque against an invoice, the cheque deposited
 * from its own page, bounced with the bank's charge and a fee re-charged to the customer, replaced by a new cheque that
 * is deposited and cleared from the slip; a cheque book for issued cheques; then the screens in Arabic, right-to-left.
 * Every screen passes axe with no serious or critical violation.
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

const today = new Date().toISOString().slice(0, 10);

test("English: a cheque received, deposited, bounced with charges, replaced and cleared; a cheque book; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `chq-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => {
    if (!window.localStorage.getItem("quicker.language")) {
      window.localStorage.setItem("quicker.language", "en");
    }
  });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Cheques " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("CHQ");
  await page.getByLabel(/Legal name \(English\)/).fill("Cheque Traders");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("CHQ");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The customer, the bank account and a posted invoice, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "CHQ")?.id ?? "";
  const partner = (await (await page.request.post(`${apiUrl}/api/v1/partners`, { headers, data: { code: "NOOR", legalName: { en: "Al-Noor Markets", ar: "أسواق النور" }, isCustomer: true } })).json()) as { id: string };
  await page.request.put(`${apiUrl}/api/v1/partners/${partner.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } });
  await page.request.post(`${apiUrl}/api/v1/banking/bank-accounts`, { headers, data: { companyId, code: "MAIN", name: { en: "Main account", ar: "الحساب الرئيسي" }, kind: "bank", currency: "IQD" } });
  const invoice = (await (await page.request.post(`${apiUrl}/api/v1/sales/invoices`, { headers, data: { companyId, partnerId: partner.id, customerReference: "PO-10", lines: [{ kind: "service", quantity: 1, unitPrice: 150000, description: "Consulting", accountRole: "Revenue" }] } })).json()) as { id: string; number: string };
  expect((await page.request.post(`${apiUrl}/api/v1/sales/invoices/${invoice.id}/post`, { headers, data: {} })).ok()).toBeTruthy();

  // A receipt by cheque: the cheque's own fields appear with the method; posted, the cheque is in hand.
  await nav(page, "Customer receipts");
  await page.getByTestId("new-receipt").click();
  await page.getByTestId("receipt-customer").selectOption(partner.id);
  await page.getByTestId("receipt-bank").selectOption({ index: 1 });
  await page.getByTestId("receipt-method").selectOption("cheque");
  await page.getByTestId("receipt-cheque-number").fill("778899");
  await page.getByTestId("receipt-drawer-bank").fill("Rafidain Bank");
  await page.getByTestId("receipt-cheque-due").fill(today);
  await page.getByTestId(`add-item-${invoice.number}`).click();
  await expectAccessible(page);
  await page.getByTestId("save-receipt").click();
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await expect(page.getByTestId("receipt-cheque")).toContainText("778899");
  await page.getByTestId("post-receipt").click();
  await expect(page.getByTestId("receipt-cheque")).toContainText("In hand");
  await closeDialog(page);

  // The cheque register: deposited from the cheque's page on one slip.
  await nav(page, "Cheques");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Cheques");
  await expect(page.getByRole("grid")).toContainText("778899");
  await expectAccessible(page);
  await page.getByRole("grid").getByText("778899", { exact: true }).dblclick();
  await expect(page.getByTestId("cheque-detail")).toBeVisible();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("In hand");
  await page.getByTestId("deposit-cheque").click();
  await expect(page.getByTestId("deposit-form")).toBeVisible();
  await expect(page.getByTestId("deposit-cheque-778899")).toBeChecked();
  await expectAccessible(page);
  await page.getByTestId("confirm-deposit").click();
  await expect(page.getByTestId("deposit-detail")).toBeVisible();
  await expect(page.getByTestId("deposit-detail")).toContainText("CHD-");
  await expect(page.getByTestId("deposit-cheque-row")).toContainText("Deposited");
  await closeDialog(page);

  // Bounced with the bank's charge and a fee re-charged to the customer: the history keeps every step.
  await page.getByTestId("tab-received").click();
  await page.getByRole("grid").getByText("778899", { exact: true }).dblclick();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Deposited");
  await page.getByTestId("bounce-cheque").click();
  await page.getByTestId("action-bank-charge").fill("1000");
  await page.getByTestId("action-customer-fee").fill("2000");
  await page.getByTestId("action-reason").fill("Insufficient funds");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Bounced");
  await expect(page.getByTestId("cheque-event-row")).toHaveCount(3);
  await expect(page.getByTestId("cheque-events")).toContainText("Insufficient funds");
  await expectAccessible(page);

  // Replaced by a new cheque for the same invoice, which is deposited and cleared from its slip.
  await page.getByTestId("replace-cheque").click();
  await page.getByTestId("action-cheque-number").fill("778950");
  await page.getByTestId("action-due-date").fill(today);
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("open-replacement")).toBeVisible();
  await page.getByTestId("open-replacement").click();
  await expect(page.getByTestId("cheque-detail")).toContainText("778950");
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("In hand");
  await page.getByTestId("deposit-cheque").click();
  await page.getByTestId("confirm-deposit").click();
  await expect(page.getByTestId("deposit-detail")).toBeVisible();
  await page.getByTestId("clear-deposit").click();
  await page.getByTestId("clear-bank-charge").fill("500");
  await page.getByTestId("confirm-clear-deposit").click();
  await expect(page.getByTestId("deposit-cheque-row")).toContainText("Cleared");
  await expect(page.getByTestId("deposit-detail").getByTestId("doc-status").first()).toContainText("Cleared");
  await closeDialog(page);
  await page.getByTestId("tab-received").click();
  await expect(page.getByRole("grid")).toContainText("Cleared");
  await expect(page.getByRole("grid")).toContainText("Bounced");

  // A cheque book for issued cheques.
  await nav(page, "Cheque books");
  await page.getByTestId("new-cheque-book").click();
  await page.getByTestId("book-first").fill("1001");
  await page.getByTestId("book-last").fill("1010");
  await expectAccessible(page);
  await page.getByTestId("save-cheque-book").click();
  await expect(page.getByRole("grid")).toContainText("1001");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الشيكات");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الشيكات");
  await expect(page.getByRole("grid")).toContainText("محصَّل");
  await expectAccessible(page);
});
