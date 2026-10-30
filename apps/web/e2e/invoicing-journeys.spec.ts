import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The invoicing journey of 5.6: a posted shipment is invoiced from its own screen with a delivery charge added, the
 * invoice posted and its receivable read from the invoice itself; two units are credited and the credit note settles
 * that much of the invoice as it posts; the receivables screen shows the open item with its settlement, the aging and
 * the customer's statement; a monthly retainer is set up as a recurring invoice and its first invoice issued. Then the
 * screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
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

test("English: a shipment invoiced with a delivery charge, a credit note applied, the receivables read, a recurring retainer issued; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `inv-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Invoicing " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("INV");
  await page.getByLabel(/Legal name \(English\)/).fill("Invoicing Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("INV");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // A stocked item, a warehouse with 20 on hand, and a customer to sell to.
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("TEA");
  await page.getByTestId("item-name-en").fill("Tea");
  await page.getByTestId("item-name-ar").fill("شاي");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await closeDialog(page);

  await nav(page, "Warehouses");
  await page.getByTestId("new-warehouse").click();
  await page.getByTestId("warehouse-code").fill("MAIN");
  await page.getByTestId("warehouse-name-en").fill("Main warehouse");
  await page.getByTestId("save-warehouse").click();
  await expect(page.getByTestId("warehouse-detail")).toBeVisible();
  await closeDialog(page);

  await nav(page, "Adjustments");
  await page.getByTestId("reason-codes").click();
  await page.getByTestId("reason-code").fill("INIT");
  await page.getByTestId("reason-name-en").fill("Opening stock");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(1);
  await closeDialog(page);
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("adjustment-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("20");
  await page.getByTestId("line-cost-0").fill("5000");
  await page.getByTestId("line-reason-0").selectOption({ label: "INIT · Opening stock" });
  await page.getByTestId("save-adjustment").click();
  await expect(page.getByTestId("adjustment-detail")).toBeVisible();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await closeDialog(page);

  await nav(page, "Customers");
  await page.getByTestId("new-customer").click();
  await page.getByTestId("partner-code").fill("CUST");
  await page.getByTestId("partner-legal-name-en").fill("Sales Customer LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة عميل المبيعات");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();
  await closeDialog(page);

  // An order for 10 TEA at 10,000, confirmed, shipped in full and posted.
  await nav(page, "Orders");
  await page.getByTestId("new-order").click();
  await page.getByTestId("order-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("order-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("save-order").click();
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Confirmed");
  await page.getByTestId("create-shipment").click();
  await page.waitForURL(/\/sales\/shipments/u, { waitUntil: "commit" });
  await expect(page.getByTestId("shipment-line-qty-TEA")).toHaveValue("10");
  await page.getByTestId("save-shipment").click();
  await expect(page.getByTestId("shipment-detail")).toBeVisible();
  await page.getByTestId("post-shipment").click();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("Posted");

  // The shipment's own "create invoice" button opens a new invoice with the shipped line already on it; a delivery
  // charge is added as a service line. Saved: 100,000 of tea plus 15,000 delivery, no tax (the company is not registered).
  await page.getByTestId("create-invoice").click();
  await page.waitForURL(/\/sales\/invoices/u, { waitUntil: "commit" });
  await expect(page.getByRole("dialog")).toContainText("New invoice");
  await expect(page.getByTestId("invoice-line")).toHaveCount(1);
  await expect(page.getByTestId("invoice-qty-0")).toHaveValue("10");
  await page.getByTestId("invoice-reference").fill("PO-4471");
  await page.getByTestId("add-service-line").click();
  await page.getByTestId("invoice-description-1").fill("Delivery");
  await page.getByTestId("invoice-price-1").fill("15000");
  await expectAccessible(page);
  await page.getByTestId("save-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail")).toBeVisible();
  await expect(page.getByTestId("sales-invoice-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expect(page.getByTestId("sales-invoice-total")).toContainText("115,000");
  await expect(page.getByTestId("sales-invoice-line")).toHaveCount(2);
  await expectAccessible(page);

  // Posted: the receivable is open for the whole amount, and no e-invoice is required without a scheme.
  await page.getByTestId("post-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("sales-invoice-open")).toContainText("115,000");
  await page.getByTestId("tab-receivables").click();
  await expect(page.getByTestId("open-item-row")).toHaveCount(1);
  await expect(page.getByTestId("open-item-remaining")).toContainText("115,000");
  await page.getByTestId("tab-einvoice").click();
  await expect(page.getByTestId("einvoice-panel")).toContainText("Not required");
  await expectAccessible(page);
  await closeDialog(page);

  // Two of the ten credited: the credit note names the invoice and settles 20,000 of it as it posts.
  await page.getByTestId("new-credit-note").click();
  await page.getByTestId("invoice-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("credit-line-1").click();
  await expect(page.getByTestId("invoice-line")).toHaveCount(1);
  await page.getByTestId("invoice-qty-0").fill("2");
  await page.getByTestId("save-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail")).toBeVisible();
  await expect(page.getByTestId("sales-invoice-total")).toContainText("20,000");
  await page.getByTestId("post-sales-invoice").click();
  await expect(page.getByTestId("sales-invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("sales-invoice-open")).toContainText("0");
  await expectAccessible(page);
  await closeDialog(page);
  await page.getByRole("grid").getByText(/INV-\d{4}-00001/u).dblclick();
  await expect(page.getByTestId("sales-invoice-open")).toContainText("95,000");
  await closeDialog(page);

  // The receivables screen: the invoice's open item with the credit applied to it, the aging and the statement.
  await nav(page, "Receivables");
  await expect(page.getByRole("grid")).toContainText("INV-");
  await page.getByRole("grid").getByText(/INV-\d{4}-00001/u).dblclick();
  await expect(page.getByTestId("open-item-detail")).toBeVisible();
  await expect(page.getByTestId("item-remaining")).toContainText("95,000");
  await expect(page.getByTestId("settlement-row")).toHaveCount(1);
  await expectAccessible(page);
  await closeDialog(page);
  await page.getByTestId("tab-aging").click();
  await expect(page.getByTestId("aging-total")).toContainText("95,000");
  await expectAccessible(page);
  await page.getByTestId("tab-statement").click();
  await expect(page.getByTestId("statement-choose")).toBeVisible();
  await page.getByTestId("customer-filter").selectOption({ label: "CUST · Sales Customer LLC" });
  await expect(page.getByTestId("statement-line")).toHaveCount(2);
  await expect(page.getByTestId("statement-closing")).toContainText("95,000");
  await expectAccessible(page);

  // A monthly retainer as a recurring invoice; its first invoice issued by hand as a draft.
  await nav(page, "Recurring invoices");
  await page.getByTestId("new-recurring-invoice").click();
  await page.getByTestId("recurring-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("recurring-code").fill("RET");
  await page.getByTestId("recurring-name-en").fill("Monthly retainer");
  await page.getByTestId("recurring-name-ar").fill("أتعاب شهرية");
  await page.getByTestId("recurring-description-0").fill("Retainer");
  await page.getByTestId("recurring-price-0").fill("50000");
  await expectAccessible(page);
  await page.getByTestId("save-recurring-invoice").click();
  await expect(page.getByTestId("recurring-invoice-detail")).toBeVisible();
  await expect(page.getByTestId("recurring-next-run")).not.toHaveText("—");
  await page.getByTestId("generate-recurring-invoice").click();
  await expect(page.getByTestId("recurring-generated")).toHaveText("1");
  await expect(page.getByTestId("issued-invoices")).toContainText("INV-");
  await expectAccessible(page);
  await closeDialog(page);

  // Arabic: the invoices, the receivables and the recurring invoices read right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("grid")).toContainText("RET");
  await expectAccessible(page);
  await nav(page, "فواتير المبيعات");
  await expect(page.getByRole("grid")).toContainText("INV-");
  await page.getByRole("grid").getByText(/INV-\d{4}-00001/u).dblclick();
  await expect(page.getByTestId("sales-invoice-detail").getByTestId("doc-status").first()).toContainText("مُرحَّل");
  await expectAccessible(page);
  await closeDialog(page);
  await nav(page, "الذمم المدينة");
  await expect(page.getByRole("grid")).toContainText("INV-");
  await expectAccessible(page);
});
