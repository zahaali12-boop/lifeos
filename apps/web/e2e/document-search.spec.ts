import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Documents found by number and opened where they live, against the real API: a purchase order and its posted goods
 * receipt (set up through the API), found from the command palette by the receipt's short number and by the order's
 * full number; the receipt reached from its journal entry's source link and from its audit event; then the palette in
 * Arabic. Every screen passes axe.
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

async function palette(page: Page, query: string): Promise<void> {
  await page.keyboard.press("ControlOrMeta+k");
  await page.getByRole("dialog").getByRole("combobox").fill(query);
}

test("English: a receipt and its order found by number, a journal entry's source and an audit event opening the receipt; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `doc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Documents " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // A company with its books, an item, a supplier, an approved order and its posted receipt, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: string, path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "DOC", legalName: { en: "Documents Co.", ar: "شركة المستندات" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad", costingMethod: "average" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" } });
  const item = await call<{ id: string }>("POST", "/api/v1/items", { code: "TEA", name: { en: "Tea", ar: "شاي" }, baseUom: "PCS" });
  const supplier = await call<{ id: string }>("POST", "/api/v1/partners", { code: "ALPHA", legalName: { en: "Alpha Supplies", ar: "ألفا" }, isSupplier: true });
  await call("PUT", `/api/v1/partners/${supplier.id}/supplier-accounts/${company.id}`, { currency: "IQD", leadTimeDays: 7 });
  const order = await call<{ id: string; number: string; lines: { id: string }[] }>("POST", "/api/v1/purchasing/orders", { companyId: company.id, partnerId: supplier.id, warehouseId: warehouse.id, lines: [{ itemId: item.id, quantity: 10, uom: "PCS", unitPrice: 1500 }] });
  await call("POST", `/api/v1/purchasing/orders/${order.id}/submit`, {});
  const receipt = await call<{ id: string }>("POST", "/api/v1/purchasing/receipts", { orderId: order.id, lines: [{ orderLineId: order.lines[0]?.id, quantity: 10 }] });
  const posted = await call<{ number: string }>("POST", `/api/v1/purchasing/receipts/${receipt.id}/post`, {});
  expect(posted.number).toMatch(/^GRN-\d{4}-00001$/);
  await page.reload();

  // The palette finds the receipt by its prefix and unpadded sequence, labelled with its type and company; Enter opens it.
  await palette(page, "GRN-1");
  const hit = page.getByTestId("palette-document").filter({ hasText: posted.number });
  await expect(hit).toContainText("Goods receipt");
  await expect(hit).toContainText("DOC");
  await expectAccessible(page);
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/purchasing\/receipts\?open=/);
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await expect(page.getByRole("dialog")).toContainText(posted.number);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  // The order by its full number, from another screen.
  await nav(page, "Items");
  await palette(page, order.number);
  await page.getByTestId("palette-document").filter({ hasText: order.number }).click();
  await expect(page.getByTestId("order-detail")).toBeVisible();
  await expect(page.getByRole("dialog")).toContainText(order.number);
  await page.keyboard.press("Escape");

  // The receipt's journal entry names its source, which opens the receipt.
  await nav(page, "Journal entries");
  await page.getByRole("grid").getByRole("row").filter({ hasText: posted.number }).dblclick();
  const source = page.getByTestId("entry-detail").getByTestId("source-document");
  await expect(source).toHaveText(posted.number);
  await source.click();
  await expect(page).toHaveURL(/\/purchasing\/receipts\?open=/);
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await page.keyboard.press("Escape");

  // The audit trail's posting event opens the receipt too.
  await nav(page, "Audit trail");
  await page.getByTestId("audit-action").fill("Posted");
  await page.getByRole("grid").getByRole("row").filter({ hasText: posted.number }).dblclick();
  await expect(page.getByTestId("audit-event-detail")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("audit-open-record").click();
  await expect(page).toHaveURL(/\/purchasing\/receipts\?open=/);
  await expect(page.getByTestId("receipt-detail")).toBeVisible();
  await page.keyboard.press("Escape");

  // Arabic: the same search reads right to left with the Arabic group and type.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await palette(page, "GRN-1");
  await expect(page.getByRole("dialog")).toContainText("المستندات");
  await expect(page.getByTestId("palette-document").filter({ hasText: posted.number })).toContainText("استلام بضاعة");
  await expectAccessible(page);
});
