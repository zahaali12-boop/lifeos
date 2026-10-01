import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The office side of pick, pack and ship (slice 5.5, A-154): a draft shipment shows where each unit will come from, is
 * released to the warehouse, appears on the pick lists screen; once picked it posts, is packed into two cartons and
 * gets the carrier's tracking number. English, then Arabic (RTL), with axe on every screen.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("English: a shipment is located bin by bin, released, picked, posted, packed and handed to a carrier; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `pp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { if (!window.localStorage.getItem("quicker.language")) { window.localStorage.setItem("quicker.language", "en"); } });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Packing " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "GET" | "POST" | "PUT", path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "PPK", legalName: { en: "Packing Co.", ar: "شركة التغليف" }, country: "IQ", functionalCurrency: "USD", timeZone: "Asia/Baghdad" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" }, binsEnabled: true });
  const binA = await call<{ id: string }>("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "A-01", zone: "A", pickSequence: 20 });
  const binB = await call<{ id: string }>("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "B-01", zone: "B", pickSequence: 10 });
  const item = await call<{ id: string }>("POST", "/api/v1/items", { code: "TEA", name: { en: "Black tea", ar: "شاي أسود" }, baseUom: "PCS", listPrice: 10, listPriceCurrency: "USD", weightKg: 0.5 });
  await call("POST", "/api/v1/inventory/reason-codes", { code: "FOUND", name: { en: "Found", ar: "موجود" }, appliesTo: "adjustment" });
  const adjustment = await call<{ id: string }>("POST", "/api/v1/inventory/adjustments", {
    companyId: company.id, warehouseId: warehouse.id, kind: "opening",
    lines: [{ itemId: item.id, quantity: 4, unitCost: 5, reasonCode: "FOUND", binId: binA.id }, { itemId: item.id, quantity: 5, unitCost: 5, reasonCode: "FOUND", binId: binB.id }],
  });
  await call("POST", `/api/v1/inventory/adjustments/${adjustment.id}/submit`, {});
  const customer = await call<{ id: string }>("POST", "/api/v1/partners", { code: "MALL", legalName: { en: "Baghdad Mall", ar: "بغداد مول" }, isCustomer: true });
  await call("PUT", `/api/v1/partners/${customer.id}/customer-accounts/${company.id}`, { currency: "USD" });
  const order = await call<{ id: string; lines: { id: string }[] }>("POST", "/api/v1/sales/orders", { companyId: company.id, partnerId: customer.id, warehouseId: warehouse.id, lines: [{ itemId: item.id, quantity: 8 }] });
  await call("POST", `/api/v1/sales/orders/${order.id}/confirm`, {});
  const shipment = await call<{ id: string; number: string }>("POST", "/api/v1/sales/shipments", { orderId: order.id, lines: [{ orderLineId: order.lines[0]?.id, quantity: 8 }] });

  // The draft shows where the stock will come from, in the bins' pick sequence.
  await page.goto(`/sales/shipments?open=${shipment.id}`);
  const detail = page.getByTestId("shipment-detail");
  await expect(detail).toBeVisible();
  await expect(detail.getByTestId("allocation")).toHaveCount(2);
  await expect(detail.getByTestId("allocation").first()).toContainText("B-01");
  await expect(detail.getByTestId("allocation").first()).toContainText("5");
  await expect(detail.getByTestId("allocation").nth(1)).toContainText("A-01");
  await expectAccessible(page);

  // Released to the warehouse: a pick list, and the shipment waits for it.
  await page.getByTestId("release-shipment").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Picking");
  await expect(detail.getByTestId("shipment-pick-list")).toContainText("PCK-");
  await expect(page.getByTestId("post-shipment")).toHaveCount(0);

  // The pick lists screen lists it; the warehouse picks it (on the scanner; here through the API).
  await page.getByTestId("shipment-pick-list").getByRole("link").click();
  await page.waitForURL(/\/inventory\/pick-lists/u);
  const pickDetail = page.getByTestId("pick-list-detail");
  await expect(pickDetail).toBeVisible();
  await expect(pickDetail.getByTestId("pick-line")).toHaveCount(2);
  await expect(pickDetail.getByTestId("pick-line").first()).toContainText("B-01");
  await expectAccessible(page);
  const list = await call<{ id: string; lines: { id: string; qtyToPick: number | string }[] }>("GET", `/api/v1/inventory/pick-lists/${new URL(page.url()).searchParams.get("open") ?? ""}`);
  for (const line of list.lines) {
    await call("POST", `/api/v1/inventory/pick-lists/${list.id}/lines/${line.id}/pick`, { quantity: Number(line.qtyToPick) });
  }
  await page.reload();
  await expect(page.getByTestId("pick-list-detail").getByTestId("pick-status").first()).toContainText("Picked");

  // Back on the shipment: post what was picked, then pack it in two cartons and give the carrier's number.
  await page.goto(`/sales/shipments?open=${shipment.id}`);
  await expect(detail.getByTestId("shipment-pick-list")).toContainText("Every line is picked");
  await page.getByTestId("post-shipment").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Posted");
  await page.getByTestId("edit-packages").click();
  await page.getByTestId("package-qty-TEA").first().fill("5");
  await page.getByTestId("package-weight").first().fill("2.8");
  await page.getByTestId("add-package").click();
  await page.getByTestId("package-qty-TEA").nth(1).fill("2");
  await expect(page.getByTestId("packing-summary")).toContainText("1 not packed yet");
  await page.getByTestId("package-qty-TEA").nth(1).fill("3");
  await expect(page.getByTestId("packing-summary")).toContainText("all packed");
  await page.getByTestId("package-weight").nth(1).fill("1.7");
  await page.getByTestId("save-packages").click();
  await expect(detail.getByTestId("package")).toHaveCount(2);
  await expect(detail.getByTestId("line-packed")).toContainText("8");
  await expect(detail).toContainText("4.5 kg");
  await page.getByTestId("edit-carrier").click();
  await page.getByTestId("carrier-name").fill("Iraqi Post Express");
  await page.getByTestId("carrier-tracking").fill("IPX-0001");
  await page.getByTestId("save-carrier").click();
  await expect(detail).toContainText("IPX-0001");
  await expectAccessible(page);

  // Arabic: the same shipment and the pick lists screen, right to left.
  await page.keyboard.press("Escape");
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.goto(`/sales/shipments?open=${shipment.id}`);
  await expect(page.getByTestId("shipment-detail")).toContainText("الطرود");
  await expectAccessible(page);
  await page.goto("/inventory/pick-lists");
  await page.getByTestId("pick-status-filter").selectOption("");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("قوائم الانتقاء");
  await expectAccessible(page);
});
