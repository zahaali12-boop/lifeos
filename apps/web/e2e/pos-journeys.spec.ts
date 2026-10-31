import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The point-of-sale journey (founder programme item 7, A-180): a register is set up from the back office, the till
 * caches its catalogue and opens a session with a float, sells for cash with tax and cash rounding (the receipt shows
 * the change and the server's number), keeps selling offline (the order waits on the device and syncs by itself when
 * the network returns), refunds one unit against the first sale, closes the session with the count; the back office
 * shows the session with its difference and the orders; then Arabic. Every screen passes axe with no serious or
 * critical violation.
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

test("English: a register sells for cash, offline and back, refunds and closes; the back office shows it; then Arabic", async ({ page, context }) => {
  test.setTimeout(180_000);
  const slug = `pos-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => {
    if (!window.localStorage.getItem("quicker.language")) {
      window.localStorage.setItem("quicker.language", "en");
    }
  });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("POS " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // The Saudi company with VAT, a shop warehouse with stock, a till cash box and a card account, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const post = async <T,>(path: string, data: unknown): Promise<T> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await post<{ id: string }>("/api/v1/organization/companies", { code: "RYD", legalName: { en: "Riyadh Retail", ar: "الرياض للتجزئة" }, country: "SA", functionalCurrency: "SAR", timeZone: "Asia/Riyadh", costingMethod: "average" });
  await post("/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  await page.request.put(`${apiUrl}/api/v1/organization/companies/${company.id}/currencies`, { headers, data: { currency: "SAR", cashRoundingIncrement: 0.05 } });
  const regime = await post<{ regime: { id: string } }>("/api/v1/tax/templates/SA-VAT/install", {});
  await post("/api/v1/tax/registrations", { companyId: company.id, regimeId: regime.regime.id, registrationNumber: "310123456700003", registeredFrom: "2018-01-01" });
  const groups = (await (await page.request.get(`${apiUrl}/api/v1/tax/groups`, { headers })).json()) as { id: string; kind: string; code: string }[];
  const group = (kind: string, code: string): string => groups.find((g) => g.kind === kind && g.code === code)?.id ?? "";
  const warehouse = await post<{ id: string }>("/api/v1/inventory/warehouses", { companyId: company.id, code: "SHOP", name: { en: "Shop floor", ar: "صالة البيع" } });
  await post("/api/v1/banking/bank-accounts", { companyId: company.id, code: "TILL", name: { en: "Till cash", ar: "نقد الصندوق" }, kind: "cash", currency: "SAR" });
  await post("/api/v1/banking/bank-accounts", { companyId: company.id, code: "CARDS", name: { en: "Card acquirer", ar: "مستحصل البطاقات" }, kind: "bank", currency: "SAR" });
  const water = await post<{ id: string }>("/api/v1/items", { code: "WATER", name: { en: "Mineral water 500 ml", ar: "مياه معدنية 500 مل" }, baseUom: "PCS", type: "stock", listPrice: 2.3, listPriceCurrency: "SAR", itemTaxGroupId: group("item", "STANDARD"), barcodes: [{ barcode: "6291041500213" }] });
  const coffee = await post<{ id: string }>("/api/v1/items", { code: "COFFEE", name: { en: "Ground coffee 250 g", ar: "قهوة مطحونة 250 غ" }, baseUom: "PCS", type: "stock", listPrice: 24, listPriceCurrency: "SAR", itemTaxGroupId: group("item", "STANDARD") });
  await post("/api/v1/inventory/reason-codes", { code: "FOUND", name: { en: "Found", ar: "موجود" }, appliesTo: "adjustment" });
  const opening = await post<{ id: string }>("/api/v1/inventory/adjustments", { companyId: company.id, warehouseId: warehouse.id, kind: "opening", lines: [{ itemId: water.id, quantity: 100, unitCost: 1, reasonCode: "FOUND" }, { itemId: coffee.id, quantity: 50, unitCost: 15, reasonCode: "FOUND" }] });
  await post(`/api/v1/inventory/adjustments/${opening.id}/submit`, {});
  const walkIn = await post<{ id: string }>("/api/v1/partners", { code: "WALKIN", legalName: { en: "Walk-in customer", ar: "عميل نقدي" }, isCustomer: true });
  await page.request.put(`${apiUrl}/api/v1/partners/${walkIn.id}/customer-accounts/${company.id}`, { headers, data: { currency: "SAR", taxGroupId: group("partner", "DOMESTIC_UNREG") } });

  // The register from the back office. The company was created behind the screen's back, so the page is loaded afresh.
  await page.goto("/sales/pos");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Point of sale");
  await page.getByTestId("new-register").click();
  await page.getByTestId("register-name-en").fill("Front till");
  await page.getByTestId("register-name-ar").fill("الصندوق الأمامي");
  await page.getByTestId("register-warehouse").selectOption({ label: "SHOP · Shop floor" });
  await page.getByTestId("register-cash-account").selectOption({ label: "TILL · Till cash" });
  await page.getByTestId("register-card-account").selectOption({ label: "CARDS · Card acquirer" });
  await page.getByTestId("register-walk-in").selectOption({ label: "WALKIN · Walk-in customer" });
  await expectAccessible(page);
  await page.getByTestId("save-register").click();
  await expect(page.getByTestId("register-detail")).toBeVisible();
  await expect(page.getByTestId("register-detail")).toContainText("FRONT-TILL");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("grid")).toContainText("Front till");

  // The till: the register's catalogue cached, a session with a 100 float.
  await page.getByTestId("open-till").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Choose a register");
  await page.getByTestId("till-company").selectOption(company.id);
  await page.getByTestId("till-register").selectOption({ label: "FRONT-TILL · Front till" });
  await page.getByTestId("till-use-register").click();
  await expect(page.getByTestId("till-register-name")).toContainText("Front till");
  await page.getByTestId("till-opening-float").fill("100");
  await expectAccessible(page);
  await page.getByTestId("till-open-session").click();
  await expect(page.getByTestId("till-session-status")).toContainText("Open");
  await page.getByTestId("till-start-selling").click();

  // A cash sale: three waters by barcode and a coffee by tap; 35.54 rounds to 35.55 for cash; 50 tendered, 14.45 change.
  await page.getByTestId("till-search").fill("6291041500213");
  await page.getByTestId("till-search").press("Enter");
  await page.getByTestId("till-search").fill("6291041500213");
  await page.getByTestId("till-search").press("Enter");
  await page.getByTestId("till-search").fill("6291041500213");
  await page.getByTestId("till-search").press("Enter");
  await page.getByTestId("till-item-COFFEE").click();
  await expect(page.getByTestId("till-cart-line")).toHaveCount(2);
  await expect(page.getByTestId("till-tax")).toContainText("4.64");
  await expect(page.getByTestId("till-rounding")).toContainText("0.01");
  await expect(page.getByTestId("till-total")).toContainText("35.55");
  await page.getByTestId("till-tendered").fill("50");
  await expect(page.getByTestId("till-change")).toContainText("14.45");
  await expectAccessible(page);
  await page.getByTestId("till-charge").click();
  await expect(page.getByTestId("till-receipt")).toBeVisible();
  await expect(page.getByTestId("till-receipt-status")).toContainText("Synced");
  await expect(page.getByTestId("till-receipt-number")).toContainText("POS-");
  await expect(page.getByTestId("till-receipt-change")).toContainText("14.45");
  const firstNumber = (await page.getByTestId("till-receipt-number").textContent()) ?? "";
  await expectAccessible(page);
  await page.getByTestId("till-new-sale").click();

  // Offline: a coffee by card is sold from the cached catalogue and waits on the device; back online it syncs by itself.
  await context.setOffline(true);
  await expect(page.getByTestId("online-status")).toContainText("Offline");
  await page.getByTestId("till-item-COFFEE").click();
  await page.getByTestId("till-method-card").click();
  await page.getByTestId("till-reference").fill("AUTH-7781");
  await expect(page.getByTestId("till-total")).toContainText("27.60");
  await page.getByTestId("till-charge").click();
  await expect(page.getByTestId("till-receipt-status")).toContainText("Waiting to sync");
  await expect(page.getByTestId("till-pending")).toHaveText("1");
  await context.setOffline(false);
  await expect(page.getByTestId("online-status")).toContainText("Online");
  await expect(page.getByTestId("till-receipt-status")).toContainText("Synced", { timeout: 15_000 });
  await expect(page.getByTestId("till-pending")).toHaveCount(0);
  await page.getByTestId("till-new-sale").click();

  // A refund of one water against the first sale, from the orders screen.
  await page.getByTestId("till-tab-orders").click();
  await expect(page.getByTestId("till-order")).toHaveCount(2);
  await page.getByTestId("till-refund-link").last().click();
  await expect(page.getByTestId("till-refund-note")).toContainText(firstNumber);
  await expect(page.getByTestId("till-cart-line")).toHaveCount(2);
  // The coffee goes, two of the three waters go: one water comes back.
  await page.getByTestId("till-cart-line").nth(1).getByLabel("Remove").click();
  await page.getByTestId("till-cart-line").first().getByLabel("One less").click();
  await page.getByTestId("till-cart-line").first().getByLabel("One less").click();
  await expect(page.getByTestId("till-total")).toContainText("2.65");
  await page.getByTestId("till-charge").click();
  await expect(page.getByTestId("till-receipt-status")).toContainText("Synced");
  await expect(page.getByTestId("till-receipt")).toContainText("Refund receipt");

  // Close the session one riyal short: expected 100 + 35.55 + 0 (card) − 2.65 = 132.90.
  await page.getByTestId("till-tab-register").click();
  await page.getByTestId("till-close-session").click();
  await page.getByTestId("till-counted-cash").fill("131.90");
  await page.getByTestId("till-confirm-close").click();
  await expect(page.getByTestId("till-session-status")).toContainText("Closed");
  await expect(page.getByTestId("till-difference")).toContainText("-1.00");

  // The back office: the session with its difference, the orders with their numbers.
  await page.goto("/sales/pos");
  await page.getByTestId("tab-sessions").click();
  await expect(page.getByRole("grid")).toContainText("Closed");
  await page.getByRole("grid").getByText("SES-", { exact: false }).first().dblclick();
  await expect(page.getByTestId("session-detail")).toBeVisible();
  await expect(page.getByTestId("session-difference")).toContainText("1.00");
  await page.keyboard.press("Escape");
  await page.getByTestId("tab-orders").click();
  await expect(page.getByRole("grid")).toContainText(firstNumber);
  await expect(page.getByRole("grid")).toContainText("Refund");
  await page.getByRole("grid").getByText(firstNumber, { exact: true }).dblclick();
  await expect(page.getByTestId("pos-order-detail")).toBeVisible();
  await expect(page.getByTestId("pos-order-total")).toContainText("35.55");
  await expect(page.getByTestId("pos-order-line")).toHaveCount(2);
  await expectAccessible(page);
  await page.keyboard.press("Escape");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "نقطة البيع");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("نقطة البيع");
  await page.getByTestId("tab-registers").click();
  await expect(page.getByRole("grid")).toContainText("الصندوق الأمامي");
  await expectAccessible(page);
});
