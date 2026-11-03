import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page } from "@playwright/test";

/**
 * Cash and bank movements (trader edition, ADR-0035 §24, T14): a workspace made as the trader program with a cash box,
 * a bank account and a dollar box; from Cash & bank's "New movement": cash paid into the bank, dollars bought from the
 * bank with the rate shown, a customer's payment settling his invoice, a supplier paid in advance, an expense, the
 * owner's money put in; each box's balance follows in its own currency; the cash paid in is undone from its row; then
 * the same window in Arabic. Every screen passes axe with no serious or critical violation.
 */
const password = "correct-horse-battery-staple";
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

test("cash and bank: deposit, dollars bought, a customer's and a supplier's payments, an expense and the owner's money from one window; a transfer undone; then Arabic", async ({ page }) => {
  test.setTimeout(180_000);
  const slug = `tca-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;
  const today = new Date().toISOString().slice(0, 10);

  // The workspace, made as the trader program, and its company set up through the API (the screens for these are covered elsewhere).
  const signup = await page.request.post(`${apiUrl}/api/v1/auth/signup`, { data: { tenantName: "Cash " + slug, slug, ownerName: "Owner", ownerEmail: email, password, edition: "trader" } });
  expect(signup.ok(), await signup.text()).toBeTruthy();
  const tokens = (await signup.json()) as { accessToken: string };
  const headers = { Authorization: `Bearer ${tokens.accessToken}`, "Content-Type": "application/json" };
  const call = async (method: "post" | "put", path: string, data: unknown): Promise<{ id: string }> => {
    const response = await page.request[method](`${apiUrl}${path}`, { headers, data });
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy();
    return (await response.json()) as { id: string };
  };
  const company = await call("post", "/api/v1/organization/companies", { code: "TRD", legalName: { en: "Parts Trading Co.", ar: "شركة تجارة القطع" }, country: "IQ", functionalCurrency: "IQD", timeZone: "Asia/Baghdad" });
  await call("post", "/api/v1/accounting/charts/from-template", { templateCode: "IFRS_SME", code: "MAIN", companyId: company.id });
  await call("post", "/api/v1/organization/rates", { rateType: "spot", fromCurrency: "USD", toCurrency: "IQD", validFrom: "2026-01-01", rate: 1310 });
  const local = await call("post", "/api/v1/partners", { code: "LOCAL", legalName: { en: "Baghdad Parts", ar: "قطع بغداد" }, isSupplier: true });
  await call("put", `/api/v1/partners/${local.id}/supplier-accounts/${company.id}`, { currency: "IQD" });
  const ali = await call("post", "/api/v1/partners", { code: "ALI", legalName: { en: "Ali Auto Repair", ar: "ورشة علي" }, isCustomer: true });
  await call("put", `/api/v1/partners/${ali.id}/customer-accounts/${company.id}`, { currency: "IQD" });
  for (const [code, en, ar, kind, currency] of [["BANK", "Rafidain account", "حساب الرافدين", "bank", "IQD"], ["CASH", "Cash box", "الصندوق", "cash", "IQD"], ["USD", "Dollar box", "صندوق الدولار", "cash", "USD"]]) {
    await call("post", "/api/v1/banking/bank-accounts", { companyId: company.id, code, name: { en, ar }, kind, currency, isActive: true });
  }
  await call("post", `/api/v1/onboarding/companies/${company.id}/opening-balances?asOf=${today}`, { lines: [{ account: "1121", debit: 10_000_000, credit: 0, bankAccount: "BANK" }, { account: "1111", debit: 2_000_000, credit: 0, bankAccount: "CASH" }, { account: "3100", debit: 0, credit: 12_000_000 }] });
  const invoice = await call("post", "/api/v1/sales/invoices", { companyId: company.id, partnerId: ali.id, documentDate: today, lines: [{ kind: "service", quantity: 1, unitPrice: 300_000, description: "Parts and fitting" }] });
  await call("post", `/api/v1/sales/invoices/${invoice.id}/post`, {});
  await call("put", "/api/v1/organization/settings/trader.rates.prompt_hour", { value: -1, valueType: "number" });

  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/login");
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByTestId("flat-nav")).toBeVisible({ timeout: 20_000 });

  await page.getByTestId("flat-nav").getByRole("link", { name: "Cash & bank" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Cash and bank");
  const box = (code: string): Locator => page.getByTestId("cash-account").filter({ hasText: `${code} ·` });
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 2,000,000.00");
  await expect(box("USD").getByTestId("cash-balance")).toContainText("USD");
  await expectAccessible(page);

  const record = async (): Promise<void> => {
    await page.getByTestId("save-movement").click();
    await expect(page.getByTestId("cash-movement-dialog")).toBeHidden();
  };

  // Cash paid into the bank: from the cash box to the bank account.
  await page.getByTestId("cash-new-movement").click();
  await expect(page.getByTestId("cash-movement-dialog")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("movement-kind-transfer").check();
  await page.getByTestId("movement-account").selectOption({ label: "CASH · Cash box (IQD)" });
  await page.getByTestId("movement-to-account").selectOption({ label: "BANK · Rafidain account (IQD)" });
  await expect(page.getByTestId("movement-received")).toHaveCount(0);
  await page.getByTestId("movement-amount").fill("1500000");
  await page.getByTestId("movement-note").fill("Cash of the week");
  await record();
  await expect(page.getByTestId("cash-notice")).toContainText("TRF-");
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 500,000.00");
  await expect(box("BANK").getByTestId("cash-balance")).toHaveText("IQD 11,500,000.00");

  // Dollars bought from the bank: what arrived is asked for, and the rate it was bought at is shown.
  await page.getByTestId("cash-new-movement").click();
  await page.getByTestId("movement-account").selectOption({ label: "BANK · Rafidain account (IQD)" });
  await page.getByTestId("movement-to-account").selectOption({ label: "USD · Dollar box (USD)" });
  await page.getByTestId("movement-amount").fill("1320000");
  await page.getByTestId("movement-received").fill("1000");
  await expect(page.getByTestId("movement-rate")).toContainText("1 USD = 1,320 IQD");
  await record();
  await expect(box("USD").getByTestId("cash-balance")).toContainText("1,000.00");
  await expect(box("BANK").getByTestId("cash-balance")).toHaveText("IQD 10,180,000.00");

  // Ali pays his invoice into the cash box: what he owes is shown and taken whole.
  await page.getByTestId("cash-new-movement").click();
  await page.getByTestId("movement-kind-customer").check();
  await page.getByTestId("movement-account").selectOption({ label: "CASH · Cash box (IQD)" });
  await page.getByTestId("movement-customer").selectOption({ label: "ALI · Ali Auto Repair" });
  await expect(page.getByTestId("movement-owed")).toContainText("Owes you IQD 300,000.00");
  await page.getByTestId("movement-pay-all").click();
  await expect(page.getByTestId("movement-amount")).toHaveValue("300,000");
  await record();
  await expect(page.getByTestId("cash-notice")).toContainText("RCT-");
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 800,000.00");

  // Baghdad Parts is paid in advance: nothing is open with them.
  await page.getByTestId("cash-new-movement").click();
  await page.getByTestId("movement-kind-supplier").check();
  await page.getByTestId("movement-account").selectOption({ label: "CASH · Cash box (IQD)" });
  await page.getByTestId("movement-supplier").selectOption({ label: "LOCAL · Baghdad Parts" });
  await expect(page.getByTestId("movement-owed")).toContainText("the whole amount is paid in advance");
  await page.getByTestId("movement-amount").fill("50000");
  await record();
  await expect(page.getByTestId("cash-notice")).toContainText("PAY-");
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 750,000.00");

  // The month's rent from the cash box; the dollar box is not offered for it.
  await page.getByTestId("cash-new-movement").click();
  await page.getByTestId("movement-kind-expense").check();
  await expect(page.getByTestId("movement-account").locator("option", { hasText: "USD · Dollar box" })).toHaveCount(0);
  await page.getByTestId("movement-add-kinds").click();
  await page.getByTestId("movement-category").selectOption({ label: "Rent" });
  await page.getByTestId("movement-account").selectOption({ label: "CASH · Cash box (IQD)" });
  await page.getByTestId("movement-amount").fill("100000");
  await record();
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 650,000.00");

  // The owner puts 200 000 into the cash box.
  await page.getByTestId("cash-new-movement").click();
  await page.getByTestId("movement-kind-ownerIn").check();
  await page.getByTestId("movement-account").selectOption({ label: "CASH · Cash box (IQD)" });
  await page.getByTestId("movement-amount").fill("200000");
  await record();
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 850,000.00");

  // The cash paid in was entered by mistake: undone from its row, both boxes get their money back.
  await box("CASH").click();
  const transferRow = page.getByTestId("cash-movement").filter({ hasText: "Transfer out" });
  await expect(transferRow).toHaveCount(1);
  await expectAccessible(page);
  await transferRow.getByTestId("cash-undo-transfer").click();
  await expect(page.getByTestId("undo-transfer-dialog")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("undo-transfer-reason").fill("Entered by mistake");
  await page.getByTestId("save-undo-transfer").click();
  await expect(page.getByTestId("cash-notice")).toContainText("undone");
  await expect(box("CASH").getByTestId("cash-balance")).toHaveText("IQD 2,350,000.00");
  await expect(box("BANK").getByTestId("cash-balance")).toHaveText("IQD 8,680,000.00");
  await expect(page.getByTestId("cash-movement").filter({ hasText: "Transfer undone" })).toHaveCount(1);
  await expect(page.getByTestId("cash-undo-transfer")).toHaveCount(0);

  // The same window in Arabic, reached from the + menu.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByTestId("new-menu").click();
  await page.getByTestId("new-menu-cashMovement").click();
  await expect(page.getByTestId("cash-movement-dialog")).toBeVisible();
  await expect(page.getByTestId("cash-movement-dialog")).toContainText("بين صناديقي وحساباتي");
  await expect(page.getByTestId("cash-movement-dialog")).toContainText("قبض من زبون");
  await expectAccessible(page);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("الصندوق والبنك");
  await expectAccessible(page);
});
