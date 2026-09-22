import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The scanning journey of slice 3.8 (ADR-0029), on an emulated phone: a frozen count is entered by scanning the bin
 * then the item barcode, the entry syncs to the count sheet; the network drops, a second capture waits in the queue
 * and syncs by itself when the network is back; the screens pass axe in English and in Arabic (RTL).
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";
const barcode = "6291041500213";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function scan(page: Page, code: string): Promise<void> {
  await page.getByTestId("scan-input").fill(code);
  await page.getByTestId("scan-submit").click();
}

test("a count is scanned bin by bin, synced online, queued offline and replayed when the network returns", async ({ page, context }) => {
  const slug = `mob-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Mobile " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // The desktop set-up (company, warehouse with bins, an item with a barcode, a frozen count) through the API with the session's token.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "GET" | "POST", path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "MOB", legalName: { en: "Mobile Co.", ar: "شركة الجوال" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" }, binsEnabled: true });
  await call("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "A-01" });
  await call("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "A-02" });
  await call("POST", "/api/v1/items", { code: "WATER", name: { en: "Water 1.5L", ar: "ماء ١٫٥ لتر" }, baseUom: "PCS", barcodes: [{ barcode }] });
  const count = await call<{ id: string; number: string }>("POST", "/api/v1/inventory/counts", { companyId: company.id, warehouseId: warehouse.id, scope: "full" });
  await call("POST", `/api/v1/inventory/counts/${count.id}/freeze`);

  // Home: where the operator works.
  await page.goto("/m");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Scanning");
  await expect(page.getByTestId("online-status")).toContainText("Online");
  await page.getByTestId("scan-company").selectOption(company.id);
  await page.getByTestId("scan-warehouse").selectOption(warehouse.id);
  await expect(page.getByTestId("pending-text")).toContainText("Nothing waiting");
  await expectAccessible(page);

  // Count: bin, then the item by barcode, then the quantity; the capture syncs at once.
  await page.getByRole("navigation").getByRole("link", { name: "Count" }).click();
  await expect(page.getByTestId("scan-count")).toHaveValue(count.id);
  await scan(page, "A-01");
  await expect(page.getByTestId("current-bin")).toContainText("A-01");
  await scan(page, barcode);
  await expect(page.getByTestId("current-item")).toContainText("WATER");
  await expect(page.getByTestId("current-item")).toContainText("Water 1.5L");
  await page.getByTestId("count-quantity").fill("12");
  await page.getByTestId("count-add").click();
  await expect(page.getByTestId("capture").first()).toContainText("WATER");
  await expect(page.getByTestId("capture").first()).toContainText("Synced");
  await expectAccessible(page);
  const afterFirst = await call<{ lines: { itemCode: string; binCode: string | null; countedQty: number | string | null }[] }>("GET", `/api/v1/inventory/counts/${count.id}/sheet`);
  expect(afterFirst.lines.map((l) => [l.itemCode, l.binCode, Number(l.countedQty)])).toEqual([["WATER", "A-01", 12]]);

  // Offline: the next bin and item (already seen on this device) are captured into the queue.
  await context.setOffline(true);
  await expect(page.getByTestId("online-status")).toContainText("Offline");
  await page.getByTestId("change-bin").click();
  await scan(page, "A-02");
  await expect(page.getByTestId("current-bin")).toContainText("A-02");
  await scan(page, "WATER");
  await expect(page.getByTestId("current-item")).toContainText("Water 1.5L");
  await page.getByTestId("count-quantity").fill("5");
  await page.getByTestId("count-add").click();
  await expect(page.getByTestId("capture").first()).toContainText("Queued");
  await expect(page.getByTestId("queue-badge")).toHaveText("1");

  // Back online: the queue replays by itself.
  await context.setOffline(false);
  await expect(page.getByTestId("online-status")).toContainText("Online");
  await expect(page.getByTestId("capture").first()).toContainText("Synced");
  await expect(page.getByTestId("queue-badge")).toHaveCount(0);
  const afterSecond = await call<{ lines: { itemCode: string; binCode: string | null; countedQty: number | string | null }[] }>("GET", `/api/v1/inventory/counts/${count.id}/sheet`);
  expect(afterSecond.lines.map((l) => [l.itemCode, l.binCode, Number(l.countedQty)]).sort()).toEqual([
    ["WATER", "A-01", 12],
    ["WATER", "A-02", 5],
  ]);

  // The queue screen is empty, in English and in Arabic (RTL).
  await page.getByRole("navigation").getByRole("link", { name: "Queue" }).click();
  await expect(page.getByText("Everything is synced.")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("mobile-language").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("قائمة المزامنة");
  await expect(page.getByTestId("online-status")).toContainText("متصل");
  await expectAccessible(page);
  await page.getByRole("navigation").getByRole("link", { name: "الجرد" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الجرد");
  await expectAccessible(page);
});
