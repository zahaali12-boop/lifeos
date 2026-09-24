import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Access control against the real API: roles designed from the permission catalogue (a whole area, a template with
 * grants held back for modules still to come, a combination a blocking segregation rule refuses), a member given two
 * roles that trip a warning rule (acknowledged), one role limited to two companies, the conflict excepted on the
 * Security screen with a reason and the exception revoked, and a role taken away. English and Arabic, with axe on every
 * screen.
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
  // Signing up provisions a whole workspace (charts, roles, calendars, units), slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText(language === "en" ? "Welcome" : "أهلاً", { timeout: 20_000 });
}

/** Opens a new role, names it and ticks the given grants (their module is expanded first). */
async function newRole(page: Page, code: string, name: string, grants: string[]): Promise<void> {
  await page.getByTestId("new-role").click();
  await page.getByTestId("role-code").fill(code);
  await page.getByTestId("role-name-en").fill(name);
  for (const grant of grants) {
    const module = grant.split(".")[0] ?? grant;
    const section = page.getByTestId(`permission-module-${module}`);
    if (!(await section.evaluate((d) => (d as HTMLDetailsElement).open))) {
      await section.locator("summary").click();
    }
    await page.getByTestId(`grant-${grant}`).check();
  }
}

test("English: roles designed and assigned, a warning acknowledged, the conflict excepted, a role taken away", async ({ page }) => {
  await signup(page, "en");
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("MAIN");
  await page.getByLabel(/Legal name \(English\)/).fill("Main Trading Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("MAIN");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("SIDE");
  await page.getByLabel(/Legal name \(English\)/).fill("Side Trading Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("SIDE");

  // A counting role over the whole count area: every count permission is included and locked.
  await nav(page, "Roles");
  await newRole(page, "counter", "Stock counter", ["inventory.count.*"]);
  await expect(page.getByTestId("grant-inventory.count.approve")).toBeChecked();
  await expect(page.getByTestId("grant-inventory.count.approve")).toBeDisabled();
  await expectAccessible(page);
  await page.getByTestId("save-role").click();
  await expect(page.getByRole("grid")).toContainText("counter");

  await newRole(page, "poster", "Adjustment poster", ["inventory.adjustment.post"]);
  await page.getByTestId("save-role").click();
  await expect(page.getByRole("grid")).toContainText("poster");

  // Creating suppliers and paying them is a blocking pair: the editor says so as it is ticked, and the server refuses.
  await newRole(page, "supplier_payer", "Supplier payer", ["partners.supplier.manage", "banking.payment.post"]);
  await expect(page.getByTestId("role-conflicts")).toContainText("Block");
  await page.getByTestId("save-role").click();
  await expect(page.getByTestId("role-problem")).toContainText("partners.supplier.manage + banking.payment.post");
  await page.keyboard.press("Escape");

  // From a template: the grants that exist today are copied, those for modules still to come are named.
  await page.getByTestId("new-role").click();
  await page.getByTestId("role-template").selectOption({ label: "Accountant" });
  await expect(page.getByTestId("role-code")).toHaveValue("accountant_custom");
  await expect(page.getByTestId("template-pending")).toContainText("finance.*");
  await page.getByTestId("save-role").click();
  await expect(page.getByRole("grid")).toContainText("accountant_custom");

  // A member invited without roles gets both; the second trips the count-versus-adjustment warning.
  await nav(page, "Members");
  await page.getByTestId("invite-member").click();
  // People are global across workspaces: a fresh address makes a fresh person with the name given here.
  await page.getByLabel(/^Email/).fill(`clerk-${Date.now().toString(36)}@example.test`);
  await page.getByLabel(/^Name/).fill("Store Clerk");
  await page.getByRole("button", { name: "Send invitation" }).click();
  await page.getByRole("grid").getByText("Store Clerk").dblclick();
  const dialog = page.getByTestId("member-dialog");
  await expect(dialog).toContainText("No roles assigned");
  await page.getByTestId("assign-role-select").selectOption({ label: "counter · Stock counter" });
  const companies = page.getByTestId("assign-scopes-company");
  await companies.getByLabel("MAIN · Main Trading Co.").check();
  await companies.getByLabel("SIDE · Side Trading Co.").check();
  await expectAccessible(page);
  await page.getByTestId("save-assignment").click();
  await expect(page.getByTestId("assignment-row")).toHaveCount(1);
  await expect(page.getByTestId("assignment-row")).toContainText("Company: MAIN · Main Trading Co.; Company: SIDE · Side Trading Co.");
  await expect(companies.getByLabel("MAIN · Main Trading Co.")).not.toBeChecked();
  await page.getByTestId("assign-role-select").selectOption({ label: "poster · Adjustment poster" });
  await page.getByTestId("save-assignment").click();
  await expect(page.getByTestId("assignment-problem")).toContainText("inventory.count.approve");
  await expectAccessible(page);
  await page.getByTestId("assign-anyway").click();
  await expect(page.getByTestId("assignment-row")).toHaveCount(2);
  await expect(page.getByTestId("member-conflicts")).toContainText("Warn");
  await page.keyboard.press("Escape");

  // The Security screen lists the clerk's conflict; an exception is granted with a reason.
  await nav(page, "Security");
  const violation = page.getByTestId("sod-violation").filter({ hasText: "Store Clerk" });
  await expect(violation).toContainText("inventory.count.approve");
  await violation.getByTestId("grant-exception").click();
  await page.getByTestId("exception-reason").fill("Single-person store until the second clerk starts");
  await page.getByTestId("exception-ends").fill(`${new Date().getFullYear() + 1}-01-31`);
  await expectAccessible(page);
  await page.getByTestId("save-exception").click();
  await expect(violation).toContainText("Exception granted");
  const excepted = page.getByTestId("sod-exception-row").filter({ hasText: "Store Clerk" });
  await expect(excepted).toContainText("Single-person store until the second clerk starts");
  await expect(excepted.getByTestId("doc-status")).toHaveText("Active");

  // The second clerk has started: the exception is revoked with a reason, stays in the list, and the conflict counts again.
  await excepted.getByTestId("revoke-exception").click();
  await page.getByTestId("revoke-exception-reason").fill("Second clerk started");
  await expectAccessible(page);
  await page.getByTestId("confirm-revoke-exception").click();
  await expect(excepted.getByTestId("doc-status")).toHaveText("Revoked");
  await expect(excepted.getByTestId("sod-exception-revoked")).toContainText("Second clerk started");
  await expect(excepted.getByTestId("revoke-exception")).toHaveCount(0);
  await expect(violation.getByTestId("grant-exception")).toBeVisible();
  await expectAccessible(page);

  // Taking the posting role away ends the conflict.
  await nav(page, "Members");
  await page.getByRole("grid").getByText("Store Clerk").dblclick();
  await page.getByTestId("assignment-row").filter({ hasText: "poster" }).getByTestId("remove-assignment").click();
  await expect(page.getByTestId("assignment-row")).toHaveCount(1);
  await expect(page.getByTestId("member-conflicts")).toHaveCount(0);
});

test("Arabic: the role designer and the member dialog read right to left", async ({ page }) => {
  await signup(page, "ar");
  await nav(page, "الأدوار");
  await page.getByTestId("new-role").click();
  await expect(page.getByTestId("role-editor")).toContainText("البدء من قالب");
  await page.getByTestId("permission-module-inventory").locator("summary").click();
  await expect(page.getByTestId("permission-module-inventory")).toContainText("الوحدة كاملة");
  await expectAccessible(page);
  await page.keyboard.press("Escape");

  await nav(page, "الأعضاء");
  await page.getByRole("grid").getByRole("gridcell").filter({ hasText: /^Owner$/ }).first().dblclick();
  await expect(page.getByTestId("member-dialog")).toContainText("إسناد دور");
  await expectAccessible(page);
});
