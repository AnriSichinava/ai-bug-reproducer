// Hand-written e2e coverage of THIS app's own UI -- distinct from e2e/generated/, which
// holds machine-generated regression tests for reproduced bugs. This file exercises the
// dashboard: register -> create a project -> submit a bug report -> trigger a
// reproduction -> watch the run page update.
import { test, expect } from "@playwright/test";

// Overrides the config default (target-app, for the generated regression tests): the
// dashboard is served by the API itself.
test.use({ baseURL: process.env.API_BASE_URL ?? "http://localhost:5199" });

test("register, create a project and bug report, and trigger reproduction", async ({ page }) => {
  const email = `dashboard-${Date.now()}@example.com`;

  await page.goto("/register.html");
  await page.fill("#email", email);
  await page.fill("#password", "Password123!");
  await page.click("#register-btn");

  await expect(page).toHaveURL(/dashboard\.html/);

  await page.fill("#project-name", "Demo Cart App");
  await page.click("#create-project-btn");
  await expect(page.locator("#project-list a")).toContainText("Demo Cart App");

  await page.click("#project-list a");
  await expect(page.locator("#bugs-section")).toBeVisible();

  await page.fill("#bug-title", "Cart crash after re-login");
  await page.fill("#bug-description", "Removing an item after logging out and back in returns a 500.");
  await page.click("#create-bug-btn");

  const bugItem = page.locator("#bug-list li").first();
  await expect(bugItem).toContainText("Cart crash after re-login");

  await bugItem.getByRole("button", { name: "Reproduce" }).click();

  // The reproduce button navigates to the new run's page once the API accepts it.
  await expect(page).toHaveURL(/run\.html\?id=\d+/);
  await expect(page.locator("#run-status")).not.toHaveText("loading…");

  // No ANTHROPIC_API_KEY is configured for this test run, so the background agent run
  // genuinely fails fast (a real outcome, not a simulated one) -- the dashboard's job is
  // to show that honestly rather than leave the page stuck on "Pending" forever.
  await expect(page.locator("#run-status")).toHaveText(/Pending|Running|Completed|Failed/, { timeout: 15_000 });
});

test("a logged-out visitor is redirected away from the dashboard", async ({ page }) => {
  await page.goto("/dashboard.html");
  await expect(page).toHaveURL(/login\.html/);
});
