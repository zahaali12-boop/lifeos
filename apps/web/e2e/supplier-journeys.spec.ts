import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The supplier master journey of 4.1: payment terms with instalments previewed to the minor unit, a supplier group
 * and a withholding code in the purchasing settings; then a supplier registered for the company with the group's
 * terms, a contact, an encrypted bank account shown masked and revealed on request, a tax registration, a purchasing
 * hold placed and released; then the screens in Arabic, right-to-left. Every screen passes axe with no serious or
 * critical violation.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation").getByRole("link", { name, exact: true }).click();
}

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: purchasing settings, then a supplier with terms, contact, encrypted bank account, registration and a hold", async ({ page }) => {
  const slug = `sup-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Suppliers " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("PUR");
  await page.getByLabel(/Legal name \(English\)/).fill("Purchasing Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("PUR");

  // Purchasing settings: the system terms are there; payment terms with two instalments preview 100.01 as 50.01 + 50.00.
  await nav(page, "Purchasing settings");
  await expect(page.getByRole("grid")).toContainText("NET30");
  await expectAccessible(page);
  await page.getByTestId("new-setting").click();
  await page.getByTestId("terms-code").fill("HALF");
  await page.getByTestId("terms-name").fill("Half now, half in 30 days");
  await page.getByTestId("add-instalment").click();
  await page.getByTestId("instalment-pct-0").fill("50");
  await page.getByTestId("instalment-days-0").fill("0");
  await page.getByTestId("add-instalment").click();
  await page.getByTestId("instalment-pct-1").fill("50");
  await page.getByTestId("instalment-days-1").fill("30");
  await expectAccessible(page);
  await page.getByTestId("save-terms").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByRole("grid")).toContainText("HALF");
  await page.getByRole("grid").getByText("HALF").first().dblclick();
  await expect(page.getByTestId("schedule-preview")).toBeVisible();
  await page.getByTestId("preview-date").fill("2026-09-10");
  await page.getByTestId("preview-amount").fill("100.01");
  await page.getByTestId("preview-schedule").click();
  await expect(page.getByTestId("schedule-lines")).toContainText("50.01");
  await expect(page.getByTestId("schedule-lines")).toContainText("50.00");
  await closeDialog(page);

  // A supplier group defaulting to NET60 and CIF, and a withholding code.
  await page.getByTestId("tab-groups").click();
  await page.getByTestId("new-setting").click();
  await page.getByTestId("setting-code").fill("IMPORTERS");
  await page.getByTestId("setting-name").fill("Importers");
  await page.getByTestId("group-payment-terms").selectOption({ label: "NET60" });
  await page.getByTestId("group-delivery-terms").selectOption({ label: "CIF" });
  await page.getByTestId("save-setting").click();
  await expect(page.getByRole("grid")).toContainText("IMPORTERS");
  await page.getByTestId("tab-wht").click();
  await page.getByTestId("new-setting").click();
  await page.getByTestId("setting-code").fill("CONTRACT");
  await page.getByTestId("setting-name").fill("Contractor withholding");
  await page.getByTestId("wht-rate").fill("3.3");
  await page.getByTestId("save-setting").click();
  await expect(page.getByRole("grid")).toContainText("CONTRACT");
  await expectAccessible(page);

  // The supplier: registered for the company in the group, so its effective terms are the group's.
  await nav(page, "Suppliers");
  await expect(page.getByText("No suppliers yet")).toBeVisible();
  await page.getByTestId("new-supplier").click();
  await page.getByTestId("partner-code").fill("ACME");
  await page.getByTestId("partner-legal-name-en").fill("ACME Trading LLC");
  await page.getByTestId("partner-legal-name-ar").fill("شركة أكمي للتجارة");
  await page.getByTestId("partner-email").fill("hello@acme.example");
  await page.getByTestId("account-group").selectOption({ label: "IMPORTERS · Importers" });
  await page.getByTestId("account-lead-time").fill("21");
  await expectAccessible(page);
  await page.getByTestId("save-supplier").click();
  await expect(page.getByTestId("supplier-detail")).toBeVisible();
  await expect(page.getByTestId("effective-terms")).toContainText("NET60");
  await expect(page.getByTestId("effective-terms")).toContainText("CIF");

  // A contact, an encrypted bank account (masked, then revealed on request), a tax registration.
  await page.getByTestId("tab-contacts").click();
  await page.getByTestId("contact-name").fill("Ali Hassan");
  await page.getByTestId("contact-email").fill("ali@acme.example");
  await page.getByTestId("add-contact").click();
  await expect(page.getByTestId("contact-row")).toHaveCount(1);
  await page.getByTestId("tab-bank").click();
  await page.getByTestId("bank-name").fill("Trade Bank of Iraq");
  await page.getByTestId("bank-currency").fill("USD");
  await page.getByTestId("bank-iban").fill("GB82 WEST 1234 5698 7654 32");
  await page.getByTestId("add-bank").click();
  await expect(page.getByTestId("bank-row")).toHaveCount(1);
  await expect(page.getByTestId("bank-row")).toContainText("GB****************5432");
  await page.getByTestId("reveal-bank").click();
  await expect(page.getByTestId("bank-row")).toContainText("GB82WEST12345698765432");
  await expectAccessible(page);
  await page.getByTestId("tab-tax").click();
  await page.getByTestId("registration-country").fill("IQ");
  await page.getByTestId("registration-number").fill("12345678");
  await page.getByTestId("add-registration").click();
  await expect(page.getByTestId("registration-row")).toHaveCount(1);

  // A purchasing hold with its reason, then released.
  await page.getByTestId("tab-account").click();
  await page.getByTestId("hold-status").selectOption("purchase");
  await page.getByTestId("hold-reason").fill("Quality claim open");
  await page.getByTestId("hold-supplier").click();
  await expect(page.getByTestId("supplier-detail").getByTestId("hold-badge")).toContainText("Purchasing");
  await expect(page.getByTestId("effective-terms")).toContainText("Quality claim open");
  await page.getByTestId("release-supplier").click();
  await expect(page.getByTestId("supplier-detail").getByTestId("hold-badge")).toHaveCount(0);
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("ACME");
  await expect(page.getByRole("grid")).toContainText("NET60");
  await expectAccessible(page);

  // Arabic: the suppliers and settings screens render right-to-left and stay accessible.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "الموردون");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الموردون");
  await expect(page.getByRole("grid")).toContainText("شركة أكمي للتجارة");
  await expectAccessible(page);
  await nav(page, "إعدادات المشتريات");
  await expect(page.getByRole("grid")).toContainText("صافي ٣٠ يومًا");
  await expectAccessible(page);
});
