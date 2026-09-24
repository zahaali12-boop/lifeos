import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { createHmac } from "node:crypto";

/**
 * The person's own account: profile, two-step verification with an authenticator app, a security key (Chromium's
 * virtual authenticator, a real WebAuthn ceremony verified by the server) and recovery codes, signing in with each,
 * sessions and a password change; then a workspace that requires two-step verification, where a member without it
 * sets up an authenticator app while signing in. Security keys need a domain name (browsers refuse them on an IP
 * address), so these journeys run on localhost, which the API allows as an origin in CI.
 */
const password = "correct-horse-battery-staple";
const port = Number(process.env.E2E_WEB_PORT ?? 5173);
test.use({ baseURL: `http://localhost:${String(port)}` });

/** RFC 6238 with the authenticator app defaults (SHA-1, 30 seconds, six digits), as the server checks it. */
function totp(secret: string, at = Date.now()): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const c of secret.replace(/[\s=]/g, "").toUpperCase()) {
    bits += alphabet.indexOf(c).toString(2).padStart(5, "0");
  }
  const key = Buffer.from(Array.from({ length: Math.floor(bits.length / 8) }, (_, i) => parseInt(bits.slice(i * 8, i * 8 + 8), 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const mac = createHmac("sha1", key).update(counter).digest();
  const offset = (mac.at(-1) ?? 0) & 0x0f;
  return String((mac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

/**
 * An authenticator app for the test: the server accepts each 30-second step once and one step either side of now, so
 * the next code is the one for the step after the last used, waiting for time to move on when that is too far ahead.
 */
function authenticator(secret: string): { current: () => string; next: () => Promise<string> } {
  let last = 0;
  return {
    current: () => {
      last = Math.floor(Date.now() / 30_000);
      return totp(secret, last * 30_000);
    },
    next: async () => {
      for (;;) {
        const step = Math.floor(Date.now() / 30_000) + 1;
        if (step > last) {
          last = step;
          return totp(secret, step * 30_000);
        }
        await new Promise((resolve) => setTimeout(resolve, 1_000));
      }
    },
  };
}

async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, serious.map((v) => `${v.id}: ${v.help}\n  ${v.nodes.map((n) => n.target.join(" ")).join("\n  ")}`).join("\n")).toEqual([]);
}

async function signup(page: Page): Promise<string> {
  const slug = `acct-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const email = `owner-${slug}@example.test`;
  await page.addInitScript(() => { if (!window.localStorage.getItem("quicker.language")) { window.localStorage.setItem("quicker.language", "en"); } });
  await page.goto("/signup");
  await page.getByLabel(/Workspace name/).fill("Account " + slug);
  await page.getByLabel(/^Slug/).fill(slug);
  await page.getByLabel(/Your name/).fill("Owner");
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(password);
  await page.getByRole("button", { name: "Create workspace" }).click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome", { timeout: 20_000 });
  return email;
}

async function signOut(page: Page): Promise<void> {
  await page.getByTestId("account-menu").click();
  await page.getByTestId("logout").click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
}

async function signIn(page: Page, email: string, secret = password): Promise<void> {
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(secret);
  await page.getByRole("button", { name: "Sign in" }).click();
}

async function openAccount(page: Page): Promise<void> {
  await page.getByTestId("account-menu").click();
  await page.getByTestId("my-account").click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("My account");
}

test("English: profile, authenticator app, security key and recovery codes, each used to sign in; sessions; password; Arabic", async ({ page }) => {
  test.setTimeout(120_000);
  // A virtual security key that answers every request, as a person touching their key would.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("WebAuthn.enable");
  await cdp.send("WebAuthn.addVirtualAuthenticator", { options: { protocol: "ctap2", transport: "usb", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });

  const email = await signup(page);
  await openAccount(page);
  await expect(page.getByTestId("two-step-status")).toHaveText("Off");
  await expect(page.getByTestId("session-current")).toBeVisible();
  await expectAccessible(page);

  // Profile: a new display name, shown in the account menu at once.
  await page.getByTestId("profile-name").fill("Owner Renamed");
  await page.getByTestId("profile-save").click();
  await expect(page.getByTestId("profile-saved")).toBeVisible();
  await page.getByTestId("account-menu").click();
  await expect(page.getByRole("menu")).toContainText("Owner Renamed");
  await page.keyboard.press("Escape");

  // An authenticator app: the key read off the screen, the code it gives, ten recovery codes shown once.
  await page.getByTestId("add-totp").click();
  await expect(page.getByTestId("totp-qr")).toBeVisible();
  const app = authenticator((await page.getByTestId("totp-secret").innerText()).replace(/\s/g, ""));
  await page.getByTestId("totp-code").fill(app.current());
  await expectAccessible(page);
  await page.getByTestId("totp-confirm").click();
  await expect(page.getByTestId("recovery-code")).toHaveCount(10);
  const recovery = await page.getByTestId("recovery-code").first().innerText();
  await page.getByTestId("recovery-done").click();
  await expect(page.getByTestId("two-step-status")).toHaveText("On");
  await expect(page.getByTestId("mfa-method")).toHaveCount(1);

  // A security key, registered through the browser and verified by the server.
  await page.getByTestId("add-key").click();
  await page.getByTestId("key-name").fill("Laptop key");
  await page.getByTestId("key-register").click();
  await expect(page.getByTestId("mfa-method")).toHaveCount(2);
  await expect(page.getByTestId("mfa-methods")).toContainText("Laptop key");

  // Signing in with the key.
  await signOut(page);
  await signIn(page, email);
  await expect(page.getByTestId("mfa-step")).toBeVisible();
  await expectAccessible(page);
  await page.getByTestId("mfa-use-key").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // Signing in with a recovery code, which then no longer works.
  await signOut(page);
  await signIn(page, email);
  await page.getByTestId("mfa-code").fill(recovery);
  await page.getByTestId("mfa-verify").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");
  await signOut(page);
  await signIn(page, email);
  await page.getByTestId("mfa-code").fill(recovery);
  await page.getByTestId("mfa-verify").click();
  await expect(page.getByRole("alert")).toBeVisible();
  // …and with the next authenticator code.
  await page.getByTestId("mfa-code").fill(await app.next());
  await page.getByTestId("mfa-verify").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // The account shows when each method was last used; the key is removed; this session is marked.
  await openAccount(page);
  await expect(page.getByTestId("mfa-methods")).not.toContainText("—");
  await expect(page.getByTestId("session-row").filter({ has: page.getByTestId("session-current") })).toContainText("authenticator code");
  await page.getByTestId("mfa-method").filter({ hasText: "Laptop key" }).getByTestId("mfa-remove").click();
  await expect(page.getByTestId("mfa-method")).toHaveCount(1);

  // A new password, then signing in with it.
  await page.getByTestId("password-current").fill(password);
  await page.getByTestId("password-new").fill("a-longer-passphrase-2026");
  await page.getByTestId("password-confirm").fill("a-longer-passphrase-2026");
  await page.getByTestId("password-save").click();
  await expect(page.getByTestId("password-changed")).toBeVisible();
  await signOut(page);
  await signIn(page, email, "a-longer-passphrase-2026");
  await page.getByTestId("mfa-code").fill(await app.next());
  await page.getByTestId("mfa-verify").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");

  // The page in Arabic, right to left, set from the profile.
  await openAccount(page);
  await page.getByTestId("profile-locale").selectOption("ar");
  await page.getByTestId("profile-save").click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("حسابي");
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await expect(page.getByTestId("two-step-status")).toHaveText("مفعّل");
  await expectAccessible(page);
});

test("English: a workspace that requires two-step verification has a member without it set up an authenticator app while signing in", async ({ page }) => {
  test.setTimeout(90_000);
  const email = await signup(page);
  await page.getByRole("navigation").getByRole("link", { name: "Security", exact: true }).click();
  await page.getByTestId("tab-policy").click();
  await page.getByTestId("policy-mfaRequired").check();
  await page.getByTestId("save-policy").click();
  await expect(page.getByTestId("save-policy")).toBeDisabled();

  await signOut(page);
  await signIn(page, email);
  await expect(page.getByTestId("mfa-enroll")).toBeVisible();
  await expect(page.getByTestId("totp-qr")).toBeVisible();
  await expectAccessible(page);
  const app = authenticator((await page.getByTestId("totp-secret").innerText()).replace(/\s/g, ""));
  await page.getByTestId("totp-code").fill(app.current());
  await page.getByTestId("totp-confirm").click();
  await expect(page.getByTestId("recovery-code")).toHaveCount(10);
  await page.getByTestId("recovery-done").click();
  // The code just used cannot be used again: the next one finishes the sign-in.
  await expect(page.getByTestId("mfa-next-code")).toBeVisible();
  await page.getByTestId("mfa-code").fill(await app.next());
  await page.getByTestId("mfa-verify").click();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");
  await openAccount(page);
  await expect(page.getByTestId("two-step-status")).toHaveText("On");
});
