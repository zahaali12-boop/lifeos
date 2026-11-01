import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The financial statements journey (founder programme item 9, slice c): capital, an invoice and an accrued rent
 * posted, then the shipped profit and loss run for September with its accounts unfolded and a figure drilled to the
 * ledger, the balance sheet balanced with the earnings not yet closed, the trial balance with equal debits and
 * credits; a copy of the profit and loss customised in the designer with a margin row, previewed and saved, then run
 * from the statements screen. Then the screens in Arabic, right-to-left. Every screen passes axe with no serious or
 * critical violation.
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

test("English: the shipped statements from the books, a customised layout designed, previewed, saved and run; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `stm-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Statements " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("STM");
  await page.getByLabel(/Legal name \(English\)/).fill("Statements Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("STM");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The books through the API: capital subscribed in August, a 5,000 invoice and 1,200 of rent accrued in September.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "STM")?.id ?? "";
  const post = async (path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  const journal = async (postingDate: string, description: string, lines: unknown[]): Promise<void> => {
    const draft = await post(`/api/v1/accounting/companies/${companyId}/journals`, { kind: "manual", postingDate, currency: "IQD", description: { en: description, ar: description }, lines });
    await post(`/api/v1/accounting/journals/${draft.id}/post`, {});
  };
  await journal("2026-08-01", "Capital", [{ accountCode: "1250", debit: 10000, credit: 0 }, { accountCode: "3100", debit: 0, credit: 10000 }]);
  const alpha = await post("/api/v1/partners", { code: "CUS-A", legalName: { en: "Alpha Retail", ar: "ألفا" }, isCustomer: true });
  expect((await page.request.put(`${apiUrl}/api/v1/partners/${alpha.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } })).ok()).toBeTruthy();
  const tea = await post("/api/v1/items", { code: "TEA", name: { en: "Tea service", ar: "خدمة شاي" }, type: "service", baseUom: "PCS", listPrice: 1000, listPriceCurrency: "IQD" });
  const invoice = await post("/api/v1/sales/invoices", { companyId, partnerId: alpha.id, kind: "invoice", documentDate: "2026-09-10", lines: [{ kind: "item", itemId: tea.id, quantity: 5, unitPrice: 1000 }] });
  await post(`/api/v1/sales/invoices/${invoice.id}/post`, {});
  await journal("2026-09-05", "Rent", [{ accountCode: "6110", debit: 1200, credit: 0 }, { accountCode: "2170", debit: 0, credit: 1200 }]);

  // The profit and loss for September: net profit 3,800; the sales row unfolds to its account, whose figure opens the ledger.
  await nav(page, "Financial statements");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Financial statements");
  await expect(page.getByTestId("statement-layout")).toHaveValue("shipped:PL");
  await page.getByTestId("statement-from").fill("2026-09-01");
  await page.getByTestId("statement-to").fill("2026-09-30");
  await expectAccessible(page);
  await page.getByTestId("run-statement").click();
  const table = page.getByTestId("statement-table");
  await expect(table).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("statement-title")).toContainText("Profit and loss");
  await expect(page.getByTestId("cell-SALES-PERIOD")).toContainText("5,000.00");
  await expect(page.getByTestId("cell-OPEX-PERIOD")).toContainText("1,200.00");
  await expect(page.getByTestId("cell-NET-PERIOD")).toContainText("3,800.00");
  await expect(page.getByTestId("cell-NET-YTD")).toContainText("3,800.00");
  await page.getByTestId("toggle-SALES").click();
  await expect(page.getByTestId("account-4100")).toContainText("5,000.00");
  await expectAccessible(page);

  // Drilling into the operating expenses lists the rent account; from there the ledger opens over the same dates.
  await page.getByTestId("cell-OPEX-PERIOD").getByRole("button").click();
  await expect(page.getByTestId("statement-drill")).toContainText("6110");
  await expectAccessible(page);
  await page.getByTestId("open-ledger-6110").click();
  await expect(page).toHaveURL(/accounting\/ledger.*from=2026-09-01/);
  await expect(page.getByRole("heading", { level: 1 })).toContainText("6110", { timeout: 15_000 });

  // The balance sheet at the end of September balances with the earnings not yet closed.
  await nav(page, "Financial statements");
  await page.getByTestId("statement-layout").selectOption("shipped:BS");
  await page.getByTestId("statement-to").fill("2026-09-30");
  await page.getByTestId("run-statement").click();
  await expect(page.getByTestId("cell-TOTAL_ASSETS-PERIOD")).toContainText("15,000.00", { timeout: 20_000 });
  await expect(page.getByTestId("cell-CURRENT_LIABILITIES-PERIOD")).toContainText("1,200.00");
  await expect(page.getByTestId("cell-CURRENT_EARNINGS-PERIOD")).toContainText("3,800.00");
  await expect(page.getByTestId("cell-TOTAL_LIAB_EQUITY-PERIOD")).toContainText("15,000.00");
  await expect(page.getByTestId("statement-row-CHECK")).toHaveCount(0);

  // The trial balance: debits equal credits, the closing total is nothing.
  await page.getByTestId("statement-layout").selectOption("shipped:TB");
  await page.getByTestId("statement-from").fill("2026-09-01");
  await page.getByTestId("run-statement").click();
  await expect(page.getByTestId("cell-TOTAL-DEBITS")).toContainText("6,200.00", { timeout: 20_000 });
  await expect(page.getByTestId("cell-TOTAL-CREDITS")).toContainText("6,200.00");
  await expect(page.getByTestId("cell-TOTAL-CLOSING")).toContainText("0.00");
  await expectAccessible(page);

  // A copy of the profit and loss with a margin row: previewed in the designer, saved, listed and run.
  await page.getByTestId("statement-layout").selectOption("shipped:PL");
  await page.getByTestId("design-layout").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("New layout");
  await expect(page.getByTestId("layout-code")).toHaveValue("PL_1");
  await page.getByTestId("layout-code").fill("PL_MARGIN");
  await page.getByTestId("layout-name-en").fill("Profit and loss with margin");
  await page.getByTestId("layout-name-ar").fill("الأرباح والخسائر مع الهامش");
  await page.getByTestId("add-row").click();
  const last = page.locator('[data-testid^="row-"][data-testid$="-code"]').last();
  await last.fill("MARGIN_PCT");
  const rows = await page.locator('[data-testid^="row-"][data-testid$="-code"]').count();
  await page.getByTestId(`row-${rows}-type`).selectOption("formula");
  await page.getByTestId(`row-${rows}-label-en`).fill("Net margin %");
  await page.getByTestId(`row-${rows}-label-ar`).fill("هامش الربح الصافي %");
  await page.getByTestId(`row-${rows}-formula`).fill("NET / NET_REVENUE * 100");
  await page.getByTestId(`row-${rows}-bold`).check();
  await page.getByTestId("preview-to").fill("2026-09-30");
  await page.getByTestId("run-preview").click();
  await expect(page.getByTestId("preview-table")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("preview-table").getByTestId("cell-MARGIN_PCT-PERIOD")).toContainText("76");
  await expectAccessible(page);
  await page.getByTestId("save-layout").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Financial statements");
  await expect(page.getByTestId("statement-layout")).toContainText("Profit and loss with margin");
  await page.getByTestId("statement-from").fill("2026-09-01");
  await page.getByTestId("statement-to").fill("2026-09-30");
  await page.getByTestId("run-statement").click();
  await expect(page.getByTestId("cell-MARGIN_PCT-PERIOD")).toContainText("76", { timeout: 20_000 });

  // Arabic, right-to-left: the same statement with its Arabic name and labels.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("القوائم المالية");
  await page.getByTestId("run-statement").click();
  await expect(page.getByTestId("statement-title")).toContainText("الأرباح والخسائر مع الهامش", { timeout: 20_000 });
  await expect(page.getByTestId("statement-row-NET")).toContainText("صافي الربح");
  await expectAccessible(page);
});
