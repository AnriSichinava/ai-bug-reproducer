import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: ".",
  timeout: 15_000,
  reporter: [["list"], ["json", { outputFile: "test-results/results.json" }]],
  use: {
    baseURL: process.env.TARGET_BASE_URL ?? "http://localhost:4000",
    screenshot: "only-on-failure",
  },
});
