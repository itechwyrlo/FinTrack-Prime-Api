# Account classification: "Other" fiat accounts + crypto price feed + currency-bucketed Financial Statement

**Date:** 2026-08-10
**Status:** Draft — pending review
**Repos affected:** `FinTrack-Prime-Api` (backend), `FinTrackPrime` (frontend, `C:\Users\Wyrlo\projects\FinTrackPrime`)

## Background

Today, any Finverse account whose `account_type.subtype` isn't literally `checking`/`current`/`savings`/`credit_card`/`credit` falls into a catch-all `AccountType.Unsupported` — visible on the Dashboard with its raw balance, but excluded from Total balance, Cash Flow, and the Financial Statement entirely. Real Testbank sandbox data surfaced three such accounts: **Bitcoin** (a crypto wallet), **HKD Ledger Account**, and **USD FX** (a foreign-currency wallet). Investigation showed these are three different problems currently lumped into one bucket:

- **HKD Ledger Account** — same currency (HKD) as the user's other accounts. No real reason to exclude it; it was just an unrecognized subtype string.
- **USD FX** — a different *fiat* currency. `CashFlowService` already solves exactly this problem (groups transactions per-currency, never blends totals) — `FinancialStatementService` does not yet have the equivalent.
- **Bitcoin** — not a fiat currency at all. Needs an actual price conversion to produce a meaningful dollar figure, which requires a real external price-feed integration.

This spec addresses all three together, per your decision to design them as one piece: a broader, more honest account classification; `FinancialStatementService` gaining the same currency-bucketing `CashFlowService` already has; and a CoinGecko-backed crypto price feed, cached at sync time (not fetched live on every page load).

## Goals

- `AccountType.Unsupported` narrows to its true meaning: an account with no usable currency at all (a defensive edge case, not "anything we didn't expect").
- Same-currency fiat accounts with an unrecognized subtype (e.g. "HKD Ledger Account") are fully supported — included everywhere Checking/Savings already are.
- Foreign-currency fiat accounts (e.g. "USD FX") are fully supported via per-currency bucketing in the Financial Statement, mirroring Cash Flow's existing `OtherCurrencies` pattern.
- Crypto accounts get a cached fiat-equivalent value (via CoinGecko, refreshed at sync time) that's included in the Financial Statement's USD bucket, while the Dashboard continues showing the raw crypto balance untouched.

## Non-goals

- **Cash Flow is not touched.** It's transaction-driven; `Other`-typed accounts sync transactions normally and participate automatically via its existing per-currency bucketing. `Crypto`-typed accounts still don't sync transactions at all (no historical price feed to convert past transactions — meaningfully larger scope, out of this spec), so they simply never appear in Cash Flow, same as today.
- **No historical crypto pricing.** Only the current balance gets converted; past transactions for a crypto account (if Finverse even reports them meaningfully) are not priced or synced.
- **Dashboard's `totalBalance` currency-blending is a pre-existing, separate bug, not fixed here.** `DashboardPage.tsx`'s `summary.totalBalance` already sums every non-Unsupported account's raw balance regardless of currency (HKD and SGD accounts are already blended together today, independent of this spec). Flagged as an open question below — fixing it would mean a third place needing the same per-currency bucketing `CashFlowService`/the new `FinancialStatementService` have, and wasn't part of what was asked.
- No support for crypto currencies beyond a short hardcoded list (starting with `BTC`, extensible later).

## Data model (backend)

### `AccountType` enum

```csharp
public enum AccountType
{
    Checking,
    Savings,
    CreditCard,
    Other,      // new — a real fiat balance whose Finverse subtype isn't one of the three above
    Crypto,     // new — a non-fiat balance; see FiatEquivalentValue below
    Unsupported, // narrowed — now only "no usable currency at all" (defensive edge case)
}
```

### `Account` entity gains three nullable fields

```csharp
// Only populated for AccountType.Crypto — the last successful
// conversion of Balance (in Currency) to a fiat value, refreshed each
// time BankLinkService syncs this account. A failed price-feed call
// leaves these as whatever they were last time rather than clearing
// them, so a transient CoinGecko outage doesn't make a crypto asset
// disappear from the Financial Statement.
public decimal? FiatEquivalentValue { get; set; }
public string? FiatEquivalentCurrency { get; set; }   // always "USD" today — see "Conversion target" below
public DateTime? PriceFetchedAtUtc { get; set; }
```

### `AssetType` enum gains `Crypto`

```csharp
public enum AssetType { Cash, Investment, RealEstate, Vehicle, Crypto, Other }
```

## Account classification (`BankLinkService`)

### `MapAccountType` redesign

