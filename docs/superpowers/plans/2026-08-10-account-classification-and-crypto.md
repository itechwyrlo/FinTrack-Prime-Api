# Account Classification: "Other" Fiat Accounts + Crypto Price Feed + Currency-Bucketed Financial Statement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Narrow `AccountType.Unsupported` to its true meaning (no usable currency at all), add `Other` (same/foreign-currency fiat accounts with an unrecognized subtype) and `Crypto` (with a CoinGecko-backed, sync-time-cached fiat-equivalent value), and give `FinancialStatementService` the same per-currency bucketing `CashFlowService` already has.

**Architecture:** `BankLinkService.MapAccountType` gains a currency parameter and classifies by currency code for crypto detection (not by guessing more Finverse subtype strings). A new `ICryptoPriceClient` (typed HttpClient, CoinGecko) is called once per crypto account during `SyncInstitutionAsync`, caching the result on `Account`. `FinancialStatementService` restructures its output to mirror `CashFlowViewModel`'s primary-currency-plus-`OtherCurrencies` shape.

**Tech Stack:** ASP.NET Core 10 / EF Core (SQL Server), xunit + Moq + EF InMemory + the existing `FakeHttpMessageHandler` (from `FinverseClientTests.cs`, reused as-is — same test project, same namespace) for backend tests. React + TypeScript frontend — no frontend test framework exists; frontend tasks are implementation + manual browser verification.

## Global Constraints

- Crypto detection is by **currency code** (`KnownCryptoCurrencies`, starting with just `BTC`), never by guessing more Finverse subtype strings — sidesteps the uncertainty already flagged in this codebase's existing "VERIFY" comment about unconfirmed subtype spellings.
- A crypto account's raw balance never enters a fiat total directly — `FinancialStatementService` always reads the cached `FiatEquivalentValue`, never `Balance`, for `AccountType.Crypto` accounts.
- A failed CoinGecko call must not erase a previously-cached fiat-equivalent value, and must not block the rest of that institution's sync — same isolation principle `BankLinkService.SyncAsync` already applies per-institution, now applied per-account for price fetches.
- Currencies are never summed across groups anywhere — `FinancialStatementService` computes one `TotalAssets`/`TotalLiabilities`/`OwnersEquity` per currency group independently, same as `CashFlowService` already does for income/expenses.
- **Cash Flow is untouched.** `Other` accounts sync transactions normally and participate automatically via its existing bucketing. `Crypto` accounts still never sync transactions — out of scope (no historical price feed).
- **`CryptoPriceClient`'s `HttpClient.BaseAddress` must end with a trailing slash, and every request path must NOT start with `/`** — `CoinGecko:ApiBaseUrl` is `https://api.coingecko.com/api/v3/` (note the trailing slash) specifically so the `/api/v3` path segment survives combination with a relative request path (`.NET`'s `HttpClient` silently drops the base path if the relative URI starts with `/`, a classic gotcha — `FinverseClient`/`PayPalClient` don't hit this because their base addresses have no path segment to preserve).
- This project is pre-launch; the current migration situation (after the earlier `migrations remove` incident this session) is a single `InitialMigration` plus incremental follow-on migrations. This plan adds a new incremental migration (`dotnet ef migrations add`, not a hand-edit), same pattern established for the Loan Calculator work.

---

## Task 1: `AccountType`/`AssetType` enum additions, `Account` entity fields

**Files:**
- Modify: `src/FinTrackPrime.Models/Entities/Account.cs`
- Modify: `src/FinTrackPrime.Models/Entities/AssetType.cs`

**Interfaces:**
- Produces: `AccountType.Other`/`AccountType.Crypto`, `Account.FiatEquivalentValue`/`FiatEquivalentCurrency`/`PriceFetchedAtUtc`, `AssetType.Crypto` — consumed by every later task.

- [ ] **Step 1: Update `AccountType` and `Account`**

Full replacement for `src/FinTrackPrime.Models/Entities/Account.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace FinTrackPrime.Models.Entities
{
    public enum AccountType
    {
        Checking,
        Savings,
        CreditCard,

        // A real fiat balance whose Finverse account_type.subtype isn't
        // one of the three above (e.g. a generic ledger account) — still
        // a real currency, safe to include everywhere via per-currency
        // bucketing (see FinancialStatementService/CashFlowService).
        Other,

        // A non-fiat balance (BTC, ...). FiatEquivalentValue/Currency
        // below carry the last successful conversion, refreshed each
        // sync — see BankLinkService.SyncInstitutionAsync.
        Crypto,

        // No usable currency at all. Narrower than it used to be: this
        // used to be the catch-all for anything unrecognized; now only
        // a defensive edge case (see BankLinkService.MapAccountType).
        Unsupported,
    }

    // A bank account linked via Finverse (or, historically, entered
    // manually before that path was removed). ExternalAccountId +
    // Institution identify which Finverse-linked account this row mirrors;
    // both are empty for any row created before linking existed.
    public class Account
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Nickname { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public decimal Balance { get; set; }

        // Multi-currency accounts (Finverse's Testbank returns HKD, USD,
        // etc.) are stored as-is; FinancialStatementService/CashFlowService
        // bucket by this rather than converting between currencies.
        public string Currency { get; set; } = string.Empty;

        // Only populated for AccountType.Crypto — the last successful
        // conversion of Balance (in Currency) to a fiat value. A failed
        // price-feed call during sync leaves these as whatever they were
        // last time, rather than clearing them.
        public decimal? FiatEquivalentValue { get; set; }
        public string? FiatEquivalentCurrency { get; set; }
        public DateTime? PriceFetchedAtUtc { get; set; }

        public string ExternalAccountId { get; set; } = string.Empty;
        public string Institution { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
}
```

- [ ] **Step 2: Add `Crypto` to `AssetType`**

