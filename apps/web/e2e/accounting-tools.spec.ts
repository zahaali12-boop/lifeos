import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";

/**
 * The chart and journal tools against the real API: the chart exported as CSV and read back with a new account, a
 * wrong file refused as a whole, accounts mapped to the Iraqi unified chart, journals imported from CSV as drafts, and
 * a supporting document attached to a journal, downloaded and removed. Every screen passes axe.
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

function today(): string {
  const d = new Date();
  return `${String(d.getFullYear())}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

test("English: chart CSV round trip, statutory mapping, journal import and an attachment", async ({ page }) => {
  const slug = `acc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Tools " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });
  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("ACC");
  await page.getByLabel(/Legal name \(English\)/).fill("Accounts Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("ACC");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // Export: the file has the import columns and the rent account with its parent.
  const [download] = await Promise.all([page.waitForEvent("download"), page.getByTestId("export-chart").click()]);
  expect(download.suggestedFilename()).toBe("chart-ACC.csv");
  const csv = await readFile(await download.path(), "utf8");
  const [header, ...rows] = csv.trim().split("\n");
  expect(header).toBe("code,parent_code,name_en,name_ar,type,subtype,category,is_header,is_control,subledger_type,currency,allow_manual_posting,revalue_fx,cash_flow_category,default_role,is_active");
  const rent = rows.find((r) => r.startsWith("6110,"));
  expect(rent).toBeDefined();
  const parent = (rent ?? "").split(",")[1] ?? "";

  // Import: a wrong row refuses the whole file; a good one creates the account.
  await page.getByTestId("import-chart-open").click();
  await page.getByTestId("import-text").fill(`${header ?? ""}\n6199,${parent},Sundry expenses,مصروفات متنوعة,expense,,,false,false,,,true,false,,,true\n6198,NOPE,Orphan,,expense,,,false,false,,,true,false,,,true`);
  await page.getByTestId("run-import").click();
  await expect(page.getByTestId("import-chart").getByRole("alert")).toBeVisible();
  await page.getByTestId("import-text").fill(`${header ?? ""}\n6199,${parent},Sundry expenses,مصروفات متنوعة,expense,,,false,false,,,true,false,,,true`);
  await expectAccessible(page);
  await page.getByTestId("run-import").click();
  await expect(page.getByTestId("import-result")).toContainText("1 created, 0 updated, 0 unchanged of 1 rows");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("table")).toContainText("6199");
  await expect(page.getByRole("table")).not.toContainText("6198");

  // Statutory mapping to the Iraqi unified chart: mapping the new account reduces the unmapped count by one.
  await page.getByTestId("statutory-mapping-open").click();
  const mapping = page.getByTestId("statutory-mapping");
  await expect(mapping.getByTestId("statutory-chart")).toHaveValue("IRAQ_UAS");
  await expect(mapping.getByTestId("mapping-row").first()).toBeVisible();
  const before = Number((await page.getByTestId("unmapped-count").textContent())?.match(/\d+/)?.[0] ?? "0");
  expect(before).toBeGreaterThan(0);
  await page.getByTestId("mapping-6199").selectOption({ index: 1 });
  await expectAccessible(page);
  await page.getByTestId("save-mappings").click();
  await expect(page.getByTestId("unmapped-count")).toContainText(String(before - 1));
  await page.getByTestId("only-unmapped").check();
  await expect(mapping.getByTestId("mapping-6199")).toHaveCount(0);
  await page.keyboard.press("Escape");

  // Journals from CSV: two lines of one journal arrive as a draft.
  await nav(page, "Journals");
  await page.getByTestId("import-journals-open").click();
  await page.getByTestId("journal-import-text").fill(
    `journal_ref,posting_date,currency,kind,description,account_code,debit,credit\nJ-1,${today()},IQD,manual,Office sundries,6199,125000,\nJ-1,${today()},IQD,manual,Office sundries,2170,,125000`,
  );
  await expectAccessible(page);
  await page.getByTestId("run-journal-import").click();
  await expect(page.getByTestId("journal-import-result")).toContainText("1 draft journal created");
  await page.keyboard.press("Escape");
  await page.getByRole("grid").getByText("Office sundries").dblclick();
  const detail = page.getByTestId("journal-detail");
  await expect(detail).toContainText("Draft");
  await expect(detail).toContainText("6199 Sundry expenses");

  // A supporting document on the journal: attached, listed, downloaded, removed.
  await detail.getByTestId("attachment-input").setInputFiles({ name: "receipt-0042.txt", mimeType: "text/plain", buffer: Buffer.from("Stationery, 125,000 IQD") });
  await expect(detail.getByTestId("attachment-row")).toContainText("receipt-0042.txt");
  await expectAccessible(page);
  const [file] = await Promise.all([page.waitForEvent("download"), detail.getByRole("button", { name: "Download receipt-0042.txt" }).click()]);
  expect(await readFile(await file.path(), "utf8")).toBe("Stationery, 125,000 IQD");
  await detail.getByTestId("remove-attachment").click();
  await expect(detail.getByTestId("attachment-row")).toHaveCount(0);
});