```csharp
// BTC is the only crypto currency Testbank has actually surfaced;
// extensible without a schema change since detection is by currency
// code, not by guessing more Finverse subtype strings.
private static readonly HashSet<string> KnownCryptoCurrencies = new(StringComparer.OrdinalIgnoreCase) { "BTC" };

private static AccountType MapAccountType(string finverseAccountType, string currency)
{
    switch (finverseAccountType.ToLowerInvariant())
    {
        case "checking":
        case "current":
            return AccountType.Checking;
        case "savings":
            return AccountType.Savings;
        case "credit_card":
        case "credit":
            return AccountType.CreditCard;
    }

    if (string.IsNullOrWhiteSpace(currency))
    {
        return AccountType.Unsupported;
    }

    return KnownCryptoCurrencies.Contains(currency) ? AccountType.Crypto : AccountType.Other;
}
```

Detecting crypto by **currency code**, not by hardcoding more Finverse subtype spellings, sidesteps the uncertainty already flagged in this codebase's existing "VERIFY" comment about unconfirmed subtype strings — everything that isn't a recognized crypto currency and has *some* currency code is treated as a normal fiat balance.

### `SyncInstitutionAsync` changes

```csharp
var accountType = MapAccountType(finverseAccount.AccountType, finverseAccount.Currency);
// ... existing account upsert (Nickname/Type/Currency/Balance) unchanged ...

if (accountType == AccountType.Crypto)
{
    try
    {
        account.FiatEquivalentValue = await _cryptoPriceClient.GetFiatEquivalentAsync(
            finverseAccount.Currency, finverseAccount.Balance, TargetFiatCurrency);
        account.FiatEquivalentCurrency = TargetFiatCurrency;
        account.PriceFetchedAtUtc = DateTime.UtcNow;
    }
    catch (Exception)
    {
        // Leave the previous cached value as-is — a stale price beats no
        // price, and one account's price-feed hiccup must not block the
        // rest of this institution's sync (same isolation principle
        // SyncAsync already applies per-institution, one level up).
    }
}

if (accountType == AccountType.Unsupported || accountType == AccountType.Crypto)
{
    continue; // balance/nickname (and, for Crypto, the fiat-equivalent) synced above; transactions are not.
}

// existing transaction sync — now also runs for AccountType.Other, unchanged code path
```

`Other` accounts fall through to the existing transaction-sync code entirely unchanged — they were never specifically excluded by name, only by never reaching that branch before (`Unsupported` was the only escape hatch, and `Other` is a new, different value).

## Crypto price feed

### `ICryptoPriceClient`

```csharp
public interface ICryptoPriceClient
{
    // Throws InvalidOperationException on an unrecognized currency or a
    // failed API call — BankLinkService decides how to handle that (see
    // above: keep the previous cached value).
    Task<decimal> GetFiatEquivalentAsync(string cryptoCurrency, decimal amount, string fiatCurrency);
}
```

Implemented against CoinGecko's free `/simple/price` endpoint (`GET /simple/price?ids={id}&vs_currencies={fiat}`, no API key required) — same typed-`HttpClient` registration pattern already used for `IPayPalClient`/`IFinverseClient`. A small static map (`"BTC" → "bitcoin"`) translates ticker symbols to CoinGecko's `id` values, since its API expects full coin names, not tickers. New config section:
```json
"CoinGecko": { "ApiBaseUrl": "https://api.coingecko.com/api/v3" }
```

### Conversion target

Always **USD** (`TargetFiatCurrency = "USD"` constant), not the user's dynamically-computed "primary" currency — that's calculated per-view (Cash Flow and, after this spec, Financial Statement each pick their own "primary" independently), which would make a cached value ambiguous about which "primary" it was converted for. A crypto account's fiat-equivalent always lands in the USD bucket of any currency-bucketed view.

## Financial Statement currency-bucketing (`FinancialStatementService`)

Mirrors `CashFlowViewModel`/`CashFlowByCurrencyViewModel` exactly.

```csharp
public class AssetLineViewModel { /* existing fields */ public string Currency { get; set; } }       // new field
public class LiabilityViewModel { /* existing fields */ public string Currency { get; set; } }       // new field

public class FinancialStatementByCurrencyViewModel
{
    public string Currency { get; set; }
    public List<AssetLineViewModel> Assets { get; set; } = new();
    public decimal TotalAssets { get; set; }
    public List<LiabilityViewModel> Liabilities { get; set; } = new();
    public decimal TotalLiabilities { get; set; }
    public decimal OwnersEquity { get; set; }
}

public class FinancialStatementViewModel
{
    public string Currency { get; set; } = string.Empty;   // primary
    public List<AssetLineViewModel> Assets { get; set; } = new();
    public decimal TotalAssets { get; set; }
    public List<LiabilityViewModel> Liabilities { get; set; } = new();
    public decimal TotalLiabilities { get; set; }
    public decimal OwnersEquity { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<FinancialStatementByCurrencyViewModel> OtherCurrencies { get; set; } = new();
}
```

