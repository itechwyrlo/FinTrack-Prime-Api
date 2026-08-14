# Bank Account Linking (Finverse) — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the manual account/transaction entry endpoints with real
(sandboxed) bank-account linking via Finverse, so `Account`/`Transaction`
rows come from a linked institution instead of hand-typed input.

**Architecture:** A new `IFinverseClient`/`FinverseClient` (typed
`HttpClient`, same pattern as the existing `IPayPalClient`/`PayPalClient`)
wraps Finverse's Link/Accounts/Transactions API. A new
`IBankLinkService`/`BankLinkService` owns the business logic: starting a
link session, exchanging the resulting code for an access token, and
syncing accounts/transactions into this app's own tables. A new thin
`BankLinkController` exposes three endpoints. The old manual
create-account/add-transaction path is deleted once the replacement is in
place.

**Tech Stack:** .NET 10 (per the actual `.csproj` files — not .NET 8 as
the stale README claims), EF Core + SQL Server, xUnit + Moq +
`Microsoft.EntityFrameworkCore.InMemory` for tests (this repo currently
has **zero** test project; Task 1 creates one).

**Scope note:** This plan covers the **backend only**
(`FinTrack-Prime-Api`). The frontend changes described in the design spec
(`docs/superpowers/specs/2026-08-03-bank-account-linking-design.md`) —
"Connect a bank" entry point, the `/bank-link/callback` route, deleting
`CreateAccountModal.tsx`/`AddTransactionModal.tsx` — live in a separate
repository (`FinTrackPrime`) with its own toolchain and need their own
plan; they are not included here.

**API-shape caveat:** Finverse's exact JSON field names for the
`/auth/token` exchange, accounts list, and transactions list were **not**
directly observed during design (only the Demo App's rendered UI was
seen, e.g. "HKD Checking" / "HKD Credit Card" / signed transaction
amounts). Only the `/link/token` **request** body fields are confirmed,
copied verbatim from Finverse's own docs example (`client_id`,
`redirect_uri`, `state`, `user_id`, `grant_type`, `response_mode`,
`response_type`). Every other field name below is a reasonable inference
and is marked `// VERIFY:` at the exact line to check against
`docs.finverse.com` (the "02 - Login Identity", "03 - Accounts", "04 -
Transactions" pages) before running this against the real sandbox. Task 5
calls this out explicitly as a manual verification step.

## Global Constraints

- Target framework: `net10.0` (all projects).
- `Nullable` and `ImplicitUsings` are both `enable`, but existing files
  still write explicit `using` statements — match that style.
- Never commit real secrets into `appsettings.json`. The existing
  `PayPal:ClientSecret` being committed in plaintext is a pre-existing gap
  in this repo, not a pattern to repeat — new `Finverse:*` secret values
  go in user-secrets or environment variables only; `appsettings.json`
  gets blank placeholders.
- External API calls parse JSON manually via `JsonDocument` (see
  `PayPalClient.cs`), not strict model binding — keep that pattern so a
  wrong field-name guess is a one-line fix, not a reshape.
- Decimal columns are explicitly typed `decimal(18,2)` in
  `OnModelCreating`, matching every existing money column.
- Controllers stay thin: `[Authorize]`, a private `GetUserId()` reading
  the JWT `sub` claim, business logic in the service layer, catch
  `InvalidOperationException` → `400 BadRequest` with `{ message }`. No
  controller-level tests exist anywhere in this codebase (only services
  are unit tested here) — keep that split.

---

## Task 1: Test project scaffold

This repo has no test project at all today (`FinTrackPrime.sln` lists only
Models/Business/WebApi). Every later task's tests depend on this existing
first.

**Files:**
- Create: `tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj`
- Create: `tests/FinTrackPrime.Business.Tests/SmokeTests.cs`
- Modify: `FinTrackPrime.sln`

**Interfaces:**
- Produces: a working `dotnet test` command any later task's tests run
  under.

- [ ] **Step 1: Create the test project**

```bash
cd "c:/Users/Wyrlo/projects/FinTrack-Prime-Api"
mkdir -p tests/FinTrackPrime.Business.Tests
```

Write `tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.10" />
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\FinTrackPrime.Business\FinTrackPrime.Business.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write a smoke test**

`tests/FinTrackPrime.Business.Tests/SmokeTests.cs`:

```csharp
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void TestHarnessRuns()
        {
            Assert.True(true);
        }
    }
}
```

- [ ] **Step 3: Add the project to the solution**

```bash
dotnet sln FinTrackPrime.sln add tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj
```

- [ ] **Step 4: Run it**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add tests/FinTrackPrime.Business.Tests FinTrackPrime.sln
git commit -m "test: add FinTrackPrime.Business.Tests project"
```

---

## Task 2: Data model — entities and DbContext

No behavior to TDD here (POCOs + Fluent config); verification is
`dotnet build`, matching how this codebase treats every other entity
change.

