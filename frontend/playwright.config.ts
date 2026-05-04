import { defineConfig, devices } from "@playwright/test";

// E2E config — Vitest covers component-level testing; Playwright is reserved for full-page
// behaviors that depend on real Next.js routing + a real browser layout (P3-12 grid
// virtualization needs `useResizeObserver` to fire against an actual element box).
//
// `webServer` boots `next dev` on 3000 for the test run and tears it down at the end.
// Tests mock the upstream `/api/data/*` endpoints via `page.route()`, so no backend is
// required.

export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:3000",
    trace: "retain-on-failure",
    headless: true,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: {
    command: "npm run dev",
    url: "http://localhost:3000",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
