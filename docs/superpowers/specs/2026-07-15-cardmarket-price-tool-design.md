# Cardmarket price tool (`card_price`)

**Date:** 2026-07-15
**Status:** Approved, ready for implementation

## Problem

Phil buys Magic singles in person (e.g. at MagicCon). His negotiating baseline is the price a
**German seller** asks on **Cardmarket** for the **English** printing. He wants to ask Erda — often by
voice over WhatsApp, so card names arrive garbled — "how much is <card>?" and get that baseline back.

Two facts constrain the design (both verified on 2026-07-15):

1. **Scryfall resolves the card cleanly.** `GET /cards/named?exact=<name>&set=<set>` returns the exact
   printing, its Cardmarket product URL (`Products?idProduct=<id>`), and `prices.eur` — which is the
   Cardmarket **EUR trend** for that card (`Ragavan, Nimble Pilferer (mh2)` → `34.13`). Fuzzy matching
   (`?fuzzy=`) is forgiving of misheard names but can land on the wrong print (a token with no
   Cardmarket link), so exact-first with a fuzzy fallback is required.
2. **Cardmarket blocks plain HTTP.** A direct `curl`/`HttpClient` GET of a product page returns
   `HTTP 403` behind Cloudflare (captcha). Reading live German-seller listings therefore **requires a
   real browser**. Erda already runs a Playwright MCP browser (gated on `Browser:Enabled`) whose
   persistent `user-data-dir` warms a Cloudflare cookie, so repeat fetches pass.

## Approach

A single MAF tool **`card_price`** that: resolves the card on Scryfall (capturing the EUR trend as an
instant baseline), then drives the **existing Playwright MCP browser deterministically** (no LLM loop)
to Cardmarket's **card-level `/Cards/<slug>` page** — all printings — filtered to **German sellers +
English cards**, scrapes the lowest N offers, and returns them as WhatsApp-friendly text. When
Cardmarket blocks or the page layout shifts, it **degrades to the trend price + a tappable
Germany/English-filtered CM link** — never nothing.

**All printings, by card name.** Pricing the card-level page (not a per-printing product page) is what
you want for a buying baseline: it surfaces the cheapest German/English copy across every set. The
filtered URL is `https://www.cardmarket.com/en/Magic/Cards/<slug>?sellerCountry=7&language=1`, where
`<slug>` is derived from the card name (diacritics folded, spaces/hyphens → `-`, other punctuation
dropped — e.g. `Ragavan, Nimble Pilferer` → `Ragavan-Nimble-Pilferer`). That one URL is **both** the
scrape target and the tappable fallback link, and needs no `idProduct` redirect handling.

Disambiguation is delegated to the orchestrator, not handled by the tool: when the card is unclear the
tool returns a **candidates list instead of prices**, and Erda asks Phil which card he means, then
calls `card_price` again with the confirmed name (+ set).

### Rejected alternatives

- **Plain `HttpClient` scrape of Cardmarket.** Dead on arrival — Cloudflare 403 (verified).
- **Scryfall EUR trend only (no scrape).** Trend is the *overall* Cardmarket trend, not the
  German-seller baseline Phil negotiates against. Kept only as the fallback layer inside this tool.
- **Agentic `browse_web` goal per lookup.** Robust to layout changes but slow and token-heavy for a
  price check standing at a booth. The deterministic direct-MCP fetch is one navigation + one
  `evaluate`.

## Components

Three single-purpose units. Scryfall resolution is host-agnostic (lives in `Erda.Core`, unit-testable
with a fake `HttpMessageHandler`); the Cardmarket scrape and the tool depend on the browser MCP (live
in `Erda.Agents.Tools`).

### 1. `ScryfallClient` (`Erda.Core/Services`) — card resolution

Interface `IScryfallClient` + implementation over `IHttpClientFactory` (named client, same pattern as
`UrlFetcher`: real browser User-Agent + `Accept`; Scryfall also wants a descriptive UA and ~100ms
throttle between calls).

