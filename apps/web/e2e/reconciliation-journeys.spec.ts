import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The bank reconciliation journey (roadmap 6.2): the books hold a transfer received, a payment and a payment the bank
 * has not seen; a rule for the bank's fee is set up; the bank's CSV is imported from the screen, matched automatically
 * (the receipt and the payment paired, the fee booked by the rule), the interest line booked by hand from the matching
 * tab, the reconciliation recorded at zero difference; then the unconfirmed aging and the screen in Arabic. Every screen
 * passes axe with no serious or critical violation.
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

test("English: a statement imported, matched, the interest booked by hand, the account reconciled; aging; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `rec-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => {
    if (!window.localStorage.getItem("quicker.language")) {
      window.localStorage.setItem("quicker.language", "en");
    }
  });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Reconciled " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("REC");
  await page.getByLabel(/Legal name \(English\)/).fill("Reconciled Traders");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("REC");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  // The books of the month, through the API: a receipt by transfer, a payment by transfer, and a payment the bank has not processed.
  const token = await page.evaluate(() => (JSON.parse(window.localStorage.getItem("quicker.session") ?? "{}") as { accessToken?: string }).accessToken);
  const headers = { Authorization: `Bearer ${token ?? ""}`, "Content-Type": "application/json" };
  const post = async <T,>(path: string, data: unknown): Promise<T> => {
    const response = await page.request.post(`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as T;
  };
  const companies = (await (await page.request.get(`${apiUrl}/api/v1/organization/companies`, { headers })).json()) as { id: string; code: string }[];
  const companyId = companies.find((c) => c.code === "REC")?.id ?? "";
  const customer = await post<{ id: string }>("/api/v1/partners", { code: "NOOR", legalName: { en: "Al-Noor Markets", ar: "أسواق النور" }, isCustomer: true });
  await page.request.put(`${apiUrl}/api/v1/partners/${customer.id}/customer-accounts/${companyId}`, { headers, data: { currency: "IQD" } });
  const supplier = await post<{ id: string }>("/api/v1/partners", { code: "STAT", legalName: { en: "Stationery House", ar: "بيت القرطاسية" }, isSupplier: true });
  await page.request.put(`${apiUrl}/api/v1/partners/${supplier.id}/supplier-accounts/${companyId}`, { headers, data: { currency: "IQD" } });
  const bank = await post<{ id: string }>("/api/v1/banking/bank-accounts", { companyId, code: "MAIN", name: { en: "Main account", ar: "الحساب الرئيسي" }, kind: "bank", currency: "IQD" });
  const invoice = await post<{ id: string }>("/api/v1/sales/invoices", { companyId, partnerId: customer.id, customerReference: "INV-1", documentDate: "2026-10-01", lines: [{ kind: "service", quantity: 1, unitPrice: 1000000, description: "Consulting", accountRole: "Revenue" }] });
  const posted = await post<{ openItems: { id: string }[] }>(`/api/v1/sales/invoices/${invoice.id}/post`, {});
  const receipt = await post<{ id: string }>("/api/v1/banking/receipts", { companyId, partnerId: customer.id, bankAccountId: bank.id, receiptDate: "2026-10-02", method: "transfer", reference: "TRF-5501", lines: [{ openItemId: posted.openItems[0]?.id, amount: 1000000 }] });
  await post(`/api/v1/banking/receipts/${receipt.id}/post`, {});
  const category = await post<{ id: string }>("/api/v1/expenses/categories", { code: "OFFICE", name: { en: "Office supplies", ar: "مستلزمات مكتبية" }, accountCode: "6150" });
  const payFor = async (amount: number, date: string, reference: string): Promise<void> => {
    const expense = await post<{ id: string }>("/api/v1/expenses", { companyId, kind: "supplier", partnerId: supplier.id, expenseDate: date, dueDate: date, reference, lines: [{ categoryId: category.id, amount, description: "Paper" }] });
    await post(`/api/v1/expenses/${expense.id}/confirm`, {});
    const items = (await (await page.request.get(`${apiUrl}/api/v1/payables/open-items?companyId=${companyId}&partnerId=${supplier.id}&status=open`, { headers })).json()) as { item: { id: string; remainingTc: number | string } }[];
    const item = items.find((i) => Number(i.item.remainingTc) === amount)?.item.id ?? "";
    const payment = await post<{ id: string }>("/api/v1/banking/payments", { companyId, partnerId: supplier.id, bankAccountId: bank.id, paymentDate: date, method: "transfer", reference, lines: [{ openItemId: item, amount }] });
    await post(`/api/v1/banking/payments/${payment.id}/post`, {});
  };
  await payFor(600000, "2026-10-05", "TRF-5502");
  await payFor(250000, "2026-10-20", "TRF-5503");

  // A rule for the bank's monthly fee, from the rules tab.
  await nav(page, "Bank reconciliation");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Bank reconciliation");
  await page.getByTestId("tab-rules").click();
  await page.getByTestId("new-rule").click();
  await page.getByTestId("rule-name-en").fill("Monthly fee");
  await page.getByTestId("rule-name-ar").fill("الرسم الشهري");
  await page.getByTestId("rule-words").fill("FEE, CHARGE");
  await page.getByTestId("rule-amount-max").fill("50000");
  await expectAccessible(page);
  await page.getByTestId("save-rule").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByRole("grid")).toContainText("Monthly fee");

  // The bank's statement imported from the screen, then matched: three lines paired or booked, the interest left.
  await page.getByTestId("tab-statements").click();
  const csv = [
    "Date,Description,Reference,Amount",
    "2026-10-03,Transfer from Al-Noor Markets,TRF-5501,1000000",
    "2026-10-05,Payment Stationery House,TRF-5502,-600000",
    "2026-10-15,MONTHLY FEE,,-12500",
    "2026-10-31,INTEREST CREDIT,,3250",
  ].join("\n");
  await page.getByTestId("statement-file").setInputFiles({ name: "october.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
  await expectAccessible(page);
  await page.getByTestId("import-statement").click();
  await expect(page.getByTestId("statement-detail")).toBeVisible();
  await expect(page.getByTestId("statement-counts")).toContainText("4 lines");
  await expect(page.getByTestId("statement-line")).toHaveCount(4);
  await page.getByTestId("auto-match-statement").click();
  await expect(page.getByTestId("statement-counts")).toContainText("3 matched");
  await expect(page.getByTestId("statement-detail").getByRole("status")).toContainText("1 movements booked");
  await expect(page.locator('[data-testid="statement-line"][data-status="unmatched"]')).toHaveCount(1);
  await expectAccessible(page);

  // The interest booked by hand: the line is taken to the matching tab, no movement is waiting, so it is booked as interest income.
  await page.getByTestId("line-match").click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("match-panel")).toBeVisible();
  await expect(page.getByTestId("match-panel")).toContainText("INTEREST CREDIT");
  await expect(page.getByTestId("candidate-transactions")).toContainText("TRF-5503");
  await page.getByTestId("book-role").selectOption("InterestIncome");
  await expectAccessible(page);
  await page.getByTestId("create-from-line").click();
  await expect(page.getByTestId("match-panel")).toHaveCount(0);
  await expect(page.getByText("Nothing left to match")).toBeVisible();

  // The reconciliation at the end of the month: the bank's balance less the payment in transit is the book's; recorded, it completes at once.
  await page.getByTestId("tab-reconcile").click();
  await page.getByTestId("reconcile-as-of").fill("2026-10-31");
  await page.getByTestId("reconcile-statement-balance").fill("390750");
  await expect(page.getByTestId("reconciliation-report")).toBeVisible();
  await expect(page.getByTestId("report-difference")).toContainText("0");
  await expect(page.getByTestId("report-verdict")).toContainText("can be completed");
  await expect(page.getByTestId("outstanding-row")).toHaveCount(1);
  await expectAccessible(page);
  await page.getByTestId("record-reconciliation").click();
  await expect(page.getByTestId("reconciliation-detail")).toBeVisible();
  await expect(page.getByTestId("reconciliation-detail")).toContainText("REC-");
  await expect(page.getByTestId("reconciliation-detail").getByTestId("doc-status")).toContainText("Completed");
  await closeDialog(page);
  await expect(page.getByRole("grid")).toContainText("Completed");

  // The aging at the end of November: the payment of 20 October has waited 41 days.
  await page.getByTestId("tab-aging").click();
  await page.getByTestId("aging-as-of").fill("2026-11-30");
  await expect(page.getByTestId("aging-row")).toHaveCount(1);
  await expect(page.getByTestId("aging-row")).toContainText("MAIN");
  await expectAccessible(page);

  // Arabic, right-to-left: the same screen reads in Arabic.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await nav(page, "مطابقة البنك");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("مطابقة البنك");
  await page.getByTestId("tab-statements").click();
  await expect(page.getByRole("grid")).toContainText("october.csv");
  await expect(page.getByRole("grid")).toContainText("مطابَق");
  await expectAccessible(page);
});