`src/FinTrackPrime.Models/Entities/AssetType.cs` — change:
```csharp
    public enum AssetType
    {
        Cash,
        Investment,
        RealEstate,
        Vehicle,
        Other,
    }
```
to:
```csharp
    public enum AssetType
    {
        Cash,
        Investment,
        RealEstate,
        Vehicle,
        Crypto,
        Other,
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/Account.cs src/FinTrackPrime.Models/Entities/AssetType.cs
git commit -m "feat: add Other/Crypto AccountType, crypto fields on Account, Crypto AssetType"
```

---

## Task 2: `FinTrackDbContext` — new `Account` column config

**Files:**
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Consumes: `Account` from Task 1.
- Produces: EF column mapping for the three new fields, consumed by Task 3 (migration).

- [ ] **Step 1: Add the new property config**

Find the existing `modelBuilder.Entity<Account>(entity => { ... });` block and add two lines:
```csharp
            modelBuilder.Entity<Account>(entity =>
            {
                entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
                entity.Property(a => a.Currency).HasMaxLength(8);
                entity.Property(a => a.FiatEquivalentValue).HasColumnType("decimal(18,2)");
                entity.Property(a => a.FiatEquivalentCurrency).HasMaxLength(8);
                entity.Property(a => a.ExternalAccountId).HasMaxLength(128);
                entity.Property(a => a.Institution).HasMaxLength(80);
                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs
git commit -m "feat: configure Account's new crypto fields"
```

---

## Task 3: Migration + CoinGecko config

**Files:**
- Generate via CLI: `<timestamp>_AddAccountCryptoFields.cs`/`.Designer.cs`
- Modify (auto-updated): `FinTrackDbContextModelSnapshot.cs`
- Modify: `src/FinTrackPrime.WebApi/appsettings.json`

**Interfaces:**
- Consumes: shape from Tasks 1–2.
- Produces: the three new `Accounts` columns, queryable by Task 5's `BankLinkService`; `CoinGecko:ApiBaseUrl` config, consumed by Task 4.

- [ ] **Step 1: Generate and apply the migration**

```bash
dotnet ef migrations add AddAccountCryptoFields --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
dotnet ef database update --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
```
Expected: adds `FiatEquivalentValue` (decimal(18,2), nullable), `FiatEquivalentCurrency` (nvarchar(8), nullable), `PriceFetchedAtUtc` (datetime2, nullable) to `Accounts`.

- [ ] **Step 2: Add the CoinGecko config section**

In `src/FinTrackPrime.WebApi/appsettings.json`, add alongside `Finverse`:
```json
  "CoinGecko": {
    "ApiBaseUrl": "https://api.coingecko.com/api/v3/"
  },
```
(Trailing slash is required — see Global Constraints.)

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations/ src/FinTrackPrime.WebApi/appsettings.json
git commit -m "feat: add Account crypto columns migration, CoinGecko config"
```

---

## Task 4: `ICryptoPriceClient` / `CryptoPriceClient`

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/ICryptoPriceClient.cs`
- Create: `src/FinTrackPrime.Business/Services/CryptoPriceClient.cs`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`
- Test: `tests/FinTrackPrime.Business.Tests/CryptoPriceClientTests.cs` (new)

**Interfaces:**
- Produces: `ICryptoPriceClient.GetFiatEquivalentAsync` — consumed by Task 5 (`BankLinkService`).

- [ ] **Step 1: Write the failing tests**

Create `tests/FinTrackPrime.Business.Tests/CryptoPriceClientTests.cs` — reuses `FakeHttpMessageHandler`, already `public` in `FinverseClientTests.cs`, same namespace:
```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class CryptoPriceClientTests
    {
        [Fact]
        public async Task GetFiatEquivalentAsync_ReturnsAmountTimesUnitPrice()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/api/v3/simple/price"] = (HttpStatusCode.OK, "{\"bitcoin\":{\"usd\":65000.00}}"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            var result = await client.GetFiatEquivalentAsync("BTC", 0.5m, "USD");

            Assert.Equal(32500.00m, result);
        }

        [Fact]
        public async Task GetFiatEquivalentAsync_ThrowsForUnrecognizedCryptoCurrency()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>());
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFiatEquivalentAsync("DOGE", 100m, "USD"));
        }

        [Fact]
        public async Task GetFiatEquivalentAsync_ThrowsOnFailedRequest()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/api/v3/simple/price"] = (HttpStatusCode.InternalServerError, "server error"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFiatEquivalentAsync("BTC", 1m, "USD"));
        }
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter CryptoPriceClientTests`
Expected: does not build — `CryptoPriceClient` doesn't exist yet.

- [ ] **Step 3: Create `ICryptoPriceClient`**

```csharp
using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    public interface ICryptoPriceClient
    {
        // Throws InvalidOperationException on an unrecognized
        // cryptoCurrency or a failed API call — BankLinkService decides
        // how to handle that (keep the previous cached value).
        Task<decimal> GetFiatEquivalentAsync(string cryptoCurrency, decimal amount, string fiatCurrency);
    }
}
```

- [ ] **Step 4: Implement `CryptoPriceClient`**

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;

namespace FinTrackPrime.Business.Services
{
    // Talks to CoinGecko's free /simple/price endpoint directly (no API
    // key). Registered with a typed HttpClient (see Program.cs) whose
    // BaseAddress must end with a trailing slash — see this plan's
    // Global Constraints for why.
    public class CryptoPriceClient : ICryptoPriceClient
    {
        // The only crypto currency Testbank has actually surfaced so
        // far — extend alongside BankLinkService.KnownCryptoCurrencies
        // if a new one is ever seen. CoinGecko's API expects full coin
        // ids, not ticker symbols.
        private static readonly Dictionary<string, string> CoinGeckoIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BTC"] = "bitcoin",
        };

        private readonly HttpClient _httpClient;

        public CryptoPriceClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<decimal> GetFiatEquivalentAsync(string cryptoCurrency, decimal amount, string fiatCurrency)
        {
            if (!CoinGeckoIds.TryGetValue(cryptoCurrency, out var coinId))
            {
                throw new InvalidOperationException($"Unrecognized crypto currency: {cryptoCurrency}.");
            }

            var fiatLower = fiatCurrency.ToLowerInvariant();

            // No leading slash — BaseAddress carries the /api/v3 segment
            // and must be combined with a relative (not rooted) path.
            using var response = await _httpClient.GetAsync($"simple/price?ids={coinId}&vs_currencies={fiatLower}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"CoinGecko price request failed ({(int)response.StatusCode}): {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var unitPrice = doc.RootElement.GetProperty(coinId).GetProperty(fiatLower).GetDecimal();

            return Math.Round(unitPrice * amount, 2);
        }
    }
}
```

