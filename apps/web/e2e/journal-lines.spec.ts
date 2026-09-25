import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * A manual journal's line details against the real API: rent that requires a cost centre, against a supplier on the
 * payables control account. Posting without the supplier is refused; the cost centre and supplier are picked in the
 * line details, survive a further edit and show on the posted journal's lines. Then Arabic.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function nav(page: Page, name: string): Promise<void> {
  await page.getByRole("navigation").getByRole("link", { name, exact: true }).click();
}

test("English: journal lines with a cost centre and a supplier, refused without the supplier, kept through an edit and posted; then Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `jln-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Lines " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  // A company and its chart; rent (6110) requires a cost centre; a cost centre and a supplier to pick; through the API.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const call = async <T>(method: string, path: string, data?: unknown): Promise<T> => {
    const response = await page.request.fetch(apiUrl + path, { method, headers: { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" }, ...(data === undefined ? {} : { data }) });
    expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const company = await call<{ id: string }>("POST", "/api/v1/organization/companies", { code: "JLN", legalName: { en: "Lines Co.", ar: "شركة البنود" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  const chart = await call<{ id: string }>("POST", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  const accounts = (await call<{ accounts: { id: string; code: string }[] }>("GET", `/api/v1/accounting/charts/${chart.id}?expand=accounts`)).accounts;
  const rent = accounts.find((a) => a.code === "6110");
  const dimensions = await call<{ id: string; code: string }[]>("GET", "/api/v1/organization/dimensions");
  const costCentre = dimensions.find((d) => d.code === "COST_CENTER");
  await call("POST", `/api/v1/organization/dimensions/${costCentre?.id ?? ""}/values`, { code: "CC-OPS", name: { en: "Operations", ar: "العمليات" }, companyId: company.id });
  await call("PUT", `/api/v1/accounting/accounts/${rent?.id ?? ""}/dimension-rules`, [{ dimensionCode: "COST_CENTER", rule: "required" }]);
  await call("POST", "/api/v1/partners", { code: "LANDLORD", legalName: { en: "Tigris Properties", ar: "عقارات دجلة" }, isSupplier: true });
  await page.reload();

  // Rent against the supplier's payable, saved without the details: posting is refused and says why.
  await nav(page, "Journals");
  await page.getByTestId("new-journal").click();
  await page.getByTestId("journal-description").fill("October rent");
  await page.getByTestId("line-account-0").fill("6110");
  await page.getByTestId("line-debit-0").fill("750000.50");
  await page.getByTestId("line-account-1").fill("2110");
  await page.getByTestId("line-credit-1").fill("750000.50");
  await page.getByTestId("save-journal").click();
  const detail = page.getByTestId("journal-detail");
  await expect(detail).toContainText("Draft");
  await page.getByTestId("post-journal").click();
  await expect(detail.getByRole("alert")).toContainText("COST_CENTER");

  // The line details: the cost centre (required by the account) and the supplier (the account is a control account).
  await page.getByTestId("edit-journal").click();
  await page.getByTestId("line-details-toggle-0").click();
  const rentDetails = page.getByTestId("line-details-0");
  await expect(rentDetails.getByText("Cost centre")).toBeVisible();
  await page.getByTestId("line-dimension-0-COST_CENTER").selectOption({ label: "CC-OPS · Operations" });
  await page.getByTestId("line-description-0").fill("Head office, October");
  await page.getByTestId("line-details-toggle-1").click();
  await expect(page.getByTestId("line-details-1").getByText("Supplier")).toBeVisible();
  await page.getByTestId("line-subledger-1").selectOption({ label: "LANDLORD · Tigris Properties" });
  await expectAccessible(page);
  await page.getByTestId("save-journal").click();
  await expect(detail).toContainText("Draft");
  const details = detail.getByTestId("line-details");
  await expect(details.first()).toContainText("Head office, October");
  await expect(details.first()).toContainText("CC-OPS Operations");
  await expect(details.last()).toContainText("Supplier: LANDLORD Tigris Properties");

  // Another edit keeps the details (saving an edit used to drop them), then the journal posts.
  await page.getByTestId("edit-journal").click();
  await expect(page.getByTestId("line-details-toggle-0").locator("xpath=..")).toContainText("2");
  await page.getByTestId("journal-description").fill("October rent, head office");
  await page.getByTestId("save-journal").click();
  await expect(detail.getByTestId("line-details").first()).toContainText("CC-OPS Operations");
  await expect(detail.getByTestId("line-details").last()).toContainText("LANDLORD");
  await page.getByTestId("post-journal").click();
  await expect(detail).toContainText("Posted");
  await expectAccessible(page);

  // Arabic: the posted journal's line details read right to left.
  await page.keyboard.press("Escape");
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByRole("grid").getByText("October rent, head office").dblclick();
  await expect(detail.getByTestId("line-details").last()).toContainText("المورد: LANDLORD عقارات دجلة");
  await expectAccessible(page);
});
