import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Comments and history on a record, and the audit trail behind them, against the real API: a comment on an item
 * that mentions a member (who is notified), a reply, an edit and a removal; the item renamed, so its history shows
 * the field that changed and opens the audit event in full; the audit explorer filtered by action and narrowed to
 * the record. Then the explorer in Arabic. Every screen passes axe.
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

test("English: a comment with a mention, a reply, an edit and a removal; the record's history and the audit event in full", async ({ page }) => {
  test.setTimeout(120_000);
  const slug = `dsc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Discussion " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Items");
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill("TEA");
  await page.getByTestId("item-name-en").fill("Tea");
  await page.getByTestId("save-item").click();
  const detail = page.getByTestId("item-detail");
  await expect(detail).toBeVisible();

  // A comment that mentions the owner: the mention writes "@Owner" into the text and shows who will be notified.
  await page.getByTestId("item-tab-discussion").click();
  await expect(detail.getByTestId("comments")).toContainText("No comments yet.");
  await page.getByTestId("comment-new-body").fill("Check the supplier price before the next order.");
  await page.getByTestId("comment-new-mention").selectOption({ label: "Owner" });
  await expect(page.getByTestId("comment-new-body")).toHaveValue("Check the supplier price before the next order. @Owner ");
  await expect(page.getByTestId("mention-chip")).toHaveText("Owner");
  await expectAccessible(page);
  await page.getByTestId("comment-new-send").click();
  const first = page.getByTestId("comment").first();
  await expect(first.getByTestId("comment-body")).toHaveText("Check the supplier price before the next order. @Owner");
  await expect(first.getByTestId("comment-mentions")).toHaveText("Notified Owner");
  await expect(page.getByTestId("comment-new-body")).toHaveValue("");

  // A reply sits under the comment it answers.
  await first.getByTestId("comment-reply").click();
  await page.getByTestId("comment-reply-box-body").fill("Price confirmed at 950.");
  await page.getByTestId("comment-reply-box-send").click();
  await expect(page.getByTestId("comment")).toHaveCount(2);
  await expect(first.getByTestId("comment")).toContainText("Price confirmed at 950.");

  // The first comment edited (marked as such); the reply removed, keeping its place.
  await first.getByTestId("comment-edit-open").first().click();
  await page.getByTestId("comment-edit-body").fill("Check the supplier price before the October order. @Owner");
  await page.getByTestId("comment-edit-send").click();
  await expect(first.getByTestId("comment-body").first()).toHaveText("Check the supplier price before the October order. @Owner");
  await expect(first.getByTestId("comment-edited").first()).toHaveText("edited");
  const reply = first.getByTestId("comment").first();
  await reply.getByTestId("comment-remove").click();
  await reply.getByTestId("comment-remove-confirm").click();
  await expect(reply.getByTestId("comment-removed")).toHaveText("This comment was removed.");
  await expect(page.getByTestId("comments").getByRole("heading")).toHaveText("Comments (1)");
  await expectAccessible(page);

  // The item renamed: its history shows the change, next to the comment activity, newest first.
  await page.getByTestId("edit-item").click();
  await page.getByTestId("item-name-en").fill("Black tea");
  await page.getByTestId("save-item").click();
  await expect(detail).toBeVisible();
  await page.getByTestId("item-tab-history").click();
  const history = page.getByTestId("record-history");
  const updated = history.getByTestId("history-audit").filter({ hasText: "Updated" });
  await expect(updated.getByTestId("history-change").filter({ hasText: "Tea" }).first()).toContainText("Black tea");
  await expect(history.getByTestId("history-audit").filter({ hasText: "Created" })).toHaveCount(1);
  await expect(history.getByTestId("history-activity").filter({ hasText: "Owner commented" })).toHaveCount(2);
  await expect(history.getByTestId("history-activity").filter({ hasText: "Comment removed" })).toHaveCount(1);
  await expectAccessible(page);

  // The event in full: the changed field, the request it came from and its place in the chain.
  await updated.getByTestId("history-open").click();
  const event = page.getByTestId("audit-event-detail");
  await expect(event.getByRole("heading", { level: 2 })).toHaveText("Updated Item TEA");
  await expect(event.getByTestId("audit-change-row").filter({ hasText: "Black tea" })).toHaveCount(1);
  await expect(event.getByTestId("audit-hash")).toHaveText(/^[0-9a-f]{64}$/);
  await expectAccessible(page);
  await event.getByRole("button", { name: "Close", exact: true }).last().click();
  await expect(event).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  // The mention reached the owner's inbox.
  await nav(page, "Notifications");
  await expect(page.getByTestId("inbox")).toContainText("Owner mentioned you");

  // The audit explorer: labelled actions and record types, filtered by action, then narrowed to the item.
  await nav(page, "Audit trail");
  await page.getByTestId("audit-action").fill("Updated");
  const grid = page.getByRole("grid");
  await expect(grid.getByRole("row").filter({ hasText: "TEA" })).toHaveCount(1);
  await expect(grid).not.toContainText("Created");
  await grid.getByRole("row").filter({ hasText: "TEA" }).dblclick();
  await expect(page.getByTestId("audit-event-detail").getByTestId("audit-change-row").first()).toBeVisible();
  await page.getByTestId("audit-show-record").click();
  await expect(page.getByTestId("audit-record-filter")).toContainText("Item");
  await page.getByTestId("audit-action").fill("");
  await expect(grid.getByRole("row").filter({ hasText: "TEA" })).toHaveCount(2);
  await expect(grid.getByRole("row").filter({ hasText: "Created" })).toHaveCount(1);
  await expectAccessible(page);
  await page.getByTestId("audit-clear-record").click();
  await expect(page.getByTestId("audit-entity-type")).toBeVisible();

  // Arabic: the explorer reads right to left with Arabic action and record-type labels.
  await page.getByTestId("language-menu").click();
  await page.getByTestId("language-ar").click();
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByTestId("audit-entity-type").fill("صنف");
  await expect(grid.getByRole("row").filter({ hasText: "تعديل" })).toHaveCount(1);
  await expectAccessible(page);
});
