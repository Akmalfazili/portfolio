# Zakat on Shares — design record

Companion to [tracker.md](tracker.md). This file existed **before** the code did: it records
what MUIS actually requires, which of its ambiguities we resolved and how, the measured state
of the data the calculation depends on, and the traps that will otherwise cost a debugging
session. §1–§8 are the design as written in advance and are left standing as such; §9 was the
implementation sketch, and [§11](#11-what-shipped-and-what-it-changed) records what actually
shipped against it.

**Status:** **implemented 2026-09-03** — backend, frontend and tests all shipped; see
[§11](#11-what-shipped-and-what-it-changed) for the two places the implementation had to go
beyond this document, and the one place §4.4 turned out to be aspirational.
[§12](#12-the-fx-rate-column-and-the-container-run-2026-09-05) records the **2026-09-05** round:
the FX rate the report uses is now shown on every line, and the calculation was verified against
the real portfolio in a container for the first time.

**Four of the 22 stocks now have a fiscal year end configured** (AAPL, AMZN, ARVLF, AVGO, entered
2026-09-05); **18 still do not**, deliberately: [§8](#8-financial-year-ends-still-to-be-supplied)
requires each one confirmed against the company's own filings first, and a guessed year end
produces a confident wrong number nothing downstream can detect. Until they are entered, every
unset stock reports `FiscalYearEndNotConfigured` and is excluded from the total, which is
therefore **knowingly partial rather than final** — the report says so, via its excluded-asset
caveat. **Entering the remaining 18 is the single thing standing between it and a real answer.**

---

## 1. What MUIS actually says

Source: <https://www.zakat.sg/types-of-zakat/zakat-on-shares/>, read 2026-09-03.
Nisab source: <https://www.zakat.sg/current-past-nisab-values/>, read the same day.

> **Read these pages in a browser.** Both return **HTTP 403** to automated fetchers. A future
> session that tries `WebFetch`/`curl` will burn a turn on it — use the Chrome tools instead.

The rules, as stated:

- **Everything counts.** "All investment tools such as stocks, shares, ETFs, unit trusts,
  REITs, and others are payable for Zakat." Our three Shariah ETFs (HLAL, SPUS, SPWO) are in
  scope exactly like a single-name equity.
- **The rate is 2.5%** — MUIS writes it as `total value × 0.025`.
- **Shares are valued at a point, not at their low.** Zakat on *savings* takes the lowest
  balance across the haul; zakat on *shares* takes the market value **at the end of the haul
  or financial year period**, explicitly "because shares fluctuate in value more rapidly based
  on prevailing market conditions". This is the single most important structural difference and
  it rules out any "lowest value in the period" logic.
- **Haul has two branches.** One company only → 355 days from the date of purchase. **Shares
  from multiple companies → "the Haul period is the end of the financial year of the
  companies."**
- **The multi-company method**, verbatim in structure:
  1. per company, `number of shares × value per unit`;
  2. add the totals of all companies together;
  3. below nisab → nothing payable;
  4. above nisab → `total value × 0.025`.
- **Nisab** is the price of **86 grams of gold**, revised monthly, published on zakat.sg and in
  Berita Harian. **September 2026 = SGD 15,902.** The threshold must be held for one Hijri year
  (355 days). Past values (1970–2025) are a **download, not an API**.

---

## 2. Conventions chosen, and where they diverge from MUIS

Each of these is a decision, not a discovery. The reason matters more than the rule.

### 2.1 Only the multi-company branch is implemented

The portfolio holds 22 stock assets. The single-company "355 days from purchase" branch can
never apply and is deliberately not built. If the portfolio is ever reduced to one company,
this report becomes wrong and must be revisited — it will not tell you that itself.

### 2.2 "End of the financial year of the companies" is read **per company**

MUIS's wording is genuinely ambiguous between *one common year end for the whole holding* and
*each company's own year end*. Secondary write-ups gloss it as a single 31 Dec. We take the
per-company reading: each asset is valued at **its own** last financial year end.

This is the more precise reading and the one requested, but record that it is a reading. The
consequence is stated plainly in [§2.7](#27-this-is-deliberately-not-a-single-point-in-time-valuation).

### 2.3 Crypto follows **no** MUIS rule

MUIS publishes no crypto or digital-asset guidance — searched 2026-09-03, nothing found on
zakat.sg or muis.gov.sg. Other jurisdictions (Selangor, the Federal Territories mufti) have
issued fatwas; Singapore has not.

**Convention adopted:** crypto is valued at **today's** market price, converted to SGD. This
sits closest to zakat on savings/wealth — 2.5% of value once nisab and haul are met — and it
is the only option the data permits, since crypto keeps no price history at all (a locked
one-way door, see `CLAUDE.md` → *Asset class segregation*).

**This is a user convention, not a ruling.** Worth putting to an asatizah. The report should
label the crypto subtotal as such rather than presenting it with the same authority as the
share calculation.

### 2.4 Nisab is **not** encoded

Nisab changes monthly and has no API — only a web page and a spreadsheet download. Hard-coding
SGD 15,902 would be exactly the silent-staleness failure this project keeps re-learning (D10,
D26, D33, D35, D38, D45): a number that looks authoritative, is wrong within a month, and
gives no signal that it went stale.

The report therefore states **the zakatable total and 2.5% of it**. The nisab comparison stays
with the user, against the current figure on zakat.sg. If a nisab field is ever added it must
carry its own as-of date and be visibly editable, never a constant compiled in.

### 2.5 This report is denominated in **SGD**

The app's reporting currency is USD everywhere else (`CostBasisTransactionFactory.ReportingCurrency`).
This report breaks that rule on purpose: nisab is an SGD figure and zakat is paid in SGD, so a
USD answer would have to be converted by hand at exactly the moment precision matters.

### 2.6 Market value only

Fees, dividends received, and realized gains are **not** added. MUIS's share rule is the market
value of the holding at the valuation date, nothing else. Cash proceeds from past sales are
zakatable as *savings*, under a different rule, and are outside this portfolio's data entirely —
the tracker has no cash balance.

### 2.7 This is deliberately not a single point-in-time valuation

Per-company year ends mean AVGO is valued at early November, MSFT at 30 June, Z74 at 31 March,
and crypto at today. The "total" is a sum across different dates. That is the method as
requested and as MUIS's multi-company branch reads — not a bug, and the report must show each
asset's valuation date so it never looks like one.

### 2.8 A position closed **today** can still owe zakat

The valuation quantity is the quantity held **at the financial year end**, not today. Six
assets currently hold zero units (AAPL, CSCO, KO, PINS, TDOC, VZ) yet may have held a position
at their last year end, in which case they contribute. Conversely JNJ, XOM and SPWO are held
today but were bought after their last year end and contribute nothing.

This is counter-intuitive enough that the report must list every stock asset, including the
zero-today ones, rather than filtering to current holdings.

---

## 3. Data model

Two things persist: a financial year end per asset, and a ledger of zakat actually paid. The
report itself is computed on read and stored nowhere.

### 3.1 Financial year end on `Asset`

Two nullable columns, no new table.

```csharp
/// <summary>Calendar month of this company's financial year end, 1-12. Null = not configured.</summary>
public int? FiscalYearEndMonth { get; set; }

/// <summary>Day of month of this company's financial year end, 1-31. Null = not configured.</summary>
public int? FiscalYearEndDay { get; set; }
```

- **Recurring month/day, not a stored date.** An explicit `LastFiscalYearEndDate` would be exact
  for 52/53-week calendars but would need re-entering across ~20 assets every year, and a
  forgotten one values against a stale date **silently**. Month/day is self-maintaining; its
  inaccuracy is bounded (see [§8](#8-financial-year-ends-still-to-be-supplied)) and visible,
  because the report publishes the close date it actually used.
- **Null means *not configured*.** It is a distinct, reported state — never "assume 31 Dec".
  Guessing a year end produces a plausible wrong number, which is worse than an excluded asset.
- **Always null for crypto.** Crypto is valued at today and has no year end.
- Both columns must be set or both null; one-of-two is invalid.
- Migration: `AddAssetFiscalYearEnd`, under
  `src\Portfolio.Infrastructure\Persistence\Migrations\`. Column configuration goes in
  `Configurations\AssetConfiguration.cs` alongside the existing ones — `OnModelCreating` only
  calls `ApplyConfigurationsFromAssembly`.
- Validation: month 1–12; day valid for that month. **The 29 February trap:** allow `2/29`
  (some entities do use it) and clamp to 28 Feb in non-leap years at resolution time. Reject
  `2/30`, `4/31` and friends at write time.
### 3.2 The zakat payment ledger

The one part of this feature that is a **recorded fact rather than a derived number**: how much
zakat was actually paid, and when. Entered by hand, never computed, never written by the report.

```csharp
public class ZakatPayment
{
    public int Id { get; set; }

    /// <summary>The date the payment was made.</summary>
    public DateOnly PaidOn { get; set; }

    /// <summary>Amount actually paid, in SGD. Entered by hand — never derived from §4.</summary>
    public decimal AmountSgd { get; set; }
}
```

- **Deliberately unlinked to the calculation.** No foreign key to `Asset`, no stored "computed
  amount", no reconciliation column. The page may *display* the computed figure from
  [§4](#4-algorithm) beside this history, but must never write it here. Same rule as
  *a close is never written into `PriceQuote`*: a derived number persisted as a recorded fact is
  indistinguishable from the real thing a year later, and the whole point of this table is that
  the amount paid is the real thing.
- **Amount and date only.** No note, no covering period, no payee — see
  [§10](#10-open-questions) for why a "which year does this cover" field was left out rather
  than forgotten.
- **Precision `decimal(19,4)`**, the monetary-total convention, configured explicitly in
  `ZakatPaymentConfiguration.cs`. Zakat amounts are ordinary dollars and cents, which is exactly
  what makes EF Core's `decimal(18,2)` default look harmless here. Configure it anyway, so this
  table never becomes the precedent for skipping it.
- **`PaidOn` is a `DateOnly`**, serialised `YYYY-MM-DD`. Never round-trip it through
  `toISOString()` — it shifts a day at any positive UTC offset, and SGT is UTC+8. The same trap
  as `Transaction.TradeDate`; see `CLAUDE.md` → *Wire contract*.
- Index `PaidOn` descending. The only read is "history, newest first".
- **It hangs off nothing.** `CLAUDE.md` requires `AssetDeleteCascadeTests` to be extended
  whenever a new child table hangs off `Asset`. This table deliberately does not — deleting an
  asset must leave the payment history untouched. Worth an explicit assertion, because the
  absence of a relationship is the kind of thing someone adds later to be helpful.
- `IPortfolioDbContext` gains `IQueryable<ZakatPayment> ZakatPayments` plus explicit
  `AddZakatPayment` / `RemoveZakatPayment` / `FindZakatPaymentAsync`, matching how that interface
  keeps EF types out of the Application layer.
- Migration: `AddZakatPayments`, separate from `AddAssetFiscalYearEnd` — they are independent
  concerns and read better apart in the migration history.

Validation, via `ServiceError.Validation`:

- `AmountSgd` must be greater than zero.
- `PaidOn` must not be in the future — a future date is a typo, not a plan.

---

## 4. Algorithm

### 4.1 Resolving the financial year end

```
clampDay(year, month, day) = min(day, DateTime.DaysInMonth(year, month))

candidate = new DateOnly(today.Year, month, clampDay(today.Year, month, day))
if (candidate > today)
    candidate = new DateOnly(today.Year - 1, month, clampDay(today.Year - 1, month, day))
return candidate
```

`candidate == today` is allowed and correct.

> **Wrinkle.** `PriceBackfillService` *defers today* — a history range collapsing to today alone
> cannot return a close, so it short-circuits without spending a provider call. A financial year
> end that lands on today therefore has no stored close and falls through to the carried-forward
> previous close. Correct behaviour, but the report must say the close date is not the year-end
> date rather than presenting it as exact.

### 4.2 Per stock

1. **Quantity.** `qty` = units held on `fyEnd` (buys minus sells with `TradeDate <= fyEnd`).
   `qty == 0` → status `NotHeldAtFiscalYearEnd`, contributes 0, and is *not* an error.
2. **Close.** `closeNative` = the `PriceHistories` row for that asset with the **greatest
   `Date <= fyEnd`**. Carry-forward is required, not optional — year ends land on weekends,
   public holidays and exchange closures. Report the date actually used and whether it was exact.
3. **Convert to SGD.**
   - `Currency == "SGD"` (Z74) → `valueSgd = qty × closeNative`. **No FX at all** — the SGX
     close is already SGD, and running it through a rate would be a double conversion.
   - `Currency == "USD"` → `rate = FxRateResolver.Resolve(usdSgdRates, closeDate)` and
     `valueSgd = qty × closeNative × rate`, using the **close's own date's** rate, never today's.
   - Any other currency → explicit failure. Never fall back to an implicit 1:1.

### 4.3 ⚠ The single easiest thing in this feature to get backwards

`FxRate` rows are stored `Base = "USD"`, `Quote = "SGD"`, so **`Rate` is SGD per USD**
(`PriceBackfillService` writes `Base = ReportingCurrency, Quote = currency`).

Every existing caller in the codebase **divides** by that rate —
`CostBasisTransactionFactory.ToUsd`, `PortfolioPerformanceService.ToUsd`,
`PortfolioSummaryService` — because every one of them converts *into* USD.

**This report is the first that converts out of USD, and must multiply.**

Copying the surrounding code's idiom is the natural mistake here and it produces a number that
is wrong by a factor of ~1.66 while still looking entirely plausible. Pin it with a unit test
that asserts a USD value converts to *more* SGD, not less — a test that fails the moment the
operator is swapped.

### 4.4 Per crypto

- `qty` = units held **today** (all transactions).
- Price from `PriceQuote` in USD. It may be `Live` or a stale `Close` — carry the existing
  `PriceSource` distinction through to the DTO, do not flatten it. Crypto has **no**
  `PriceHistory` by design, so today's quote is the only possible input and a missing quote is a
  hard exclusion, not a fallback.

  > **This paragraph was aspirational and is not yet true in practice.** `QuoteFreshness.Classify`
  > returns `PriceSource.Live` *unconditionally* for any provider `ProviderMarkets.For(...)` has no
  > market calendar for — which includes CoinGecko — by existing documented design. So a crypto
  > zakat line reads `"Live"` whenever a quote exists at all, however stale that quote is. This is
  > not zakat-specific: `PortfolioSummaryService` has behaved this way for crypto all along, and
  > `ZakatService` reuses the shared classifier rather than special-casing itself. The DTO carries
  > the field honestly, so this starts telling the truth the moment the classifier learns about
  > CoinGecko. **Do not read a crypto `"Live"` as evidence the price is fresh.**
- `valueSgd = qty × priceUsd × FxRateResolver.Resolve(usdSgdRates, today)`.

### 4.5 Totals

`stockZakatableSgd`, `cryptoZakatableSgd`, `totalZakatableSgd`, and
`zakatPayableSgd = totalZakatableSgd × 0.025`.

`DisplayRounding.Money` (4 dp) at the **DTO boundary only** — never inside the calculation, and
never on an intermediate. Prices stay at `Price` (10 dp). See [§7](#7-measured-state-of-the-data)
for why this is not theoretical here.

---

## 5. What to reuse, and the two resolvers that must be new

### Reuse

| Piece | Path |
|---|---|
| `FxRateResolver.Resolve(IReadOnlyList<FxRate> ascending, DateOnly)` | `src\Portfolio.Application\Services\Calculators\FxRateResolver.cs` |
| `DisplayRounding` — `Money` 4 dp / `Price` 10 dp / `Percent` 4 dp | `…\Calculators\DisplayRounding.cs` |
| `ServiceResult<T>` / `ServiceError` (`NotFound` \| `Validation`) | `src\Portfolio.Application\Common\ServiceResult.cs` |
| Service shape to copy (primary constructor, `IPortfolioDbContext`, `TimeProvider`) | `…\Services\PortfolioPerformanceService.cs` |
| Endpoint shape to copy | `src\Portfolio.Api\Endpoints\PortfolioEndpoints.cs` |
| Per-currency FX loading | `PortfolioPerformanceService.LoadFxRatesAsync` |

### `FxRateResolver`'s one behaviour this report must defeat

For a date **before** every stored rate, `FxRateResolver` falls back to the **earliest** rate
rather than throwing. For a daily series that is a reasonable edge policy. For a single headline
zakat figure it is a wrong number wearing a right number's clothes.

The zakat path must compare the resolved rate's date against the requested date and set
`fxCarriedBack = true` when `resolvedDate > requestedDate`. Note the asymmetry: carrying a rate
*forward* (the normal case, a weekend) is fine; carrying one *backward* means we had no data and
substituted the oldest thing we had.

### New: `QuantityAsOf`

`AverageCostCalculator` already produces `CostBasisStep(TradeDate, QuantityHeld, CostBasisUsd, …)`,
but building its input goes through `CostBasisTransactionFactory.ToUsd`, which needs FX. Zakat
needs **quantity only**, and a missing FX rate must not be able to fail a quantity — the two
failures have different causes and different fixes, and collapsing them loses that.

So: a small `QuantityAsOf(IReadOnlyList<Transaction>, DateOnly)` beside the calculators — buys
minus sells with `TradeDate <= date`, ordered `OrderBy(t => t.TradeDate).ThenBy(t => t.Id)` to
match the stable sort the cost-basis path relies on.

**A second definition of "quantity held" is only safe if something proves the two agree.** Pin it
with a unit test asserting `QuantityAsOf` matches the last `CostBasisStep.QuantityHeld` over the
same transaction set. Without that test this helper is a future divergence waiting to happen.

### New: `CloseAsOf`

Mirrors `FxRateResolver`'s carry-forward, with **one deliberate difference: no fallback to the
earliest close.** Nothing at or before the year end is `NoCloseOnOrBeforeFiscalYearEnd`, a named
failure — never the oldest price on file. Do not copy `FxRateResolver` wholesale.

---

## 6. Outcome taxonomy — never one "skipped" bucket

The most repeated defect family in this project (D10, D26, D33, D35, D38, D45) is a list whose
name asserts a reason it cannot actually distinguish. Every asset gets exactly one status:

| Status | Attempted? | Meaning | In total |
|---|---|---|---|
| `Included` | yes | Valued | yes |
| `NotHeldAtFiscalYearEnd` | yes | Quantity legitimately 0 at the year end | yes, as 0 |
| `FiscalYearEndNotConfigured` | **no** | No month/day recorded on the asset | **excluded** |
| `NoCloseOnOrBeforeFiscalYearEnd` | yes | No stored close at or before the date | **excluded** |
| `NoFxRateForCloseDate` | yes | No USD/SGD rates at all for that currency | **excluded** |
| `NoQuote` | yes | Crypto with no live or stale quote | **excluded** |

`NotHeldAtFiscalYearEnd` and `FiscalYearEndNotConfigured` both produce a zero contribution and
must never share a bucket: the first is a correct answer, the second is a missing input.

Per asset the report also carries `fiscalYearEndDate`, `closeDateUsed`, `closeDateExact`,
`fxDateUsed`, `fxCarriedBack`, and the native close alongside the SGD value.

At the top level: an excluded-asset count and an explicit caveat, so a total missing an asset can
never read as complete. Same principle as `PortfolioSummaryDto.UnpricedHoldingsCount`.

---

## 7. Measured state of the data

Measured 2026-09-03 against the running stack (`http://localhost:8080`), not inferred.

### 7.1 The coverage invariant, and why it holds

`PriceBackfillService` backfills each asset from its **earliest transaction `TradeDate`** to
today. Therefore:

> If quantity at a year end is greater than zero, a transaction exists at or before that year
> end, so the price history necessarily reaches back past it.

An asset bought *after* its last year end needs no price at all — it lands on
`NotHeldAtFiscalYearEnd`. The existing backfill range is exactly sufficient for this feature; no
new historical fetching, and **no new Twelve Data credit spend**, is required.

Today that "bought after the year end" case is **JNJ** (history from 2026-03-24), **XOM**
(2026-02-27) and **SPWO** (2026-05-27).

### 7.2 Price history spans

First stored close per stock asset (all running to 2026-09-02, Z74 to 2026-09-03):

| Asset | From | Asset | From | Asset | From |
|---|---|---|---|---|---|
| TTD | 2020-07-10 | AVGO | 2022-10-21 | HLAL | 2024-07-17 |
| Z74 | 2020-07-13 | PG | 2022-11-25 | SPWO | 2026-05-27 |
| FSLY | 2020-07-13 | MSFT | 2023-06-16 | XOM | 2026-02-27 |
| ERIC | 2020-08-24 | AMZN | 2023-11-06 | JNJ | 2026-03-24 |
| ARVLF | 2021-03-25 | SPUS | 2024-06-17 | | |
| NIO | 2021-06-01 | AAPL | 2022-10-10 | | |
| NVDA | 2021-09-07 | | | | |

### 7.3 The USD/SGD floor

FX history starts at roughly **2020-07-13**, driven by Z74's first trade. The earliest stock
history is TTD at **2020-07-10** — a three-day window in which `FxRateResolver` would carry a
rate *backwards*. No plausible year end falls inside it, but this is precisely what
`fxCarriedBack` exists to surface.

### 7.4 Structural FX risk — write this down

USD/SGD rows are backfilled **only because Z74 is an SGD asset**. The currency pairs are derived
from the currencies of assets that actually hold transactions; they are not configured.

Sell out of Z74 entirely and the pair stops being maintained. Every stock in this report is USD,
so the whole SGD conversion path quietly loses its only source — and because `FxRateResolver`
throws on an empty list rather than returning garbage, the failure would at least be loud. The
implementation round should either pin SGD as an always-backfilled reporting-adjacent currency
(1 extra credit per backfill run) or make the dependency explicit in the report.

### 7.5 Precision is not theoretical here

Crypto today: ETH 0.0007004 @ $2,400.46; **AMP 39,732.4862639449 @ $0.0004419**; **ANVL
36,810.905 @ $0.00074545** — USD 46.68 in total.

AMP and ANVL are the standing proof that `decimal(28,10)` is not optional. A two-decimal
assumption anywhere in this path — a validator, an input `step`, a display pipe, a DTO — reads
these positions as zero. See `CLAUDE.md` → *Decimal precision*; the EF Core InMemory provider
does not enforce precision, so only `tests/Portfolio.IntegrationTests` against real SQL Server
can catch a regression.

For scale: stock market value today is USD 54,245.93 and crypto USD 46.68. At roughly 1.29
SGD/USD the stock leg alone is far above the September 2026 nisab of SGD 15,902 — but note that
the zakat basis is the **year-end** values, not today's, so the figures will differ.

---

## 8. Financial year ends still to be supplied

**Four are now entered** (2026-09-05, in the container database): AAPL `9/27`, AMZN `12/31`,
ARVLF `12/31`, AVGO `11/2`. The remaining **18 are still unset**, and until they are the total is
knowingly partial. All 22 stock assets are listed below — including the six
holding zero units today, because [§2.8](#28-a-position-closed-today-can-still-owe-zakat) means
they may still contribute.

The four entered values are recorded here as *entered*, not as *confirmed against filings* —
this document cannot know which. AAPL's `9/27` and AVGO's `11/2` are both consistent with their
52/53-week calendars for the 2025 cycle, but see the caveat below: consistent for one cycle is
not the same as correct for the next.

The "commonly reported" column is a **starting point, not a source of truth**. Confirm each
against the company's own annual report or investor-relations page before entering it: a wrong
year end produces a confident wrong number, and nothing downstream can detect it.

| Asset | Name | Held today | Commonly reported FY end | Confirmed? |
|---|---|---|---|---|
| AAPL | Apple | no (0 units) | last Sat of Sep — 52/53-week | ☐ |
| MSFT | Microsoft | yes | 30 Jun | ☐ |
| Z74 | Singtel | yes | 31 Mar | ☐ |
| AMZN | Amazon | yes | 31 Dec | ☐ |
| ARVLF | Arrival SA | yes | 31 Dec | ☐ |
| AVGO | Broadcom | yes | ~1st Sun of Nov — 52/53-week | ☐ |
| ERIC | Ericsson | yes | 31 Dec | ☐ |
| FSLY | Fastly | yes | 31 Dec | ☐ |
| HLAL | Wahed FTSE USA Shariah ETF | yes | — look up, do not assume | ☐ |
| JNJ | Johnson & Johnson | yes | Sun nearest 31 Dec — 52/53-week | ☐ |
| NIO | Nio | yes | 31 Dec | ☐ |
| NVDA | Nvidia | yes | last Sun of Jan — 52/53-week | ☐ |
| PG | Procter & Gamble | yes | 30 Jun | ☐ |
| SPUS | SP Funds S&P 500 Sharia | yes | — look up, do not assume | ☐ |
| SPWO | SP Funds S&P World ex-US | yes | — look up, do not assume | ☐ |
| TTD | Trade Desk | yes | 31 Dec | ☐ |
| XOM | ExxonMobil | yes | 31 Dec | ☐ |
| CSCO | Cisco | no (0 units) | last Sat of Jul — 52/53-week | ☐ |
| KO | Coca-Cola | no (0 units) | 31 Dec | ☐ |
| PINS | Pinterest | no (0 units) | 31 Dec | ☐ |
| TDOC | Teladoc Health | no (0 units) | 31 Dec | ☐ |
| VZ | Verizon | no (0 units) | 31 Dec | ☐ |

### The 52/53-week caveat

**AAPL, AVGO, JNJ, NVDA and CSCO** run 52/53-week fiscal calendars — the year end is "the last
Saturday of September", not "27 September", and it moves by up to about six days each year. A
fixed month/day therefore drifts.

This is accepted, for two reasons: the drift moves the valuation by a handful of trading days on
a figure that is a snapshot of a fluctuating market anyway, and the report publishes the close
date it actually used, so a materially wrong year end is visible rather than silent. If the drift
ever matters, the fix is the optional explicit-override column deliberately left out of
[§3](#3-data-model).

---

## 9. Implementation round

Sketch, so the next session starts from a decision rather than a blank page.

**Backend**
- `src\Portfolio.Domain\Entities\Asset.cs` — the two nullable ints; `AssetConfiguration.cs`;
  migration `AddAssetFiscalYearEnd`.
- `src\Portfolio.Domain\Entities\ZakatPayment.cs` — the ledger of §3.2;
  `Configurations\ZakatPaymentConfiguration.cs`; `IPortfolioDbContext` members; migration
  `AddZakatPayments`.
- `src\Portfolio.Application\Services\Calculators\` — `QuantityAsOf`, `CloseAsOf`.
- `…\Services\IZakatService.cs` / `ZakatService.cs`, `…\Dtos\ZakatDtos.cs`.
- `src\Portfolio.Api\Endpoints\ZakatEndpoints.cs`, registered from `Program.cs` beside the other
  five `MapXEndpoints` calls:
  - `GET /api/zakat` — the computed report, optional `?asOf=`.
  - `GET /api/zakat/payments` — history, newest first.
  - `POST /api/zakat/payments`, `PUT /api/zakat/payments/{id:int}`,
    `DELETE /api/zakat/payments/{id:int}` — the same thin-lambda,
    `Results<Ok<T>, NotFound, ValidationProblem>` shape as `TransactionsEndpoints`.

> **The one sanctioned exception to asset-class segregation.** This endpoint returns stocks
> **and** crypto in a single response, because MUIS requires the grand total. Keep them in
> separate sub-objects with separate subtotals so nothing aggregates implicitly, and note the
> exception in `CLAUDE.md` when it ships — otherwise it reads as a violation of the rule.

**Frontend**
- Financial-year-end month/day inputs in `features\asset-management\asset-form.dialog.*`,
  disabled for crypto.
- New `features\zakat\` page + a nav entry in `layout\app-shell\app-shell.html`. Two parts:
  the computed breakdown from §4, and below it the **payment history** — a table of
  `PaidOn` / `AmountSgd` newest first, with an add/edit dialog mirroring
  `features\transactions\transaction-form.dialog.*`.
- The two must read as different kinds of number. The computed figure is an estimate derived
  from market data with caveats attached; a payment is a fact you recorded. Do not put them in
  one table, and do not render a "shortfall" that implies the computed figure is owed.
- Design tokens only — no hex, no raw `px`. SGD formatting, not the app-wide USD pipe.

**Docs**
- A `CLAUDE.md` pointer to this file, and a `tracker.md` entry once there is behaviour to record.

**Tests**
- Year-end resolution: already passed this year; not yet reached; exactly today; 29 Feb clamped
  in a non-leap year; null month/day.
- `QuantityAsOf` agrees with the last `CostBasisStep.QuantityHeld` on the same transaction set.
- `CloseAsOf` carries forward across a weekend, and **fails** rather than reaching back when
  nothing exists at or before the date.
- **FX direction:** a USD value converts to *more* SGD — fails if multiply and divide are swapped.
- Precision, integration, real SQL Server: AMP/ANVL positions do not round to zero.
- Every one of the six statuses in [§6](#6-outcome-taxonomy--never-one-skipped-bucket) reachable.
- Payment ledger: amount ≤ 0 and a future `PaidOn` both rejected; `PaidOn` survives a round trip
  through the API unshifted (the `DateOnly`/UTC+8 trap); deleting an asset leaves payments intact.

---

## 10. Open questions

1. **Crypto valuation basis** ([§2.3](#23-crypto-follows-no-muis-rule)) — today's value is our
   convention, not a MUIS ruling. Worth asatizah confirmation.
2. **Per-company vs. single common year end** ([§2.2](#22-end-of-the-financial-year-of-the-companies-is-read-per-company))
   — we resolved MUIS's ambiguity one way. Worth confirming.
3. **Cash and dividends already received** are zakatable as savings under a different rule and
   are entirely outside this tracker's data. This report does not and cannot cover your full
   zakat obligation.
4. **What a payment covers is not recorded** ([§3.2](#32-the-zakat-payment-ledger)). Amount and
   date only, as asked. A "covering period" field was left out rather than forgotten: with
   per-company year ends there is no single zakat year to name, and the choice between Hijri
   year, Gregorian year and "the cycle ending at each company's year end" is exactly the kind of
   ambiguity that would be baked in wrong. Add a free-text note later if the history becomes hard
   to read; do not add a structured period without deciding what it means first.
5. **D6 interaction** — none expected. The invariant in
   [§7.1](#71-the-coverage-invariant-and-why-it-holds) means this feature spends **zero** Twelve
   Data credits, which is worth preserving through implementation.

---

## 11. What shipped, and what it changed

Implemented 2026-09-03, backend then frontend, against the design above. Build clean; 311 backend
unit tests, 12 integration tests against real SQL Server, 340 frontend tests, all green. Both
halves were verified against a running process and a real browser, not against tests alone.

### 11.1 Two places the implementation had to go beyond this document

Neither is a disagreement with the design — both are things §9's sketch simply did not reach, and
in both cases the feature would have been unreachable without them.

- **`AssetDto` / `CreateAssetRequest` / `UpdateAssetRequest` gained the two fiscal-year-end
  fields.** §9's backend list names the entity, the configuration and the migration but not the
  DTOs, which would have left two columns nothing could ever write to. Added as optional trailing
  parameters defaulting to `null`, so every existing call site compiled unchanged.
- **`AssetFormDialog` gained an edit mode.** It was create-only, so there was no way to reach any
  of the 22 assets already in the database — the new inputs would have been permanently
  unreachable for exactly the assets that need them. The dialog now takes
  `{mode:'create'} | {mode:'edit'; asset}`, and edit deliberately narrows to `name`, `exchange`
  and the year end; identity and provider fields render disabled for context rather than being
  editable, because changing an asset's symbol or provider after it has transactions and price
  history is a different and much larger decision.

### 11.2 §4.4 was aspirational

See the callout in [§4.4](#44-per-crypto): crypto's `priceSource` always reads `"Live"` today,
because the shared freshness classifier has no market calendar for CoinGecko. Pre-existing
behaviour, not introduced here, and reused rather than special-cased.

### 11.3 What the FX-direction trap actually cost

Nothing — [§4.3](#43--the-single-easiest-thing-in-this-feature-to-get-backwards) worked exactly as
intended. It was read before the code was written, the multiply was correct first time, and the
pinning test (a USD value converts to *more* SGD) exists to keep it that way. Recorded because a
trap that costs nothing is the only evidence that writing it down in advance was worth doing.

### 11.4 Verified, and not

**Verified live:** both migrations applied to SQLEXPRESS with no model drift; every zakat endpoint
and the asset PUT exercised against a running API, including `2/30` rejected and crypto's year end
correctly forced null; the day-one all-unconfigured page rendered in a real browser in both themes,
showing an informational setup state rather than a bare zero; setting one asset's year end and
watching the report recalculate to a *distinct* `NotHeldAtFiscalYearEnd` badge, proving the two
zero-contribution statuses do not look alike. All test data reverted afterwards.

**Not verified _in that round_ — most of this was closed on 2026-09-05, see
[§12](#12-the-fx-rate-column-and-the-container-run-2026-09-05):** the report had never been run
against the full 22-asset portfolio — the dev
database seeds 3 stocks and 3 crypto, and the real holdings live in the container database.
**AMP/ANVL precision has not been seen end-to-end through a real zakat calculation**: dev crypto
holdings are 0 units, so `valueSgd` was trivially 0 and the pipes were never handed
`39,732.4862639449 @ $0.0004419`. [§7.5](#75-precision-is-not-theoretical-here) says exactly why
that is the case worth looking at. `NoCloseOnOrBeforeFiscalYearEnd` and `NoFxRateForCloseDate` have
been exercised only from test fixtures, never from a live asset genuinely in that state.

### 11.5 Still open from this round

- **Enter the fiscal year ends** — 4 done as of 2026-09-05, **18 still outstanding** — ([§8](#8-financial-year-ends-still-to-be-supplied)), each
  confirmed against the company's own filings. Nothing else unblocks a real figure.
- **[§7.4](#74-structural-fx-risk--write-this-down) was not acted on.** USD/SGD is still backfilled
  only as a side effect of Z74 being an SGD asset. Sell out of Z74 and this report's only FX source
  stops being maintained. The failure would at least be loud — `FxRateResolver` throws on an empty
  list — but the dependency is still implicit.

---

## 12. The FX rate column, and the container run (2026-09-05)

### 12.1 The rate itself is now on the wire

Both line DTOs already carried `FxDateUsed` and `FxCarriedBack` — the *provenance* of the rate —
but not the rate itself, so the report asserted an SGD figure the reader had no way to check. Both
records gained `decimal? FxRateUsed`, sitting between `FxCarriedBack` and `ValueSgd`, and both
tables gained an **FX rate (USD/SGD)** column immediately before **Value (SGD)**, so a row now
reads as the arithmetic that produces its own total.

Three decisions worth keeping:

- **Null for an SGD-native asset, never `1.0`.** Z74 does no conversion at all. A literal 1.0
  would be indistinguishable from a genuine rate that happened to be 1 — the same
  not-attempted-vs-attempted confusion [§6](#6-outcome-taxonomy--never-one-skipped-bucket) exists
  to prevent. The UI therefore renders a footnote (*"Already SGD — no FX conversion applied"*)
  rather than the bare em-dash a missing rate gets: three visibly different cells, a rate, a
  no-conversion note, and a placeholder.
- **The rate is emitted unrounded**, at its stored `decimal(18,8)`. `ValueSgd` is computed from
  this exact value before `DisplayRounding.Money` is applied once at the end; a second, independent
  rounding step would let the displayed rate stop reconciling against the displayed value.
  Rounding it to `Money`'s 4 dp would have been actively wrong here.
- The `fxCarriedBack` warning footnote **moved** out of "Valued as of" and under the FX column. It
  is a statement about the rate, and it now sits beneath the rate it qualifies.

### 12.2 What the container run actually proved

Run against the real 22-stock/3-crypto portfolio, zero Twelve Data credits, via both the API and a
real browser.

**AMP/ANVL precision, end to end — the gap [§11.4](#114-verified-and-not) named first, now closed.**
The page renders AMP as `39,732.4862639449 @ $0.00044562` reaching **SGD 22.43**, and ANVL as
`36,810.905 @ $0.0007048` reaching **SGD 32.87**. Under any two-decimal assumption AMP's price
reads `0.00` and its entire contribution silently disappears. It does not. This is the first time
those two positions have been seen through the display pipes in this report rather than reasoned
about.

**Historical FX is genuinely per-date.** Three different rates appeared on three different dates in
one response — AMZN **1.2852** (2025-12-31), AVGO **1.3016** (2025-10-31), all crypto **1.26688**
(2026-09-04) — and every one of the six included lines reconciles exactly as
`quantity x price x fxRateUsed = valueSgd`, checked arithmetically. That reconciliation is also the
live proof of [§4.3](#43--the-single-easiest-thing-in-this-feature-to-get-backwards): the USD
amounts get *larger* in SGD, so the conversion multiplied.

**AVGO exercised the 52/53-week path on real data.** Its year end 2025-11-02 is a Sunday, so
`closeDateUsed` is 2025-10-31 with `closeDateExact: false` — the carry-forward working outside a
fixture for the first time.

**The SGD-native branch was reached by temporarily setting Z74's year end to `3/31`**, confirming
`fxDateUsed` and `fxRateUsed` both null and `valueSgd` exactly `255 x 4.94 = 1259.70` with no FX
applied, then **reverted to null** — Z74 is back to `FiscalYearEndNotConfigured` and the excluded
count back to 18. No real year end was entered on the strength of a guess.

### 12.3 Still not verified

`NoCloseOnOrBeforeFiscalYearEnd` and `NoFxRateForCloseDate` remain exercised **only from test
fixtures**. No live asset is in either state, and neither can be reached by configuration the way
the SGD-native branch could — reaching them means removing price or FX history, which is not worth
doing to the real database.

[§7.4](#74-structural-fx-risk--write-this-down) is still not acted on. Every USD/SGD row still
exists only as a side effect of Z74 being an SGD asset, and this report now *displays* that rate
prominently on every USD line — so the blast radius of that dependency going stale is larger and
more visible than when §7.4 was written, not smaller.