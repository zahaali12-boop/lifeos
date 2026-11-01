import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The dashboards and schedules journey (founder programme item 9, slice d): a saved report and the books posted
 * through the API, then a dashboard built in the designer with a KPI, a chart and a statement widget, run for this
 * month and pinned to the home page; a schedule of the report as XLSX created, run now, its run shown in the history
 * with its file; then the screens in Arabic, right-to-left. Every screen passes axe with no serious or critical
 * violation. The API runs the embedded worker, so "run now" completes within seconds.
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

test("English: a dashboard designed, run and pinned; a schedule created, run now and its file kept; then Arabic", async ({ page }) => {
  test.setTimeout(240_000);
  const slug = `dsh-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Dashboards " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("DSH");
  await page.getByLabel(/Legal name \(English\)/).fill("Dashboards Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("DSH");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The books and a saved report through the API: an invoice this month, rent accrued, net sales by customer.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "DSH")?.id ?? "";
  const post = async (path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  const today = new Date();
  const iso = (d: Date): string => d.toISOString().slice(0, 10);
  const monthStart = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1));
  const onDay = (day: number): string => iso(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), Math.min(day, today.getUTCDate()))));
  const alpha = await post("/api/v1/partners", { code: "CUS-A", legalName: { en: "Alpha Retail", ar: "ألفا" }, isCustomer: true });
  expect((await page.request.put(`${apiUrl}/api/v1/partners/${alpha.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } })).ok()).toBeTruthy();
  const tea = await post("/api/v1/items", { code: "TEA", name: { en: "Tea service", ar: "خدمة شاي" }, type: "service", baseUom: "PCS", listPrice: 1000, listPriceCurrency: "IQD" });
  const invoice = await post("/api/v1/sales/invoices", { companyId, partnerId: alpha.id, kind: "invoice", documentDate: onDay(1), lines: [{ kind: "item", itemId: tea.id, quantity: 5, unitPrice: 1000 }] });
  await post(`/api/v1/sales/invoices/${invoice.id}/post`, {});
  const draft = await post(`/api/v1/accounting/companies/${companyId}/journals`, { kind: "manual", postingDate: onDay(2), currency: "IQD", description: { en: "Rent", ar: "إيجار" }, lines: [{ accountCode: "6110", debit: 1200, credit: 0 }, { accountCode: "2170", debit: 0, credit: 1200 }] });
  await post(`/api/v1/accounting/journals/${draft.id}/post`, {});
  await post("/api/v1/reporting/reports", {
    name: { en: "Sales by customer", ar: "المبيعات حسب العميل" },
    kind: "chart",
    chart: { type: "bar", categoryField: "customer", measures: ["net_amount"] },
    definition: { model: "sales_lines", companyId, rows: ["customer"], values: [{ field: "net_amount" }, { field: "invoice_count" }], from: iso(monthStart), to: iso(today) },
  });

  // The designer: a KPI and a chart over the report, the profit and loss as a statement, pinned to the home page.
  await nav(page, "Dashboards");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Dashboards");
  await expectAccessible(page);
  await page.getByTestId("new-dashboard").click();
  await expect(page.getByTestId("dashboard-designer")).toBeVisible();
  await page.getByTestId("dashboard-name-en").fill("Sales at a glance");
  await page.getByTestId("dashboard-name-ar").fill("المبيعات في لمحة");
  await page.getByTestId("dashboard-home").click();
  await page.getByTestId("widget-1-report").selectOption({ label: "Sales by customer" });
  await page.getByTestId("widget-1-viz").selectOption("kpi");
  await page.getByTestId("add-widget").click();
  await page.getByTestId("widget-2-report").selectOption({ label: "Sales by customer" });
  await page.getByTestId("widget-2-viz").selectOption("bar");
  await page.getByTestId("widget-2-width").selectOption("2");
  await page.getByTestId("add-widget").click();
  await page.getByTestId("widget-3-source").selectOption("statement");
  await page.getByTestId("widget-3-layout").selectOption({ label: "Profit and loss" });
  await page.getByTestId("widget-3-viz").selectOption("kpi");
  await page.getByTestId("widget-3-row").selectOption("NET");
  await page.getByTestId("widget-3-title-en").fill("Net profit this month");
  await page.getByTestId("widget-3-title-ar").fill("صافي الربح هذا الشهر");
  await expectAccessible(page);
  await page.getByTestId("save-dashboard").click();

  // Run for this month: sales 5,000; the net profit 3,800; the chart drawn.
  await expect(page.getByTestId("dashboard-title")).toContainText("Sales at a glance", { timeout: 20_000 });
  await expect(page.getByTestId("kpi-W1-value")).toContainText("5,000", { timeout: 20_000 });
  await expect(page.getByTestId("kpi-W3-value")).toContainText("3,800");
  await expect(page.getByTestId("widget-W2").getByTestId("report-chart")).toBeVisible();
  await expect(page.getByTestId("dashboard-window")).toContainText(iso(monthStart));
  await expectAccessible(page);

  // Pinned, it shows on the home page with the same figures.
  await nav(page, "Dashboard");
  await expect(page.getByTestId("home-dashboards")).toContainText("Sales at a glance", { timeout: 20_000 });
  await expect(page.getByTestId("home-dashboards").getByTestId("kpi-W1-value")).toContainText("5,000", { timeout: 20_000 });

  // A schedule of the report as XLSX to the owner, monthly; run now goes through the worker and leaves a run with its file.
  await nav(page, "Scheduled reports");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Scheduled reports");
  await page.getByTestId("new-schedule").click();
  await page.getByTestId("schedule-name-en").fill("Monthly sales");
  await page.getByTestId("schedule-name-ar").fill("المبيعات الشهرية");
  await page.getByTestId("schedule-report").selectOption({ label: "Sales by customer" });
  await page.getByTestId("schedule-period").selectOption("this_month");
  await page.getByTestId("schedule-cadence").selectOption("monthly");
  await page.getByTestId("schedule-format").selectOption("xlsx");
  await page.getByTestId("schedule-members").getByRole("checkbox").first().click();
  await expectAccessible(page);
  await page.getByTestId("save-schedule").click();
  await expect(page.getByTestId("schedule-row-1")).toContainText("Monthly sales");
  await expect(page.getByTestId("schedule-row-1")).toContainText("The 1st at 07:00");
  await page.getByTestId("schedule-1-run").click();
  await expect(page.getByTestId("runs-table")).toBeVisible();
  await expect(page.getByTestId("run-1-status")).toContainText("Sent", { timeout: 60_000 });
  await expect(page.getByTestId("run-row-1")).toContainText(`owner-${slug}@example.test`);
  await expect(page.getByTestId("run-1-file")).toContainText(".xlsx");
  await expectAccessible(page);
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("schedule-1-status")).toContainText("Sent", { timeout: 20_000 });

  // Arabic, right-to-left: the schedule in its Arabic name, the dashboard with its Arabic widget title.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("التقارير المجدولة");
  await expect(page.getByTestId("schedule-row-1")).toContainText("المبيعات الشهرية");
  await expectAccessible(page);
  await nav(page, "لوحات المعلومات");
  await page.getByRole("grid").getByText("المبيعات في لمحة", { exact: true }).dblclick();
  await expect(page.getByTestId("dashboard-title")).toContainText("المبيعات في لمحة", { timeout: 20_000 });
  await expect(page.getByTestId("widget-W3")).toContainText("صافي الربح هذا الشهر", { timeout: 20_000 });
  await expectAccessible(page);
});
