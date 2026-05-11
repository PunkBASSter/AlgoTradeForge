import { defineConfig } from "vitest/config";
import path from "path";

export default defineConfig({
  test: {
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
    globals: true,
    // Playwright specs live under tests/e2e/ and use @playwright/test, not Vitest. Without
    // this exclude, Vitest discovers the .spec.ts files and fails on Playwright globals.
    exclude: ["**/node_modules/**", "**/dist/**", "tests/e2e/**"],
  },
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "."),
    },
  },
});