- [ ] **Step 5: Register in `Program.cs`**

Add alongside the existing typed-HttpClient registrations:
```csharp
builder.Services.AddHttpClient<ICryptoPriceClient, CryptoPriceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["CoinGecko:ApiBaseUrl"]!);
});
```

- [ ] **Step 6: Run the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter CryptoPriceClientTests`
Expected: PASS, all 3 tests.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/ICryptoPriceClient.cs src/FinTrackPrime.Business/Services/CryptoPriceClient.cs src/FinTrackPrime.WebApi/Program.cs tests/FinTrackPrime.Business.Tests/CryptoPriceClientTests.cs
git commit -m "feat: add CryptoPriceClient (CoinGecko)"
```

---

## Task 5: `BankLinkService` — classification redesign, crypto price caching

**Files:**
- Modify: `src/FinTrackPrime.Business/Services/BankLinkService.cs`
- Modify: `tests/FinTrackPrime.Business.Tests/BankLinkServiceTests.cs`

**Interfaces:**
- Consumes: `ICryptoPriceClient` (Task 4), `Account`'s new fields (Task 1).
- Produces: reclassified `AccountType` assignment, consumed by Task 7 (`FinancialStatementService`) and the frontend (Task 9/10).

### Part A: Service changes

- [ ] **Step 1: Add the `ICryptoPriceClient` dependency**

In `BankLinkService`, add a field/constructor parameter:
```csharp
        private readonly ICryptoPriceClient _cryptoPriceClient;

        public BankLinkService(
            FinTrackDbContext db,
            IFinverseClient finverseClient,
            ICryptoPriceClient cryptoPriceClient,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration config)
        {
            _db = db;
            _finverseClient = finverseClient;
            _cryptoPriceClient = cryptoPriceClient;
            _protector = dataProtectionProvider.CreateProtector("FinTrackPrime.LinkedInstitution.AccessToken");
            _config = config;
        }
```

- [ ] **Step 2: Redesign `MapAccountType`**

Replace:
```csharp
        // Unknown/unsupported types return null; the caller maps that to
        // AccountType.Unsupported rather than guessing which known type it
        // might be.
        // VERIFY: confirm these raw strings against real Finverse account
        // "type" values (seen only via the Demo App's rendered labels so
        // far, e.g. "HKD Checking" / "HKD Statement Savings" / "HKD
        // Credit Card" — not the underlying JSON "type" field itself).
        private static AccountType? MapAccountType(string finverseAccountType)
        {
            return finverseAccountType.ToLowerInvariant() switch
            {
                "checking" or "current" => AccountType.Checking,
                "savings" => AccountType.Savings,
                "credit_card" or "credit" => AccountType.CreditCard,
                _ => null,
            };
        }
```
with:
```csharp
        // The only crypto currency Testbank has actually surfaced so
        // far — extend alongside CryptoPriceClient.CoinGeckoIds if a new
        // one is ever seen.
        private static readonly HashSet<string> KnownCryptoCurrencies = new(StringComparer.OrdinalIgnoreCase) { "BTC" };

        // Detects crypto by currency code, not by guessing more Finverse
        // subtype strings — sidesteps the same uncertainty the old VERIFY
        // comment here used to flag. Anything with a recognized subtype
        // maps directly; anything else with a real (non-crypto) currency
        // becomes Other — a real balance in a real currency, no reason to
        // exclude it just because its subtype string is unrecognized.
        // Unsupported now only means "no usable currency at all."
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

Add `using System.Collections.Generic;` to the file's usings if not already present (it already is, for `IEnumerable`/collection types used elsewhere — verify before assuming).

- [ ] **Step 3: Update `SyncInstitutionAsync`**

Replace:
```csharp
                // null (unrecognized subtype, e.g. Bitcoin/FX in Testbank)
                // maps to Unsupported below instead of being skipped — the
                // account is still stored and shown with its balance, just
                // without transaction sync (see AccountType.Unsupported).
                var accountType = MapAccountType(finverseAccount.AccountType) ?? AccountType.Unsupported;
```
with:
```csharp
                var accountType = MapAccountType(finverseAccount.AccountType, finverseAccount.Currency);
```

Replace:
```csharp
                account.Nickname = finverseAccount.AccountName;
                account.Type = accountType;
                account.Currency = finverseAccount.Currency;
                account.Balance = finverseAccount.Balance;

                if (accountType == AccountType.Unsupported)
                {
                    continue; // balance/nickname synced above; transactions are not.
                }
```
with:
```csharp
                account.Nickname = finverseAccount.AccountName;
                account.Type = accountType;
                account.Currency = finverseAccount.Currency;
                account.Balance = finverseAccount.Balance;

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
                        // Leave the previous cached value as-is — a stale
                        // price beats no price, and one account's
                        // price-feed hiccup must not block the rest of
                        // this institution's sync.
                    }
                }

                if (accountType == AccountType.Unsupported || accountType == AccountType.Crypto)
                {
                    continue; // balance/nickname (and, for Crypto, the fiat-equivalent) synced above; transactions are not.
                }
```

Add the constant near the top of the class, alongside the other fields:
```csharp
        private const string TargetFiatCurrency = "USD";
```

- [ ] **Step 4: Build**

Run: `dotnet build src/FinTrackPrime.Business/FinTrackPrime.Business.csproj`
Expected: **succeeds** — no `Program.cs` change is needed for this new constructor parameter; ASP.NET Core's DI container resolves `ICryptoPriceClient` automatically by type once `AddScoped<IBankLinkService, BankLinkService>` runs, since `ICryptoPriceClient` is already registered from Task 4. If this fails to build/resolve at runtime later, check that Task 4's registration landed correctly.

### Part B: Tests

- [ ] **Step 5: Add the `ICryptoPriceClient` mock helper and update every existing constructor call**

In `BankLinkServiceTests.cs`, add near the other `Build*` helpers:
```csharp
        private static ICryptoPriceClient BuildCryptoPriceClient() => new Mock<ICryptoPriceClient>().Object;
