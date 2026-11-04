import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The e-invoicing journey (roadmap 8.6, ZATCA phase 2): a Saudi company registered for VAT onboards a device from
 * the screen against the simulator (the certificate request, the compliance certificate with an OTP, the compliance
 * checks, the production certificate), a posted invoice to a VAT-registered customer is submitted from the invoice
 * screen and comes back cleared with its QR, the submission shows on the e-invoicing screen; then Arabic. Every screen
 * passes axe with no serious or critical violation.
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

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: a device onboarded against the simulator, an invoice cleared with its QR; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `zat-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => {
    if (!window.localStorage.getItem("quicker.language")) {
      window.localStorage.setItem("quicker.language", "en");
    }
  });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("ZATCA " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // The Saudi company, its chart, the VAT regime and registration, a registered customer and a posted invoice, through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const post = async <T,>(path: string, data: unknown): Promise<T> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await post<{ id: string }>("/api/v1/organization/companies", { code: "RYD", legalName: { en: "Riyadh Services", ar: "الرياض للخدمات" }, country: "SA", functionalCurrency: "SAR", timeZone: "Asia/Riyadh", costingMethod: "average", registrationNumbers: { crn: "1010010000" }, address: { street: "King Fahd Road", buildingNumber: "1234", district: "Al Olaya", city: "Riyadh", postalCode: "12211" } });
  await post("/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const regime = await post<{ regime: { id: string }; codes: { id: string; code: string }[] }>("/api/v1/tax/templates/SA-VAT/install", {});
  await post("/api/v1/tax/registrations", { companyId: company.id, regimeId: regime.regime.id, registrationNumber: "310123456700003", registeredFrom: "2018-01-01" });
  const groups = (await (await page.request.get(`${apiUrl}/api/v1/tax/groups`, { headers })).json()) as { id: string; kind: string; code: string }[];
  const domestic = groups.find((g) => g.kind === "partner" && g.code === "DOMESTIC")?.id ?? "";
  const customer = await post<{ id: string }>("/api/v1/partners", { code: "DAMMAM-CO", legalName: { en: "Dammam Co", ar: "شركة الدمام" }, isCustomer: true });
  await page.request.put(`${apiUrl}/api/v1/partners/${customer.id}/customer-accounts/${company.id}`, { headers, data: { currency: "SAR", taxGroupId: domestic } });
  await post(`/api/v1/partners/${customer.id}/tax-registrations`, { country: "SA", registrationType: "vat", number: "399999999900003" });
  await post(`/api/v1/partners/${customer.id}/addresses`, { role: "billing", country: "SA", isDefault: true, address: { line1: { en: "Prince Sultan Street", ar: "شارع الأمير سلطان" }, city: { en: "Dammam", ar: "الدمام" }, buildingNumber: "4321", district: "Al Faisaliyah", postalCode: "32241" } });
  const standard = regime.codes.find((c) => c.code === "SA-S")?.id ?? "";
  const draft = await post<{ id: string; number: string }>("/api/v1/sales/invoices", { companyId: company.id, partnerId: customer.id, documentDate: "2026-10-01", lines: [{ kind: "service", quantity: 1, unitPrice: 500, description: "Office cleaning", taxCodeId: standard }] });
  await post(`/api/v1/sales/invoices/${draft.id}/post`, {});

  // The device from the screen: created against the simulator, the compliance certificate with an OTP, the checks, production.
  // The company was created behind the screen's back, so the page is loaded afresh to read it.
  await page.goto("/tax/einvoicing");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("E-invoicing");
  await page.getByTestId("new-device").click();
  await page.getByTestId("device-name").fill("Riyadh POS 1");
  await page.getByTestId("device-industry").fill("Cleaning services");
  await expectAccessible(page);
  await page.getByTestId("save-device").click();
  await expect(page.getByTestId("device-detail")).toBeVisible();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Request ready");
  await expect(page.getByTestId("simulator-note")).toBeVisible();
  await page.getByTestId("request-compliance").click();
  await page.getByTestId("otp").fill("123456");
  await expectAccessible(page);
  await page.getByTestId("confirm-otp").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Compliance certificate");
  await page.getByTestId("run-checks").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Checks passed");
  await expect(page.getByTestId("compliance-check-row")).toHaveCount(6);
  await expectAccessible(page);
  await page.getByTestId("request-production").click();
  await expect(page.getByTestId("status-bar").getByTestId("doc-status")).toContainText("Live");
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Live");

  // The invoice submitted from its own screen: cleared, with the scheme's QR, and a link to the submission.
  await nav(page, "Sales invoices");
  await page.getByRole("grid").getByText(draft.number, { exact: true }).dblclick();
  await expect(page.getByTestId("sales-invoice-detail")).toBeVisible();
  await page.getByTestId("tab-einvoice").click();
  await expect(page.getByTestId("einvoice-panel")).toContainText("Pending");
  await page.getByTestId("submit-einvoice").click();
  await expect(page.getByTestId("einvoice-status")).toContainText("Cleared");
  await expect(page.getByTestId("einvoice-qr")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("open-submission").click();
  await expect(page.getByTestId("submission-detail")).toBeVisible();
  await expect(page.getByTestId("submission-detail")).toContainText(draft.number);
  await expect(page.getByTestId("submission-detail").getByTestId("doc-status")).toContainText("Cleared");
  await expect(page.getByTestId("download-cleared-xml")).toBeVisible();
  await expectAccessible(page);
  await closeDialog(page);
  await page.getByTestId("tab-submissions").click();
  await expect(page.getByRole("grid")).toContainText(draft.number);
  await expect(page.getByRole("grid")).toContainText("CHK-01");

  // Arabic, right-to-left.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الفوترة الإلكترونية");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الفوترة الإلكترونية");
  await expect(page.getByRole("grid")).toContainText("معتمدة");
  await page.getByTestId("tab-devices").click();
  await expect(page.getByRole("grid")).toContainText("فعّال");
  await expectAccessible(page);
});
