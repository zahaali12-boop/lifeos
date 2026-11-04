import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The report builder journey (founder programme item 9, slice b): two invoices posted, then a report of net sales by
 * customer and month built by clicking fields, run, pivoted, charted, drilled to the invoice behind a number, its
 * query shown, exported, and saved with a name in both languages and a share with the workspace; the saved report
 * reopens from the list. Then the screens in Arabic, right-to-left. Every screen passes axe with no serious or
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

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

test("English: a report built by clicking fields, run, charted, drilled, explained, exported and saved; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `rpt-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Reports " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("RPT");
  await page.getByLabel(/Legal name \(English\)/).fill("Reports Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("RPT");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // Two customers, an item and three posted invoices through the API: the facts the report reads.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "RPT")?.id ?? "";
  const post = async (path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  const alpha = await post("/api/v1/partners", { code: "CUS-A", legalName: { en: "Alpha Retail", ar: "ألفا" }, isCustomer: true });
  const beta = await post("/api/v1/partners", { code: "CUS-B", legalName: { en: "Beta Stores", ar: "بيتا" }, isCustomer: true });
  for (const partner of [alpha, beta]) {
    expect((await page.request.put(`${apiUrl}/api/v1/partners/${partner.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } })).ok()).toBeTruthy();
  }
  const tea = await post("/api/v1/items", { code: "TEA", name: { en: "Tea service", ar: "خدمة شاي" }, type: "service", baseUom: "PCS", listPrice: 1000, listPriceCurrency: "IQD" });
  const invoices = [
    { partnerId: alpha.id, documentDate: "2026-08-15", quantity: 5 },
    { partnerId: alpha.id, documentDate: "2026-09-22", quantity: 3 },
    { partnerId: beta.id, documentDate: "2026-09-10", quantity: 2 },
  ];
  for (const invoice of invoices) {
    const created = await post("/api/v1/sales/invoices", { companyId, partnerId: invoice.partnerId, kind: "invoice", documentDate: invoice.documentDate, lines: [{ kind: "item", itemId: tea.id, quantity: invoice.quantity, unitPrice: 1000 }] });
    await post(`/api/v1/sales/invoices/${created.id}/post`, {});
  }

  // The reports list, empty, and a new report started from the sales lines model.
  await nav(page, "Reports");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Reports");
  await expect(page.getByText("No saved reports yet")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("start-sales_lines").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("New report");
  await expect(page.getByTestId("report-model")).toHaveValue("sales_lines");

  // Customer in rows, month in columns, net amount and the invoice count as values; the whole year.
  await page.getByTestId("field-customer").click();
  await page.getByTestId("field-period_month-column").click();
  await page.getByTestId("field-net_amount").click();
  await page.getByTestId("field-invoice_count").click();
  await expect(page.getByTestId("row-customer")).toBeVisible();
  await expect(page.getByTestId("column-period_month")).toBeVisible();
  await expect(page.getByTestId("value-net_amount")).toBeVisible();
  await page.getByTestId("report-from").fill("2026-01-01");
  await page.getByTestId("report-to").fill("2026-12-31");
  await expectAccessible(page);
  await page.getByTestId("run-report").click();
  const table = page.getByTestId("report-table");
  await expect(table).toBeVisible({ timeout: 20_000 });
  await expect(table).toContainText("Alpha Retail");
  await expect(table).toContainText("2026-08");
  await expect(table).toContainText("2026-09");
  await expect(table).toContainText("5,000.00");
  // Totals per month column: August 5,000 and September 3,000 + 2,000; a distinct count does not add up, so it shows none.
  await expect(page.getByTestId("report-totals")).toContainText("Total5,000.00—5,000.00—");
  await expect(page.getByTestId("report-footer")).toContainText("Amounts in IQD");
  await expectAccessible(page);

  // Flat table: the month becomes a row dimension and the totals add the three invoices.
  await page.getByTestId("kind-table").click();
  await expect(page.getByTestId("report-row-1")).toContainText("Alpha Retail");
  await expect(page.getByTestId("report-totals")).toContainText("10,000.00");

  // The chart draws the same numbers; the table stays beside it.
  await page.getByTestId("kind-chart").click();
  await expect(page.getByTestId("report-chart")).toBeVisible();
  await expect(page.getByTestId("report-chart").locator("canvas")).toHaveCount(1);
  await page.getByTestId("chart-series").selectOption("period_month");
  await page.getByTestId("chart-stacked").check();
  await expect(page.getByTestId("report-chart").locator("canvas")).toHaveCount(1);
  await expectAccessible(page);

  // The query behind the report, with its values in place.
  await page.getByTestId("show-query").click();
  await expect(page.getByTestId("query-sql")).toContainText("GROUP BY");
  await expect(page.getByTestId("query-sql")).toContainText("fact_sales_lines");
  await expectAccessible(page);
  await closeDialog(page);

  // Drilling into Beta's number lists the invoice behind it, with a link to open it.
  await page.getByTestId("kind-pivot").click();
  const betaRow = page.getByTestId("report-row-2");
  await expect(betaRow).toContainText("Beta Stores");
  await betaRow.getByRole("button", { name: "2,000.00" }).click();
  await expect(page.getByTestId("drill-subject")).toContainText("Beta Stores");
  await expect(page.getByTestId("drill-row-1")).toContainText("Tea service");
  await expect(page.getByTestId("drill-open-1")).toContainText("INV-");
  await expectAccessible(page);
  await closeDialog(page);

  // Exported as a spreadsheet of what the table shows.
  const download = page.waitForEvent("download");
  await page.getByTestId("export-xlsx").click();
  expect((await download).suggestedFilename()).toMatch(/\.xlsx$/);

  // Saved under a name in both languages and shared with the workspace.
  await page.getByTestId("save-report").click();
  await page.getByTestId("report-name-en").fill("Sales by customer and month");
  await page.getByTestId("report-name-ar").fill("المبيعات حسب العميل والشهر");
  await page.getByTestId("add-share").click();
  await expect(page.getByTestId("share-1-with")).toHaveValue("tenant");
  await expectAccessible(page);
  await page.getByTestId("confirm-save-report").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Sales by customer and month");
  await expect(page.getByTestId("report-table")).toContainText("Alpha Retail", { timeout: 20_000 });

  // Back in the list, the report opens again with its definition and runs.
  await page.getByTestId("back-to-reports").click();
  await expect(page.getByRole("grid")).toContainText("Sales by customer and month");
  await expect(page.getByRole("grid")).toContainText("Mine, shared");
  await page.getByRole("grid").getByText("Sales by customer and month", { exact: true }).dblclick();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Sales by customer and month");
  await expect(page.getByTestId("row-customer")).toBeVisible();
  await expect(page.getByTestId("report-table")).toContainText("Beta Stores", { timeout: 20_000 });

  // Arabic, right-to-left: the same report with its Arabic name, labels and customer names.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("المبيعات حسب العميل والشهر");
  await expect(page.getByTestId("zone-rows")).toContainText("العميل");
  await expect(page.getByTestId("report-table")).toContainText("ألفا");
  await expectAccessible(page);
  await nav(page, "التقارير");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("التقارير");
  await expect(page.getByRole("grid")).toContainText("المبيعات حسب العميل والشهر");
  await expectAccessible(page);
});
