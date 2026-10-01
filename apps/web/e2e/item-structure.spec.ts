import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * The item's structure against the real API: an assembly's bill of material (a first version with scrap, its explosion
 * for ten, a second version activated in its place), a size attribute and a variant (a duplicate combination refused),
 * a substitute, and a picture added and removed. English, with axe.
 */
const password = "correct-horse-battery-staple";
// A 1×1 transparent PNG.
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=", "base64");

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

async function item(page: Page, code: string, name: string, type = "stock"): Promise<void> {
  await page.getByTestId("new-item").click();
  await page.getByTestId("item-code").fill(code);
  await page.getByTestId("item-name-en").fill(name);
  await page.getByRole("dialog").getByLabel("Type", { exact: true }).selectOption(type);
  await page.getByTestId("save-item").click();
  await expect(page.getByTestId("item-detail")).toBeVisible();
}

test("English: an assembly's bill of material, variants by attribute, a substitute and a picture", async ({ page }) => {
  const slug = `itm-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  await page.addInitScript(() => { window.localStorage.setItem("quicker.language", "en"); });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Items " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(`owner-${slug}@example.test`);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  // Signing up provisions a whole workspace, slow on a cold API.
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });

  await nav(page, "Items");
  await item(page, "BOTTLE", "Bottle 1L");
  await closeDialog(page);
  await item(page, "CAP", "Bottle cap");
  await closeDialog(page);
  await item(page, "FLASK", "Flask 1L");
  await closeDialog(page);

  // The assembly: six bottles and six caps (5% of caps are lost) make one pack.
  await item(page, "PACK6", "Six-pack", "assembly");
  await page.getByTestId("item-tab-bom").click();
  await page.getByTestId("new-bom-version").click();
  await page.getByTestId("bom-line-item-0").fill("BOTTLE");
  await page.getByTestId("bom-line-qty-0").fill("6");
  await page.getByTestId("add-bom-line").click();
  await page.getByTestId("bom-line-item-1").fill("CAP");
  await page.getByTestId("bom-line-qty-1").fill("6");
  await page.getByTestId("bom-line-scrap-1").fill("5");
  await expectAccessible(page);
  await page.getByTestId("save-bom").click();
  await expect(page.getByTestId("bom-version-row")).toHaveCount(1);
  await expect(page.getByTestId("bom-version-row")).toContainText("Active");
  await page.getByTestId("explode-qty").fill("10");
  const explosion = page.getByTestId("bom-explosion");
  await expect(explosion.getByTestId("explosion-row").filter({ hasText: "Bottle 1L" })).toContainText(/60 PCS.*60 PCS/);
  await expect(explosion.getByTestId("explosion-row").filter({ hasText: "Bottle cap" })).toContainText(/60 PCS.*63 PCS/);
  await expectAccessible(page);

  // A second version saved inactive, then made active in place of the first.
  await page.getByTestId("new-bom-version").click();
  await page.getByTestId("bom-line-qty-0").fill("12");
  await page.getByTestId("bom-output").fill("2");
  await page.getByRole("checkbox", { name: "Make this version active when saved" }).uncheck();
  await page.getByTestId("save-bom").click();
  await expect(page.getByTestId("bom-version-row")).toHaveCount(2);
  const second = page.getByTestId("bom-version-row").filter({ hasText: "Version 2" });
  await second.getByTestId("activate-bom").click();
  await expect(second).toContainText("Active");
  await expect(page.getByTestId("bom-version-row").filter({ hasText: "Version 1" })).toContainText("Inactive");
  await closeDialog(page);

  // A size attribute with its values, then variants of the bottle; the same combination twice is refused.
  await page.getByTestId("master-data").click();
  await page.getByTestId("attribute-code").fill("SIZE");
  await page.getByTestId("attribute-name-en").fill("Size");
  await page.getByTestId("attribute-values").fill("Small, Large");
  await page.getByTestId("save-attribute").click();
  await expect(page.getByTestId("attribute-row")).toContainText("Small");
  await expectAccessible(page);
  await closeDialog(page);
  await page.getByRole("grid").getByText("BOTTLE", { exact: true }).dblclick();
  await page.getByTestId("item-tab-variants").click();
  await page.getByTestId("new-variant").click();
  await expect(page.getByTestId("variant-sku")).toHaveValue("BOTTLE-");
  await page.getByTestId("variant-sku").fill("BOTTLE-S");
  await page.getByTestId("variant-attr-SIZE").selectOption("SMALL");
  await page.getByTestId("save-variant").click();
  await expect(page.getByTestId("variant-row")).toHaveCount(1);
  await expect(page.getByTestId("variant-row")).toContainText("SIZE: Small");
  await page.getByTestId("new-variant").click();
  await page.getByTestId("variant-sku").fill("BOTTLE-S2");
  await page.getByTestId("variant-attr-SIZE").selectOption("SMALL");
  await page.getByTestId("save-variant").click();
  await expect(page.getByTestId("variant-editor").getByRole("alert")).toContainText("same attribute values");
  await page.getByTestId("variant-attr-SIZE").selectOption("LARGE");
  await page.getByTestId("save-variant").click();
  await expect(page.getByTestId("variant-row")).toHaveCount(2);
  await expectAccessible(page);

  // A substitute for the bottle.
  await page.getByTestId("item-tab-substitutes").click();
  await page.getByTestId("substitute-code").fill("FLASK");
  await page.getByTestId("add-substitute").click();
  await expect(page.getByTestId("substitute-row")).toHaveCount(1);
  await expect(page.getByTestId("substitute-row")).toContainText("FLASK");

  // A picture: added, shown, removed.
  await page.getByTestId("item-image-input").setInputFiles({ name: "bottle.png", mimeType: "image/png", buffer: png });
  await expect(page.getByTestId("item-image").getByRole("img", { name: "Picture of Bottle 1L" })).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("remove-item-image").click();
  await expect(page.getByTestId("item-image").getByRole("img")).toHaveCount(0);
});
