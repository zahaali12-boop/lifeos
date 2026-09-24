import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The requisition-to-order journey of 4.2: a requisition with a suggested supplier is submitted (approved at once,
 * no workflow definition yet) and turned into a purchase order; the order is submitted, sent to the supplier and
 * changed through a revision; an RFQ is sent to two suppliers, their quotes recorded and compared; a blanket
 * agreement is activated. Then the screens in Arabic, right-to-left. Every screen passes axe with no serious or
 * critical violation.
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

async function supplier(page: Page, code: string, name: string, email: string): Promise<void> {
  await page.getByTestId("new-supplier").click();
  await page.getByTestId("partner-code").fill(code);
  await page.getByTestId("partner-legal-name-en").fill(name);
  await page.getByTestId("partner-legal-name-ar").fill(name);
  await page.getByTestId("partner-email").fill(email);
  await page.getByTestId("save-supplier").click();
  await expect(page.getByTestId("supplier-detail")).toBeVisible();
  await closeDialog(page);
}

test("English: requisition to purchase order, a change order, a send, a receipt, an invoice and a landed cost, an RFQ compared and a blanket agreement", async ({ page }) => {
  // Ten documents and twenty accessibility scans: about 45 s alone, over a minute beside the other journeys.
  test.setTimeout(120_000);
  const slug = `pur-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Purchasing " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("PUR");
  await page.getByLabel(/Legal name \(English\)/).fill("Purchasing Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("PUR");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // An item to buy, a warehouse to receive into and two suppliers registered for the company.
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
  await nav(page, "Suppliers");
  await supplier(page, "ALPHA", "Alpha Supplies", "alpha@example.test");
  await supplier(page, "BETA", "Beta Trading", "beta@example.test");

  // TEA is bought from ALPHA: the supplier's code for it, a lead time and a last price, marked preferred.
  await nav(page, "Items");
  await page.getByRole("grid").getByText("TEA", { exact: true }).dblclick();
  await page.getByTestId("item-tab-suppliers").click();
  await page.getByTestId("add-supplier").click();
  await page.getByTestId("supplier-partner").selectOption({ label: "ALPHA · Alpha Supplies" });
  await page.getByTestId("supplier-item-code").fill("ALP-TEA-01");
  await page.getByTestId("supplier-lead-time").fill("7");
  await page.getByTestId("supplier-price").fill("950");
  await page.getByTestId("save-supplier-link").click();
  await expect(page.getByTestId("supplier-row")).toContainText("ALP-TEA-01");
  await expect(page.getByTestId("supplier-row")).toContainText("Preferred");
  await expectAccessible(page);
  await closeDialog(page);

  // A requisition: submitted, approved at once, turned into an order for the suggested supplier.
  await nav(page, "Requisitions");
  await expect(page.getByText("No requisitions yet")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("new-requisition").click();
  await page.getByTestId("requisition-justification").fill("Canteen restock");
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-price-0").fill("1500");
  await page.getByTestId("line-supplier-0").selectOption({ label: "ALPHA" });
  await expectAccessible(page);
  await page.getByTestId("save-requisition").click();
  await expect(page.getByTestId("requisition-detail")).toBeVisible();
  await expect(page.getByTestId("requisition-detail")).toContainText("REQ-");
  await page.getByTestId("submit-requisition").click();
  await expect(page.getByTestId("requisition-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("create-orders").click();
  await expect(page.getByTestId("created-orders")).toContainText("PO-");
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Ordered");

  // The order: submitted (approved at once), sent by email, then changed through a revision that goes back to approval.
  await nav(page, "Purchase orders");
  await expect(page.getByRole("grid")).toContainText("PO-");
  await page.getByRole("grid").getByRole("row").filter({ hasText: "PO-" }).first().dblclick();
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await expect(page.getByTestId("order-total")).toContainText("15,000");
  await page.getByTestId("submit-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("tab-commitments").click();
  await expect(page.getByTestId("commitment-row")).toHaveCount(1);
  await page.getByTestId("send-order").click();
  await page.getByTestId("send-message").fill("Please confirm.");
  await page.getByTestId("confirm-send").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Sent");
  await expect(page.getByTestId("order-detail")).toContainText("alpha@example.test");
  await expectAccessible(page);
  await page.getByTestId("change-order").click();
  await page.getByTestId("change-reason").fill("Two more cartons");
  await page.getByTestId("line-qty-0").fill("12");
  await page.getByTestId("save-order").click();
  await expect(page.getByTestId("order-revision")).toContainText("revision 2");
  await expect(page.getByTestId("order-total")).toContainText("18,000");
  await page.getByTestId("tab-revisions").click();
  await expect(page.getByTestId("revision-row")).toHaveCount(1);
  await expect(page.getByTestId("revision-row")).toContainText("Two more cartons");

  // A goods receipt started from the order with one click: the order and its open line are filled in; 8 of 12
  // arrive, posted into stock at the expected cost.
  await expect(page.getByTestId("order-detail").getByTestId("flow-requisitions")).toContainText("1");
  await page.getByTestId("order-receive").click();
  await expect(page).toHaveURL(/\/purchasing\/receipts$/);
  await expect(page.getByTestId("receipt-line")).toHaveCount(1);
  await expect(page.getByTestId("receipt-order")).not.toHaveValue("");
  await expect(page.getByTestId("receipt-warehouse")).not.toHaveValue("");
  await page.getByTestId("receive-qty-0").fill("8");
  await page.getByTestId("receipt-delivery-note").fill("DN-1001");
  await expectAccessible(page);
  await page.getByTestId("save-receipt").click();
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await expect(page.getByTestId("receipt-detail")).toContainText("GRN-");
  await expect(page.getByTestId("receipt-value")).toContainText("12,000");
  await page.getByTestId("post-receipt").click();
  await expect(page.getByTestId("receipt-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("flow-orders")).toContainText("1");
  await expectAccessible(page);

  // The supplier's invoice for the 8 received, started from the receipt: the supplier and the receipt's line are
  // filled in; at the order price it is matched, approved at once and posted with one payable.
  await page.getByTestId("receipt-create-invoice").click();
  await expect(page).toHaveURL(/\/purchasing\/invoices$/);
  await expect(page.getByTestId("invoice-line")).toHaveCount(1);
  await expect(page.getByTestId("invoice-supplier")).not.toHaveValue("");
  await page.getByTestId("invoice-reference").fill("A-1001");
  await expectAccessible(page);
  await page.getByTestId("save-invoice").click();
  await expect(page.getByTestId("invoice-detail")).toBeVisible();
  await expect(page.getByTestId("invoice-total")).toContainText("12,000");
  await page.getByTestId("submit-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("tab-match").click();
  await expect(page.getByTestId("match-status")).toContainText("Matched");
  await page.getByTestId("post-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await page.getByTestId("tab-payables").click();
  await expect(page.getByTestId("open-item-row")).toHaveCount(1);
  await expectAccessible(page);
  await closeDialog(page);

  // Freight of 240 lands on the 8 received tea (nothing sold yet): all of it to stock.
  await nav(page, "Landed costs");
  await expect(page.getByText("No landed costs yet")).toBeVisible();
  await page.getByTestId("new-landed-cost").click();
  await page.getByTestId("charge-type-0").selectOption({ label: "FREIGHT · Freight" });
  await page.getByTestId("charge-amount-0").fill("240");
  await page.getByTestId("landed-cost-reference").fill("BL-77");
  await page.getByTestId("allocatable-lines").getByRole("checkbox").first().check();
  await expectAccessible(page);
  await page.getByTestId("save-landed-cost").click();
  await expect(page.getByTestId("landed-cost-detail")).toBeVisible();
  await expect(page.getByTestId("landed-cost-total")).toContainText("240");
  await page.getByTestId("post-landed-cost").click();
  await expect(page.getByTestId("landed-cost-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("landed-cost-on-hand")).toContainText("240");
  await expect(page.getByTestId("allocation-row")).toHaveCount(1);
  await expectAccessible(page);
  await closeDialog(page);

  // Two of the eight tea go back at their landed cost (1 530 each); the supplier's debit note credits them at the order price and is applied to the invoice.
  await nav(page, "Supplier returns");
  await expect(page.getByText("No returns yet")).toBeVisible();
  await page.getByTestId("new-return").click();
  await page.getByTestId("return-receipt").selectOption({ index: 1 });
  await page.getByTestId("return-qty-0").fill("2");
  await page.getByTestId("return-rma").fill("RMA-1");
  await expectAccessible(page);
  await page.getByTestId("save-return").click();
  await expect(page.getByTestId("return-detail")).toBeVisible();
  await page.getByTestId("post-return").click();
  await expect(page.getByTestId("return-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("return-value")).toContainText("3,060");
  await expectAccessible(page);

  // The debit note started from the return: a debit note for the supplier with the returned line.
  await page.getByTestId("return-create-debit-note").click();
  await expect(page.getByTestId("invoice-kind")).toHaveValue("debit_note");
  await expect(page.getByTestId("invoice-line")).toHaveCount(1);
  await page.getByTestId("invoice-reference").fill("CN-1");
  await page.getByTestId("save-invoice").click();
  await expect(page.getByTestId("invoice-detail")).toBeVisible();
  await expect(page.getByTestId("invoice-total")).toContainText("3,000");
  await page.getByTestId("submit-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Approved");
  await page.getByTestId("post-invoice").click();
  await expect(page.getByTestId("invoice-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await page.getByTestId("tab-payables").click();
  await page.getByTestId("start-apply-credit").click();
  await page.getByTestId("credit-target").selectOption({ index: 1 });
  await expectAccessible(page);
  await page.getByTestId("confirm-apply-credit").click();
  await expect(page.getByTestId("settlement-row")).toHaveCount(1);
  await expect(page.getByTestId("open-item-remaining")).toContainText("0");
  await closeDialog(page);

  // The four tea still to come are an open order line, valued at the order price.
  await nav(page, "Open order lines");
  await expect(page.getByRole("grid")).toContainText("TEA");
  await expect(page.getByTestId("open-lines-summary")).toContainText("1 open line");
  await expect(page.getByTestId("open-lines-summary")).toContainText("6,000");
  await expectAccessible(page);

  // The order's smart buttons: everything that followed it, one click away. The one receipt opens directly; nothing
  // is left to invoice on the order once its receipt is billed.
  await nav(page, "Purchase orders");
  await expect(page.getByRole("grid")).toContainText("Partially received");
  await page.getByRole("grid").getByRole("row").filter({ hasText: "PO-" }).first().dblclick();
  const flow = page.getByTestId("order-detail").getByTestId("document-flow");
  await expect(flow.getByTestId("flow-requisitions")).toContainText("1");
  await expect(flow.getByTestId("flow-receipts")).toContainText("1");
  await expect(flow.getByTestId("flow-returns")).toContainText("1");
  await expect(flow.getByTestId("flow-landed-costs")).toContainText("1");
  await expect(flow.getByTestId("flow-invoices")).toContainText("1");
  await expect(flow.getByTestId("flow-debit-notes")).toContainText("1");
  await expectAccessible(page);
  await page.getByTestId("order-create-invoice").click();
  await expect(page.getByText("Nothing is left to invoice on this document.")).toBeVisible();
  await closeDialog(page);
  await nav(page, "Purchase orders");
  await page.getByRole("grid").getByRole("row").filter({ hasText: "PO-" }).first().dblclick();
  await page.getByTestId("order-detail").getByTestId("flow-receipts").click();
  await expect(page.getByTestId("receipt-detail")).toContainText("GRN-");
  await expect(page.getByTestId("receipt-detail").getByTestId("flow-invoices")).toContainText("1");
  await closeDialog(page);

  // Supplier intelligence: Alpha is scored from its receipt, invoice and return; its lead time and prices are listed.
  await nav(page, "Supplier intelligence");
  await expect(page.getByTestId("scorecard-row").first()).toContainText("ALPHA");
  await expect(page.getByTestId("grade").first()).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("tab-lead-times").click();
  await expect(page.getByTestId("lead-time-row").first()).toContainText("ALPHA");
  await page.getByTestId("tab-prices").click();
  await expect(page.getByTestId("price-summary-row").first()).toContainText("TEA");
  await expectAccessible(page);

  // An RFQ to both suppliers, two quotes, compared: the cheaper one ranks first.
  await nav(page, "Requests for quotation");
  await page.getByTestId("new-rfq").click();
  await page.getByTestId("rfq-title").fill("Tea for Q4");
  await page.getByTestId("invite-ALPHA").check();
  await page.getByTestId("invite-BETA").check();
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("100");
  await page.getByTestId("save-rfq").click();
  await expect(page.getByTestId("rfq-detail")).toBeVisible();
  await page.getByTestId("send-rfq").click();
  await expect(page.getByTestId("rfq-detail").getByTestId("doc-status").first()).toContainText("Sent");
  await page.getByTestId("tab-suppliers").click();
  await expect(page.getByTestId("rfq-supplier-row")).toHaveCount(2);
  await page.getByTestId("record-quote-ALPHA").click();
  await page.getByTestId("quote-lead-time").fill("5");
  await page.getByTestId("quote-price-0").fill("1400");
  await page.getByTestId("save-quote").click();
  await expect(page.getByTestId("quote-form")).toHaveCount(0);
  await page.getByTestId("record-quote-BETA").click();
  await page.getByTestId("quote-lead-time").fill("3");
  await page.getByTestId("quote-price-0").fill("1350");
  await page.getByTestId("save-quote").click();
  await expect(page.getByTestId("quote-form")).toHaveCount(0);
  await page.getByTestId("compare-quotes").click();
  await expect(page.getByTestId("comparison-note")).toBeVisible();
  const rows = page.getByTestId("quote-row");
  await expect(rows.filter({ hasText: "BETA" }).getByTestId("quote-rank")).toHaveText("1");
  await expect(rows.filter({ hasText: "ALPHA" }).getByTestId("quote-rank")).toHaveText("2");
  await expectAccessible(page);
  await page.getByTestId("award-BETA").click();
  await expect(page.getByTestId("awarded-order")).toContainText("PO-");
  await closeDialog(page);

  // A blanket agreement with ALPHA, activated.
  await nav(page, "Blanket agreements");
  await page.getByTestId("new-agreement").click();
  await page.getByTestId("agreement-supplier").selectOption({ label: "ALPHA · Alpha Supplies" });
  await page.getByTestId("agreement-to").fill("2027-12-31");
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("1000");
  await page.getByTestId("line-price-0").fill("1400");
  await page.getByTestId("save-agreement").click();
  await expect(page.getByTestId("agreement-detail")).toBeVisible();
  await page.getByTestId("activate-agreement").click();
  await expect(page.getByTestId("agreement-detail").getByTestId("doc-status").first()).toContainText("Active");
  await expect(page.getByTestId("remaining-qty")).toContainText("1,000");
  await expectAccessible(page);
  await closeDialog(page);

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "أوامر الشراء");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("أوامر الشراء");
  await expect(page.getByRole("grid")).toContainText("PO-");
  await expectAccessible(page);
  await nav(page, "طلبات عروض الأسعار");
  await expect(page.getByRole("grid")).toContainText("RFQ-");
  await expectAccessible(page);
});
