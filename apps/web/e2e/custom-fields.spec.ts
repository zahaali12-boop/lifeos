import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Configuration over code: an administrator adds fields to purchase orders (a required choice and a multiple choice)
 * from the custom fields screen, and they appear on the order form at once. The API refuses the order without the
 * required one and says so under the field; once filled, the order shows the values, in Arabic too.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("English: fields added to purchase orders are filled on the order form, enforced by the API and shown on the order; then Arabic", async ({ page }) => {
  test.setTimeout(90_000);
  const slug = `cf-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.goto("/signup");
  await page.evaluate(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.reload();
  await page.getByLabel(/Workspace name/).fill("Custom fields " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // A company, an item, a supplier and a draft order through the API with the session's token.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  expect(token).toBeTruthy();
  const call = async <T>(method: "POST" | "PUT", path: string, data: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" }, data });
    expect(response.ok(), `${method} ${path}: ${String(response.status())} ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "CFX", legalName: { en: "Fields Co.", ar: "شركة الحقول" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  await call("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const warehouse = await call<{ id: string }>("POST", "/api/v1/inventory/warehouses", { companyId: company.id, code: "MAIN", name: { en: "Main", ar: "الرئيسي" } });
  const item = await call<{ id: string }>("POST", "/api/v1/items", { code: "PAPER", name: { en: "Paper A4", ar: "ورق A4" }, baseUom: "PCS" });
  const partner = await call<{ id: string }>("POST", "/api/v1/partners", { code: "ALPHA", legalName: { en: "Alpha Supplies", ar: "ألفا" }, isSupplier: true });
  await call("PUT", `/api/v1/partners/${partner.id}/supplier-accounts/${company.id}`, { currency: "IQD", leadTimeDays: 5 });
  const order = await call<{ id: string }>("POST", "/api/v1/purchasing/orders", { companyId: company.id, partnerId: partner.id, warehouseId: warehouse.id, lines: [{ itemId: item.id, quantity: 20, uom: "PCS", unitPrice: 5000 }] });
  await page.reload();

  // The administrator adds two fields to purchase orders.
  await page.getByRole("navigation").getByRole("link", { name: "Custom fields", exact: true }).click();
  await page.getByTestId("custom-field-host").selectOption({ label: "Purchase order" });
  const define = async (key: string, en: string, ar: string, type: string, options: string, required: boolean): Promise<void> => {
    await page.getByTestId("new-custom-field").click();
    await page.getByLabel(/^Key/).fill(key);
    await page.getByLabel(/Label \(English\)/).fill(en);
    await page.getByLabel(/Label \(Arabic\)/).fill(ar);
    await page.getByLabel(/^Type/).selectOption(type);
    await page.getByLabel(/^Options/).fill(options);
    if (required) {
      await page.getByLabel(/required/i).first().check();
    }
    await page.getByTestId("save-custom-field").click();
    await expect(page.getByRole("grid")).toContainText(key);
  };
  await define("priority", "Delivery priority", "أولوية التسليم", "select", "normal, urgent", true);
  await define("channels", "Sent by", "أُرسل عبر", "multi_select", "email, portal", false);
  await expectAccessible(page);

  // The order form carries them; the API refuses the order without the required one, under the field.
  await page.goto(`/purchasing/orders?open=${order.id}`);
  await page.getByTestId("edit-order").click();
  const fields = page.getByTestId("custom-fields-purchase_order");
  await expect(fields).toContainText("Delivery priority");
  await expect(fields).toContainText("Sent by");
  await page.getByTestId("save-order").click();
  await expect(fields.getByRole("alert")).toContainText("required");
  await page.getByTestId("cf-priority").selectOption("urgent");
  await fields.getByLabel("email").check();
  await fields.getByLabel("portal").check();
  await expectAccessible(page);
  await page.getByTestId("save-order").click();

  // The order shows what was chosen, and keeps it through another edit.
  const values = page.getByTestId("order-detail").getByTestId("custom-field-values");
  await expect(values.getByTestId("cf-value-priority")).toHaveText("urgent");
  await expect(values.getByTestId("cf-value-channels")).toHaveText("email, portal");
  await page.getByTestId("edit-order").click();
  await expect(page.getByTestId("cf-priority")).toHaveValue("urgent");
  await page.getByTestId("save-order").click();
  await expect(values.getByTestId("cf-value-channels")).toHaveText("email, portal");
  await expectAccessible(page);

  // The same on the item master: a shelf field defined for items is filled on the item and shown with it.
  await page.keyboard.press("Escape");
  await page.getByRole("navigation").getByRole("link", { name: "Custom fields", exact: true }).click();
  await page.getByTestId("custom-field-host").selectOption({ label: "Item" });
  await page.getByTestId("new-custom-field").click();
  await page.getByLabel(/^Key/).fill("shelf");
  await page.getByLabel(/Label \(English\)/).fill("Shelf");
  await page.getByLabel(/Label \(Arabic\)/).fill("الرف");
  await page.getByTestId("save-custom-field").click();
  await expect(page.getByRole("grid")).toContainText("shelf");
  await page.goto(`/inventory/items?open=${item.id}`);
  await page.getByTestId("edit-item").click();
  await page.getByTestId("cf-shelf").fill("A-12");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("cf-value-shelf")).toHaveText("A-12");
  await page.goto(`/purchasing/orders?open=${order.id}`);
  await expect(values.getByTestId("cf-value-priority")).toHaveText("urgent");

  // In Arabic the labels are the Arabic ones.
  await page.keyboard.press("Escape");
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.goto(`/purchasing/orders?open=${order.id}`);
  await expect(values).toContainText("أولوية التسليم");
  await expect(values).toContainText("أُرسل عبر");
  await expectAccessible(page);
});