```

Then replace **every** occurrence of:
```
new BankLinkService(db, finverseClient.Object, BuildDataProtection(), BuildConfig())
```
with:
```
new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig())
```
This string appears identically in all 7 existing test methods — a single find-and-replace-all across the file is correct here (verify with a search first that no other call site's parameter order or wording differs before doing so blindly).

- [ ] **Step 6: Replace the now-outdated classification test with the new set**

The existing test asserted Bitcoin lands on `Unsupported` — that's no longer correct behavior (Bitcoin is now `Crypto`). Replace:
```csharp
        [Fact]
        public async Task CompleteLinkAsync_SurfacesUnrecognizedAccountTypesAsUnsupportedWithoutSyncingTransactions()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
                new("acc-bitcoin", "Bitcoin", "bitcoin", "BTC", 420.69m),
                new("acc-credit", "HKD Credit Card", "credit_card", "HKD", -1833.22m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var service = new BankLinkService(db, finverseClient.Object, BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var accounts = await db.Accounts.Where(a => a.UserId == userId).ToListAsync();
            Assert.Equal(3, accounts.Count);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-checking" && a.Type == AccountType.Checking);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-credit" && a.Type == AccountType.CreditCard);

            // Stored and visible (nickname/balance), not silently dropped —
            // but its type is unrecognized, so no transactions are synced
            // for it.
            var bitcoin = Assert.Single(accounts, a => a.ExternalAccountId == "acc-bitcoin");
            Assert.Equal(AccountType.Unsupported, bitcoin.Type);
            Assert.Equal(420.69m, bitcoin.Balance);
            var bitcoinTransactions = await db.Transactions.Where(t => t.AccountId == bitcoin.Id).ToListAsync();
            Assert.Empty(bitcoinTransactions);
        }
```
with:
```csharp
        [Fact]
        public async Task CompleteLinkAsync_ClassifiesUnrecognizedFiatSubtypeAsOtherAndSyncsItsTransactions()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-ledger", "HKD Ledger Account", "ledger", "HKD", 100.00m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-ledger")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-1", "Deposit", 50.00m, new DateTime(2024, 11, 11)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var ledger = Assert.Single(await db.Accounts.Where(a => a.UserId == userId).ToListAsync());
            Assert.Equal(AccountType.Other, ledger.Type);
            Assert.Equal(100.00m, ledger.Balance);

            // Unlike Unsupported, Other accounts DO sync transactions.
            var transactions = await db.Transactions.Where(t => t.AccountId == ledger.Id).ToListAsync();
            Assert.Single(transactions);
        }

        [Fact]
        public async Task CompleteLinkAsync_ClassifiesKnownCryptoCurrencyAsCryptoWithFiatEquivalentAndNoTransactionSync()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-bitcoin", "Bitcoin", "bitcoin", "BTC", 420.69m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var cryptoPriceClient = new Mock<ICryptoPriceClient>();
            cryptoPriceClient.Setup(c => c.GetFiatEquivalentAsync("BTC", 420.69m, "USD")).ReturnsAsync(27450.00m);

            var service = new BankLinkService(db, finverseClient.Object, cryptoPriceClient.Object, BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var bitcoin = Assert.Single(await db.Accounts.Where(a => a.UserId == userId).ToListAsync());
            Assert.Equal(AccountType.Crypto, bitcoin.Type);
            Assert.Equal(420.69m, bitcoin.Balance);   // raw balance untouched
            Assert.Equal(27450.00m, bitcoin.FiatEquivalentValue);
            Assert.Equal("USD", bitcoin.FiatEquivalentCurrency);
            Assert.NotNull(bitcoin.PriceFetchedAtUtc);

            var bitcoinTransactions = await db.Transactions.Where(t => t.AccountId == bitcoin.Id).ToListAsync();
            Assert.Empty(bitcoinTransactions);
        }

        [Fact]
        public async Task CompleteLinkAsync_ClassifiesMissingCurrencyAsUnsupported()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-unknown", "Mystery Account", "some_new_subtype", "", 1.00m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var account = Assert.Single(await db.Accounts.Where(a => a.UserId == userId).ToListAsync());
            Assert.Equal(AccountType.Unsupported, account.Type);
        }

        [Fact]
        public async Task CompleteLinkAsync_CryptoPriceFetchFailure_LeavesPreviousCachedValueAndDoesNotThrow()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-bitcoin", "Bitcoin", "bitcoin", "BTC", 420.69m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var cryptoPriceClient = new Mock<ICryptoPriceClient>();
            cryptoPriceClient.Setup(c => c.GetFiatEquivalentAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("CoinGecko is down"));

            var service = new BankLinkService(db, finverseClient.Object, cryptoPriceClient.Object, BuildDataProtection(), BuildConfig());

            // Must not throw despite the price feed failing.
            await service.CompleteLinkAsync(userId, "link-code");

            var bitcoin = Assert.Single(await db.Accounts.Where(a => a.UserId == userId).ToListAsync());
            Assert.Equal(AccountType.Crypto, bitcoin.Type);
            Assert.Null(bitcoin.FiatEquivalentValue);   // no previous value existed to preserve, so still null — not populated with garbage
        }
```

Add `using FinTrackPrime.Business.Interfaces;` at the top of the file if not already present (it already is, for `IFinverseClient`).

- [ ] **Step 7: Run the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter BankLinkServiceTests`
Expected: PASS, all 9 tests (5 pre-existing unaffected by classification logic + 4 new/replaced).

- [ ] **Step 8: Run the full test project to confirm nothing else broke**

Run: `dotnet test tests/FinTrackPrime.Business.Tests`
Expected: PASS, every test in the project.

