import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The setup wizard (founder programme item 1g, A-173): a fresh workspace is nudged from the home page into the eight
 * steps; the company, its chart, a cash box, customers and suppliers, items and the opening balances are set up from
 * the wizard itself, imports checked before they are kept; the opening journal opens the customer's receivable; the
 * readiness list ends green. Then the wizard in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function signup(page: Page, language: "en" | "ar"): Promise<string> {
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
  return slug;
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByTestId("apps-menu").click();
  await page.getByTestId("apps-panel").getByRole("link", { name, exact: true }).click();
}

test("English: from an empty workspace to posted opening balances through the wizard; then the wizard in Arabic", async ({ page }) => {
  test.setTimeout(150_000);
  await signup(page, "en");

  // The home page nudges a new workspace into the wizard: nothing is done yet, the company comes first.
  await expect(page.getByTestId("setup-card")).toContainText("0 of 8 steps");
  await expect(page.getByTestId("setup-card")).toContainText("Company");
  await page.getByTestId("setup-card-continue").click();
  await expect(page.getByTestId("setup-progress")).toHaveText("0 of 8 steps done");
  await expect(page.getByTestId("setup-step-company")).not.toHaveAttribute("data-done");
  await expectAccessible(page);

  // 1. The company.
  await page.getByTestId("setup-company-code").fill("GO");
  await page.getByTestId("setup-company-name-en").fill("Go Live Trading");
  await page.getByTestId("setup-company-name-ar").fill("شركة الانطلاق للتجارة");
  await page.getByTestId("setup-company-save").click();
  await expect(page.getByTestId("setup-status-company")).toHaveText("Done");
  // A step finished here stays open with what it made in view; the next step opens beneath it.
  await expect(page.getByTestId("setup-companies")).toContainText("GO · Go Live Trading · IQD");

  // 2. The chart from the IFRS for SMEs template; the next step opens by itself.
  await expect(page.getByTestId("setup-chart-template")).toBeVisible();
  await page.getByTestId("setup-chart-create").click();
  await expect(page.getByTestId("setup-status-chart")).toHaveText("Done");
  await expect(page.getByTestId("setup-chart-done")).toBeVisible();

  // 3. Not registered for tax: the step is skipped and counts as done.
  await page.getByTestId("setup-tax-skip").click();
  await expect(page.getByTestId("setup-status-tax")).toHaveText("Done");

  // 4. A cash box on the cash control account.
  await page.getByTestId("setup-bank-save").click();
  await expect(page.getByTestId("setup-status-bank")).toHaveText("Done");
  await expect(page.getByTestId("setup-bank-list")).toContainText("CASH · Main cash box · IQD · Cash");
  await expectAccessible(page);

  // 5. Customers and suppliers pasted as rows: checked first (nothing kept), then imported with their company accounts.
  const partners = "code,name_en,name_ar,kind,customer,supplier,email,phone,currency,payment_terms,credit_limit\n"
    + "CUS-001,Alpha Markets,أسواق ألفا,organization,yes,no,alpha@example.test,,IQD,,5000000\n"
    + "SUP-001,Beta Supplies,بيتا للتوريدات,organization,no,yes,,,USD,,\n";
  await page.getByTestId("setup-partners-text").fill(partners);
  await page.getByTestId("setup-partners-check").click();
  await expect(page.getByTestId("setup-partners-result")).toContainText("Checked: 2 to create, 0 to update, 2 company accounts");
  await expect(page.getByTestId("setup-partners-count")).toContainText("0 customers and 0 suppliers");
  await page.getByTestId("setup-partners-run").click();
  await expect(page.getByTestId("setup-partners-result")).toContainText("Imported: 2 created, 0 updated, 2 company accounts");
  await expect(page.getByTestId("setup-status-partners")).toHaveText("Done");
  await expect(page.getByTestId("setup-partners-count")).toContainText("1 customer and 1 supplier");

  // 6. Items, the same way; a wrong row refuses the whole file and names itself.
  await page.getByTestId("setup-items-text").fill("code,name_en,name_ar,type,base_uom,list_price,list_price_currency\nTEA-500,Tea 500g,شاي ٥٠٠غ,stock,PCS,2500,IQD\nBAD,No unit,بلا وحدة,stock,NOPE,1,IQD\n");
  await page.getByTestId("setup-items-check").click();
  await expect(page.getByTestId("setup-items-import").getByRole("alert")).toBeVisible();
  await expect(page.getByTestId("setup-status-items")).toHaveText("To do");
  await page.getByTestId("setup-items-text").fill("code,name_en,name_ar,type,base_uom,list_price,list_price_currency\nTEA-500,Tea 500g,شاي ٥٠٠غ,stock,PCS,2500,IQD\nRICE-5,Rice 5kg,رز ٥ كغ,stock,PCS,12000,IQD\n");
  await page.getByTestId("setup-items-run").click();
  await expect(page.getByTestId("setup-items-result")).toContainText("Imported: 2 created, 0 updated");
  await expect(page.getByTestId("setup-status-items")).toHaveText("Done");

  // 7. Opening balances as of a day: cash in the box, the customer's open invoice, capital; 300,000 short, which the
  // check reports before anything is posted; the import posts the opening journal.
  await page.getByTestId("setup-opening-asof").fill("2026-09-30");
  await page.getByTestId("setup-opening-text").fill("account,debit,credit,partner,bank_account,due_date,description\n1111,1000000,,,CASH,,Cash count\n1210,500000,,CUS-001,,2026-10-15,Invoice 2026-114\n3100,,1200000,,,,Capital\n");
  await page.getByTestId("setup-opening-check").click();
  await expect(page.getByTestId("setup-opening-result")).toContainText("Checked: 3 lines.");
  await expect(page.getByTestId("setup-opening-result")).toContainText("IQD 300,000.00 goes to opening balance equity");
  await expect(page.getByTestId("setup-status-opening")).toHaveText("To do");
  await page.getByTestId("setup-opening-run").click();
  await expect(page.getByTestId("setup-opening-result")).toContainText("Posted as MJ-");
  await expect(page.getByTestId("setup-status-opening")).toHaveText("Done");

  // 8. Ready: every step green, and the customer's opening receivable is a live open item.
  await expect(page.getByTestId("setup-status-ready")).toHaveText("Done");
  await expect(page.getByTestId("setup-progress")).toHaveText("8 of 8 steps done");
  await expect(page.getByTestId("setup-checklist")).toContainText("Opening balances");
  await expectAccessible(page);
  await nav(page, "Receivables");
  await expect(page.getByRole("grid")).toContainText("CUS-001");
  await expect(page.getByRole("grid")).toContainText("500,000");
  await page.goto("/");
  await expect(page.getByTestId("setup-card")).toHaveCount(0);

  // Arabic: the wizard reads right-to-left with every step done.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "معالج الإعداد");
  await expect(page.getByTestId("setup-progress")).toHaveText("اكتملت 8 من 8 خطوات");
  await expect(page.getByTestId("setup-status-opening")).toHaveText("تم");
  await page.getByTestId("setup-toggle-opening").click();
  await expect(page.getByTestId("setup-opening-import")).toBeVisible();
  await expectAccessible(page);
});