**Files:**
- Modify: `src/FinTrackPrime.Models/Entities/Account.cs`
- Modify: `src/FinTrackPrime.Models/Entities/Transaction.cs`
- Modify: `src/FinTrackPrime.Models/Entities/User.cs`
- Create: `src/FinTrackPrime.Models/Entities/LinkedInstitution.cs`
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Produces: `AccountType.CreditCard`, `Account.ExternalAccountId`,
  `Account.Institution`, `Account.Currency`,
  `Transaction.ExternalTransactionId`, `LinkedInstitution` entity,
  `FinTrackDbContext.LinkedInstitutions` (`DbSet<LinkedInstitution>`).

- [ ] **Step 1: Update `Account.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace FinTrackPrime.Models.Entities
{
    public enum AccountType
    {
        Checking,
        Savings,
        CreditCard
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
        // etc.) are stored as-is; balances/sums elsewhere in the app do
        // not convert between currencies. Known limitation, not solved
        // here — out of scope per the design spec.
        public string Currency { get; set; } = string.Empty;

        public string ExternalAccountId { get; set; } = string.Empty;
        public string Institution { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
}
```

- [ ] **Step 2: Update `Transaction.cs`**

Add one field; everything else is unchanged:

```csharp
        public string ExternalTransactionId { get; set; } = string.Empty;
```

Insert it directly below the `AccountId`/`Account` properties, above
`Description`.

- [ ] **Step 3: Create `LinkedInstitution.cs`**

```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per bank a user has connected through Finverse. One
    // institution can back multiple Account rows (e.g. Testbank returns a
    // checking, a savings, and a credit card account from one login).
    // AccessToken is encrypted at rest via ASP.NET Core's Data Protection
    // API (see BankLinkService) — treat it with the same care as a
    // password, never log it.
    public class LinkedInstitution
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Institution { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;

        public DateTime LinkedAtUtc { get; set; }
        public DateTime? LastSyncedAtUtc { get; set; }
    }
}
```

- [ ] **Step 4: Add the navigation collection to `User.cs`**

Add below the existing `RefreshTokens` collection:

```csharp
        public ICollection<LinkedInstitution> LinkedInstitutions { get; set; } = new List<LinkedInstitution>();
```

- [ ] **Step 5: Update `FinTrackDbContext.cs`**

Add the `DbSet` below the existing `RefreshTokens` line:

```csharp
        public DbSet<LinkedInstitution> LinkedInstitutions => Set<LinkedInstitution>();
```

Update the `Account` entity config (replace the existing block):

```csharp
            modelBuilder.Entity<Account>(entity =>
            {
                entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
                entity.Property(a => a.Currency).HasMaxLength(8);
                entity.Property(a => a.ExternalAccountId).HasMaxLength(128);
                entity.Property(a => a.Institution).HasMaxLength(80);
                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

Update the `Transaction` entity config (add one line inside the existing
block, after the `Category` line):

```csharp
                entity.Property(t => t.ExternalTransactionId).HasMaxLength(128);