```
Task<CardResolution> ResolveAsync(string name, string? set, CancellationToken ct)
```

`CardResolution` is a discriminated result, one of:

- **`Match`** — a single confident printing: `{ Name, SetCode, SetName, CardmarketUrl, EurTrend?,
  EurFoilTrend? }`. `CardmarketUrl` is the product URL from `purchase_uris.cardmarket`, or null if the
  print has none.
- **`Candidates`** — ambiguous: `{ Names: string[] }` (top ~5–8 from `/cards/search`), returned so the
  orchestrator can ask Phil to pick.
- **`NotFound`** — no match and no candidates.

Resolution logic:

1. `GET /cards/named?exact=<name>` (+ `&set=<set>` when `set` given). On 200 → `Match`.
2. On 404 → `GET /cards/named?fuzzy=<name>`. On 200 **and** the result is a real card **with** a
   Cardmarket product link → `Match`.
3. Otherwise → `GET /cards/search?q=<name>` (order by relevance). ≥1 result → `Candidates` (unique
   card names, capped). 0 results / 404 → `NotFound`.

Scryfall JSON is parsed with `System.Text.Json` into minimal DTOs. `prices.eur` / `prices.eur_foil`
are strings or null. `purchase_uris.cardmarket` is the CM link (may itself carry `referrer=scryfall`
tracking params — kept as-is; filter params are appended by component 2).

### 2. URL building + live scrape (`Erda.Agents/Tools`)

**`CardmarketUrl`** (static, unit-testable, no browser): `Slug(cardName)` derives the CM card slug
(NFD-fold diacritics, drop combining marks, letters/digits kept, spaces/hyphens → single `-`, other
punctuation dropped); `CardPage(cardName, language)` → the filtered card-level URL
(`sellerCountry=7` + `language`). One place owns the slug rules + filter IDs.

**`CardmarketPriceService`** depends on `IBrowserMcp` and invokes the MCP tools **directly as
`AIFunction`s** (found by name in `IBrowserMcp.Tools`: `browser_navigate`, `browser_evaluate`) — no
orchestrator/LLM involved.

```
Task<IReadOnlyList<CardmarketOffer>> GetGermanOffersAsync(
    string cardPageUrl, int count, CancellationToken ct)
```

`CardmarketOffer` = `{ decimal Price, string Condition, string Seller }`. The URL is already filtered
(built by `CardmarketUrl.CardPage`), so the service just navigates + scrapes — no redirect handling.

Flow:

1. `browser_navigate` to the card-level filtered URL; allow the offer table to settle.
2. `browser_evaluate` a **single centralized JS snippet** (one `const` string — the one fragile spot)
   that reads the offer rows into JSON: `[{ price, condition, seller }]`, defensively (tolerant
   selectors; `return out` — an object, not a string — so the MCP's `### Result` section holds clean
   JSON). Parse the German-formatted price `"31,00 €"` → `31.00` in C#. Cap at `count` rows.
3. A C# parser isolates the MCP response's `### Result` section, slices the JSON array, and maps it to
   `CardmarketOffer` records. **The `sellerCountry=7` / `language` IDs and the offer-row selector are
   verified once against a live page during implementation** (CM blocks curl; the warmed browser can).

A private `SemaphoreSlim` serializes navigations, since the browser tab is shared with `browse_web`.
Any failure (browser not connected, Cloudflare challenge, empty/changed DOM, timeout) throws or
returns empty so the tool falls back — this service never blocks the whole tool.

### 3. `CardPriceTool` (`Erda.Agents/Tools`) — the MAF tool

`AsTools()` → `[ AIFunctionFactory.Create(CardPrice, "card_price") ]`, following `NotifyTools` /
`ReminderTools`. Depends on `IScryfallClient` + `CardmarketPriceService`.

```
card_price(string name, string? set = null, int count = 5, string? language = "en")
```

