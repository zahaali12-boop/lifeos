import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The trader's phone site (ADR-0035 §21, T12c): a workspace made as the trader program opens on the phone site when
 * the screen is a phone's; the trader sees his cash box and who owes him, sells two brake pads and prints the
 * invoice, collects the payment with its receipt right after, finds the customer's statement, and sees the money in
 * the box; then the same screens in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("the phone site: balances, a sale with its invoice, the payment with its receipt, the customer's statement; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `php-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;

  // The workspace, made as the trader program, and its company set up through the API (the screens for these are covered elsewhere).
  const signup = await page.request.post(`${apiUrl}/api/v1/auth/signup`, { data: { tenantName: "Phone " + slug, slug, ownerName: "Owner", ownerEmail: email, password, edition: "trader" } });
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
  const warehouse = await call("post", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main store", ar: "المخزن الرئيسي" } });
  const pads = await call("post", "/api/v1/items", { code: "PADS", name: { en: "Brake pads", ar: "فحمات فرامل" }, baseUom: "PCS" });
  await call("post", "/api/v1/inventory/reason-codes", { code: "FOUND", name: { en: "Opening stock", ar: "رصيد افتتاحي" }, appliesTo: "adjustment" });
  const opening = await call("post", "/api/v1/inventory/adjustments", { companyId: company.id, warehouseId: warehouse.id, kind: "opening", lines: [{ itemId: pads.id, quantity: 10, unitCost: 9000, reasonCode: "FOUND" }] });
  await call("post", `/api/v1/inventory/adjustments/${opening.id}/submit`, {});
  const ali = await call("post", "/api/v1/partners", { code: "ALI", legalName: { en: "Ali Auto Repair", ar: "ورشة علي" }, isCustomer: true });
  await call("put", `/api/v1/partners/${ali.id}/customer-accounts/${company.id}`, { currency: "IQD" });
  await call("post", "/api/v1/banking/bank-accounts", { companyId: company.id, code: "CASH", name: { en: "Cash box", ar: "الصندوق" }, kind: "cash", currency: "IQD", isActive: true });
  await call("put", "/api/v1/organization/settings/trader.rates.prompt_hour", { value: -1, valueType: "number" });

  // Signed in on a phone, the trader program opens its phone site.
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/login");
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page).toHaveURL(/\/p$/, { timeout: 20_000 });
  await expect(page.getByTestId("phone-cash-account")).toContainText("CASH");
  await expect(page.getByTestId("phone-cash-balance")).toContainText("0");
  await expect(page.getByTestId("phone-no-sales")).toBeVisible();
  await expectAccessible(page);

  // Sell: Ali takes two pads at 15,000; the invoice is right there.
  await page.getByTestId("phone-tab-sell").click();
  await page.getByTestId("phone-sale-customer").selectOption(ali.id);
  await page.getByTestId("phone-line-item-0").selectOption(pads.id);
  await page.getByTestId("phone-line-qty-0").fill("2");
  await page.getByTestId("phone-line-price-0").fill("15000");
  await expect(page.getByTestId("phone-sale-total")).toContainText("30,000");
  await expectAccessible(page);
  await page.getByTestId("phone-sell-submit").click();
  await expect(page.getByTestId("phone-sold")).toContainText("30,000", { timeout: 20_000 });
  await page.getByTestId("phone-invoice-pdf").click();
  await expect(page.getByTestId("print-dialog")).toBeVisible();
  await expect(page.getByTestId("print-preview")).toBeVisible({ timeout: 20_000 });
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("print-dialog")).toHaveCount(0);

  // Collect: the payment of that invoice, into the cash box, with its receipt.
  await page.getByTestId("phone-collect-now").click();
  await expect(page).toHaveURL(/\/p\/collect/);
  await expect(page.getByTestId("phone-collect-amount")).toHaveValue("30,000");
  await expect(page.getByTestId("phone-collect-account")).toContainText("CASH");
  await expectAccessible(page);
  await page.getByTestId("phone-collect-submit").click();
  await expect(page.getByTestId("phone-collected")).toContainText("30,000", { timeout: 20_000 });
  await page.getByTestId("phone-receipt-pdf").click();
  await expect(page.getByTestId("print-dialog")).toBeVisible();
  await expect(page.getByTestId("print-preview")).toBeVisible({ timeout: 20_000 });
  await page.keyboard.press("Escape");

  // Customers: Ali owes nothing now; his statement is one tap away.
  await page.getByTestId("phone-tab-customers").click();
  await expect(page.getByTestId("phone-customer-row").filter({ hasText: "ALI" })).toContainText("Nothing owed");
  await page.getByTestId("phone-statement-ALI").click();
  await expect(page.getByTestId("print-dialog")).toBeVisible();
  await expect(page.getByTestId("print-preview")).toBeVisible({ timeout: 20_000 });
  await page.keyboard.press("Escape");
  await expectAccessible(page);

  // Pay: nothing is owed to suppliers yet; the way to record purchases is said.
  await page.getByTestId("phone-tab-buy").click();
  await expect(page.getByTestId("phone-nothing-to-pay")).toBeVisible();
  await expectAccessible(page);

  // Home: the box holds the 30,000, and the sale is listed as paid.
  await page.getByTestId("phone-tab-home").click();
  await expect(page.getByTestId("phone-cash-balance")).toContainText("30,000");
  await expect(page.getByTestId("phone-invoice")).toContainText("Paid");

  // Arabic, right to left.
  await page.getByTestId("phone-language").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("اليوم");
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expectAccessible(page);
  await page.getByTestId("phone-tab-sell").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("بيع جديد");
  await expectAccessible(page);

  // The full program is a tap away and stays for the session (its menu folds behind a button at this width).
  await page.getByTestId("phone-full-program").click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { level: 1 })).toContainText("مرحباً");
  await expect(page.getByTestId("flat-nav")).toHaveCount(1);
});