- [ ] **Step 9: Commit**

```bash
git add src/FinTrackPrime.Business/Services/BankLinkService.cs tests/FinTrackPrime.Business.Tests/BankLinkServiceTests.cs
git commit -m "feat: reclassify accounts as Other/Crypto, cache crypto fiat-equivalent at sync time"
```

---

## Task 6: `FinancialStatementViewModels.cs` — currency fields, per-currency shape

**Files:**
- Modify: `src/FinTrackPrime.Models/ViewModels/FinancialStatementViewModels.cs`

**Interfaces:**
- Produces: `AssetLineViewModel.Currency`, `LiabilityViewModel.Currency`, `FinancialStatementByCurrencyViewModel`, restructured `FinancialStatementViewModel` — consumed by Task 7 (service) and Task 10 (frontend).

- [ ] **Step 1: Update the file**

Add `Currency` to both line view models, and add/restructure the statement-level view models. Full replacement:
```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class LiabilityViewModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public LiabilityType Type { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class CreateLiabilityRequest
    {
        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public LiabilityType Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }
    }

    public class AssetLineViewModel
    {
        // Null for synced lines (Cash from an Account, Investment from a
        // holding) — those aren't removable. Set for manual Asset rows,
        // which are.
        public Guid? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public AssetType Type { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class CreateAssetRequest
    {
        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public AssetType Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }
    }

    // One currency's worth of the statement — same shape as the
    // top-level FinancialStatementViewModel below minus GeneratedAtUtc
    // (that's a whole-statement concept, not per-currency), mirroring
    // CashFlowByCurrencyViewModel.
    public class FinancialStatementByCurrencyViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal OwnersEquity { get; set; }
    }

    // A simple personal balance sheet: everything owned, everything
    // owed, and the difference — now bucketed per currency, same
    // principle CashFlowViewModel already uses. Currency/Assets/
    // TotalAssets/Liabilities/TotalLiabilities/OwnersEquity below are
    // whichever currency has the most combined asset+liability lines;
    // every other currency present is in OtherCurrencies, never blended
    // into this one.
    public class FinancialStatementViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal OwnersEquity { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public List<FinancialStatementByCurrencyViewModel> OtherCurrencies { get; set; } = new();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/FinancialStatementViewModels.cs
git commit -m "feat: add Currency to Financial Statement view models, per-currency statement shape"
```

---

## Task 7: `FinancialStatementService` — currency-bucketed statement

**Files:**
- Modify: `src/FinTrackPrime.Business/Services/FinancialStatementService.cs`
- Modify: `tests/FinTrackPrime.Business.Tests/FinancialStatementServiceTests.cs`

**Interfaces:**
- Consumes: view models (Task 6), `Account`'s new fields (Task 1).
- Produces: currency-bucketed `GetStatementAsync`, consumed by Task 10 (frontend).

Existing tests seed all their accounts with a single currency (`"USD"`) each, so they exercise the "everything lands in the primary bucket, `OtherCurrencies` is empty" path and their existing assertions against `statement.TotalAssets`/`TotalLiabilities`/`OwnersEquity` should keep passing unchanged — this task adds new tests specifically for the multi-currency bucketing behavior, rather than rewriting the existing ones.

- [ ] **Step 1: Write the failing tests**

Append to `FinancialStatementServiceTests.cs`:
```csharp
        [Fact]
        public async Task GetStatementAsync_BucketsDifferentCurrenciesSeparately()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "HKD Checking", Type = AccountType.Checking,
                Balance = 10000m, Currency = "HKD", ExternalAccountId = "acc-hkd", Institution = "Testbank", CreatedAtUtc = DateTime.UtcNow,
            });
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "USD FX", Type = AccountType.Other,
                Balance = 500m, Currency = "USD", ExternalAccountId = "acc-usd", Institution = "Testbank", CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            // HKD has more lines here only because there's exactly one of
            // each — the primary-currency pick is a tie broken by
            // GroupBy/OrderByDescending's stable ordering; the meaningful
            // assertion is that HKD and USD never appear summed together.
            Assert.Equal(10000m, statement.TotalAssets);
            var usdBucket = Assert.Single(statement.OtherCurrencies, c => c.Currency == "USD");
            Assert.Equal(500m, usdBucket.TotalAssets);
        }

        [Fact]
        public async Task GetStatementAsync_CryptoAssetUsesFiatEquivalentNotRawBalance()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "Bitcoin", Type = AccountType.Crypto,
                Balance = 420.69m, Currency = "BTC",
                FiatEquivalentValue = 27450.00m, FiatEquivalentCurrency = "USD", PriceFetchedAtUtc = DateTime.UtcNow,
                ExternalAccountId = "acc-btc", Institution = "Testbank", CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            var line = Assert.Single(statement.Assets);
            Assert.Equal(AssetType.Crypto, line.Type);
            Assert.Equal("USD", line.Currency);
            Assert.Equal(27450.00m, line.Amount);   // fiat-equivalent, not 420.69
            Assert.Equal(27450.00m, statement.TotalAssets);
        }

        [Fact]
        public async Task GetStatementAsync_OtherAccountIsIncludedLikeCheckingOrSavings()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "HKD Ledger Account", Type = AccountType.Other,
                Balance = 100m, Currency = "HKD", ExternalAccountId = "acc-ledger", Institution = "Testbank", CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            var line = Assert.Single(statement.Assets);
            Assert.Equal(AssetType.Cash, line.Type);
            Assert.Equal(100m, line.Amount);
        }
```

