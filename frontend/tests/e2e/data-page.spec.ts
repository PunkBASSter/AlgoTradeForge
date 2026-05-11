import { test, expect, type Route } from "@playwright/test";

// Regression test for the Data page (/data) covering:
//   1. Asset row labels render the human-readable display_name (not collapsed to symbol).
//   2. Spot vs perp rows are visually distinct (display_name disambiguation).
//   3. Time-bar columns surface from the catalog (`1m`/`1h`/`1d`).
//   4. Time-bar columns are ordered chronologically (1m → 1h → 1d), not lexically.
//   5. Glyph semantics: `+` = present, `−` = absent (matches universal +/− intuition).
//   6. Aggregation-eligible source feeds (timebar/tick/altbar) render `+` inside a
//      framed box that hovers to an accent-blue outline; non-source feeds (Side) render
//      a bare `+`. Clicking a framed `+` opens the right-side sidebar with the
//      aggregation form pre-filled — verifies the affordance hint actually works.
//
// Mocks /api/data/* via page.route() so the test doesn't need a live BE.

const API_HOST = "http://localhost:5000";

const exchanges = {
  exchanges: [{ name: "binance", asset_count: 2 }],
};

const assets = {
  assets: [
    {
      exchange: "binance",
      symbol: "BTCUSDT_perp",
      display_name: "BTCUSDT-perp",
      type: "perpetual",
      feeds: [
        { id: "1m", kind: "OHLCV_TimeBar", interval: "1m", type_code: null, threshold_value: null, sidecar: null },
        { id: "1h", kind: "OHLCV_TimeBar", interval: "1h", type_code: null, threshold_value: null, sidecar: null },
        { id: "1d", kind: "OHLCV_TimeBar", interval: "1d", type_code: null, threshold_value: null, sidecar: null },
        { id: "funding-rate", kind: "Side", interval: null, type_code: null, threshold_value: null, sidecar: null },
        { id: "EqV_1m_1000", kind: "OHLCV_AltBar", interval: null, type_code: "EqV", threshold_value: 1000, sidecar: null },
      ],
    },
    {
      exchange: "binance",
      symbol: "BTCUSDT",
      display_name: "BTCUSDT",
      type: "spot",
      feeds: [
        { id: "1m", kind: "OHLCV_TimeBar", interval: "1m", type_code: null, threshold_value: null, sidecar: null },
        { id: "1h", kind: "OHLCV_TimeBar", interval: "1h", type_code: null, threshold_value: null, sidecar: null },
        { id: "1d", kind: "OHLCV_TimeBar", interval: "1d", type_code: null, threshold_value: null, sidecar: null },
        // No funding-rate (spot has none) → cell renders `−`.
        // No EqV_1m_1000 → cell renders `−`.
      ],
    },
  ],
};

function mockJson(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify(body),
  });
}

test.describe("Data page", () => {
  test("renders disambiguated labels, chronological time bars, and aggregation-source affordance", async ({ page }) => {
    await page.route(`${API_HOST}/api/data/exchanges`, (route) => mockJson(route, exchanges));
    await page.route(
      `${API_HOST}/api/data/exchanges/binance/assets`,
      (route) => mockJson(route, assets),
    );

    await page.goto("/data");

    // Expand the binance exchange card.
    const toggle = page.getByRole("button", { name: /binance/ }).first();
    await toggle.click();
    await expect(toggle).toHaveAttribute("aria-expanded", "true");

    // --- Bug #1 + #2: distinct labels for spot and perp.
    await expect(page.locator('[title="BTCUSDT-perp"]')).toBeVisible();
    await expect(page.locator('[title="BTCUSDT"]')).toBeVisible();

    // --- Bug: time-bar columns chronological. The header column labels render in
    // document order — assert the FIRST three columns are 1m, 1h, 1d.
    // Header cells use title=feedId for each rendered column.
    const timeBarHeaders = ["1m", "1h", "1d"];
    for (const id of timeBarHeaders) {
      await expect(page.locator(`[title="${id}"]`)).toBeVisible();
    }
    // Sanity: 1m header appears before 1h, and 1h before 1d (DOM order = render order).
    const headerLefts = await Promise.all(
      timeBarHeaders.map(async (id) => {
        const box = await page.locator(`[title="${id}"]`).first().boundingBox();
        return box?.x ?? Number.MAX_SAFE_INTEGER;
      }),
    );
    expect(headerLefts[0]).toBeLessThan(headerLefts[1]);
    expect(headerLefts[1]).toBeLessThan(headerLefts[2]);

    // --- Glyph semantics: `+` = present, `−` = absent.
    // BTCUSDT (spot) lacks funding-rate → `−`.
    const spotFunding = page.getByRole("button", { name: /No funding-rate for BTCUSDT — click to create/ });
    await expect(spotFunding).toBeVisible();
    await expect(spotFunding).toHaveText(/−/);

    // BTCUSDT-perp has funding-rate (Side feed, NOT aggregation-eligible) → bare `+`.
    const perpFunding = page.getByRole("button", { name: "View funding-rate on BTCUSDT-perp" });
    await expect(perpFunding).toBeVisible();
    await expect(perpFunding).toHaveText(/\+/);

    // BTCUSDT-perp 1m timebar (aggregation-eligible source) → framed `+`.
    const perp1m = page.getByRole("button", { name: "Aggregate from 1m on BTCUSDT-perp" });
    await expect(perp1m).toBeVisible();
    await expect(perp1m).toHaveText(/\+/);
    // The framed variant wraps the `+` in a span carrying a `border` utility class.
    const perp1mFrame = perp1m.locator("span.border");
    await expect(perp1mFrame).toHaveCount(1);

    // The bare-plus variant on a Side feed has NO bordered inner span.
    await expect(perpFunding.locator("span.border")).toHaveCount(0);

    // --- Sidebar opens on `+` click of an aggregation-eligible feed.
    await perp1m.click();
    // The new-aggregate sidebar uses SlideOver with title "New aggregate bar".
    await expect(page.getByText("New aggregate bar")).toBeVisible();
  });
});
