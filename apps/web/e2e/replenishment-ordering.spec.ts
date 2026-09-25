import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * From the planner to the buyer: the replenishment run suggests what to buy, the buyer selects the suggestions and
 * creates purchase orders in one step (one draft per supplier, at the supplier's last price, a supplier chosen for
 * the item that has none), opens a draft from the result, and the suggestions show they are on order.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("English: replenishment suggestions become draft purchase orders per supplier", async ({ page }) => {
  test.setTimeout(90_000);
  const slug = `rep-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Replenishment " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // The set-up through the API with the session's token: a company and warehouse, two items below their reorder
  // points (water with a preferred supplier who sold it before, rice with none), and two registered suppliers.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "POST" | "PUT", path: string, data: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" }, data });
    expect(response.ok(), `${method} ${path}: ${String(response.status())} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "REP", legalName: { en: "Replenish Co.", ar: "شركة التزويد" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" } });
  const water = await call<{ id: string }>("POST", "/api/v1/items", { code: "WATER", name: { en: "Water 1.5L", ar: "ماء ١٫٥ لتر" }, baseUom: "PCS" });
  const rice = await call<{ id: string }>("POST", "/api/v1/items", { code: "RICE", name: { en: "Rice 5kg", ar: "أرز ٥ كغ" }, baseUom: "PCS" });
  const supplier = async (code: string, name: string): Promise<string> => {
    const partner = await call<{ id: string }>("POST", "/api/v1/partners", { code, legalName: { en: name, ar: name }, isSupplier: true });
    await call("PUT", `/api/v1/partners/${partner.id}/supplier-accounts/${company.id}`, { currency: "IQD", leadTimeDays: 5 });
    return partner.id;
  };
  const alpha = await supplier("ALPHA", "Alpha Supplies");
  await supplier("BETA", "Beta Trading");
  await call("POST", `/api/v1/items/${water.id}/suppliers`, { partnerId: alpha, leadTimeDays: 5, isPreferred: true });
  await call("PUT", `/api/v1/items/${water.id}/warehouse-settings/${warehouse.id}`, { reorderPoint: 50, maxQty: 200 });
  await call("PUT", `/api/v1/items/${rice.id}/warehouse-settings/${warehouse.id}`, { reorderPoint: 10, maxQty: 40 });
  const history = await call<{ id: string }>("POST", "/api/v1/purchasing/orders", { companyId: company.id, partnerId: alpha, warehouseId: warehouse.id, lines: [{ itemId: water.id, quantity: 10, uom: "PCS", unitPrice: 750 }] });
  await call("POST", `/api/v1/purchasing/orders/${history.id}/submit`, {});
  await call("POST", `/api/v1/purchasing/orders/${history.id}/close`, {});

  // The planner suggests both; the buyer selects them and creates the orders.
  await page.reload(); // the company was created behind the app's back
  await page.getByRole("navigation").getByRole("link", { name: "Replenishment", exact: true }).click();
  await page.getByTestId("run-planner").click();
  const grid = page.getByRole("grid");
  await expect(grid.getByRole("row").filter({ hasText: "WATER" })).toContainText("ALPHA");
  await expect(grid.getByRole("row").filter({ hasText: "RICE" })).toContainText("—");
  await grid.getByRole("checkbox").first().check();
  await page.getByTestId("order-suggestions").click();
  await expect(page.getByTestId("order-supplier")).toBeVisible();
  await page.getByTestId("order-supplier").selectOption({ label: "BETA · Beta Trading" });
  await expectAccessible(page);
  await page.getByTestId("confirm-order").click();
  await expect(page.getByTestId("ordered-link")).toHaveCount(2);

  // A draft opens from the result: water from Alpha at what Alpha charged last.
  await page.getByTestId("ordered").locator("li").filter({ hasText: "ALPHA" }).getByTestId("ordered-link").click();
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await expect(page.getByTestId("order-detail").getByTestId("doc-status").first()).toContainText("Draft");
  await expect(page.getByTestId("order-lines")).toContainText("WATER");
  await expect(page.getByTestId("order-lines")).toContainText("750");
  await page.keyboard.press("Escape");

  // Back on the planner, the decided suggestions are on order and can no longer be ordered.
  await page.getByRole("navigation").getByRole("link", { name: "Replenishment", exact: true }).click();
  await page.getByLabel("Status").selectOption("accepted");
  await expect(page.getByTestId("on-order")).toHaveCount(2);
});
