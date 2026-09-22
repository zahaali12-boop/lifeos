import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The admin journeys of slice 1.10 in English and Arabic (ADR-0029): create a workspace, create a company with a
 * custom field, invite a member, announce; every screen passes axe with no serious or critical violation; RTL is
 * verified from the document direction and the mirrored layout.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

/** Clicks an entry of the primary navigation (the dashboard also links to the same screens). */
async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation").getByRole("link", { name }).click();
}

async function signup(page: Page, language: "en" | "ar"): Promise<{ slug: string; email: string }> {
  const slug = `e2e-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;
  await page.addInitScript((lang) => { window.localStorage.setItem("quicker.language", lang); }, language);
  await page.goto("/signup");
  await page.getByLabel(language === "en" ? /Workspace name/ : /اسم مساحة العمل/).fill("E2E " + slug);
  await page.getByLabel(language === "en" ? /^Slug/ : /المعرّف/).fill(slug);
  await page.getByLabel(language === "en" ? /Your name/ : /اسمك/).fill("Owner");
  await page.getByLabel(language === "en" ? /^Email/ : /البريد/).fill(email);
  await page.getByLabel(language === "en" ? /^Password/ : /كلمة المرور/).fill(password);
  await page.getByRole("button", { name: language === "en" ? "Create workspace" : "إنشاء مساحة عمل" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText(language === "en" ? "Welcome" : "أهلاً");
  return { slug, email };
}

test("English: workspace, custom field, company, member invitation, announcement — all accessible", async ({ page }) => {
  await signup(page, "en");
  await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  await expectAccessible(page);

  // A custom field on companies.
  await nav(page, "Custom fields");
  await page.getByTestId("new-custom-field").click();
  await page.getByLabel(/^Key/).fill("region");
  await page.getByLabel(/Label \(English\)/).fill("Region");
  await page.getByLabel(/^Type/).selectOption("select");
  await page.getByLabel(/^Options/).fill("north, south");
  await page.getByLabel(/required/i).first().check();
  await page.getByTestId("save-custom-field").click();
  await expect(page.getByRole("grid")).toContainText("region");
  await expectAccessible(page);

  // A company: the required custom field is enforced by the API and mapped to the field.
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("MAIN");
  await page.getByLabel(/Legal name \(English\)/).fill("Main Trading Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("alert")).toContainText("required");
  await page.getByLabel("Region").selectOption("north");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("MAIN");
  await expectAccessible(page);

  // The command palette finds the company.
  await page.keyboard.press("ControlOrMeta+k");
  await page.getByPlaceholder(/Search/).fill("MA");
  await expect(page.getByRole("dialog")).toContainText("Main Trading Co.");
  await page.keyboard.press("Escape");

  // A member is invited; an announcement reaches the inbox.
  await nav(page, "Members");
  await page.getByTestId("invite-member").click();
  await page.getByLabel(/^Email/).fill("clerk@example.test");
  await page.getByRole("button", { name: "Send invitation" }).click();
  await expect(page.getByRole("grid")).toContainText("clerk@example.test");
  await nav(page, "Notifications");
  await page.getByTestId("announce").click();
  await page.getByLabel(/Title \(English\)/).fill("Welcome aboard");
  await page.getByTestId("send-announcement").click();
  await expect(page.getByTestId("inbox")).toContainText("Welcome aboard");
  await expectAccessible(page);

  // Keyboard shortcuts overlay.
  await page.keyboard.press("?");
  await expect(page.getByRole("dialog")).toContainText("Keyboard shortcuts");
});

test("Arabic: the same journey renders right-to-left and stays accessible", async ({ page }) => {
  await signup(page, "ar");
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  await expectAccessible(page);

  // The sidebar sits on the right in RTL: its box starts past the middle of the viewport.
  const sidebar = page.getByRole("complementary");
  const box = await sidebar.boundingBox();
  const viewport = page.viewportSize();
  expect(box && viewport && box.x > viewport.width / 2).toBeTruthy();

  await nav(page, "الشركات");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^الرمز/).fill("RTL1");
  await page.getByLabel(/الاسم القانوني \(الإنجليزية\)/).fill("RTL Company");
  await page.getByLabel(/الاسم القانوني \(العربية\)/).fill("شركة الاختبار");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("شركة الاختبار");
  await expectAccessible(page);

  // Eastern Arabic digits when chosen.
  await page.getByTestId("language-menu").click();
  await page.getByRole("menuitemradio", { name: /٠١٢٣/ }).click();
  await nav(page, "لوحة المتابعة");
  await expect(page.getByRole("main")).toContainText("١");
});
