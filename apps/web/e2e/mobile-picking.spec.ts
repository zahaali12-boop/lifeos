import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The picking journey of slice 5.5 (A-154), on an emulated phone: a shipment released to the warehouse appears on the
 * scanner; the picker takes it, walks the bins in their pick sequence scanning each bin and item, short-picks the last
 * stop with a reason, and the list is picked; the office then ships exactly what was picked. English and Arabic (RTL).
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";
const barcode = "6291041500992";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function scan(page: Page, code: string): Promise<void> {
  await page.getByTestId("scan-input").fill(code);
  await page.getByTestId("scan-submit").click();
}

test("a released shipment is picked on the scanner bin by bin, one stop short, and ships what was picked", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `pck-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Picking " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // The office's side through the API: a warehouse with bins, stock in three of them, a confirmed order, a released shipment.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "GET" | "POST" | "PUT", path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "PCK", legalName: { en: "Picking Co.", ar: "شركة الانتقاء" }, country: "IQ", functionalCurrency: "USD", timeZone: "Asia/Baghdad" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" }, binsEnabled: true });
  const binA = await call<{ id: string }>("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "A-01", zone: "A", pickSequence: 20 });
  const binB = await call<{ id: string }>("POST", `/api/v1/inventory/warehouses/${warehouse.id}/bins`, { code: "B-01", zone: "B", pickSequence: 10 });
  const item = await call<{ id: string }>("POST", "/api/v1/items", { code: "TEA", name: { en: "Black tea", ar: "شاي أسود" }, baseUom: "PCS", listPrice: 10, listPriceCurrency: "USD", barcodes: [{ barcode }] });
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
  const released = await call<{ pickListNumber: string }>("POST", `/api/v1/sales/shipments/${shipment.id}/release`, {});

  // The scanner: where the operator works, then the pick list in the queue.
  await page.goto("/m");
  await page.getByTestId("scan-company").selectOption(company.id);
  await page.getByTestId("scan-warehouse").selectOption(warehouse.id);
  await page.getByTestId("start-pick").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Pick");
  await expect(page.getByTestId("pick-queue-item")).toContainText(released.pickListNumber);
  await expect(page.getByTestId("pick-queue-item")).toContainText(shipment.number);
  await expectAccessible(page);
  await page.getByTestId("pick-start").click();

  // First stop: bin B-01 (pick sequence 10) before A-01 (20). Scanning the wrong item is refused.
  await expect(page.getByTestId("pick-current-bin")).toHaveText("B-01");
  await expect(page.getByTestId("pick-current-qty")).toContainText("5");
  await scan(page, "B-01");
  await scan(page, "COFFEE");
  await expect(page.getByText(/No item matches COFFEE/)).toBeVisible();
  await scan(page, barcode);
  await expectAccessible(page);
  await page.getByTestId("pick-confirm").click();

  // Second stop: A-01, three to pick; only two on the shelf, so a short pick with a reason.
  await expect(page.getByTestId("pick-current-bin")).toHaveText("A-01");
  await expect(page.getByTestId("pick-current-qty")).toContainText("3");
  await scan(page, "A-01");
  await scan(page, "TEA");
  await page.getByTestId("pick-quantity").fill("2");
  await expect(page.getByTestId("pick-short")).toBeVisible();
  await page.getByTestId("pick-quick-reason").nth(1).click();
  await expect(page.getByTestId("pick-short-reason")).toHaveValue("Damaged");
  await page.getByTestId("pick-confirm").click();
  await expect(page.getByTestId("pick-done")).toContainText("Every line is picked");
  await expect(page.getByTestId("pick-route-status").nth(1)).toContainText("Short");
  await expectAccessible(page);

  // The office posts what was picked: 7 of 8, the eighth still reserved on the order.
  const posted = await call<{ status: string; lines: { quantity: number | string }[] }>("POST", `/api/v1/sales/shipments/${shipment.id}/post`, {});
  expect(posted.status).toBe("posted");
  expect(Number(posted.lines[0]?.quantity)).toBe(7);
  const after = await call<{ lines: { qtyShipped: number | string; qtyReserved: number | string }[] }>("GET", `/api/v1/sales/orders/${order.id}`);
  expect([Number(after.lines[0]?.qtyShipped), Number(after.lines[0]?.qtyReserved)]).toEqual([7, 1]);

  // Arabic: the empty queue reads right to left.
  await page.getByTestId("pick-back").click();
  await page.getByTestId("mobile-language").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الانتقاء");
  await expect(page.getByText("لا شيء للانتقاء هنا")).toBeVisible();
  await expectAccessible(page);
});
