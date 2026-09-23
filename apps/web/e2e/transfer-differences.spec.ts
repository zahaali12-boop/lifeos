import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * A two-step transfer that does not go to plan, against the real API: 40 requested, 35 shipped, a first receipt of 20
 * with 5 missing (written off from transit with a shortage reason and its note), then the remaining 10 received in
 * full. The lines, the status and the stock in each warehouse show exactly that, and the screens pass axe.
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

async function closeDialog(page: Page): Promise<void> {
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

async function warehouse(page: Page, code: string, name: string, kind = "standard"): Promise<void> {
  await page.getByTestId("new-warehouse").click();
  await page.getByTestId("warehouse-code").fill(code);
  await page.getByTestId("warehouse-name-en").fill(name);
  await page.getByRole("dialog").getByLabel("Kind").selectOption(kind);
  await page.getByTestId("save-warehouse").click();
  await expect(page.getByTestId("warehouse-detail")).toBeVisible();
  await closeDialog(page);
}

test("English: ship part of a transfer, receive with a reasoned shortage, then receive the rest", async ({ page }) => {
  const slug = `trf-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Transfers " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Companies");
  await page.getByTestId("new-company").click();
  await page.getByLabel(/^Code/).fill("TRF");
  await page.getByLabel(/Legal name \(English\)/).fill("Transfer Co.");
  await page.getByTestId("save-company").click();
  await expect(page.getByRole("grid")).toContainText("TRF");
  await nav(page, "Chart of accounts");
  await page.getByTestId("create-chart").click();
  await expect(page.getByTestId("account-row").first()).toBeVisible();

  await nav(page, "Warehouses");
  await warehouse(page, "MAIN", "Main warehouse");
  await warehouse(page, "EAST", "East branch");
  await warehouse(page, "ROAD", "On the road", "in_transit");

  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("JUICE");
  await page.getByTestId("item-name-en").fill("Orange juice 1L");
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
  await closeDialog(page);

  // Reason codes: opening stock for the adjustment, and a shortage reason that asks for a note.
  await nav(page, "Adjustments");
  await page.getByTestId("reason-codes").click();
  await page.getByTestId("reason-code").fill("INIT");
  await page.getByTestId("reason-name-en").fill("Opening stock");
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(1);
  await page.getByTestId("reason-code").fill("LOST");
  await page.getByLabel(/Applies to/).selectOption("shortage");
  await page.getByTestId("reason-name-en").fill("Lost in transit");
  await page.getByRole("dialog").getByLabel(/note/i).check();
  await page.getByTestId("save-reason").click();
  await expect(page.getByTestId("reason-row")).toHaveCount(2);
  await closeDialog(page);
  await page.getByTestId("new-adjustment").click();
  await page.getByTestId("adjustment-warehouse").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("line-item-0").fill("JUICE");
  await page.getByTestId("line-qty-0").fill("50");
  await page.getByTestId("line-cost-0").fill("1500");
  await page.getByTestId("line-reason-0").selectOption({ label: "INIT · Opening stock" });
  await page.getByTestId("save-adjustment").click();
  await page.getByTestId("submit-adjustment").click();
  await expect(page.getByTestId("adjustment-detail").getByTestId("doc-status")).toContainText("Posted");
  await closeDialog(page);

  // A two-step transfer of 40; only 35 go on the truck.
  await nav(page, "Transfers");
  await page.getByTestId("new-transfer").click();
  await page.getByTestId("transfer-from").selectOption({ label: "MAIN · Main warehouse" });
  await page.getByTestId("transfer-to").selectOption({ label: "EAST · East branch" });
  await page.getByTestId("transfer-kind").selectOption("two_step");
  await page.getByTestId("transfer-transit").selectOption({ label: "ROAD · On the road" });
  await page.getByTestId("line-item-0").fill("JUICE");
  await page.getByTestId("line-qty-0").fill("40");
  await page.getByTestId("save-transfer").click();
  await expect(page.getByTestId("transfer-detail")).toBeVisible();
  // Shipped one day, received the next, within one month. A receipt dated on its ship date under average cost trips
  // open issue I3 in the costing engine (recorded in PROGRESS for the costing design pass).
  const now = new Date();
  const iso = (d: Date): string => `${String(d.getFullYear())}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  const first = now.getDate() === 1;
  const shipOn = first ? now : new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  const receiveOn = first ? new Date(now.getFullYear(), now.getMonth(), 2) : now;
  await page.getByTestId("transfer-date").fill(iso(shipOn));
  await page.getByTestId("ship-partial").click();
  await expect(page.getByTestId("ship-qty-JUICE")).toHaveValue("40");
  await page.getByTestId("ship-qty-JUICE").fill("41");
  await expect(page.getByTestId("transfer-ship-quantities")).toContainText("more than was requested");
  await expect(page.getByTestId("confirm-ship")).toBeDisabled();
  await page.getByTestId("ship-qty-JUICE").fill("35");
  await expectAccessible(page);
  await page.getByTestId("confirm-ship").click();
  await expect(page.getByTestId("transfer-detail").getByTestId("doc-status")).toContainText("Shipped");

  // First receipt: 20 arrive, 5 are missing; the shortage needs its reason and, for LOST, a note.
  await page.getByTestId("transfer-date").fill(iso(receiveOn));
  await page.getByTestId("receive-partial").click();
  await expect(page.getByTestId("receive-qty-JUICE")).toHaveValue("35");
  await page.getByTestId("receive-qty-JUICE").fill("20");
  await page.getByTestId("short-qty-JUICE").fill("5");
  await expect(page.getByTestId("confirm-receive")).toBeDisabled();
  await expect(page.getByTestId("transfer-receive-quantities")).toContainText("Choose why the rest is short");
  await page.getByTestId("shortage-reason").selectOption({ label: "LOST · Lost in transit" });
  await expect(page.getByTestId("transfer-receive-quantities")).toContainText("This reason needs a note");
  await expect(page.getByTestId("confirm-receive")).toBeDisabled();
  await page.getByTestId("shortage-note").fill("Carton crushed at the Kut checkpoint");
  await expectAccessible(page);
  await page.getByTestId("confirm-receive").click();
  const detail = page.getByTestId("transfer-detail");
  await expect(detail.getByTestId("doc-status")).toContainText("Partially received");
  await expect(detail.getByRole("row", { name: /JUICE/ })).toContainText(/40.*35.*20.*5/);

  // The rest (10) arrives in full with one click.
  await page.getByTestId("receive-transfer").click();
  await expect(detail.getByTestId("doc-status")).toContainText("Received");
  await expect(detail.getByRole("row", { name: /JUICE/ })).toContainText(/40.*35.*30.*5/);
  await closeDialog(page);

  // Stock: 15 left in MAIN, 30 in EAST, nothing left on the road.
  await nav(page, "Stock");
  await page.getByTestId("stock-search").fill("JUICE");
  const grid = page.getByRole("grid");
  await expect(grid.getByRole("row", { name: /MAIN/ })).toContainText("15");
  await expect(grid.getByRole("row", { name: /EAST/ })).toContainText("30");
  await expect(grid.getByRole("row", { name: /ROAD/ })).toContainText("0 PCS");
});
