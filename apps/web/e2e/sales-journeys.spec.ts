import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The customer and CRM journey of 5.1: a customer group, a sales rep paid under a tiered commission plan (tried
 * before it is used), a customer with its account, credit limit and contact, a deal moved through the pipeline from
 * its 360 and from the board, lost with its reason, a second one won, an activity planned and done, credit put on
 * hold; then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

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

/**
 * The quotation-to-order journey of 5.4: a quotation priced for a customer is sent, accepted and converted to an
 * order with its lines frozen; confirming reserves stock (a short line backorders, then is retried once more arrives),
 * a line is part-cancelled; a large order is held for the customer's credit limit; a drop-ship line skips reservation
 * and its purchase order is raised and linked from the order screen itself. Then the screens in Arabic, right-to-left.
 */
test("English: a quotation converts to an order, reserves and backorders stock, holds for credit, and drop-ships a line to a purchase order; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `sod-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Sales Orders " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("SOD");
  await page.getByLabel(/Legal name \(English\)/).fill("Sales Orders Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("SOD");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // A stocked item (TEA), a drop-ship-only item (PLUG, never given stock), a warehouse, a reason code and 20 TEA on
  // hand; a supplier to raise the drop-ship purchase order against; a customer with a credit limit of 1,000,000.
  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("TEA");
  await page.getByTestId("item-name-en").fill("Tea");
  await page.getByTestId("item-name-ar").fill("شاي");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await closeDialog(page);
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("PLUG");
  await page.getByTestId("item-name-en").fill("Plug");
  await page.getByTestId("item-name-ar").fill("قابس");
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

  await nav(page, "Suppliers");
  await page.getByTestId("new-supplier").click();
  await page.getByTestId("partner-code").fill("ACME");
  await page.getByTestId("partner-legal-name-en").fill("Acme Fittings");
  await page.getByTestId("partner-legal-name-ar").fill("أكمي للتجهيزات");
  await page.getByTestId("partner-email").fill("sales@acme.example.test");
  await page.getByTestId("save-supplier").click();
  await expect(page.getByTestId("supplier-detail")).toBeVisible();
  await closeDialog(page);

  await nav(page, "Customers");
  await page.getByTestId("new-customer").click();
  await page.getByTestId("partner-code").fill("CUST");
  await page.getByTestId("partner-legal-name-en").fill("Sales Customer LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة عميل المبيعات");
  await page.getByTestId("credit-limit").fill("1000000");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();
  await closeDialog(page);

  // A retail cost centre, through the API: the quotation line is charged to it and the order inherits it (A-157).
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const dimensions = (await (await page.request.get(`${apiUrl}/api/v1/organization/dimensions`, { headers })).json()) as { id: string; code: string }[];
  const costCentre = dimensions.find((d) => d.code === "COST_CENTER");
  expect((await page.request.post(`${apiUrl}/api/v1/organization/dimensions/${costCentre?.id ?? ""}/values`, { headers, data: { code: "CC-RTL", name: { en: "Retail", ar: "التجزئة" } } })).ok()).toBeTruthy();

  // A quotation for 5 TEA is sent, accepted and converted to an order with the same frozen line.
  await nav(page, "Quotations");
  await page.getByTestId("new-quotation").click();
  await page.getByTestId("quotation-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("5");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("line-cost-centre-0").selectOption({ label: "CC-RTL · Retail" });
  await page.getByTestId("save-quotation").click();
  await expect(page.getByTestId("quotation-detail")).toBeVisible();
  await expect(page.getByTestId("quotation-detail").getByTestId("doc-line-cost-centre")).toHaveText("CC-RTL · Retail");
  await expect(page.getByTestId("quotation-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expectAccessible(page);
  await page.getByTestId("send-quotation").click();
  await expect(page.getByTestId("quotation-detail").getByTestId("doc-status").first()).toContainText("Sent");
  await page.getByTestId("accept-quotation").click();
  await expect(page.getByTestId("quotation-detail").getByTestId("doc-status").first()).toContainText("Accepted");
  await page.getByTestId("start-convert-quotation").click();
  await page.getByTestId("convert-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("confirm-convert-quotation").click();

  // The order carries the frozen line; confirming reserves all 5 of the 20 on hand.
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expect(page.getByTestId("order-total")).toContainText("50,000");
  await expect(page.getByTestId("order-detail").getByTestId("doc-line-cost-centre")).toHaveText("CC-RTL · Retail");
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Confirmed");
  await expect(page.getByTestId("doc-line").filter({ hasText: "TEA" })).toContainText("Open");
  await expectAccessible(page);

  // Cancelling 2 of the 5 releases them; the line stays open for the remaining 3.
  await page.getByTestId(/cancel-line-\d+/).click();
  await page.getByTestId(/cancel-line-qty-\d+/).fill("2");
  await page.getByTestId(/confirm-cancel-line-\d+/).click();
  await expect(page.getByTestId("doc-line").filter({ hasText: "TEA" })).toContainText("Open");
  await closeDialog(page);

  // A second order for 30 TEA: most of the 20 on hand is already reserved above, so it backorders the rest.
  await nav(page, "Orders");
  await page.getByTestId("new-order").click();
  await page.getByTestId("order-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("order-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("30");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("save-order").click();
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Confirmed");
  await expect(page.getByTestId("doc-line").filter({ hasText: "TEA" })).toContainText("Backordered");
  const backorderNumber = await page.locator('[data-testid="order-detail"] span[dir="ltr"]').first().innerText();
  await expectAccessible(page);
  await closeDialog(page);

  // 20 more TEA arrive; retrying backorders on that same order reserves the rest, so the line opens.
  await nav(page, "Adjustments");
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("adjustment-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("20");
  await page.getByTestId("line-cost-0").fill("5000");
  await page.getByTestId("line-reason-0").selectOption({ label: "INIT · Opening stock" });
  await page.getByTestId("save-adjustment").click();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await closeDialog(page);
  await nav(page, "Orders");
  await page.getByRole("grid").getByText(backorderNumber, { exact: true }).dblclick();
  await page.getByTestId("retry-backorders").click();
  await expect(page.getByTestId("doc-line").filter({ hasText: "TEA" })).toContainText("Open");
  await closeDialog(page);

  // A third order of 1,000,000 pushes exposure well past the 1,000,000 limit: held for credit, with the reason shown
  // and a link to the approvals inbox; cancelled with a reason instead of overridden.
  await page.getByTestId("new-order").click();
  await page.getByTestId("order-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("order-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("100");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("save-order").click();
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("On hold");
  await expect(page.getByTestId("credit-hold-banner")).toContainText("Credit limit exceeded");
  await expectAccessible(page);
  const heldNumber = await page.locator('[data-testid="order-detail"] span[dir="ltr"]').first().innerText();
  await page.getByTestId("open-approvals").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Approvals");
  await nav(page, "Orders");
  await page.getByRole("grid").getByText(heldNumber, { exact: true }).dblclick();
  await page.getByTestId("start-cancel-order").click();
  await page.getByTestId("cancel-reason").fill("Held for credit; will not proceed");
  await page.getByTestId("confirm-cancel-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Cancelled");
  await closeDialog(page);

  // A fourth order drop-ships a PLUG (never given stock): confirming opens the line at once with nothing reserved.
  // Its own "create purchase order" button pre-fills a new purchase order for ACME; saving it links back automatically.
  await page.getByTestId("new-order").click();
  await page.getByTestId("order-customer").selectOption({ label: "CUST · Sales Customer LLC" });
  await page.getByTestId("order-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("PLUG");
  await page.getByTestId("line-qty-0").fill("5");
  await page.getByTestId("line-price-0").fill("20000");
  await page.getByTestId("line-drop-ship-0").check();
  await page.getByTestId("save-order").click();
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Confirmed");
  await expect(page.getByTestId("doc-line").filter({ hasText: "PLUG" })).toContainText("Open");
  await expect(page.getByTestId("doc-line").filter({ hasText: "PLUG" })).toContainText("Drop-ship");
  await expectAccessible(page);

  await page.getByTestId("create-purchase-order").click();
  // A new purchase order pre-filled from the sales order's drop-ship line: the item, its quantity, its warehouse.
  await expect(page.getByRole("dialog")).toContainText("New purchase order");
  await expect(page.getByTestId("line-item-0")).toHaveValue("PLUG");
  await expect(page.getByTestId("line-qty-0")).toHaveValue("5");
  await page.getByTestId("order-supplier").selectOption({ label: "ACME · Acme Fittings" });
  await page.getByTestId("save-order").click();
  // Once the purchase order is saved, its first line links back to the sales order line and the browser returns
  // there; a client-side route change, so "commit" (not the default "load") is what actually fires.
  await page.waitForURL(/\/sales\/orders/u, { waitUntil: "commit" });
  await expect(page.getByTestId("order-detail")).toBeVisible();

  // Back on the sales order automatically once the purchase order is saved: the line shows it is now linked.
  await expect(page.getByTestId("doc-line").filter({ hasText: "PLUG" })).toContainText("Linked to a purchase order");
  await expectAccessible(page);
  const dropShipNumber = await page.locator('[data-testid="order-detail"] span[dir="ltr"]').first().innerText();
  await closeDialog(page);

  // Arabic: the nav labels, the order screen and the drop-ship link read right-to-left; the quotations grid too.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByRole("grid").getByText(dropShipNumber, { exact: true }).dblclick();
  await expect(page.getByTestId("doc-line").filter({ hasText: "PLUG" })).toContainText("مرتبط بأمر شراء");
  await expectAccessible(page);
  await closeDialog(page);
  await nav(page, "عروض الأسعار");
  await expect(page.getByRole("grid")).toContainText("CUST");
  await expectAccessible(page);
});

/**
 * The shipment journey of 5.5a: a confirmed order's "Create shipment" button opens the Shipments screen pre-filled
 * with its reserved line; a partial shipment posts, moving stock and booking cost of goods sold, and leaves the
 * order partially shipped; a second shipment for the remainder posts it fully shipped; reversing that second
 * shipment gives the stock and the reservation back. Then the screens in Arabic, right-to-left.
 */
test("English: a confirmed order ships in two parts through its own screen, posting cost of goods sold, and a reversal gives the stock back; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `shp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Shipments " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("SHP");
  await page.getByLabel(/Legal name \(English\)/).fill("Shipping Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("SHP");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

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
  await page.getByTestId("partner-legal-name-en").fill("Shipping Customer LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة عميل الشحن");
  await page.getByTestId("save-customer").click();
  await expect(page.getByTestId("customer-360")).toBeVisible();
  await closeDialog(page);

  // A confirmed order for 10 TEA reserves all 10 of the 20 on hand.
  await nav(page, "Orders");
  await page.getByTestId("new-order").click();
  await page.getByTestId("order-customer").selectOption({ label: "CUST · Shipping Customer LLC" });
  await page.getByTestId("order-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("TEA");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("save-order").click();
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Confirmed");
  await expectAccessible(page);

  // Its own "create shipment" button opens the Shipments screen with the order pre-filled.
  await page.getByTestId("create-shipment").click();
  await page.waitForURL(/\/sales\/shipments/u, { waitUntil: "commit" });
  await expect(page.getByRole("dialog")).toContainText("New shipment");
  await expect(page.getByTestId("shipment-order")).not.toHaveValue("");
  // Ship only 6 of the 10 reserved: a partial shipment.
  await page.getByTestId("shipment-line-qty-TEA").fill("6");
  await page.getByTestId("save-shipment").click();
  await expect(page.getByTestId("shipment-detail")).toBeVisible();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expectAccessible(page);
  await page.getByTestId("post-shipment").click();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("Posted");
  await expect(page.getByTestId("shipment-lines")).toContainText("TEA");
  const firstShipmentNumber = await page.locator('[data-testid="shipment-detail"] span[dir="ltr"]').first().innerText();
  await expectAccessible(page);
  await closeDialog(page);

  // The order is only partially shipped: the line still shows some quantity reserved for a later shipment.
  await nav(page, "Orders");
  await page.getByRole("grid").getByText("SO-", { exact: false }).first().dblclick();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Partially shipped");
  await page.getByTestId("create-shipment").click();
  await page.waitForURL(/\/sales\/shipments/u, { waitUntil: "commit" });
  // The remaining 4 are offered by default; ship them all.
  await expect(page.getByTestId("shipment-line-qty-TEA")).toHaveValue("4");
  await page.getByTestId("save-shipment").click();
  await page.getByTestId("post-shipment").click();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("Posted");
  const secondShipmentNumber = await page.locator('[data-testid="shipment-detail"] span[dir="ltr"]').first().innerText();
  await closeDialog(page);

  // The order is now fully shipped.
  await nav(page, "Orders");
  await page.getByRole("grid").getByText("SO-", { exact: false }).first().dblclick();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Shipped");
  await expectAccessible(page);
  await closeDialog(page);

  // Reversing the second shipment gives its 4 units back to stock and re-reserves them for the order.
  await nav(page, "Shipments");
  await page.getByRole("grid").getByText(secondShipmentNumber, { exact: true }).dblclick();
  await page.getByTestId("start-reverse-shipment").click();
  await page.getByTestId("reversal-reason").fill("Customer asked to delay the rest");
  await page.getByTestId("confirm-reverse-shipment").click();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("Reversed");
  await expectAccessible(page);
  await closeDialog(page);

  await nav(page, "Orders");
  await page.getByRole("grid").getByText("SO-", { exact: false }).first().dblclick();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Partially shipped");
  await closeDialog(page);

  // Arabic: the shipments list and a shipment's own reversed status read right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الشحنات");
  await expect(page.getByRole("grid")).toContainText(firstShipmentNumber);
  await expectAccessible(page);
  await page.getByRole("grid").getByText(secondShipmentNumber, { exact: true }).dblclick();
  await expect(page.getByTestId("shipment-detail").getByTestId("doc-status").first()).toContainText("معكوس");
  await expectAccessible(page);
});