```

Add a new config block after the `Liability` block, before
`base.OnModelCreating(modelBuilder);`:

```csharp
            modelBuilder.Entity<LinkedInstitution>(entity =>
            {
                entity.Property(l => l.Institution).HasMaxLength(80).IsRequired();
                entity.Property(l => l.AccessToken).HasMaxLength(2048).IsRequired();
                entity.HasOne(l => l.User)
                      .WithMany(u => u.LinkedInstitutions)
                      .HasForeignKey(l => l.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

- [ ] **Step 6: Verify it compiles**

```bash
dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Models
git commit -m "feat: add bank-linking fields and LinkedInstitution entity"
```

---

## Task 3: EF Core migration

**Files:**
- Create: `src/FinTrackPrime.Models/Migrations/<timestamp>_AddBankLinking.cs`
  and its `.Designer.cs` (generated by the tool, not hand-written).
- Modify: `src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs`
  (also generated).

**Interfaces:**
- Consumes: the entity/DbContext changes from Task 2.
- Produces: a migration named `AddBankLinking` applying those changes.

- [ ] **Step 1: Generate the migration**

```bash
cd src/FinTrackPrime.WebApi
dotnet ef migrations add AddBankLinking --project ../FinTrackPrime.Models --startup-project .
```

- [ ] **Step 2: Inspect the generated `Up()` method**

Open the new `*_AddBankLinking.cs` file and confirm it contains, at
minimum:
- `AddColumn` calls for `Account.Currency`, `Account.ExternalAccountId`,
  `Account.Institution`, and `Transaction.ExternalTransactionId`.
- A `CreateTable` call for `LinkedInstitutions` with a foreign key to
  `Users` and `onDelete: ReferentialAction.Cascade`.

If any of those are missing, Task 2 wasn't fully applied before
generating — go back and check before proceeding.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations
git commit -m "feat: add AddBankLinking EF Core migration"
```

(Do not run `dotnet ef database update` as part of this task — no dev
database is assumed to be configured in this environment. That's a
deployment-time step, not a plan step.)

---

## Task 4: Finverse client interface and DTOs

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/IFinverseClient.cs`

**Interfaces:**
- Produces: `IFinverseClient`, `FinverseLinkSession`, `FinverseAccountDto`,
  `FinverseTransactionDto` — the exact types Task 5 (implementation) and
  Task 6 (`BankLinkService`) both consume.

- [ ] **Step 1: Write the interface and DTOs**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    // A short-lived link_token + the browser URL to open it, returned by
    // Finverse's POST /link/token.
    public record FinverseLinkSession(string LinkToken, string LinkUrl);

    // AccountType here is Finverse's raw string (e.g. "checking",
    // "credit_card") — mapping to this app's own AccountType enum, and
    // filtering out unsupported types, happens in BankLinkService, not
    // here. This DTO is a direct mirror of what Finverse returns.
    public record FinverseAccountDto(
        string ExternalAccountId,
        string AccountName,
        string AccountType,
        string Currency,
        decimal Balance);

    public record FinverseTransactionDto(
        string ExternalTransactionId,
        string Description,
        decimal Amount,
        DateTime PostedAtUtc);

    public interface IFinverseClient
    {
        // Starts a Link session for one end user. redirectUri must be one
        // of the Callback URLs registered for this app in Finverse's API
        // Settings.
        Task<FinverseLinkSession> GenerateLinkTokenAsync(Guid userId, string redirectUri);

        // Exchanges the code Finverse's redirect handed back to the
        // frontend for a long-lived access token scoped to that one
        // linked institution.
        Task<string> ExchangeLinkCodeAsync(string linkCode);

        Task<IReadOnlyList<FinverseAccountDto>> GetAccountsAsync(string accessToken);

        Task<IReadOnlyList<FinverseTransactionDto>> GetTransactionsAsync(
            string accessToken, string externalAccountId);
    }
}
```

- [ ] **Step 2: Verify it compiles**

```bash
dotnet build src/FinTrackPrime.Business/FinTrackPrime.Business.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/IFinverseClient.cs
git commit -m "feat: add IFinverseClient interface and DTOs"
```

---

## Task 5: `FinverseClient` implementation

**Files:**
- Create: `src/FinTrackPrime.Business/Services/FinverseClient.cs`
- Test: `tests/FinTrackPrime.Business.Tests/FinverseClientTests.cs`

**Interfaces:**
- Consumes: `IFinverseClient` and its DTOs (Task 4).
- Produces: `FinverseClient : IFinverseClient`, constructed with
  `(HttpClient httpClient, IConfiguration config)` — same constructor
  shape as `PayPalClient`, so `Program.cs` registers it identically.

**Before writing this task's implementation**, open
`docs.finverse.com` → Data API → "02 - Login Identity" and "04 -
Transactions" and confirm the exact JSON field names against the
`// VERIFY:` comments below. Adjust the `GetProperty("...")` calls to
match — the tests in Step 1 use fixture JSON you control, so they'll pass
regardless; only the real sandbox call in Step 5 proves the field names
are actually right.

- [ ] **Step 1: Write the failing tests**

`tests/FinTrackPrime.Business.Tests/FinverseClientTests.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    // Routes every request to a canned response instead of a real socket,
    // same technique used to test any typed-HttpClient class without a
    // network call.
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }

    public class FinverseClientTests
    {
        private static IConfiguration BuildConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Finverse:ClientId"] = "test-client-id",
                    ["Finverse:ClientSecret"] = "test-client-secret",
                })
                .Build();

        [Fact]
        public async Task GenerateLinkTokenAsync_SendsExpectedRequestFields()
        {
            var handler = new FakeHttpMessageHandler(
                HttpStatusCode.OK,
                "{\"link_token\":\"lt-123\",\"link_url\":\"https://link.finverse.net/lt-123\"}");
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());
            var userId = Guid.NewGuid();

            var result = await client.GenerateLinkTokenAsync(userId, "https://developer.prod.finverse.net/sink");

            Assert.Equal("lt-123", result.LinkToken);
            Assert.Equal("https://link.finverse.net/lt-123", result.LinkUrl);
            Assert.Contains("\"client_id\":\"test-client-id\"", handler.LastRequestBody);
            Assert.Contains("\"redirect_uri\":\"https://developer.prod.finverse.net/sink\"", handler.LastRequestBody);
            Assert.Contains($"\"user_id\":\"{userId}\"", handler.LastRequestBody);
        }

        [Fact]
        public async Task ExchangeLinkCodeAsync_ReturnsAccessToken()
        {
            // VERIFY: confirm the real response envelope has a top-level
            // "access_token" string field against docs.finverse.com's
            // /auth/token reference before trusting this in production.
            var handler = new FakeHttpMessageHandler(
                HttpStatusCode.OK, "{\"access_token\":\"at-456\"}");
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var token = await client.ExchangeLinkCodeAsync("link-code-abc");

            Assert.Equal("at-456", token);
        }

        [Fact]
        public async Task GetAccountsAsync_ParsesAccountList()
        {
            // VERIFY: confirm field names ("account_id", "name", "type",
            // "currency", "balance") against docs.finverse.com's Accounts
            // reference before trusting this in production.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
                {
                  "accounts": [
                    {
                      "account_id": "acc-1",
                      "name": "HKD Checking",
                      "type": "checking",
                      "currency": "HKD",
                      "balance": 70013.12
                    },
                    {
                      "account_id": "acc-2",
                      "name": "HKD Credit Card",
                      "type": "credit_card",
                      "currency": "HKD",
                      "balance": -1833.22
                    }
                  ]
                }
                """);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var accounts = await client.GetAccountsAsync("at-456");

            Assert.Equal(2, accounts.Count);
            Assert.Equal("acc-1", accounts[0].ExternalAccountId);
            Assert.Equal("HKD Checking", accounts[0].AccountName);
            Assert.Equal("checking", accounts[0].AccountType);
            Assert.Equal("HKD", accounts[0].Currency);
            Assert.Equal(70013.12m, accounts[0].Balance);
            Assert.Equal(-1833.22m, accounts[1].Balance);
        }

        [Fact]
        public async Task GetTransactionsAsync_ParsesTransactionList()
        {
            // VERIFY: confirm field names ("transaction_id",
            // "description", "amount", "posted_date") against
            // docs.finverse.com's Transactions reference before trusting
            // this in production.
            var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
                {
                  "transactions": [
                    {
                      "transaction_id": "txn-1",
                      "description": "BAT STARBUCKS@CITY SI NG 25MAY",
                      "amount": -40.00,
                      "posted_date": "2023-06-30"
                    }
                  ]
                }
                """);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var transactions = await client.GetTransactionsAsync("at-456", "acc-1");

            Assert.Single(transactions);
            Assert.Equal("txn-1", transactions[0].ExternalTransactionId);
            Assert.Equal(-40.00m, transactions[0].Amount);
            Assert.Equal(new DateTime(2023, 6, 30), transactions[0].PostedAtUtc);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj --filter FinverseClientTests
```

Expected: FAIL — `FinverseClient` doesn't exist yet (compile error).

- [ ] **Step 3: Write `FinverseClient.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    // Talks to Finverse's REST API directly. Registered with a typed
    // HttpClient (see Program.cs) whose BaseAddress is
    // Finverse:ApiBaseUrl, same pattern as PayPalClient.
    public class FinverseClient : IFinverseClient
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public FinverseClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<FinverseLinkSession> GenerateLinkTokenAsync(Guid userId, string redirectUri)
        {
            // Field names below are copied verbatim from Finverse's own
            // documented example request for POST /link/token.
            var payload = new
            {
                client_id = _config["Finverse:ClientId"],
                redirect_uri = redirectUri,
                state = Guid.NewGuid().ToString("N"),
                user_id = userId.ToString(),
                grant_type = "client_credentials",
                response_mode = "form_post",
                response_type = "code",
            };

            using var response = await _httpClient.PostAsJsonAsync("/link/token", payload);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse link/token request failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new FinverseLinkSession(
                root.GetProperty("link_token").GetString() ?? string.Empty,
                root.GetProperty("link_url").GetString() ?? string.Empty);
        }

        public async Task<string> ExchangeLinkCodeAsync(string linkCode)
        {
            var payload = new
            {
                client_id = _config["Finverse:ClientId"],
                client_secret = _config["Finverse:ClientSecret"],
                grant_type = "authorization_code",
                code = linkCode,
            };

            using var response = await _httpClient.PostAsJsonAsync("/auth/token", payload);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse auth/token exchange failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        }

        public async Task<IReadOnlyList<FinverseAccountDto>> GetAccountsAsync(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/accounts");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse accounts request failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var accounts = new List<FinverseAccountDto>();

            foreach (var element in doc.RootElement.GetProperty("accounts").EnumerateArray())
            {
                accounts.Add(new FinverseAccountDto(
                    element.GetProperty("account_id").GetString() ?? string.Empty,
                    element.GetProperty("name").GetString() ?? string.Empty,
                    element.GetProperty("type").GetString() ?? string.Empty,
                    element.GetProperty("currency").GetString() ?? string.Empty,
                    element.GetProperty("balance").GetDecimal()));
            }

            return accounts;
        }

        public async Task<IReadOnlyList<FinverseTransactionDto>> GetTransactionsAsync(
            string accessToken, string externalAccountId)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/accounts/{externalAccountId}/transactions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse transactions request failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var transactions = new List<FinverseTransactionDto>();

            foreach (var element in doc.RootElement.GetProperty("transactions").EnumerateArray())
            {
                transactions.Add(new FinverseTransactionDto(
                    element.GetProperty("transaction_id").GetString() ?? string.Empty,
                    element.GetProperty("description").GetString() ?? string.Empty,
                    element.GetProperty("amount").GetDecimal(),
                    DateTime.Parse(
                        element.GetProperty("posted_date").GetString() ?? string.Empty,
                        CultureInfo.InvariantCulture)));
            }

            return transactions;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj --filter FinverseClientTests
```

Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.Business/Services/FinverseClient.cs tests/FinTrackPrime.Business.Tests/FinverseClientTests.cs
git commit -m "feat: add FinverseClient with tests"
```

---

## Task 6: `BankLinkService`

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/IBankLinkService.cs`
- Create: `src/FinTrackPrime.Business/Services/BankLinkService.cs`
- Test: `tests/FinTrackPrime.Business.Tests/BankLinkServiceTests.cs`
- Modify: `src/FinTrackPrime.Business/FinTrackPrime.Business.csproj`
  (add `Microsoft.AspNetCore.DataProtection.Abstractions`)

**Interfaces:**
- Consumes: `IFinverseClient` and its DTOs (Task 4/5),
  `FinTrackDbContext`, `LinkedInstitution`, `Account`, `Transaction`
  (Task 2), `IDataProtectionProvider` (built into ASP.NET Core, used to
  encrypt `AccessToken` at rest per the design spec).
- Produces: `IBankLinkService` with
  `Task<string> StartLinkAsync(Guid userId, string redirectUri)`,
  `Task<DashboardViewModel> CompleteLinkAsync(Guid userId, string linkCode)`,
  `Task<DashboardViewModel> SyncAsync(Guid userId)` — these three exact
  signatures are what Task 7's controller calls.

- [ ] **Step 1: Add the DataProtection package reference**

In `src/FinTrackPrime.Business/FinTrackPrime.Business.csproj`, add inside
the existing `<ItemGroup>` of `PackageReference`s:

```xml
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.Abstractions" Version="10.0.10" />
```

- [ ] **Step 2: Write `IBankLinkService.cs`**

```csharp
using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IBankLinkService
    {
        // Starts a Finverse Link session for this user; returns the URL
        // the frontend opens to run the Link UI.
        Task<string> StartLinkAsync(Guid userId, string redirectUri);

        // Exchanges the code Finverse's redirect handed back for an
        // access token, stores it, and performs the initial account +
        // transaction sync for that institution.
        Task<DashboardViewModel> CompleteLinkAsync(Guid userId, string linkCode);

        // Re-syncs every institution this user has already linked.
        // A failure on one institution does not stop the others from
        // syncing.
        Task<DashboardViewModel> SyncAsync(Guid userId);
    }
}
```

- [ ] **Step 3: Write the failing tests**

`tests/FinTrackPrime.Business.Tests/BankLinkServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class BankLinkServiceTests
    {
        private static FinTrackDbContext BuildDb()
        {
            var options = new DbContextOptionsBuilder<FinTrackDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new FinTrackDbContext(options);
        }

        private static IDataProtectionProvider BuildDataProtection() =>
            DataProtectionProvider.Create("FinTrackPrime.Business.Tests");

        private static IConfiguration BuildConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Finverse:RedirectUri"] = "https://developer.prod.finverse.net/sink",
                })
                .Build();

        [Fact]
        public async Task CompleteLinkAsync_CreatesOnlyInScopeAccountTypes()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code")).ReturnsAsync("at-456");
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
            Assert.Equal(2, accounts.Count);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-checking" && a.Type == AccountType.Checking);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-credit" && a.Type == AccountType.CreditCard);
            Assert.DoesNotContain(accounts, a => a.ExternalAccountId == "acc-bitcoin");
        }

        [Fact]
        public async Task CompleteLinkAsync_DerivesTransactionDirectionFromAmountSign()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u2@test.com", FullName = "Test User 2", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code")).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-in", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
                new("txn-out", "BAT STARBUCKS", -40.00m, new DateTime(2023, 6, 30)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var transactions = await db.Transactions.ToListAsync();
            Assert.Equal(TransactionDirection.Income, transactions.Single(t => t.ExternalTransactionId == "txn-in").Direction);
            Assert.Equal(TransactionDirection.Expense, transactions.Single(t => t.ExternalTransactionId == "txn-out").Direction);
        }

        [Fact]
        public async Task SyncAsync_DoesNotDuplicateAlreadySyncedTransactions()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u3@test.com", FullName = "Test User 3", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code")).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-1", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildDataProtection(), BuildConfig());
            await service.CompleteLinkAsync(userId, "link-code");

            // Second sync returns the same transaction again.
            await service.SyncAsync(userId);

            var transactions = await db.Transactions.Where(t => t.ExternalTransactionId == "txn-1").ToListAsync();
            Assert.Single(transactions);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj --filter BankLinkServiceTests
```

Expected: FAIL — `BankLinkService` doesn't exist yet (compile error).

- [ ] **Step 5: Write `BankLinkService.cs`**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class BankLinkService : IBankLinkService
    {
        private readonly FinTrackDbContext _db;
        private readonly IFinverseClient _finverseClient;
        private readonly IDataProtector _protector;
        private readonly IConfiguration _config;

        public BankLinkService(
            FinTrackDbContext db,
            IFinverseClient finverseClient,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration config)
        {
            _db = db;
            _finverseClient = finverseClient;
            // A dedicated purpose string scopes this protector so it can
            // never accidentally decrypt data protected for another
            // purpose elsewhere in the app.
            _protector = dataProtectionProvider.CreateProtector("FinTrackPrime.LinkedInstitution.AccessToken");
            _config = config;
        }

        public async Task<string> StartLinkAsync(Guid userId, string redirectUri)
        {
            var session = await _finverseClient.GenerateLinkTokenAsync(userId, redirectUri);
            return session.LinkUrl;
        }

        public async Task<DashboardViewModel> CompleteLinkAsync(Guid userId, string linkCode)
        {
            var accessToken = await _finverseClient.ExchangeLinkCodeAsync(linkCode);

            var institution = new LinkedInstitution
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Institution = "Testbank", // VERIFY: Finverse's exchange response may
                                           // include the institution name directly;
                                           // if so, use that instead of hardcoding.
                AccessToken = _protector.Protect(accessToken),
                LinkedAtUtc = DateTime.UtcNow,
            };
            _db.LinkedInstitutions.Add(institution);
            await _db.SaveChangesAsync();

            await SyncInstitutionAsync(userId, institution, accessToken);

            return await BuildDashboardAsync(userId);
        }

        public async Task<DashboardViewModel> SyncAsync(Guid userId)
        {
            var institutions = await _db.LinkedInstitutions
                .Where(i => i.UserId == userId)
                .ToListAsync();

            foreach (var institution in institutions)
            {
                try
                {
                    var accessToken = _protector.Unprotect(institution.AccessToken);
                    await SyncInstitutionAsync(userId, institution, accessToken);
                }
                catch (Exception)
                {
                    // One institution's Finverse call failing (expired
                    // token, outage) must not block syncing the others,
                    // or block the dashboard from loading at all — that
                    // institution's data just stays as of its last
                    // successful sync.
                }
            }

            return await BuildDashboardAsync(userId);
        }

        private async Task SyncInstitutionAsync(Guid userId, LinkedInstitution institution, string accessToken)
        {
            var finverseAccounts = await _finverseClient.GetAccountsAsync(accessToken);

            foreach (var finverseAccount in finverseAccounts)
            {
                var accountType = MapAccountType(finverseAccount.AccountType);
                if (accountType is null)
                {
                    continue; // out of scope (Bitcoin, FX, Ledger, ...)
                }

                var account = await _db.Accounts.FirstOrDefaultAsync(
                    a => a.UserId == userId && a.ExternalAccountId == finverseAccount.ExternalAccountId);

                if (account is null)
                {
                    account = new Account
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        ExternalAccountId = finverseAccount.ExternalAccountId,
                        Institution = institution.Institution,
                        CreatedAtUtc = DateTime.UtcNow,
                    };
                    _db.Accounts.Add(account);
                }

                account.Nickname = finverseAccount.AccountName;
                account.Type = accountType.Value;
                account.Currency = finverseAccount.Currency;
                account.Balance = finverseAccount.Balance;

                var finverseTransactions = await _finverseClient.GetTransactionsAsync(
                    accessToken, finverseAccount.ExternalAccountId);

                foreach (var finverseTransaction in finverseTransactions)
                {
                    var alreadySynced = await _db.Transactions.AnyAsync(
                        t => t.ExternalTransactionId == finverseTransaction.ExternalTransactionId);
                    if (alreadySynced)
                    {
                        continue;
                    }

                    _db.Transactions.Add(new Transaction
                    {
                        Id = Guid.NewGuid(),
                        AccountId = account.Id,
                        ExternalTransactionId = finverseTransaction.ExternalTransactionId,
                        Description = finverseTransaction.Description,
                        Category = string.Empty, // Finverse has no category field.
                        Amount = Math.Abs(finverseTransaction.Amount),
                        Direction = finverseTransaction.Amount >= 0
                            ? TransactionDirection.Income
                            : TransactionDirection.Expense,
                        OccurredAtUtc = finverseTransaction.PostedAtUtc,
                    });
                }
            }

            institution.LastSyncedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // Unknown/unsupported types return null and are skipped by the
        // caller — safe-by-default rather than guessing.
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

        private async Task<DashboardViewModel> BuildDashboardAsync(Guid userId)
        {
            var accounts = await _db.Accounts
                .Where(a => a.UserId == userId)
                .Include(a => a.Transactions)
                .ToListAsync();

            var dashboard = new DashboardViewModel();
            foreach (var account in accounts)
            {
                dashboard.Accounts.Add(new AccountViewModel
                {
                    Id = account.Id,
                    Nickname = account.Nickname,
                    Type = account.Type,
                    Balance = account.Balance,
                    RecentTransactions = account.Transactions
                        .OrderByDescending(t => t.OccurredAtUtc)
                        .Take(25)
                        .Select(t => new TransactionViewModel
                        {
                            Id = t.Id,
                            Description = t.Description,
                            Category = t.Category,
                            Amount = t.Amount,
                            Direction = t.Direction,
                            OccurredAtUtc = t.OccurredAtUtc,
                        })
                        .ToList(),
                });
            }
            return dashboard;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj --filter BankLinkServiceTests
```

Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Business tests/FinTrackPrime.Business.Tests/BankLinkServiceTests.cs
git commit -m "feat: add BankLinkService with tests"
```

---

## Task 7: `BankLinkController` and view models

**Files:**
- Modify: `src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs`
  (add request/response models)
- Create: `src/FinTrackPrime.WebApi/Controllers/BankLinkController.cs`

**Interfaces:**
- Consumes: `IBankLinkService` (Task 6).
- Produces: `POST api/bank-link/token`, `POST api/bank-link/complete`,
  `POST api/bank-link/sync`.

- [ ] **Step 1: Add view models**

Append to `src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs`,
inside the existing namespace, after `CreateTransactionRequest` (which
Task 9 will delete — these new ones replace it):

```csharp
    public class StartLinkResponse
    {
        public string LinkUrl { get; set; } = string.Empty;
    }

    public class CompleteLinkRequest
    {
        [Required]
        public string LinkCode { get; set; } = string.Empty;
    }
```

- [ ] **Step 2: Write `BankLinkController.cs`**

```csharp
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/bank-link")]
    [Authorize]
    public class BankLinkController : ControllerBase
    {
        private readonly IBankLinkService _bankLinkService;
        private readonly IConfiguration _config;

        public BankLinkController(IBankLinkService bankLinkService, IConfiguration config)
        {
            _bankLinkService = bankLinkService;
            _config = config;
        }

        [HttpPost("token")]
        public async Task<ActionResult<StartLinkResponse>> StartLink()
        {
            try
            {
                var redirectUri = _config["Finverse:RedirectUri"]!;
                var linkUrl = await _bankLinkService.StartLinkAsync(GetUserId(), redirectUri);
                return Ok(new StartLinkResponse { LinkUrl = linkUrl });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("complete")]
        public async Task<ActionResult<DashboardViewModel>> CompleteLink(CompleteLinkRequest request)
        {
            try
            {
                var dashboard = await _bankLinkService.CompleteLinkAsync(GetUserId(), request.LinkCode);
                return Ok(dashboard);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("sync")]
        public async Task<ActionResult<DashboardViewModel>> Sync()
        {
            var dashboard = await _bankLinkService.SyncAsync(GetUserId());
            return Ok(dashboard);
        }

        private Guid GetUserId()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
```

- [ ] **Step 3: Verify it compiles**

```bash
dotnet build src/FinTrackPrime.WebApi/FinTrackPrime.WebApi.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs src/FinTrackPrime.WebApi/Controllers/BankLinkController.cs
git commit -m "feat: add BankLinkController"
```

---

## Task 8: Wire up DI, config, and fail-fast checks

**Files:**
- Modify: `src/FinTrackPrime.WebApi/Program.cs`
- Modify: `src/FinTrackPrime.WebApi/appsettings.json`

**Interfaces:**
- Consumes: `IFinverseClient`/`FinverseClient` (Task 5),
  `IBankLinkService`/`BankLinkService` (Task 6).

- [ ] **Step 1: Add blank Finverse config to `appsettings.json`**

Add after the existing `"PayPal"` section:

```json
  "Finverse": {
    "ClientId": "",
    "ClientSecret": "",
    "ApiBaseUrl": "https://api.prod.finverse.net",
    "RedirectUri": "https://developer.prod.finverse.net/sink"
  },
```

Leave `ClientId`/`ClientSecret` blank here — set the real values via
user-secrets (`dotnet user-secrets set "Finverse:ClientId" "..."` from
`src/FinTrackPrime.WebApi`) or environment variables, never committed.

- [ ] **Step 2: Register services in `Program.cs`**

Add to the `required` array (around line 20-26), alongside the existing
`PayPal:ClientId`/`PayPal:ClientSecret` entries:

```csharp
        ("Finverse:ClientId", builder.Configuration["Finverse:ClientId"] ?? ""),
        ("Finverse:ClientSecret", builder.Configuration["Finverse:ClientSecret"] ?? ""),
```

Add below the existing `IPremiumAccessService` registration (around line
56):

```csharp
builder.Services.AddScoped<IBankLinkService, BankLinkService>();
```

Add below the existing `PayPal` typed `HttpClient` registration (around
line 64-67):

```csharp
builder.Services.AddHttpClient<IFinverseClient, FinverseClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Finverse:ApiBaseUrl"]!);
});
```

Add Data Protection registration below the CORS block (around line 129),
before `builder.Services.AddControllers()`:

```csharp
// Encrypts LinkedInstitution.AccessToken at rest (see BankLinkService).
builder.Services.AddDataProtection();
```

Add the two missing `using` statements at the top of the file:

```csharp
using FinTrackPrime.Business.Interfaces;
```

(Check first — `FinTrackPrime.Business.Interfaces` and
`FinTrackPrime.Business.Services` are likely already imported; only add
what's missing.)

- [ ] **Step 3: Verify it compiles**

```bash
dotnet build src/FinTrackPrime.WebApi/FinTrackPrime.WebApi.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run the full test suite**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj
```

Expected: all tests passed (8: 1 smoke + 4 FinverseClient + 3
BankLinkService).

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.WebApi/Program.cs src/FinTrackPrime.WebApi/appsettings.json
git commit -m "feat: wire up Finverse client and BankLinkService in DI"
```

---

## Task 9: Remove manual account/transaction entry

Only do this once Tasks 1-8 are complete and passing — this deletes the
old path being replaced.

**Files:**
- Modify: `src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs`
- Modify: `src/FinTrackPrime.Business/Interfaces/IAccountService.cs`
- Modify: `src/FinTrackPrime.Business/Services/AccountService.cs`
- Modify: `src/FinTrackPrime.WebApi/Controllers/AccountsController.cs`

- [ ] **Step 1: Remove the request DTOs**

In `AccountViewModels.cs`, delete the `CreateAccountRequest` and
`CreateTransactionRequest` classes (the two `[Required]`-annotated
classes below `DashboardViewModel`).

- [ ] **Step 2: Remove the interface methods**

In `IAccountService.cs`, delete `CreateAccountAsync` and
`AddTransactionAsync`, leaving only `GetDashboardAsync`.

- [ ] **Step 3: Remove the implementations**

In `AccountService.cs`, delete the `CreateAccountAsync` and
`AddTransactionAsync` method bodies, leaving only `GetDashboardAsync`.
Remove now-unused `using System.Collections.Generic;` if nothing else in
the file needs it.

- [ ] **Step 4: Remove the endpoints**

In `AccountsController.cs`, delete the `CreateAccount` and
`AddTransaction` action methods, leaving only the constructor and
`GetUserId()`. If nothing else references `IAccountService` in this
controller (the dashboard read lives in `DashboardController`, not
here — confirm before deleting), this controller may end up empty of
actions; leave the class in place regardless, since removing it entirely
is a bigger change than this task's scope.

- [ ] **Step 5: Verify the solution still builds**

```bash
dotnet build FinTrackPrime.sln
```

Expected: Build succeeded, 0 errors (confirms nothing else referenced the
removed methods).

- [ ] **Step 6: Run the full test suite**

```bash
dotnet test tests/FinTrackPrime.Business.Tests/FinTrackPrime.Business.Tests.csproj
```

Expected: all tests still passing.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs src/FinTrackPrime.Business/Interfaces/IAccountService.cs src/FinTrackPrime.Business/Services/AccountService.cs src/FinTrackPrime.WebApi/Controllers/AccountsController.cs
git commit -m "refactor: remove manual account/transaction entry, superseded by bank linking"
```

---

## Task 10: Manual end-to-end verification against the real Finverse sandbox

Everything up to this point is verified with mocked/fake HTTP responses.
This task is the one place this plan touches the real Finverse sandbox,
proving the `// VERIFY:` field-name assumptions from Tasks 5-6 are
actually correct (or fixing them if not) — there is no frontend yet to
drive this through, so it's done directly against the API via Swagger.

**Files:** none (verification only — fix any file above if this step
reveals a wrong assumption).

- [ ] **Step 1: Set real Finverse credentials**

```bash
cd src/FinTrackPrime.WebApi
dotnet user-secrets set "Finverse:ClientId" "<your real sandbox client id>"
dotnet user-secrets set "Finverse:ClientSecret" "<your real sandbox client secret>"
```

- [ ] **Step 2: Run the API and open Swagger**

```bash
dotnet run --project src/FinTrackPrime.WebApi
```

Open `/swagger`, register/login a test user via `api/auth`, and
authorize Swagger with the returned bearer token.

- [ ] **Step 3: Start a link session**

Call `POST api/bank-link/token`. Confirm the response has a non-empty
`linkUrl`. Open that URL in a browser, pick **Testbank**, and log in with
Testbank's documented sandbox credentials (from `docs.finverse.com`).

- [ ] **Step 4: Capture the redirect code**

After completing the fake login, Finverse redirects the browser to the
configured `Finverse:RedirectUri`
(`https://developer.prod.finverse.net/sink`). Copy the `code` query
parameter from that URL.

- [ ] **Step 5: Complete the link**

Call `POST api/bank-link/complete` with `{ "linkCode": "<copied code>" }`.

If this returns a `400` with a parsing-related error, or an empty
`DashboardViewModel` when accounts were clearly linked, the `// VERIFY:`
field names in `FinverseClient.cs` (Task 5) are wrong — open the actual
response body (add a temporary log of `body` in
`ExchangeLinkCodeAsync`/`GetAccountsAsync`/`GetTransactionsAsync` to see
it), fix the `GetProperty("...")` calls to match, and re-run this step.

- [ ] **Step 6: Verify the result**

Confirm the response includes exactly the in-scope accounts (Checking,
Savings, Credit Card — not Bitcoin/FX/Ledger), with balances matching
what the Testbank UI showed, and that the credit card account's balance
is negative.

- [ ] **Step 7: Verify sync doesn't duplicate**

Call `POST api/bank-link/sync` a second time immediately after. Confirm
the transaction count per account is unchanged (no duplicates).

- [ ] **Step 8: Commit any field-name fixes from Step 5**

Only if Step 5 required changes:

```bash
git add src/FinTrackPrime.Business/Services/FinverseClient.cs src/FinTrackPrime.Business/Services/BankLinkService.cs
git commit -m "fix: correct Finverse API field names against real sandbox responses"
```