`language` maps `"en"→1`, `"de"→3` (extendable); default English. `[Description]` on the tool tells
Erda: **when the result is a candidates list, confirm which card with Phil before calling again.**

Orchestration:

1. `ResolveAsync`. On `Candidates` → return a clearly-marked "did you mean" string listing the names
   (no prices). On `NotFound` → "couldn't find a card named …".
2. On `Match` → build the card-level filtered URL with `CardmarketUrl.CardPage(match.Name, language)`
   (from the name, so a null Scryfall `CardmarketUrl` doesn't block) and `GetGermanOffersAsync` it. On
   success → formatted offer list + trend line + filtered CM link. On failure/empty → **fallback**:
   trend price (if known) + the same Germany/English filtered CM link for Phil to tap. When no set was
   passed, the trend's printing is stated ("assumed <SET>; say the set to pick another").

Output is plain text tuned for WhatsApp:

```
Ragavan, Nimble Pilferer (MH2) — English, DE sellers
 1. €31,00 · NM · seller123
 2. €31,50 · EX · otherseller
 …
Trend: €34,13 · <germany+english CM link>
```

### Wiring

- `AddErdaCore`: `services.AddHttpClient(nameof(ScryfallClient));`
  `services.AddSingleton<IScryfallClient, ScryfallClient>();`
- `AddErdaAgents` (or the agents DI extension): register `CardmarketPriceService` + `CardPriceTool`.
- `ErdaAgent.Create`: add `card_price` **only when the browser is exposed** — reuse the existing
  `BrowserAgent.ShouldExpose(mcp)` / `browseTool is not null` gate, so the tool never appears when the
  browser is off (its scrape can't work). Add right after the `browseTool` block.

## Error handling

| Situation | Result |
|---|---|
| Card not found, no candidates | `NotFound` → "couldn't find a card named X" |
| Ambiguous / garbled voice name | `Candidates` → "did you mean …" list; Erda asks Phil |
| CM Cloudflare / DOM changed / browser down / empty / wrong slug | Trend (if any) + tappable Germany+English card-level CM link |
| Scryfall HTTP error | Surface a short "couldn't reach Scryfall" message |

## Testing

- **`ScryfallClientTests`** (unit, fake `HttpMessageHandler` like existing tests): exact hit →
  `Match` with trend + CM URL; 404→fuzzy hit → `Match`; fuzzy token-with-no-CM-link → `Candidates`;
  search-only → `Candidates`; nothing → `NotFound`; `set` param forwarded to the query.
- **`CardmarketUrl` slug tests**: card names → CM slugs (spaces, commas, apostrophes, diacritics, split
  `//` cards) and the full filtered `CardPage` URL.
- **Offer-parser tests**: the full multi-section MCP `browser_evaluate` blob → isolate `### Result`,
  slice the JSON array, parse (German price `"31,00 €"`, thousands separator, missing condition, rows
  beyond `count`) → correct `CardmarketOffer` list + `count` cap; garbage/no-Result → empty.
- **`CardPriceTool` formatting/fallback tests**: `Candidates` → "did you mean" text (no prices);
  card-level URL built from name even when Scryfall has no product link; scrape-empty → trend + link
  fallback; happy path → offer list format. Use a fake `IScryfallClient` and a fake
  `ICardmarketPriceService` so no live browser/network is needed.
- **Wiring test** (like `VaultEditorWiringTests` / `BrowserAgentGateTests`): `card_price` present when
  browser exposed, absent when off.
- The live `browser_navigate`/`evaluate` path stays thin and is **manually integration-verified**
  against a real Cardmarket page (selector + filter IDs) — not covered by automated tests.

## Out of scope (YAGNI)

Non-English/German languages beyond the `en`/`de` map, foil-vs-nonfoil selection, condition filters,
multi-card batch lookups, price history/graphs, and any Cardmarket login/checkout. The tool reports a
baseline; Phil buys in person.
