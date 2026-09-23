import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The workspace set-up screens against the real API: units of measure and conversions, fiscal and business calendars,
 * company settings, and security (segregation-of-duties rules, API keys, single sign-on, the sign-in policy), with
 * the step-up prompt that sensitive actions raise once the sign-in is no longer recent. English and Arabic; every
 * screen passes axe with no serious or critical violation.
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

async function signup(page: Page, language: "en" | "ar"): Promise<void> {
  const slug = `e2e-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript((lang) => { window.localStorage.setItem("quicker.language", lang); }, language);
  await page.goto("/signup");
  await page.getByLabel(language === "en" ? /Workspace name/ : /اسم مساحة العمل/).fill("E2E " + slug);
  await page.getByLabel(language === "en" ? /^Slug/ : /المعرّف/).fill(slug);
  await page.getByLabel(language === "en" ? /Your name/ : /اسمك/).fill("Owner");
  await page.getByLabel(language === "en" ? /^Email/ : /البريد/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(language === "en" ? /^Password/ : /كلمة المرور/).fill(password);
  await page.getByRole("button", { name: language === "en" ? "Create workspace" : "إنشاء مساحة عمل" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText(language === "en" ? "Welcome" : "أهلاً");
}

async function createCompany(page: Page): Promise<void> {
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("MAIN");
  await page.getByLabel(/Legal name \(English\)/).fill("Main Trading Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("MAIN");
}

test("English: units and conversions, calendars, company settings", async ({ page }) => {
  await signup(page, "en");
  await createCompany(page);

  // Units: a custom unit, a conversion within its family, and the converter using it.
  await nav(page, "Units of measure");
  await expect(page.getByRole("grid")).toContainText("PCS");
  await page.getByTestId("new-unit").click();
  await page.getByTestId("unit-code").fill("gross");
  await expect(page.getByTestId("unit-code")).toHaveValue("GROSS");
  await page.getByTestId("unit-family").selectOption("count");
  await page.getByTestId("unit-name-en").fill("Gross");
  await page.getByTestId("unit-name-ar").fill("غروس");
  await page.getByTestId("save-unit-master").click();
  await expect(page.getByRole("grid")).toContainText("GROSS");
  await expectAccessible(page);

  await page.getByTestId("new-conversion").click();
  await page.getByTestId("conversion-from").selectOption({ label: "GROSS · Gross" });
  // Only units of the same family are offered.
  await expect(page.getByTestId("conversion-to").locator("option", { hasText: "KG" })).toHaveCount(0);
  await page.getByTestId("conversion-to").selectOption({ label: "PCS · Piece" });
  await page.getByTestId("conversion-numerator").fill("144");
  await page.getByTestId("save-conversion").click();
  await expect(page.getByTestId("conversion-row").filter({ hasText: "GROSS" })).toContainText("1 GROSS = 144 PCS");
  await page.getByTestId("convert-value").fill("2");
  await page.getByTestId("convert-from").selectOption({ label: "GROSS" });
  await page.getByTestId("convert-to").selectOption({ label: "DZ" });
  await expect(page.getByTestId("convert-result")).toContainText("2 GROSS = 24 DZ");
  await expectAccessible(page);

  // Fiscal calendars: a July-to-June calendar and its first year, open for postings.
  await nav(page, "Calendars");
  await page.getByTestId("new-fiscal-calendar").click();
  await page.getByTestId("fiscal-code").fill("JULY");
  await expect(page.getByTestId("fiscal-code")).toHaveValue("july");
  await page.getByTestId("fiscal-start-month").selectOption("7");
  await page.getByTestId("fiscal-name-en").fill("July to June");
  await page.getByTestId("save-fiscal-calendar").click();
  await expect(page.getByTestId("fiscal-calendars")).toContainText("Years start in July");
  await expect(page.getByTestId("fiscal-calendars")).toContainText("12 periods a year");
  await page.getByTestId("open-next-year").click();
  await expectAccessible(page);
  await page.getByTestId("confirm-add-year").click();
  await expect(page.getByTestId("fiscal-year-row")).toHaveCount(1);
  await expect(page.getByTestId("fiscal-year-row")).toContainText("12");
  await expect(page.getByTestId("fiscal-year-row")).toContainText("Open");
  await expectAccessible(page);

  // Business calendars: system calendars are read-only; a new one takes a six-day week and a holiday.
  await page.getByTestId("tab-business").click();
  await expect(page.getByTestId("working-day-0")).toBeDisabled();
  await page.getByTestId("new-business-calendar").click();
  await page.getByTestId("business-code").fill("six_day");
  await page.getByTestId("business-name-en").fill("Six-day week");
  await page.getByTestId("save-business-calendar").click();
  await expect(page.getByTestId("business-select")).toContainText("six_day");
  await expect(page.getByTestId("working-day-0")).toBeChecked();
  await expect(page.getByTestId("working-day-6")).not.toBeChecked();
  await page.getByTestId("working-day-6").check();
  await page.getByTestId("save-working-days").click();
  await expect(page.getByTestId("save-working-days")).toHaveCount(0);
  await expect(page.getByTestId("working-day-6")).toBeChecked();
  await page.getByTestId("add-holiday").click();
  await page.getByTestId("holiday-date").fill(`${new Date().getFullYear()}-12-25`);
  await page.getByTestId("holiday-name-en").fill("Christmas");
  await page.getByTestId("holiday-name-ar").fill("عيد الميلاد");
  await page.getByTestId("save-holiday").click();
  await expect(page.getByTestId("holiday-row")).toHaveCount(1);
  await expectAccessible(page);
  await page.getByRole("button", { name: "Remove Christmas" }).click();
  await expect(page.getByTestId("holiday-row")).toHaveCount(0);

  // Settings: manual journals need approval for this company; the store shows the value.
  await nav(page, "Settings");
  await page.getByTestId("setting-accounting.journals.approval").selectOption("required");
  await expect(page.getByTestId("setting-row").filter({ hasText: "accounting.journals.approval" })).toContainText("required");
  await page.getByTestId("new-setting").click();
  await page.getByTestId("setting-key").fill("reports.default.basis");
  await page.getByTestId("setting-value").fill("fc");
  await page.getByTestId("save-setting").click();
  await expect(page.getByTestId("setting-row")).toHaveCount(2);
  await page.getByTestId("tab-workspace-settings").click();
  await expect(page.getByTestId("known-settings")).toHaveCount(0);
  await expectAccessible(page);
});

test("English: segregation-of-duties rule, API key shown once and revoked, SSO connection, sign-in policy", async ({ page }) => {
  await signup(page, "en");

  await nav(page, "Security");
  await expect(page.getByTestId("sod-rule-row")).not.toHaveCount(0);
  await expect(page.getByTestId("sod-clean")).toBeVisible();
  // The owner holds every permission, so the report lists them apart from segregation of duties.
  await expect(page.getByTestId("sod-superusers")).toContainText("Owner");
  await page.getByTestId("new-sod-rule").click();
  await page.getByTestId("sod-permission-a").fill("purchasing.receipt.post");
  await page.getByTestId("sod-permission-b").fill("banking.bank_account.manage");
  await page.getByTestId("sod-severity").selectOption("block");
  await page.getByTestId("sod-rationale-en").fill("Receiving goods and managing bank accounts must be separated.");
  await expectAccessible(page);
  await page.getByTestId("save-sod-rule").click();
  await expect(page.getByTestId("sod-rule-row").filter({ hasText: "purchasing.receipt.post" })).toContainText("Block");
  await expectAccessible(page);

  // API keys: the secret is shown once; the list keeps only its prefix; revoking marks it.
  await page.getByTestId("tab-keys").click();
  await page.getByTestId("new-api-key").click();
  await page.getByTestId("key-name").fill("Warehouse bridge");
  await page.getByTestId("key-scopes").fill("inventory.item.read, inventory.stock.read");
  await page.getByTestId("create-key").click();
  await expect(page.getByTestId("created-key")).toContainText("Warehouse bridge");
  const secret = (await page.getByTestId("created-key-value").textContent()) ?? "";
  expect(secret).toMatch(/^qk_/);
  const row = page.getByTestId("api-key-row").filter({ hasText: "Warehouse bridge" });
  await expect(row).toContainText("inventory.item.read, inventory.stock.read");
  await expect(row).not.toContainText(secret);
  await expectAccessible(page);
  await row.getByTestId("revoke-key").click();
  await expect(row.getByTestId("doc-status")).toHaveText("Revoked");
  await expect(row.getByTestId("revoke-key")).toHaveCount(0);

  // Single sign-on: a connection, then edited; the stored secret is kept when left empty.
  await page.getByTestId("tab-sso").click();
  await page.getByTestId("new-sso").click();
  await page.getByTestId("sso-code").fill("corp");
  await page.getByTestId("sso-display-name").fill("Corporate sign-in");
  await page.getByTestId("sso-authority").fill("https://login.example.test");
  await page.getByTestId("sso-client-id").fill("quicker-erp");
  await page.getByTestId("sso-client-secret").fill("s3cret-value");
  await page.getByTestId("sso-domains").fill("example.test");
  await expectAccessible(page);
  await page.getByTestId("save-sso").click();
  await expect(page.getByTestId("sso-row")).toContainText("Corporate sign-in");
  await page.getByTestId("sso-row").getByRole("button", { name: "Edit" }).click();
  await expect(page.getByRole("dialog")).toContainText("Leave empty to keep the stored secret.");
  await closeDialog(page);

  // Sign-in policy: with an active connection password sign-in may be turned off; the step-up window is shortened.
  await page.getByTestId("tab-policy").click();
  await expect(page.getByTestId("policy-stepUpWindowMinutes")).toHaveValue("5");
  await expect(page.getByTestId("policy-allowPasswordLogin")).toBeEnabled();
  await page.getByTestId("policy-passwordMinLength").fill("14");
  await page.getByTestId("save-policy").click();
  await expect(page.getByTestId("save-policy")).toBeDisabled();
  await expect(page.getByTestId("policy-passwordMinLength")).toHaveValue("14");
  await expectAccessible(page);
});

test("English: a sensitive action after the step-up window asks to confirm identity and then completes", async ({ page }) => {
  test.slow();
  await signup(page, "en");
  await nav(page, "Security");
  await page.getByTestId("tab-policy").click();
  await page.getByTestId("policy-stepUpWindowMinutes").fill("1");
  await page.getByTestId("save-policy").click();
  await expect(page.getByTestId("save-policy")).toBeDisabled();

  // Past the one-minute window, creating a key asks for the password once, then goes through.
  await page.waitForTimeout(65_000);
  await page.getByTestId("tab-keys").click();
  await page.getByTestId("new-api-key").click();
  await page.getByTestId("key-name").fill("Late key");
  await page.getByTestId("create-key").click();
  const stepUp = page.getByTestId("step-up");
  await expect(stepUp).toContainText("Confirm it's you");
  await expectAccessible(page);
  await page.getByTestId("step-up-secret").fill("not-the-password");
  await page.getByTestId("step-up-confirm").click();
  await expect(stepUp).toContainText("The password is not valid.");
  await page.getByTestId("step-up-secret").fill(password);
  await page.getByTestId("step-up-confirm").click();
  await expect(stepUp).toHaveCount(0);
  await expect(page.getByTestId("created-key")).toContainText("Late key");
});

test("Arabic: the set-up screens read right to left and stay accessible", async ({ page }) => {
  await signup(page, "ar");
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

  await nav(page, "وحدات القياس");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("وحدات القياس");
  await expect(page.getByRole("grid")).toContainText("قطعة");
  await expectAccessible(page);

  await nav(page, "التقاويم");
  await expect(page.getByTestId("fiscal-calendars")).toContainText("تبدأ السنوات في");
  await page.getByTestId("tab-business").click();
  await expect(page.getByTestId("business-calendars")).toContainText("الأحد");
  await expectAccessible(page);

  await nav(page, "الإعدادات");
  await expect(page.getByTestId("tab-company-settings")).toHaveText("الشركة");
  await expectAccessible(page);

  await nav(page, "الأمان");
  await expect(page.getByTestId("sod-rule-row").first()).toContainText("منع");
  await page.getByTestId("tab-policy").click();
  await expect(page.getByTestId("policy")).toContainText("الحد الأدنى لطول كلمة المرور");
  await expectAccessible(page);
});