**Line construction changes:**
- `Other`-typed accounts → `AssetLineViewModel { Type = AssetType.Cash, Currency = account.Currency, Amount = account.Balance }` — same treatment as Checking/Savings, just also carrying its currency now (which every asset line does, post-spec).
- `Crypto`-typed accounts → `AssetLineViewModel { Type = AssetType.Crypto, Currency = account.FiatEquivalentCurrency ?? "USD", Amount = account.FiatEquivalentValue ?? 0m }` — uses the *cached converted* value, never the raw BTC count, so it buckets correctly.
- Every other existing line (manual assets, investment holdings, liabilities) gains `Currency` too — manual `Asset`/`Liability` rows don't currently track a currency, so they default to the statement's primary currency at construction time (they're manually entered by the user in whatever currency they're already thinking in — same currency as their main accounts, in practice).

**"Primary" currency**: same spirit as `CashFlowService` ("whichever currency has the most transactions"), adapted to a non-transaction-based statement — whichever currency has the most combined asset+liability lines.

**Grouping/totals**: assets and liabilities are grouped by `Currency` first, each group's `TotalAssets`/`TotalLiabilities`/`OwnersEquity` computed independently (never summed across groups) — the group with the most lines becomes the top-level `FinancialStatementViewModel`, the rest populate `OtherCurrencies`.

## Frontend changes

### Types (`types/api.ts`)

- `AccountType` gains `'Other' | 'Crypto'`.
- `AssetType` gains `'Crypto'`.
- `AssetLineViewModel`/`LiabilityViewModel` gain `currency: string`.
- `FinancialStatementViewModel` restructures to `{ currency, assets, totalAssets, liabilities, totalLiabilities, ownersEquity, generatedAtUtc, otherCurrencies: FinancialStatementByCurrencyViewModel[] }`; new `FinancialStatementByCurrencyViewModel`.

### `DashboardPage.tsx`

- `ACCOUNT_TYPE_LABELS` gains `Other: 'Other'`, `Crypto: 'Crypto'`.
- The `isUnsupported` explanatory note stays exactly as-is (still accurate — genuinely unsupported accounts are now rare). A new, separate note for `Crypto` accounts: *"Converted to its dollar value for your Financial Statement using the last synced price — not included in Cash Flow."* `Other` accounts get no special note; they behave fully normally, same as Checking/Savings.
- `summary.totalBalance`'s filter changes from `account.type !== 'Unsupported'` to `account.type !== 'Unsupported' && account.type !== 'Crypto'` — `Other` accounts now count toward it (same as any other fiat account; the pre-existing cross-currency blending in this total is unchanged, not fixed here per the Non-goals above), `Crypto` still doesn't (raw BTC units still aren't safe to add to a dollar figure without conversion, which this naive sum doesn't do).

### `FinancialStatementPage.tsx`

Restructures to render the primary currency's Assets/Liabilities/Owner's Equity (using the existing Type-grouped tables already built), followed by one additional section per entry in `otherCurrencies`, each with its own Type-grouped tables and its own Owner's Equity figure — labeled by currency code (e.g. "USD" as a section heading) so it's unambiguous these are separate, not summed together.

## Validation / integrity summary

| Rule | Enforced where |
|---|---|
| A crypto account's raw balance never enters a fiat total directly | `FinancialStatementService` always reads `FiatEquivalentValue`, never `Balance`, for `Crypto` accounts |
| A failed price-feed call doesn't erase an existing cached value | `BankLinkService`'s try/catch around the price-fetch call only ever *updates* the cached fields on success |
| One crypto account's price-feed failure doesn't block the rest of that institution's sync | Same try/catch, scoped to just the price fetch, not wrapping the whole account/transaction sync |
| Currencies are never summed across groups, in Financial Statement or Cash Flow | Both build one `TotalX`/`OwnersEquity` per currency group independently, never a cross-group sum |

## Open questions for review

1. **Dashboard's `totalBalance` currency-blending** (HKD + SGD accounts already summed together today) is a pre-existing, separate issue, not fixed by this spec — confirm that's acceptable to leave as a known follow-on rather than folding a fourth subsystem into this one.
2. **`KnownCryptoCurrencies` starts with just `BTC`** — the only one actually seen in Testbank data. Extend the list once other crypto currencies are observed, rather than guessing more now.
3. **CoinGecko rate limits**: the free tier has a request-per-minute cap. Not a concern at current scale (prices fetched once per crypto account per sync cycle, not per page load), but worth knowing if this ever needs to scale to many users syncing simultaneously.
4. **Manual `Asset`/`Liability` rows defaulting to the statement's primary currency**: reasonable assumption (the user is presumably entering them in the currency they're already thinking in), but there's no way today for a user to say "this real estate asset is actually in USD, not HKD" — a possible future enhancement, not part of this spec.
