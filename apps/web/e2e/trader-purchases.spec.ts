import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Supplier invoices and goods already here (trader edition, ADR-0035 §22, T13): a workspace made as the trader
 * program; goods bought from a supplier are entered without an offer or a shipment, with the freight owed to the
 * forwarder, and the supplier's invoice appears unpaid; it is paid in part and then in full from the cash box, each
 * payment listed under the invoice; the pricing board lists the goods at their landed cost; stock that was in the store
 * before the program is entered at cost and priced too; a sale is paid in part and its payments are read from its own
 * invoice; then the same screens in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("supplier invoices: goods entered without a shipment, paid in part and in full, priced; a sale's payments on its invoice; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `tpu-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;

  // The workspace, made as the trader program, and its company set up through the API (the screens for these are covered elsewhere).
  const signup = await page.request.post(`${apiUrl}/api/v1/auth/signup`, { data: { tenantName: "Purchases " + slug, slug, ownerName: "Owner", ownerEmail: email, password, edition: "trader" } });
  expect(signup.ok(), await signup.text()).toBeTruthy();
  const tokens = (await signup.json()) as { accessToken: string };
  const headers = { Authorization: `Bearer ${tokens.accessToken}`, "Content-Type": "application/json" };
  const call = async (method: "post" | "put", path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request[method](`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  const company = await call("post", "/api/v1/organization/companies", { code: "TRD", legalName: { en: "Parts Trading Co.", ar: "شركة تجارة القطع" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  await call("post", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  await call("post", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main store", ar: "المخزن الرئيسي" } });
  for (const [code, en, ar] of [["PADS", "Brake pads", "فحمات فرامل"], ["FILTER", "Oil filter", "فلتر زيت"], ["BELT", "Timing belt", "سير التوقيت"]]) {
    await call("post", "/api/v1/items", { code, name: { en, ar }, baseUom: "PCS" });
  }
  for (const [code, en, ar] of [["LOCAL", "Baghdad Parts", "قطع بغداد"], ["FWD", "Gulf Freight", "الخليج للشحن"]]) {
    const supplier = await call("post", "/api/v1/partners", { code, legalName: { en, ar }, isSupplier: true });
    await call("put", `/api/v1/partners/${supplier.id}/supplier-accounts/${company.id}`, { currency: "IQD" });
  }
  const ali = await call("post", "/api/v1/partners", { code: "ALI", legalName: { en: "Ali Auto Repair", ar: "ورشة علي" }, isCustomer: true });
  await call("put", `/api/v1/partners/${ali.id}/customer-accounts/${company.id}`, { currency: "IQD" });
  await call("post", "/api/v1/banking/bank-accounts", { companyId: company.id, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency: "IQD", isActive: true });
  await call("put", "/api/v1/organization/settings/trader.rates.prompt_hour", { value: -1, valueType: "number" });

  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/login");
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByTestId("flat-nav")).toBeVisible({ timeout: 20_000 });

  // Supplier invoices: nothing yet; goods bought from Baghdad Parts are entered without an offer or a shipment, the freight owed to the forwarder.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Supplier invoices" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Supplier invoices");
  await expect(page.getByText("No supplier invoices yet.")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("new-goods").click();
  await expect(page.getByTestId("goods-dialog")).toBeVisible();
  await page.getByTestId("goods-supplier").selectOption({ label: "LOCAL · Baghdad Parts" });
  await page.getByTestId("goods-invoice-number").fill("BP-1001");
  await page.getByTestId("line-item-0").fill("PADS");
  await page.getByTestId("line-qty-0").fill("20");
  await page.getByTestId("line-price-0").fill("10000");
  await page.getByTestId("add-line").click();
  await page.getByTestId("line-item-1").fill("FILTER");
  await page.getByTestId("line-qty-1").fill("30");
  await page.getByTestId("line-price-1").fill("4000");
  await expect(page.getByTestId("goods-total")).toContainText("320,000.00");
  await page.getByTestId("goods-add-cost").click();
  await page.getByTestId("goods-cost-amount-0").fill("50000");
  await page.getByTestId("goods-cost-partner-0").selectOption({ label: "FWD · Gulf Freight" });
  await expectAccessible(page);
  await page.getByTestId("save-goods").click();
  await expect(page.getByTestId("purchases-notice")).toContainText("owed to Baghdad Parts", { timeout: 20_000 });
  await expect(page.getByTestId("purchases-notice")).toContainText("320,000.00");
  await expect(page.getByTestId("next-step")).toContainText("Set the selling prices");
  const invoiceRow = page.getByRole("row").filter({ hasText: "BP-1001" });
  await expect(invoiceRow.getByTestId("purchase-state")).toContainText("Unpaid");
  await expect(invoiceRow.getByTestId("purchase-remaining")).toContainText("320,000.00");
  const invoiceNumber = (await invoiceRow.getByRole("gridcell").first().innerText()).split("·")[0]?.trim() ?? "";
  expect(invoiceNumber).toMatch(/^PI-/);

  // Paid in part from the cash box: the invoice is partly paid and the payment is under it.
  await page.getByTestId("next-pay").click();
  await expect(page.getByTestId("supplier-payment-dialog")).toBeVisible();
  await page.getByTestId("supplier-payment-amount").fill("120000");
  await expectAccessible(page);
  await page.getByTestId("save-supplier-payment").click();
  await expect(page.getByTestId("purchases-notice")).toContainText("120,000.00", { timeout: 20_000 });
  await expect(invoiceRow.getByTestId("purchase-state")).toContainText("Partly paid");
  await expect(invoiceRow.getByTestId("purchase-paid")).toContainText("120,000.00");
  await expect(invoiceRow.getByTestId("purchase-remaining")).toContainText("200,000.00");

  // Every column can be widened from its edge; the width stays when the screen is opened again.
  const supplierHeading = page.getByRole("columnheader", { name: /Supplier/ });
  const before = (await supplierHeading.boundingBox())?.width ?? 0;
  await page.getByTestId("grid-resize-supplier").focus();
  for (let i = 0; i < 5; i += 1) {
    await page.keyboard.press("ArrowRight");
  }
  await expect.poll(async () => (await supplierHeading.boundingBox())?.width ?? 0).toBeGreaterThan(before + 40);
  const widened = (await supplierHeading.boundingBox())?.width ?? 0;
  await page.reload();
  await expect(page.getByRole("columnheader", { name: /Supplier/ })).toBeVisible({ timeout: 20_000 });
  await expect.poll(async () => Math.round((await page.getByRole("columnheader", { name: /Supplier/ }).boundingBox())?.width ?? 0)).toBe(Math.round(widened));
  await invoiceRow.getByRole("gridcell").first().dblclick();
  await expect(page.getByTestId("purchase-dialog")).toBeVisible();
  await expect(page.getByTestId("payments-list").getByTestId("payment-row")).toHaveCount(1);
  await expect(page.getByTestId("payments-list")).toContainText("120,000.00");
  await expect(page.getByTestId("dialog-remaining")).toContainText("200,000.00");
  await expectAccessible(page);

  // The rest, from the invoice itself: paid in full, two payments listed.
  await page.getByTestId("dialog-pay").click();
  await expect(page.getByTestId("supplier-payment-amount")).toHaveValue("200,000");
  await page.getByTestId("save-supplier-payment").click();
  await expect(page.getByTestId("purchases-notice")).toContainText("200,000.00", { timeout: 20_000 });
  // The invoice is still open behind the payment: it shows both payments and nothing left, then the list says paid.
  await expect(page.getByTestId("payments-list").getByTestId("payment-row")).toHaveCount(2);
  await expect(page.getByTestId("dialog-remaining")).toContainText("0.00");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("purchase-dialog")).toHaveCount(0);
  await expect(invoiceRow.getByTestId("purchase-state")).toContainText("Paid");

  // Stock that was in the store before the program: ten belts at 5,000, booked as opening stock.
  await page.getByTestId("next-more-goods").click();
  await page.getByTestId("goods-mode-prior").check();
  await expect(page.getByTestId("goods-supplier")).toHaveCount(0);
  await page.getByTestId("line-item-0").fill("BELT");
  await page.getByTestId("line-qty-0").fill("10");
  await page.getByTestId("line-price-0").fill("5000");
  await page.getByTestId("save-goods").click();
  await expect(page.getByTestId("purchases-notice")).toContainText("Recorded: 1 items", { timeout: 20_000 });

  // Prices: the pads at their landed cost (freight landed by value: 10,000 + 1,562.50), the belts at what they cost.
  await page.getByTestId("next-price").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Selling prices");
  const padsRow = page.getByTestId("price-row").filter({ hasText: "PADS" });
  await expect(padsRow).toContainText("11,562.50");
  const beltRow = page.getByTestId("price-row").filter({ hasText: "BELT" });
  await expect(beltRow).toContainText("5,000.00");
  await expect(beltRow.getByTestId("alert-no_price")).toBeVisible();
  await expectAccessible(page);

  // Sales: two pads sold to Ali at 15,000; 10,000 paid now; the invoice shows that payment and what is left; the rest paid from the invoice.
  await page.getByTestId("flat-nav").getByRole("link", { name: "Sales" }).click();
  await page.getByTestId("new-sale").click();
  await page.getByTestId("sale-customer").selectOption({ label: "ALI · Ali Auto Repair" });
  await page.getByTestId("line-item-0").fill("PADS");
  await page.getByTestId("line-qty-0").fill("2");
  await page.getByTestId("line-price-0").fill("15000");
  await page.getByTestId("sell").click();
  await expect(page.getByTestId("sales-notice")).toContainText("30,000.00", { timeout: 20_000 });
  await page.getByTestId("next-record-payment").click();
  await page.getByTestId("payment-amount").fill("10000");
  await page.getByTestId("save-payment").click();
  await expect(page.getByTestId("sales-notice")).toContainText("Payment recorded", { timeout: 20_000 });
  const saleRow = page.getByRole("row").filter({ hasText: "ALI" });
  await expect(saleRow.getByTestId("invoice-state")).toContainText("Partly paid");
  await expect(saleRow.getByTestId("invoice-paid")).toContainText("10,000.00");
  await saleRow.getByRole("gridcell").first().dblclick();
  await expect(page.getByTestId("invoice-dialog")).toBeVisible();
  await expect(page.getByTestId("payments-list").getByTestId("payment-row")).toHaveCount(1);
  await expect(page.getByTestId("payments-list")).toContainText("10,000.00");
  await expect(page.getByTestId("dialog-remaining")).toContainText("20,000.00");
  await expectAccessible(page);
  await page.getByTestId("dialog-pay").click();
  await expect(page.getByTestId("payment-amount")).toHaveValue("20,000");
  await page.getByTestId("save-payment").click();
  await expect(page.getByTestId("sales-notice")).toContainText("Payment recorded", { timeout: 20_000 });
  await expect(page.getByTestId("payments-list").getByTestId("payment-row")).toHaveCount(2);
  await expect(page.getByTestId("dialog-remaining")).toContainText("0.00");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("invoice-dialog")).toHaveCount(0);
  await expect(saleRow.getByTestId("invoice-state")).toContainText("Paid");

  // Arabic, right to left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByTestId("flat-nav").getByRole("link", { name: "فواتير الموردين" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("فواتير الموردين");
  await expect(page.getByRole("row").filter({ hasText: "BP-1001" }).getByTestId("purchase-state")).toContainText("مدفوعة");
  await page.getByTestId("new-goods").click();
  await expect(page.getByTestId("goods-dialog")).toContainText("بضاعة واصلة");
  await expectAccessible(page);
});
