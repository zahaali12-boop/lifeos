import { defineConfig, devices } from "@playwright/test";

/**
 * Browser journeys (ADR-0029): the app served by Vite against a running API (E2E_API_URL, default the local host
 * on 8080, as `make api` starts it). CI starts PostgreSQL, the migrator and the API before this runs.
 */
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:8080";
const port = Number(process.env.E2E_WEB_PORT ?? 5173);

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["github"], ["list"]] : "list",
  timeout: 60_000,
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  // E2E_CHROMIUM_PATH points at a pre-installed Chromium (containers without browser downloads); CI installs the matching build.
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], ...(process.env.E2E_CHROMIUM_PATH ? { launchOptions: { executablePath: process.env.E2E_CHROMIUM_PATH } } : {}) } }],
  webServer: {
    command: `pnpm exec vite --host 127.0.0.1 --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: !process.env.CI,
    env: { VITE_API_BASE_URL: apiUrl },
    timeout: 120_000,
  },
});
