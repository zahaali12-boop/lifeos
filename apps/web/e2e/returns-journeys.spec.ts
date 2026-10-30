import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The returns journey of 5.8: a lot sold and invoiced is recalled and three units come back on the returns screen
 * (requested, authorised, received on hold with the recall flagged, quarantined at inspection, credited at the
 * invoice's price), then a sold phone comes back from the shipment's own "Return goods", goes into repair and its
 * whole chain reads on the return. Then the screens in Arabic, right-to-left. Every screen passes axe with no
 * serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: a recalled lot comes back on hold, is quarantined and credited; a phone comes back for repair with its chain; then Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  const slug = `rma-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Returns " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // What the screens of earlier slices already cover is set up through the API: a company, stock of a lot-tracked
  // milk and a serialised phone, both sold, shipped and invoiced; then the milk's lot is recalled.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "GET" | "POST" | "PUT", path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "RMA", legalName: { en: "Returns Co.", ar: "شركة المرتجعات" }, country: "IQ", functionalCurrency: "USD", timeZone: "Asia/Baghdad" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" } });
  const milk = await call<{ id: string }>("POST", "/api/v1/items", { code: "MILK", name: { en: "Milk 1L", ar: "حليب ١ لتر" }, baseUom: "PCS", tracking: "lot", expiryRequired: true, fefo: true, listPrice: 5, listPriceCurrency: "USD" });
  const phone = await call<{ id: string }>("POST", "/api/v1/items", { code: "PHONE", name: { en: "Phone", ar: "هاتف" }, baseUom: "PCS", tracking: "serial", listPrice: 500, listPriceCurrency: "USD" });
  await call("POST", "/api/v1/inventory/reason-codes", { code: "FOUND", name: { en: "Found", ar: "موجود" }, appliesTo: "adjustment" });
  await call("POST", "/api/v1/inventory/reason-codes", { code: "RECALL", name: { en: "Recalled", ar: "مسحوب" }, appliesTo: "return" });
  await call("POST", "/api/v1/inventory/reason-codes", { code: "DEFECT", name: { en: "Defective", ar: "معيب" }, appliesTo: "return" });
  const opening = await call<{ id: string }>("POST", "/api/v1/inventory/adjustments", {
    companyId: company.id, warehouseId: warehouse.id, kind: "opening",
    lines: [
      { itemId: milk.id, quantity: 8, unitCost: 2, reasonCode: "FOUND", lotNumber: "LOT-A", expiresOn: "2026-12-01" },
      { itemId: phone.id, quantity: 2, unitCost: 200, reasonCode: "FOUND", serialNumbers: ["SN-1", "SN-2"] },
    ],
  });
  await call("POST", `/api/v1/inventory/adjustments/${opening.id}/submit`, {});
  const customer = await call<{ id: string }>("POST", "/api/v1/partners", { code: "MALL", legalName: { en: "Baghdad Mall", ar: "بغداد مول" }, isCustomer: true });
  await call("PUT", `/api/v1/partners/${customer.id}/customer-accounts/${company.id}`, { currency: "USD" });
  const ship = async (itemId: string, quantity: number): Promise<{ shipmentId: string; shipmentLineId: string }> => {
    const order = await call<{ id: string; lines: { id: string }[] }>("POST", "/api/v1/sales/orders", { companyId: company.id, partnerId: customer.id, warehouseId: warehouse.id, lines: [{ itemId, quantity }] });
    await call("POST", `/api/v1/sales/orders/${order.id}/confirm`, {});
    const shipment = await call<{ id: string }>("POST", "/api/v1/sales/shipments", { orderId: order.id, lines: [{ orderLineId: order.lines[0]?.id, quantity }] });
    const posted = await call<{ id: string; lines: { id: string }[] }>("POST", `/api/v1/sales/shipments/${shipment.id}/post`, {});
    return { shipmentId: posted.id, shipmentLineId: posted.lines[0]?.id ?? "" };
  };
  const milkShipment = await ship(milk.id, 5);
  const phoneShipment = await ship(phone.id, 2);
  for (const line of [milkShipment.shipmentLineId, phoneShipment.shipmentLineId]) {
    const invoice = await call<{ id: string }>("POST", "/api/v1/sales/invoices", { companyId: company.id, partnerId: customer.id, lines: [{ kind: "shipment", shipmentLineId: line, quantity: line === milkShipment.shipmentLineId ? 5 : 2 }] });
    await call("POST", `/api/v1/sales/invoices/${invoice.id}/post`, {});
  }
  const lots = await call<{ id: string }[]>("GET", `/api/v1/inventory/lots?itemId=${milk.id}`);
  await call("POST", `/api/v1/inventory/lots/${lots[0]?.id ?? ""}/status`, { status: "recalled", reason: "Supplier notice", recallReference: "RC-2026-11" });

  // The returns screen (a full load, so the company set up through the API is picked up): three units of the recalled milk requested back, with the reason code and the proposed disposition.
  await page.goto("/sales/returns");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Sales returns");
  await page.getByTestId("new-return").click();
  await page.getByTestId("return-customer").selectOption({ label: "MALL · Baghdad Mall" });
  await page.getByTestId("return-reason").fill("Recall RC-2026-11");
  await expect(page.getByTestId("returnable-lines").getByRole("button")).toHaveCount(2);
  await page.getByTestId("returnable-lines").getByRole("button", { name: /MILK/u }).click();
  await page.getByTestId("return-qty-0").fill("3");
  await page.getByTestId("return-reason-code-0").selectOption("RECALL");
  await page.getByTestId("return-disposition-0").selectOption("restock");
  await expectAccessible(page);
  await page.getByTestId("save-return").click();
  const detail = page.getByTestId("return-detail");
  await expect(detail).toBeVisible();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Requested");
  await expect(detail).toContainText("RMA-");
  await expectAccessible(page);

  // Authorised, then received: on hold and flagged recalled, the disposition forced to quarantine, the cost 6 (3 at the shipment's 2).
  await page.getByTestId("authorise-return").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Authorised");
  await page.getByTestId("receive-return").click();
  await page.getByTestId("confirm-receive").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Received");
  await expect(detail.getByTestId("return-cost")).toContainText("6.00");
  await expect(detail.getByTestId("line-lot-status")).toContainText("Recalled");
  await expect(detail.getByTestId("line-disposition")).toContainText("Quarantine");

  // Inspection refuses to restock a recalled lot; quarantine is accepted; the credit note credits 15 on the invoice.
  await page.getByTestId("inspect-return").click();
  await page.getByTestId("inspect-disposition-1").selectOption("restock");
  await page.getByTestId("confirm-inspect").click();
  await expect(detail).toContainText("quarantined or scrapped");
  await page.getByTestId("inspect-disposition-1").selectOption("quarantine");
  await page.getByTestId("inspect-note-1").fill("Held for the recall");
  await page.getByTestId("confirm-inspect").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Inspected");
  await page.getByTestId("credit-return").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Credited");
  await expect(detail.getByTestId("return-credit-notes")).toContainText("CN-");
  await expect(detail.getByTestId("return-credit-notes")).toContainText("15.00");
  await expect(detail.getByTestId("line-credited")).toContainText("3");
  await expectAccessible(page);
  await page.getByTestId("close-return").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Closed");
  await closeDialog(page);

  // From the phone's shipment: "Return goods" pre-fills the request; SN-1 comes back for repair, and its chain reads on the return.
  await page.goto(`/sales/shipments?open=${phoneShipment.shipmentId}`);
  await expect(page.getByTestId("shipment-detail")).toBeVisible();
  await page.getByTestId("create-return").click();
  await page.waitForURL(/\/sales\/returns/u);
  await expect(page.getByTestId("return-line-editor")).toBeVisible();
  await page.getByTestId("return-qty-0").fill("1");
  await page.getByTestId("return-serials-0").fill("SN-1");
  await page.getByTestId("return-reason-code-0").selectOption("DEFECT");
  await page.getByTestId("return-line-reason-0").fill("Screen flickers");
  await page.getByTestId("return-disposition-0").selectOption("repair");
  await page.getByTestId("save-return").click();
  await expect(detail).toBeVisible();
  await page.getByTestId("authorise-return").click();
  await page.getByTestId("receive-return").click();
  await page.getByTestId("confirm-receive").click();
  await expect(detail.getByTestId("doc-status").first()).toContainText("Received");
  await page.getByTestId("inspect-return").click();
  await page.getByTestId("confirm-inspect").click();
  await expect(detail.getByTestId("line-disposition")).toContainText("Repair");
  await page.getByTestId("serial-SN-1").click();
  const history = page.getByTestId("serial-history");
  await expect(history).toBeVisible();
  await expect(history.getByTestId("serial-event")).toHaveCount(4);
  await expect(history.getByTestId("serial-event").nth(1)).toContainText("Sold");
  await expect(history.getByTestId("serial-event").nth(2)).toContainText("Returned");
  await expect(history.getByTestId("serial-event").nth(3)).toContainText("In repair");
  await expectAccessible(page);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Inspected");

  // Arabic: the list and a return, right-to-left and accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("مرتجعات المبيعات");
  await expectAccessible(page);
  await page.getByRole("grid").getByText(/RMA-\d{4}-00001/u).dblclick();
  await expect(detail).toBeVisible();
  await expect(detail).toContainText("حجر");
  await expectAccessible(page);
});
