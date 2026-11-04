import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The print and send journey (roadmap 5.9, founder programme item 4): a posted invoice previewed from its template,
 * downloaded as the PDF the worker makes, emailed with it attached, shared by WhatsApp through a link that opens
 * without signing in (counted, then revoked); the customer's statement printed from receivables; a shipped template
 * edited into a new version, previewed with sample data and rolled back; then the screens in Arabic, right-to-left.
 * Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  // The previews are the templates' own HTML in sandboxed frames, not the app's screen: axe checks the screen around them.
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).exclude("iframe").analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByTestId("apps-menu").click();
  await page.getByTestId("apps-panel").getByRole("link", { name, exact: true }).click();
}

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: an invoice previewed, downloaded, emailed and shared by WhatsApp, a statement printed, a template versioned; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `prt-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => {
    // English to start with; the Arabic chosen later in the journey survives the full page loads that follow.
    if (!window.localStorage.getItem("quicker.language")) {
      window.localStorage.setItem("quicker.language", "en");
    }
    // WhatsApp opens in a new window; the journey records what would have opened instead of leaving the app.
    window.open = (url?: string | URL): null => { (window as unknown as { __opened?: string }).__opened = String(url); return null; };
  });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Print " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("PRT");
  await page.getByLabel(/Legal name \(English\)/).fill("Print Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("PRT");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // A customer with an email and a WhatsApp number, and a posted service invoice, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "PRT")?.id ?? "";
  const partner = (await (await page.request.post(`${apiUrl}/api/v1/partners`, { headers, data: { code: "WADI", legalName: { en: "Wadi Trading", ar: "وادي للتجارة" }, isCustomer: true, email: "ap@wadi.example", phone: "+964 770 123 4567" } })).json()) as { id: string };
  await page.request.put(`${apiUrl}/api/v1/partners/${partner.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } });
  const invoice = (await (await page.request.post(`${apiUrl}/api/v1/sales/invoices`, { headers, data: { companyId, partnerId: partner.id, customerReference: "PO-9", lines: [{ kind: "service", quantity: 1, unitPrice: 150000, description: "Consulting day", accountRole: "Revenue" }] } })).json()) as { id: string; number: string };
  expect((await page.request.post(`${apiUrl}/api/v1/sales/invoices/${invoice.id}/post`, { headers, data: {} })).ok()).toBeTruthy();

  // The invoice's print dialog: the preview from the template, then the PDF downloaded.
  await page.goto(`/sales/invoices?open=${invoice.id}`);
  await expect(page.getByTestId("sales-invoice-detail")).toBeVisible();
  await page.getByTestId("print-document").click();
  await expect(page.getByTestId("print-dialog")).toBeVisible();
  const preview = page.frameLocator('[data-testid="print-preview"]');
  await expect(preview.locator("body")).toContainText(invoice.number);
  await expect(preview.locator("body")).toContainText("Wadi Trading");
  await expectAccessible(page);
  const [download] = await Promise.all([page.waitForEvent("download"), page.getByTestId("download-pdf").click()]);
  expect(download.suggestedFilename()).toBe(`${invoice.number}.pdf`);

  // Emailed to the customer's address, the send logged and reported sent once the worker has run.
  await page.getByTestId("print-tab-email").click();
  await expect(page.getByTestId("email-to")).toHaveValue("ap@wadi.example");
  await page.getByTestId("email-message").fill("Attached is your invoice.");
  await page.getByTestId("send-email").click();
  await expect(page.getByTestId("print-email-row")).toHaveCount(1);
  await expect(page.getByTestId("print-email-row")).toContainText("Sent", { timeout: 30_000 });

  // WhatsApp: the number prefilled, a link made to the PDF, WhatsApp opened with it; the link works for anyone.
  await page.getByTestId("print-tab-whatsapp").click();
  await expect(page.getByTestId("whatsapp-phone")).toHaveValue("+964 770 123 4567");
  await page.getByTestId("share-whatsapp").click();
  await expect(page.getByTestId("whatsapp-made")).toBeVisible({ timeout: 30_000 });
  const href = (await page.getByTestId("whatsapp-link").getAttribute("href")) ?? "";
  expect(href).toContain("https://wa.me/9647701234567?text=");
  expect(await page.evaluate(() => (window as unknown as { __opened?: string }).__opened)).toBe(href);
  const shareUrl = (await page.getByTestId("share-url").textContent()) ?? "";
  expect(shareUrl).toContain("/api/v1/public/prints/");
  expect(decodeURIComponent(href)).toContain(shareUrl);
  const anonymous = await page.request.get(shareUrl);
  expect(anonymous.status()).toBe(200);
  expect(anonymous.headers()["content-type"]).toContain("application/pdf");

  // The history: the PDF, the link opened once, then revoked and dead.
  await page.getByTestId("print-tab-history").click();
  await expect(page.getByTestId("print-render-row")).toHaveCount(1);
  await expect(page.getByTestId("print-share-row")).toHaveCount(1);
  await expect(page.getByTestId("share-opens")).toContainText("1", { timeout: 15_000 });
  await expectAccessible(page);
  await page.getByTestId("revoke-share").click();
  await expect(page.getByTestId("print-share-row")).toContainText("Revoked");
  expect((await page.request.get(shareUrl)).status()).toBe(404);
  await closeDialog(page);

  // The customer's statement prints from receivables with the same dialog.
  await nav(page, "Receivables");
  await page.getByTestId("customer-filter").selectOption(partner.id);
  await page.getByTestId("tab-statement").click();
  await page.getByTestId("statement-from").fill("2026-01-01");
  await page.getByTestId("print-statement").click();
  const statement = page.frameLocator('[data-testid="print-preview"]');
  await expect(statement.locator("body")).toContainText("Statement of account");
  await expect(statement.locator("body")).toContainText(invoice.number);
  await closeDialog(page);

  // Templates: the shipped tax invoice edited into version 2, previewed with sample data, rolled back to version 1 as version 3.
  await nav(page, "Print templates");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Print templates");
  await expect(page.getByRole("grid")).toContainText("Tax invoice");
  await expectAccessible(page);
  await page.getByRole("grid").getByText("Tax invoice", { exact: true }).dblclick();
  await expect(page.getByTestId("template-detail")).toBeVisible();
  await expect(page.getByTestId("template-detail")).toContainText("v1");
  await expect(page.getByTestId("template-html")).not.toHaveValue("");
  const css = await page.getByTestId("template-css").inputValue();
  await page.getByTestId("template-css").fill(`${css}\n.journey { color: red; }`);
  await page.getByTestId("template-note").fill("Journey tweak");
  await page.getByTestId("save-template").click();
  await expect(page.getByTestId("template-detail")).toContainText("v2");
  await page.getByTestId("template-tab-preview").click();
  await expect(page.frameLocator('[data-testid="template-preview"]').locator("body")).toContainText("Tax invoice");
  await page.getByTestId("template-tab-versions").click();
  await expect(page.getByTestId("template-version-row")).toHaveCount(2);
  await expect(page.getByTestId("template-version-row").first()).toContainText("Journey tweak");
  await page.getByTestId("rollback-1").click();
  await expect(page.getByTestId("template-detail")).toContainText("v3");
  await expect(page.getByTestId("template-version-row")).toHaveCount(3);
  await expectAccessible(page);
  await closeDialog(page);

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "قوالب الطباعة");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("قوالب الطباعة");
  await expect(page.getByRole("grid")).toContainText("فاتورة ضريبية");
  await expectAccessible(page);
  await page.goto(`/sales/invoices?open=${invoice.id}`);
  await page.getByTestId("print-document").click();
  await expect(page.getByTestId("print-dialog")).toContainText("طباعة وإرسال");
  await expectAccessible(page);
});