- [ ] **Step 2: Run the tests, confirm they fail**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter FinancialStatementServiceTests`
Expected: the 3 new tests FAIL (no bucketing/crypto/Other handling yet); the pre-existing tests still PASS unchanged.

- [ ] **Step 3: Rewrite `GetStatementAsync`**

Replace the entire method:
```csharp
        public async Task<FinancialStatementViewModel> GetStatementAsync(Guid userId)
        {
            var accounts = await _db.Accounts
                .Where(a => a.UserId == userId)
                .ToListAsync();

            var holdings = await _db.InvestmentHoldings
                .Where(h => h.UserId == userId)
                .ToListAsync();

            var manualAssets = await _db.Assets
                .Where(a => a.UserId == userId)
                .ToListAsync();

            var liabilities = await _db.Liabilities
                .Where(l => l.UserId == userId)
                .ToListAsync();

            var creditCardAccounts = accounts.Where(a => a.Type == AccountType.CreditCard).ToList();
            var cashAccounts = accounts.Where(a => a.Type is AccountType.Checking or AccountType.Savings or AccountType.Other).ToList();
            var cryptoAccounts = accounts.Where(a => a.Type == AccountType.Crypto).ToList();

            // Primary currency is derived from accounts only (the
            // objective source of truth) — manual assets/liabilities and
            // investment holdings have no currency of their own and
            // simply inherit whichever currency wins here.
            var accountCurrencies = cashAccounts.Select(a => a.Currency)
                .Concat(creditCardAccounts.Select(a => a.Currency))
                .Concat(cryptoAccounts.Select(a => a.FiatEquivalentCurrency ?? "USD"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();

            var primaryCurrency = accountCurrencies
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "USD";

            var assetLines = cashAccounts
                .Select(a => new AssetLineViewModel { Id = null, Label = a.Nickname, Type = AssetType.Cash, Currency = a.Currency, Amount = a.Balance })
                .Concat(cryptoAccounts.Select(a => new AssetLineViewModel
                {
                    Id = null,
                    Label = a.Nickname,
                    Type = AssetType.Crypto,
                    Currency = a.FiatEquivalentCurrency ?? "USD",
                    Amount = a.FiatEquivalentValue ?? 0m,
                }))
                .Concat(holdings.Select(h => new AssetLineViewModel
                {
                    Id = null,
                    Label = $"{h.Symbol} holding",
                    Type = AssetType.Investment,
                    Currency = primaryCurrency,
                    Amount = h.Shares * h.CurrentPricePerShare,
                }))
                .Concat(manualAssets.Select(a => new AssetLineViewModel
                {
                    Id = a.Id,
                    Label = a.Name,
                    Type = a.Type,
                    Currency = primaryCurrency,
                    Amount = a.Amount,
                }))
                .ToList();

            var liabilityLines = liabilities
                .Select(l => new LiabilityViewModel { Id = l.Id, Name = l.Name, Type = l.Type, Currency = primaryCurrency, Amount = l.Amount })
                .Concat(creditCardAccounts.Select(a => new LiabilityViewModel
                {
                    Id = a.Id,
                    Name = a.Nickname,
                    Type = LiabilityType.CreditCard,
                    Currency = a.Currency,
                    Amount = Math.Abs(a.Balance),
                }))
                .ToList();

            var assetsByCurrency = assetLines
                .GroupBy(a => a.Currency, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Amount).ToList(), StringComparer.OrdinalIgnoreCase);
            var liabilitiesByCurrency = liabilityLines
                .GroupBy(l => l.Currency, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Amount).ToList(), StringComparer.OrdinalIgnoreCase);

            var allCurrencies = assetsByCurrency.Keys
                .Concat(liabilitiesByCurrency.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            FinancialStatementByCurrencyViewModel BuildBucket(string currency)
            {
                var currencyAssets = assetsByCurrency.TryGetValue(currency, out var a) ? a : new List<AssetLineViewModel>();
                var currencyLiabilities = liabilitiesByCurrency.TryGetValue(currency, out var l) ? l : new List<LiabilityViewModel>();
                var totalAssets = currencyAssets.Sum(x => x.Amount);
                var totalLiabilities = currencyLiabilities.Sum(x => x.Amount);

                return new FinancialStatementByCurrencyViewModel
                {
                    Currency = currency,
                    Assets = currencyAssets,
                    TotalAssets = totalAssets,
                    Liabilities = currencyLiabilities,
                    TotalLiabilities = totalLiabilities,
                    OwnersEquity = totalAssets - totalLiabilities,
                };
            }

            var primaryBucket = BuildBucket(primaryCurrency);
            var otherCurrencies = allCurrencies
                .Where(c => !string.Equals(c, primaryCurrency, StringComparison.OrdinalIgnoreCase))
                .Select(BuildBucket)
                .ToList();

            return new FinancialStatementViewModel
            {
                Currency = primaryCurrency,
                Assets = primaryBucket.Assets,
                TotalAssets = primaryBucket.TotalAssets,
                Liabilities = primaryBucket.Liabilities,
                TotalLiabilities = primaryBucket.TotalLiabilities,
                OwnersEquity = primaryBucket.OwnersEquity,
                GeneratedAtUtc = DateTime.UtcNow,
                OtherCurrencies = otherCurrencies,
            };
        }
```

- [ ] **Step 4: Update `AddAssetAsync`/`AddLiabilityAsync` return values**

Both already construct an `AssetLineViewModel`/`LiabilityViewModel` directly — add `Currency = string.Empty` to each (the immediate response isn't what's rendered; the frontend's existing `invalidate()` call re-fetches the full, correctly-bucketed statement right after, same pattern already in place):
```csharp
            return new AssetLineViewModel { Id = asset.Id, Label = asset.Name, Type = asset.Type, Currency = string.Empty, Amount = asset.Amount };
```
```csharp
            return new LiabilityViewModel { Id = liability.Id, Name = liability.Name, Type = liability.Type, Currency = string.Empty, Amount = liability.Amount };
```

- [ ] **Step 5: Run the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter FinancialStatementServiceTests`
Expected: PASS, all tests (pre-existing + 3 new).

- [ ] **Step 6: Run the full test project to confirm nothing else broke**

Run: `dotnet test tests/FinTrackPrime.Business.Tests`
Expected: PASS, every test in the project.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Business/Services/FinancialStatementService.cs tests/FinTrackPrime.Business.Tests/FinancialStatementServiceTests.cs
git commit -m "feat: currency-bucket the Financial Statement, fold crypto fiat-equivalent into its bucket"
```

---

## Task 8: Frontend types (`types/api.ts`)

**Files:**
- Modify: `src/types/api.ts` (in `C:\Users\Wyrlo\projects\FinTrackPrime`)

**Interfaces:**
- Produces: updated `AccountType`, `AssetType`, `AssetLineViewModel`, `LiabilityViewModel`, `FinancialStatementViewModel`, new `FinancialStatementByCurrencyViewModel` — consumed by Task 9 (Dashboard) and Task 10 (Financial Statement page).

- [ ] **Step 1: Update `AccountType`**

Find (likely near the top of the file, alongside `AccountViewModel`):
```ts
export type AccountType = 'Checking' | 'Savings' | 'CreditCard' | 'Unsupported'
```
Replace with:
```ts
export type AccountType = 'Checking' | 'Savings' | 'CreditCard' | 'Other' | 'Crypto' | 'Unsupported'
```

- [ ] **Step 2: Update `AssetType`**

Find:
```ts
export type AssetType = 'Cash' | 'Investment' | 'RealEstate' | 'Vehicle' | 'Other'
```
Replace with:
```ts
export type AssetType = 'Cash' | 'Investment' | 'RealEstate' | 'Vehicle' | 'Crypto' | 'Other'
```

- [ ] **Step 3: Update the Financial Statement type block**

Find and replace:
```ts
export interface AssetLineViewModel {
  // Undefined for synced lines (Cash from an account, Investment from a
  // holding) — those aren't removable. Set for manual assets, which are.
  id?: string
  label: string
  type: AssetType
  amount: number
}

export interface CreateAssetRequest {
  name: string
  type: AssetType
  amount: number
}

// OwnersEquity is totalAssets - totalLiabilities — the same figure this
// app used to call netWorth, relabeled to match the bank's requested
// Assets/Liabilities/Owner's-Equity presentation.
export interface FinancialStatementViewModel {
  assets: AssetLineViewModel[]
  totalAssets: number
  liabilities: LiabilityViewModel[]
  totalLiabilities: number
  ownersEquity: number
  generatedAtUtc: string
}
```
with:
```ts
export interface AssetLineViewModel {
  // Undefined for synced lines (Cash from an account, Investment from a
  // holding) — those aren't removable. Set for manual assets, which are.
  id?: string
  label: string
  type: AssetType
  currency: string
  amount: number
}

export interface CreateAssetRequest {
  name: string
  type: AssetType
  amount: number
}

// One currency's worth of the statement, mirroring
// CashFlowByCurrencyViewModel — never summed together with any other
// currency's bucket.
export interface FinancialStatementByCurrencyViewModel {
  currency: string
  assets: AssetLineViewModel[]
  totalAssets: number
  liabilities: LiabilityViewModel[]
  totalLiabilities: number
  ownersEquity: number
}

// currency/assets/totalAssets/liabilities/totalLiabilities/ownersEquity
// below are whichever currency has the most combined asset+liability
// lines; every other currency present is in otherCurrencies.
export interface FinancialStatementViewModel {
  currency: string
  assets: AssetLineViewModel[]
  totalAssets: number
  liabilities: LiabilityViewModel[]
  totalLiabilities: number
  ownersEquity: number
  generatedAtUtc: string
  otherCurrencies: FinancialStatementByCurrencyViewModel[]
}
```

- [ ] **Step 4: Add `currency` to `LiabilityViewModel`**

Find:
```ts
export interface LiabilityViewModel {
  id: string
  name: string
  type: LiabilityType
  amount: number
}
```
Replace with:
```ts
export interface LiabilityViewModel {
  id: string
  name: string
  type: LiabilityType
  currency: string
  amount: number
}
```

- [ ] **Step 5: Commit**

```bash
git add src/types/api.ts
git commit -m "feat: add Other/Crypto account and asset types, currency-bucketed Financial Statement types"
```

---

## Task 9: `DashboardPage.tsx` — labels, Crypto note, total-balance filter

**Files:**
- Modify: `src/pages/DashboardPage.tsx`

**Interfaces:**
- Consumes: `AccountType` (Task 8).
- Produces: the updated Dashboard — nothing downstream depends on it.

- [ ] **Step 1: Update `ACCOUNT_TYPE_LABELS`**

Find:
```ts
const ACCOUNT_TYPE_LABELS: Record<AccountType, string> = {
  Checking: 'Checking',
  Savings: 'Savings',
  CreditCard: 'Credit Card',
  Unsupported: 'Unsupported',
}
```
Replace with:
```ts
const ACCOUNT_TYPE_LABELS: Record<AccountType, string> = {
  Checking: 'Checking',
  Savings: 'Savings',
  CreditCard: 'Credit Card',
  Other: 'Other',
  Crypto: 'Crypto',
  Unsupported: 'Unsupported',
}
```

- [ ] **Step 2: Add a Crypto-specific note in `AccountCard`**

Find:
```tsx
function AccountCard({ account }: { account: AccountViewModel }) {
  const isUnsupported = account.type === 'Unsupported'

  return (
    <Card hoverElevate>
      <div className="flex items-baseline justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">
            {ACCOUNT_TYPE_LABELS[account.type]}
          </p>
          <h2 className="font-display text-lg text-text-primary">{account.nickname}</h2>
        </div>
        <p className="tabular-figure font-display text-2xl text-text-primary">
          {formatCurrency(account.balance, account.currency)}
        </p>
      </div>

      {isUnsupported && (
        <p className="mt-2 text-xs text-text-muted">
          Not supported yet — balance shown above, but excluded from Total balance, Cash Flow, and the Financial
          Statement. No transactions are synced for it.
        </p>
      )}
```
Replace with:
```tsx
function AccountCard({ account }: { account: AccountViewModel }) {
  const isUnsupported = account.type === 'Unsupported'
  const isCrypto = account.type === 'Crypto'

  return (
    <Card hoverElevate>
      <div className="flex items-baseline justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">
            {ACCOUNT_TYPE_LABELS[account.type]}
          </p>
          <h2 className="font-display text-lg text-text-primary">{account.nickname}</h2>
        </div>
        <p className="tabular-figure font-display text-2xl text-text-primary">
          {formatCurrency(account.balance, account.currency)}
        </p>
      </div>

      {isUnsupported && (
        <p className="mt-2 text-xs text-text-muted">
          Not supported yet — balance shown above, but excluded from Total balance, Cash Flow, and the Financial
          Statement. No transactions are synced for it.
        </p>
      )}

      {isCrypto && (
        <p className="mt-2 text-xs text-text-muted">
          Converted to its dollar value for your Financial Statement using the last synced price — not included in
          Cash Flow.
        </p>
      )}
```

- [ ] **Step 3: Update the `totalBalance` filter**

Find:
```ts
    const totalBalance = accounts
      .filter((account) => account.type !== 'Unsupported')
      .reduce((sum, account) => sum + account.balance, 0)
```
Replace with:
```ts
    // Other accounts count toward this the same as Checking/Savings
    // (the pre-existing cross-currency blending this naive sum has for
    // e.g. HKD + SGD accounts is unchanged, not fixed here). Crypto
    // still doesn't — its raw balance isn't a dollar figure without the
    // conversion this sum doesn't do.
    const totalBalance = accounts
      .filter((account) => account.type !== 'Unsupported' && account.type !== 'Crypto')
      .reduce((sum, account) => sum + account.balance, 0)
```

- [ ] **Step 4: Build**

Run: `npm run build` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: succeeds, no TypeScript errors.

- [ ] **Step 5: Commit**

```bash
git add src/pages/DashboardPage.tsx
git commit -m "feat: label Other/Crypto accounts on the Dashboard, exclude Crypto from totalBalance"
```

---

## Task 10: `FinancialStatementPage.tsx` — currency-bucketed rendering

**Files:**
- Modify: `src/pages/FinancialStatementPage.tsx`

**Interfaces:**
- Consumes: types (Task 8).
- Produces: the rendered page — last task, nothing downstream depends on it.

- [ ] **Step 1: Extract the existing per-currency rendering into a reusable section, then render it once per currency**

The page today renders one Assets column + one Liabilities column + one Owner's Equity card directly from `data.assets`/`data.liabilities`/`data.ownersEquity`. Wrap that existing markup (the `ASSET_TYPE_ORDER`/`LIABILITY_TYPE_ORDER` grouped-table logic already built for the Type-grouping feature) into a small local component that takes `{ currency, assets, totalAssets, liabilities, totalLiabilities, ownersEquity }` as props, used once for the primary currency and once per entry in `data.otherCurrencies` — each instance gets a currency-code heading (e.g. a `<h2>{currency}</h2>` above its own three-column block) so multiple currencies are visually unambiguous, never implying they're summed together. The "Add an asset"/"Add a liability" forms stay attached to the primary-currency section only (matching how manual entries are always tagged with the primary currency server-side, per Task 7).

- [ ] **Step 2: Build**

Run: `npm run build`
Expected: succeeds, no TypeScript errors.

- [ ] **Step 3: Manual verification**

Run `npm run dev`, sign in, sync a bank with a mix of HKD/USD/crypto accounts (or manually verify against the seeded Testbank data), navigate to Financial Statement. Confirm:
- The primary currency's section renders exactly as before (Type-grouped Assets/Liabilities/Owner's Equity).
- A "USD FX"-style account produces a second, clearly-labeled section for USD, with its own totals — never combined with the primary section's numbers.
- A crypto account's asset line shows its fiat-equivalent dollar amount (not the raw BTC count), tagged as the "Crypto" asset type, inside whichever currency section matches its `FiatEquivalentCurrency` (USD).
- Dashboard no longer shows "UNSUPPORTED" for "HKD Ledger Account" or "USD FX" — shows "Other" — and shows "Crypto" (not "Unsupported") for the Bitcoin account, with the new explanatory note.

- [ ] **Step 4: Commit**

```bash
git add src/pages/FinancialStatementPage.tsx
git commit -m "feat: render Financial Statement per currency, one section per currency present"
```

---

## Self-Review

**Spec coverage:**
- `AccountType.Unsupported` narrowed to "no usable currency" → Task 5 (`MapAccountType` redesign). ✓
- `Other` accounts fully supported (transactions sync, included in Cash Flow automatically via its existing bucketing, included in Financial Statement) → Tasks 5, 7. ✓
- Crypto detection by currency code, not more subtype guessing → Task 5. ✓
- CoinGecko price feed, cached at sync time, isolated failure handling → Tasks 4, 5. ✓
- Financial Statement currency-bucketing mirroring Cash Flow → Tasks 6, 7, 10. ✓
- Crypto's fiat-equivalent (not raw balance) used in Financial Statement, dashboard shows raw balance unchanged → Tasks 7, 9. ✓
- Cash Flow explicitly untouched → no task modifies `CashFlowService`/`CashFlowViewModel`. ✓
- Dashboard's pre-existing `totalBalance` currency-blending explicitly not fixed → Task 9 only adds the Crypto exclusion, doesn't restructure the sum into per-currency buckets. ✓

**Placeholder scan:** no "TBD"/"add appropriate handling"/"similar to Task N" — every step has literal code or an exact command, except Task 10 Step 1, which describes a refactor (extracting existing markup into a reusable local component) rather than providing the full JSX — this is intentional: the exact JSX depends on `FinancialStatementPage.tsx`'s current full content (already built this session with Type-grouping), and re-deriving it verbatim here risks drifting from whatever hand-edits happened since; the instructions are precise about what to extract and how to reuse it.

**Type consistency check:** `AccountType`/`AssetType` new values (`Other`, `Crypto`) match exactly across Task 1 (C# enums), Task 5 (`MapAccountType`/tests), Task 8 (TS unions), Task 9 (`ACCOUNT_TYPE_LABELS`). `Currency` field naming consistent on `AssetLineViewModel`/`LiabilityViewModel` across Tasks 6, 7, 8. `FiatEquivalentValue`/`FiatEquivalentCurrency`/`PriceFetchedAtUtc` consistent across Tasks 1, 5, 7 (tests reference all three by these exact names).
