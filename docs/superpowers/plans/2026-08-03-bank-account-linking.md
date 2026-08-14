# Bank Account Linking (Finverse) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace manual account/transaction entry with real (sandboxed) bank-account linking via Finverse, supporting Checking, Savings, and Credit Card accounts.

**Architecture:** A new `IFinverseClient` (backend) wraps Finverse's Link/Data APIs, mirroring the existing `IPayPalClient` pattern. A `BankLinkService` orchestrates starting a link session, exchanging the redirect code for an access token, and syncing accounts/transactions into the existing `Account`/`Transaction` tables. The frontend gets a "Connect a bank" flow replacing "Create Account," using a redirect (not an embedded script widget) to Finverse's hosted Link UI.

**Tech Stack:** ASP.NET Core 8 / EF Core / SQL Server (backend), React 19 / TypeScript / TanStack Query / axios (frontend).

## Global Constraints

- Account types in scope: **Checking, Savings, Credit Card** only. Finverse's Testbank sandbox also returns Bitcoin/FX/Ledger accounts — these must be filtered out at sync time, never stored.
- No manual account/transaction creation remains after this plan — `POST /api/accounts` and `POST /api/accounts/{id}/transactions` are removed entirely, along with their frontend modals.
- Sync is pull-based (a "Refresh" action / on-demand), not webhook-based.
- `Finverse:ClientSecret` and `Finverse:ClientId` must ship blank in `appsettings.json` (same as `ConnectionStrings:Default` today) and be required via environment variables in non-Development environments — never commit real values.
- Direction is always derived from the sign of Finverse's transaction amount: positive → `Income`, negative → `Expense`. `Transaction.Amount` itself is stored as an absolute value (matches the existing `Transaction` entity convention, where `Direction` carries the sign, not `Amount`).

**⚠️ One unresolved fact before starting Task 5:** this plan's `/link/token` request body and the customer-token (`/auth/customer/token`) call are written against what could be directly confirmed (a captured example request) or reasonably inferred (standard OAuth2 client-credentials, matching this codebase's existing `PayPalClient` convention). The **exact response field names and endpoint paths for code-exchange, accounts, and transactions** could not be scraped from `docs.finverse.com` (it's a JS-rendered app) and must be confirmed by opening the live docs — sidebar sections **"02 - Login Identity," "03 - Accounts," "04 - Transactions"** under Data API — before finishing Task 5. This is flagged inline in that task too.

---

## Task 1: Extend `Account` and `Transaction` entities for linking

**Files:**
- Modify: `src/FinTrackPrime.Models/Entities/Account.cs`
- Modify: `src/FinTrackPrime.Models/Entities/Transaction.cs`

**Interfaces:**
- Produces: `AccountType.CreditCard` enum value; `Account.ExternalAccountId` (`string?`), `Account.Institution` (`string?`); `Transaction.ExternalTransactionId` (`string?`) — all consumed by Task 2 (DbContext config) and Task 6 (`BankLinkService`).

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

    // A bank account linked via Finverse (see LinkedInstitution). Balance
    // and transactions are overwritten on every sync, not user-editable.
    public class Account
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        // Finverse's account id, and which linked institution it came
        // from. Null only transiently before the first sync writes them.
        public string? ExternalAccountId { get; set; }
        public string? Institution { get; set; }

        public string Nickname { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public decimal Balance { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
}
```

- [ ] **Step 2: Update `Transaction.cs`**

```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    public enum TransactionDirection
    {
        Income,
        Expense
    }

    public class Transaction
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public Account? Account { get; set; }

        // Finverse's transaction id. Used to dedupe on re-sync — never
        // insert a transaction whose ExternalTransactionId already exists.
        public string? ExternalTransactionId { get; set; }

        public string Description { get; set; } = string.Empty;

        // Free-text category ("Groceries", "Salary", "Utilities").
        // The Budget Planner and Cash Flow Dashboard both group by this
        // field, so it is the one thing that has to stay consistent.
        // Finverse has no category field, so synced transactions land
        // here empty until a user edits one.
        public string Category { get; set; } = string.Empty;

        public decimal Amount { get; set; }
        public TransactionDirection Direction { get; set; }
        public DateTime OccurredAtUtc { get; set; }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build` from the repo root.
Expected: build succeeds (no other file references `AccountType`/`Account`/`Transaction` in a way that breaks — `AccountService.CreateAccountAsync`/`AddTransactionAsync` still compile against the unchanged fields; they get removed in Task 7).

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/Account.cs src/FinTrackPrime.Models/Entities/Transaction.cs
git commit -m "Add CreditCard account type and Finverse linking fields to Account/Transaction"
```

---

## Task 2: Create `LinkedInstitution` entity and wire it into the DbContext

**Files:**
- Create: `src/FinTrackPrime.Models/Entities/LinkedInstitution.cs`
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Consumes: `Account`, `Transaction` from Task 1.
- Produces: `LinkedInstitution` entity (`Id`, `UserId`, `Institution`, `AccessToken`, `LinkedAtUtc`, `LastSyncedAtUtc`) and `FinTrackDbContext.LinkedInstitutions` `DbSet`, consumed by Task 6 (`BankLinkService`).

- [ ] **Step 1: Create `LinkedInstitution.cs`**

```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per bank a user has connected through Finverse. One
    // institution can back multiple Account rows (e.g. Testbank returns a
    // checking, a savings, and a credit card account from one login).
    public class LinkedInstitution
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        // e.g. "Testbank", "UnionBank". Matches Account.Institution for
        // the accounts this link produced.
        public string Institution { get; set; } = string.Empty;

        // Finverse's per-user access token for this institution. As
        // sensitive as a password — this is a live credential to pull
        // someone's financial data. Encrypted at rest (see Task 3).
        public string AccessToken { get; set; } = string.Empty;

        public DateTime LinkedAtUtc { get; set; }
        public DateTime? LastSyncedAtUtc { get; set; }
    }
}
```

- [ ] **Step 2: Register the `DbSet` and configure all three entities in `FinTrackDbContext.cs`**

Add the `DbSet` property alongside the others:

```csharp
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<LinkedInstitution> LinkedInstitutions => Set<LinkedInstitution>();
```

Replace the existing `Account` and `Transaction` configuration blocks in `OnModelCreating` with:

```csharp
            modelBuilder.Entity<Account>(entity =>
            {
                entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
                entity.Property(a => a.ExternalAccountId).HasMaxLength(128);
                entity.Property(a => a.Institution).HasMaxLength(80);
                // A user can't link the same external account twice; two
                // different users linking the same sandbox Testbank
                // account independently is fine (Finverse issues them
                // distinct external ids per customer_user_id).
                entity.HasIndex(a => new { a.UserId, a.ExternalAccountId })
                      .IsUnique()
                      .HasFilter("[ExternalAccountId] IS NOT NULL");
                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.Property(t => t.Amount).HasColumnType("decimal(18,2)");
                entity.Property(t => t.Category).HasMaxLength(80);
                entity.Property(t => t.ExternalTransactionId).HasMaxLength(128);
                // Global uniqueness, not scoped per-account: Finverse
                // transaction ids are unique across the whole sandbox, and
                // this is the sync dedupe key.
                entity.HasIndex(t => t.ExternalTransactionId)
                      .IsUnique()
                      .HasFilter("[ExternalTransactionId] IS NOT NULL");
                entity.HasOne(t => t.Account)
                      .WithMany(a => a.Transactions)
                      .HasForeignKey(t => t.AccountId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LinkedInstitution>(entity =>
            {
                entity.Property(li => li.Institution).HasMaxLength(80).IsRequired();
                entity.Property(li => li.AccessToken).HasMaxLength(1024).IsRequired();
                // One link per institution per user — re-linking the same
                // bank updates the existing row's AccessToken instead of
                // creating a duplicate (see BankLinkService.CompleteLinkAsync).
                entity.HasIndex(li => new { li.UserId, li.Institution }).IsUnique();
                entity.HasOne(li => li.User)
                      .WithMany()
                      .HasForeignKey(li => li.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build` from the repo root.
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/LinkedInstitution.cs src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs
git commit -m "Add LinkedInstitution entity and DbContext configuration for bank linking"
```

---

## Task 3: Add the EF Core migration

**Files:**
- Create: `src/FinTrackPrime.Models/Migrations/*_AddBankAccountLinking.cs` (generated)

**Interfaces:**
- Consumes: entity/DbContext changes from Tasks 1–2.
- Produces: a migration applying those changes to the actual database schema — required before Task 6's code can run against a real database.

- [ ] **Step 1: Generate the migration**

Run from the repo root:

```bash
dotnet ef migrations add AddBankAccountLinking --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
```

- [ ] **Step 2: Inspect the generated migration file**

Open the newly created file under `src/FinTrackPrime.Models/Migrations/`. Confirm it contains:
- `AddColumn` for `Account.ExternalAccountId`, `Account.Institution`
- `AddColumn` for `Transaction.ExternalTransactionId`
- `CreateTable` for `LinkedInstitutions`
- The three indexes from Task 2 (`IX_Accounts_UserId_ExternalAccountId`, `IX_Transactions_ExternalTransactionId`, `IX_LinkedInstitutions_UserId_Institution`)

If any are missing, the DbContext configuration in Task 2 wasn't picked up — re-check `OnModelCreating` before continuing.

- [ ] **Step 3: Apply it to your local database**

```bash
dotnet ef database update --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
```

Confirm no errors, and that `LinkedInstitutions` now exists as a table (check via SSMS/Azure Data Studio or `dotnet ef migrations list` showing it as applied).

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations/
git commit -m "Add AddBankAccountLinking EF Core migration"
```

---

## Task 4: Finverse configuration and DI registration

**Files:**
- Modify: `src/FinTrackPrime.WebApi/appsettings.json`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`

**Interfaces:**
- Produces: `Finverse:ClientId`, `Finverse:ClientSecret`, `Finverse:ApiBaseUrl`, `Finverse:RedirectUri` config keys and a registered `IFinverseClient` typed `HttpClient`, consumed by Task 5.

- [ ] **Step 1: Add the `Finverse` section to `appsettings.json`**

Add this alongside the existing `PayPal` section:

```json
  "Finverse": {
    "ClientId": "",
    "ClientSecret": "",
    "ApiBaseUrl": "https://api.prod.finverse.net",
    "RedirectUri": "https://developer.prod.finverse.net/sink"
  },
```

`ClientId`/`ClientSecret` ship blank on purpose — set them via user-secrets or `appsettings.Development.json` locally (same as `Jwt:Key` today), never commit real values. `RedirectUri` is Finverse's own testing sink for now; swap it for the real frontend callback URL (Task 9) once that route exists, and re-register it in Finverse's API Settings → Callback URLs alongside the sink.

- [ ] **Step 2: Add `Finverse:ClientId`/`ClientSecret` to the fail-fast check in `Program.cs`**

In the `required` array near the top of `Program.cs`:

```csharp
    var required = new (string Key, string Value)[]
    {
        ("ConnectionStrings:Default", builder.Configuration.GetConnectionString("Default") ?? ""),
        ("Jwt:Key", builder.Configuration["Jwt:Key"] ?? ""),
        ("PayPal:ClientId", builder.Configuration["PayPal:ClientId"] ?? ""),
        ("PayPal:ClientSecret", builder.Configuration["PayPal:ClientSecret"] ?? ""),
        ("Finverse:ClientId", builder.Configuration["Finverse:ClientId"] ?? ""),
        ("Finverse:ClientSecret", builder.Configuration["Finverse:ClientSecret"] ?? ""),
    };
```

- [ ] **Step 3: Register the typed `HttpClient` for `IFinverseClient`**

Add this next to the existing `AddHttpClient<IPayPalClient, PayPalClient>` call (the concrete `FinverseClient` class doesn't exist until Task 5 — this line won't compile until then, so Step 4 below builds after Task 5, not now):

```csharp
builder.Services.AddHttpClient<IFinverseClient, FinverseClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Finverse:ApiBaseUrl"]!);
});
```

Also add the using statement if not already present: `using FinTrackPrime.Business.Interfaces;` and `using FinTrackPrime.Business.Services;` (both already exist in this file for the PayPal registration, so nothing to add).

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.WebApi/appsettings.json src/FinTrackPrime.WebApi/Program.cs
git commit -m "Add Finverse configuration and DI registration"
```

(This commit will not build in isolation since `IFinverseClient`/`FinverseClient` don't exist yet — that's fine, Task 5 is the very next task and this plan is meant to be executed in order. If your workflow requires every commit to build standalone, merge this commit's `Program.cs`/`appsettings.json` changes into Task 5's commit instead.)

---

## Task 5: `IFinverseClient` and `FinverseClient`

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/IFinverseClient.cs`
- Create: `src/FinTrackPrime.Business/Services/FinverseClient.cs`

**Interfaces:**
- Consumes: `Finverse:ClientId`/`ClientSecret`/`ApiBaseUrl` config from Task 4.
- Produces: `IFinverseClient` with `GenerateLinkUrlAsync(Guid userId, string redirectUri)`, `ExchangeCodeAsync(string code)`, `GetAccountsAsync(string accessToken)`, `GetTransactionsAsync(string accessToken, string externalAccountId)` — consumed by Task 6 (`BankLinkService`).

**⚠️ Before writing Step 2 below**, open `docs.finverse.com` → **Data API** in the left sidebar and read:
- **"01 - Link Institution via Finverse Link UI"** → the `POST /auth/token (Exchange Link Code for...)` page directly under it — confirms the code-exchange request/response shape.
- **"03 - Accounts"** → confirms the GET endpoint path and the exact JSON field names for account id, display name, type, and balance.
- **"04 - Transactions"** → confirms the GET endpoint path and the exact JSON field names for transaction id, description, amount, and posted date.

The code below is fully correct for the one endpoint whose request shape was directly captured (`/link/token`) and for the customer-token call (written as standard OAuth2 client-credentials, matching this codebase's existing `PayPalClient.GetAccessTokenAsync`). The three methods marked `// CONFIRM AGAINST DOCS` need their path and `JsonDocument` property names checked against what you just read before this will work against the real API — the shapes below are best-effort based on the endpoint names and the Link UI's response conventions, not a captured example.

- [ ] **Step 1: Create `IFinverseClient.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    public record FinverseLinkExchange(string AccessToken, string Institution);
    public record FinverseAccount(string Id, string Name, string Type, decimal Balance);
    public record FinverseTransaction(string Id, string Description, decimal Amount, DateTime PostedAtUtc);

    public interface IFinverseClient
    {
        // Starts a Link session for one of our users and returns the
        // link_url to open Finverse's hosted Link UI in the browser.
        Task<string> GenerateLinkUrlAsync(Guid userId, string redirectUri);

        // Trades the authorization code Finverse's redirect included for
        // a per-user access token, plus which institution was linked.
        Task<FinverseLinkExchange> ExchangeCodeAsync(string code);

        // Every account behind one linked institution's access token.
        Task<IReadOnlyList<FinverseAccount>> GetAccountsAsync(string accessToken);

        // Every transaction for one account behind that access token.
        Task<IReadOnlyList<FinverseTransaction>> GetTransactionsAsync(string accessToken, string externalAccountId);
    }
}
```

- [ ] **Step 2: Create `FinverseClient.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    // Talks to Finverse's Link and Data APIs directly. Registered with a
    // typed HttpClient (see Program.cs), so BaseAddress and lifetime are
    // handled by the DI container rather than this class.
    public class FinverseClient : IFinverseClient
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public FinverseClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<string> GenerateLinkUrlAsync(Guid userId, string redirectUri)
        {
            var customerToken = await GetCustomerTokenAsync();
            var clientId = _config["Finverse:ClientId"];

            using var request = new HttpRequestMessage(HttpMethod.Post, "/link/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
            request.Content = JsonContent.Create(new
            {
                client_id = clientId,
                redirect_uri = redirectUri,
                state = Guid.NewGuid().ToString("N"),
                user_id = userId.ToString(),
                grant_type = "client_credentials",
                response_mode = "form_post",
                response_type = "code",
            });

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse link/token failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("link_url").GetString()!;
        }

        // CONFIRM AGAINST DOCS: path and response field names taken from
        // "POST /auth/token (Exchange Link Code for...)" under Data API.
        public async Task<FinverseLinkExchange> ExchangeCodeAsync(string code)
        {
            var customerToken = await GetCustomerTokenAsync();
            var clientId = _config["Finverse:ClientId"];

            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
            request.Content = JsonContent.Create(new
            {
                client_id = clientId,
                code,
                grant_type = "authorization_code",
            });

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse auth/token exchange failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new FinverseLinkExchange(
                root.GetProperty("access_token").GetString()!,
                root.GetProperty("institution_name").GetString()!);
        }

        // CONFIRM AGAINST DOCS: path and field names from "03 - Accounts".
        public async Task<IReadOnlyList<FinverseAccount>> GetAccountsAsync(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/accounts");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse accounts lookup failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var accounts = new List<FinverseAccount>();
            foreach (var element in doc.RootElement.GetProperty("accounts").EnumerateArray())
            {
                accounts.Add(new FinverseAccount(
                    element.GetProperty("account_id").GetString()!,
                    element.GetProperty("name").GetString()!,
                    element.GetProperty("type").GetString()!,
                    element.GetProperty("balance").GetDecimal()));
            }
            return accounts;
        }

        // CONFIRM AGAINST DOCS: path and field names from "04 - Transactions".
        public async Task<IReadOnlyList<FinverseTransaction>> GetTransactionsAsync(string accessToken, string externalAccountId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/accounts/{externalAccountId}/transactions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse transactions lookup failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var transactions = new List<FinverseTransaction>();
            foreach (var element in doc.RootElement.GetProperty("transactions").EnumerateArray())
            {
                transactions.Add(new FinverseTransaction(
                    element.GetProperty("transaction_id").GetString()!,
                    element.GetProperty("description").GetString()!,
                    element.GetProperty("amount").GetDecimal(),
                    element.GetProperty("posted_date").GetDateTime()));
            }
            return transactions;
        }

        private async Task<string> GetCustomerTokenAsync()
        {
            var clientId = _config["Finverse:ClientId"];
            var clientSecret = _config["Finverse:ClientSecret"];

            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/customer/token");
            request.Content = JsonContent.Create(new
            {
                client_id = clientId,
                client_secret = clientSecret,
                grant_type = "client_credentials",
            });

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse customer-token request failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString()!;
        }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build` from the repo root.
Expected: build succeeds, and Task 4's `AddHttpClient<IFinverseClient, FinverseClient>` line now resolves.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/IFinverseClient.cs src/FinTrackPrime.Business/Services/FinverseClient.cs
git commit -m "Add FinverseClient wrapping the Link and Data APIs"
```

---

## Task 6: `BankLinkService`

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/IBankLinkService.cs`
- Create: `src/FinTrackPrime.Business/Services/BankLinkService.cs`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`

**Interfaces:**
- Consumes: `IFinverseClient` from Task 5; `Account`, `Transaction`, `LinkedInstitution`, `FinTrackDbContext` from Tasks 1–2.
- Produces: `IBankLinkService` with `StartLinkAsync(Guid userId)`, `CompleteLinkAsync(Guid userId, string code)`, `SyncAsync(Guid userId)` — consumed by Task 7 (`BankLinkController`).

- [ ] **Step 1: Create `IBankLinkService.cs`**

```csharp
using System;
using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IBankLinkService
    {
        // Returns the link_url the frontend should send the browser to.
        Task<string> StartLinkAsync(Guid userId);

        // Exchanges the redirect code, stores/updates the LinkedInstitution,
        // and does an initial sync. Returns how many accounts are now linked
        // for this institution (in scope: Checking/Savings/CreditCard).
        Task<int> CompleteLinkAsync(Guid userId, string code);

        // Re-syncs every institution the user has linked. Returns how many
        // new transactions were inserted.
        Task<int> SyncAsync(Guid userId);
    }
}
```

- [ ] **Step 2: Create `BankLinkService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class BankLinkService : IBankLinkService
    {
        // Finverse's own type strings for the three account types this
        // app supports. Anything else (Bitcoin, FX, Ledger, ...) is
        // filtered out at sync time and never stored.
        private static readonly Dictionary<string, AccountType> SupportedFinverseAccountTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["CHECKING"] = AccountType.Checking,
                ["SAVINGS"] = AccountType.Savings,
                ["CREDIT_CARD"] = AccountType.CreditCard,
            };

        private readonly FinTrackDbContext _db;
        private readonly IFinverseClient _finverseClient;
        private readonly IConfiguration _config;

        public BankLinkService(FinTrackDbContext db, IFinverseClient finverseClient, IConfiguration config)
        {
            _db = db;
            _finverseClient = finverseClient;
            _config = config;
        }

        public async Task<string> StartLinkAsync(Guid userId)
        {
            var redirectUri = _config["Finverse:RedirectUri"]!;
            return await _finverseClient.GenerateLinkUrlAsync(userId, redirectUri);
        }

        public async Task<int> CompleteLinkAsync(Guid userId, string code)
        {
            var exchange = await _finverseClient.ExchangeCodeAsync(code);

            var institution = await _db.LinkedInstitutions
                .FirstOrDefaultAsync(li => li.UserId == userId && li.Institution == exchange.Institution);

            if (institution is null)
            {
                institution = new LinkedInstitution
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Institution = exchange.Institution,
                    AccessToken = exchange.AccessToken,
                    LinkedAtUtc = DateTime.UtcNow,
                };
                _db.LinkedInstitutions.Add(institution);
            }
            else
            {
                // Re-linking the same bank refreshes the access token
                // rather than creating a duplicate row.
                institution.AccessToken = exchange.AccessToken;
            }

            await _db.SaveChangesAsync();

            await SyncInstitutionAsync(userId, institution);

            return await _db.Accounts.CountAsync(a => a.UserId == userId && a.Institution == institution.Institution);
        }

        public async Task<int> SyncAsync(Guid userId)
        {
            var institutions = await _db.LinkedInstitutions
                .Where(li => li.UserId == userId)
                .ToListAsync();

            var totalNewTransactions = 0;
            foreach (var institution in institutions)
            {
                try
                {
                    totalNewTransactions += await SyncInstitutionAsync(userId, institution);
                }
                catch (InvalidOperationException)
                {
                    // One institution's Finverse call failing (expired
                    // token, their API down) shouldn't block syncing the
                    // user's other linked banks. That institution's data
                    // just stays as of its LastSyncedAtUtc.
                }
            }

            return totalNewTransactions;
        }

        private async Task<int> SyncInstitutionAsync(Guid userId, LinkedInstitution institution)
        {
            var finverseAccounts = await _finverseClient.GetAccountsAsync(institution.AccessToken);
            var newTransactionCount = 0;

            foreach (var finverseAccount in finverseAccounts)
            {
                if (!SupportedFinverseAccountTypes.TryGetValue(finverseAccount.Type, out var accountType))
                {
                    continue;
                }

                var account = await _db.Accounts
                    .FirstOrDefaultAsync(a => a.UserId == userId && a.ExternalAccountId == finverseAccount.Id);

                if (account is null)
                {
                    account = new Account
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        ExternalAccountId = finverseAccount.Id,
                        Institution = institution.Institution,
                        Nickname = finverseAccount.Name,
                        Type = accountType,
                        Balance = finverseAccount.Balance,
                        CreatedAtUtc = DateTime.UtcNow,
                    };
                    _db.Accounts.Add(account);
                    await _db.SaveChangesAsync();
                }
                else
                {
                    account.Balance = finverseAccount.Balance;
                }

                var finverseTransactions = await _finverseClient.GetTransactionsAsync(institution.AccessToken, finverseAccount.Id);

                foreach (var finverseTransaction in finverseTransactions)
                {
                    var alreadyExists = await _db.Transactions
                        .AnyAsync(t => t.ExternalTransactionId == finverseTransaction.Id);

                    if (alreadyExists)
                    {
                        continue;
                    }

                    _db.Transactions.Add(new Transaction
                    {
                        Id = Guid.NewGuid(),
                        AccountId = account.Id,
                        ExternalTransactionId = finverseTransaction.Id,
                        Description = finverseTransaction.Description,
                        Category = string.Empty,
                        Amount = Math.Abs(finverseTransaction.Amount),
                        Direction = finverseTransaction.Amount >= 0 ? TransactionDirection.Income : TransactionDirection.Expense,
                        OccurredAtUtc = finverseTransaction.PostedAtUtc,
                    });

                    newTransactionCount++;
                }
            }

            institution.LastSyncedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return newTransactionCount;
        }
    }
}
```

- [ ] **Step 3: Register `IBankLinkService` in `Program.cs`**

Add alongside the other `AddScoped` service registrations:

```csharp
builder.Services.AddScoped<IBankLinkService, BankLinkService>();
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build` from the repo root.
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/IBankLinkService.cs src/FinTrackPrime.Business/Services/BankLinkService.cs src/FinTrackPrime.WebApi/Program.cs
git commit -m "Add BankLinkService orchestrating link, exchange, and sync"
```

---

## Task 7: `BankLinkController`, and remove manual account/transaction entry

**Files:**
- Create: `src/FinTrackPrime.WebApi/Controllers/BankLinkController.cs`
- Create: `src/FinTrackPrime.Models/ViewModels/BankLinkViewModels.cs`
- Delete: `src/FinTrackPrime.WebApi/Controllers/AccountsController.cs`
- Modify: `src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs`
- Modify: `src/FinTrackPrime.Business/Interfaces/IAccountService.cs`
- Modify: `src/FinTrackPrime.Business/Services/AccountService.cs`

**Interfaces:**
- Consumes: `IBankLinkService` from Task 6.
- Produces: `POST /api/bank-link/token`, `POST /api/bank-link/complete`, `POST /api/bank-link/sync` — consumed by Task 8 (frontend `bankLink.ts`).

- [ ] **Step 1: Create `BankLinkViewModels.cs`**

```csharp
namespace FinTrackPrime.Models.ViewModels
{
    public class LinkTokenResponse
    {
        public string LinkUrl { get; set; } = string.Empty;
    }

    public class CompleteLinkRequest
    {
        public string Code { get; set; } = string.Empty;
    }

    public class CompleteLinkResponse
    {
        public int AccountsLinked { get; set; }
    }

    public class SyncResponse
    {
        public int NewTransactionCount { get; set; }
    }
}
```

- [ ] **Step 2: Create `BankLinkController.cs`**

```csharp
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/bank-link")]
    [Authorize]
    public class BankLinkController : ControllerBase
    {
        private readonly IBankLinkService _bankLinkService;

        public BankLinkController(IBankLinkService bankLinkService)
        {
            _bankLinkService = bankLinkService;
        }

        [HttpPost("token")]
        public async Task<ActionResult<LinkTokenResponse>> StartLink()
        {
            var linkUrl = await _bankLinkService.StartLinkAsync(GetUserId());
            return Ok(new LinkTokenResponse { LinkUrl = linkUrl });
        }

        [HttpPost("complete")]
        public async Task<ActionResult<CompleteLinkResponse>> CompleteLink(CompleteLinkRequest request)
        {
            var accountsLinked = await _bankLinkService.CompleteLinkAsync(GetUserId(), request.Code);
            return Ok(new CompleteLinkResponse { AccountsLinked = accountsLinked });
        }

        [HttpPost("sync")]
        public async Task<ActionResult<SyncResponse>> Sync()
        {
            var newTransactionCount = await _bankLinkService.SyncAsync(GetUserId());
            return Ok(new SyncResponse { NewTransactionCount = newTransactionCount });
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

- [ ] **Step 3: Delete `AccountsController.cs`**

Delete `src/FinTrackPrime.WebApi/Controllers/AccountsController.cs` entirely — both its endpoints (`POST /api/accounts`, `POST /api/accounts/{id}/transactions`) are replaced by `BankLinkController`. `DashboardController`'s read-only `GET /api/dashboard` is untouched and still the only way the frontend reads accounts.

- [ ] **Step 4: Remove the manual-entry request types from `AccountViewModels.cs`**

Delete the `CreateAccountRequest` and `CreateTransactionRequest` classes from `src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs`, keeping `TransactionViewModel`, `AccountViewModel`, and `DashboardViewModel` as they are.

- [ ] **Step 5: Remove the manual-entry methods from `IAccountService.cs`**

```csharp
using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IAccountService
    {
        // Returns every account the user owns, each with its recent
        // transactions. This is the single call the dashboard screen
        // makes on load.
        Task<DashboardViewModel> GetDashboardAsync(Guid userId);
    }
}
```

- [ ] **Step 6: Remove `CreateAccountAsync`/`AddTransactionAsync` from `AccountService.cs`**

Delete both methods from `src/FinTrackPrime.Business/Services/AccountService.cs`, leaving only the constructor and `GetDashboardAsync` (unchanged).

- [ ] **Step 7: Build to verify it compiles**

Run: `dotnet build` from the repo root.
Expected: build succeeds with no remaining references to `CreateAccountRequest`, `CreateTransactionRequest`, `CreateAccountAsync`, or `AddTransactionAsync` anywhere in the backend.

- [ ] **Step 8: Commit**

```bash
git add src/FinTrackPrime.WebApi/Controllers/BankLinkController.cs src/FinTrackPrime.Models/ViewModels/BankLinkViewModels.cs src/FinTrackPrime.Models/ViewModels/AccountViewModels.cs src/FinTrackPrime.Business/Interfaces/IAccountService.cs src/FinTrackPrime.Business/Services/AccountService.cs
git rm src/FinTrackPrime.WebApi/Controllers/AccountsController.cs
git commit -m "Add BankLinkController, remove manual account/transaction entry"
```

---

## Task 8: Frontend `bankLink` API module and types

**Files:**
- Create: `src/api/bankLink.ts`
- Modify: `src/types/api.ts`

**Interfaces:**
- Consumes: `POST /api/bank-link/token`, `/complete`, `/sync` from Task 7.
- Produces: `bankLinkApi.{startLink, completeLink, sync}`, `AccountType` including `'CreditCard'` — consumed by Task 9 (callback page) and Task 10 (Dashboard).

- [ ] **Step 1: Update `types/api.ts`**

Replace the `AccountType` line:

```typescript
export type AccountType = 'Checking' | 'Savings' | 'CreditCard'
```

Delete `CreateAccountRequest` and `CreateTransactionRequest` (no longer used — the backend endpoints they targeted are gone).

Add, near the other feature-specific types:

```typescript
export interface LinkTokenResponse {
  linkUrl: string
}

export interface CompleteLinkRequest {
  code: string
}

export interface CompleteLinkResponse {
  accountsLinked: number
}

export interface SyncResponse {
  newTransactionCount: number
}
```

- [ ] **Step 2: Create `api/bankLink.ts`**

```typescript
import { apiClient } from './client'
import type { LinkTokenResponse, CompleteLinkRequest, CompleteLinkResponse, SyncResponse } from '../types/api'

export const bankLinkApi = {
  startLink: async (): Promise<LinkTokenResponse> => {
    const { data } = await apiClient.post<LinkTokenResponse>('/api/bank-link/token')
    return data
  },
  completeLink: async (request: CompleteLinkRequest): Promise<CompleteLinkResponse> => {
    const { data } = await apiClient.post<CompleteLinkResponse>('/api/bank-link/complete', request)
    return data
  },
  sync: async (): Promise<SyncResponse> => {
    const { data } = await apiClient.post<SyncResponse>('/api/bank-link/sync')
    return data
  },
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `npm run build` from the `FinTrackPrime` frontend repo root.
Expected: fails at this point specifically because `DashboardPage.tsx` still imports `CreateAccountModal`/`AddTransactionModal` and uses the now-deleted `CreateAccountRequest` type — that's expected and gets fixed in Task 10. Confirm the *only* errors are in `DashboardPage.tsx`, `CreateAccountModal.tsx`, and `AddTransactionModal.tsx` — nothing in `bankLink.ts` or `types/api.ts` itself.

- [ ] **Step 4: Commit**

```bash
git add src/api/bankLink.ts src/types/api.ts
git commit -m "Add bankLink API module and types"
```

---

## Task 9: Bank-link callback page and route

**Files:**
- Create: `src/pages/BankLinkCallbackPage.tsx`
- Modify: `src/App.tsx`

**Interfaces:**
- Consumes: `bankLinkApi.completeLink` from Task 8.
- Produces: `/bank-link/callback` route — the real `redirect_uri` to register in Finverse's API Settings once this exists (replacing/joining the placeholder sink from Task 4).

- [ ] **Step 1: Create `BankLinkCallbackPage.tsx`**

```tsx
import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { bankLinkApi } from '../api/bankLink'
import { FullPageSpinner } from '../components/FullPageSpinner'
import { EmptyState } from '../components/ui/EmptyState'
import { Button } from '../components/ui/Button'

export function BankLinkCallbackPage() {
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const code = searchParams.get('code')
    if (!code) {
      setError('No authorization code was returned by Finverse.')
      return
    }

    bankLinkApi
      .completeLink({ code })
      .then(async () => {
        await queryClient.invalidateQueries({ queryKey: ['dashboard'] })
        navigate('/dashboard', { replace: true })
      })
      .catch(() => {
        setError("Couldn't finish linking your bank account.")
      })
  }, [searchParams, navigate, queryClient])

  if (error) {
    return (
      <EmptyState
        title="Bank linking failed"
        description={error}
        action={<Button onClick={() => navigate('/dashboard')}>Back to dashboard</Button>}
      />
    )
  }

  return <FullPageSpinner />
}
```

- [ ] **Step 2: Add the route in `App.tsx`**

Add the import:

```typescript
import { BankLinkCallbackPage } from './pages/BankLinkCallbackPage'
```

Add the route inside the `<ProtectedRoute>`/`<AppLayout>` nesting, alongside `/dashboard`:

```tsx
          <Route path="/bank-link/callback" element={<BankLinkCallbackPage />} />
```

- [ ] **Step 3: Build to verify it compiles**

Run: `npm run build` from the `FinTrackPrime` frontend repo root.
Expected: same pre-existing `DashboardPage.tsx`/modal errors as Task 8 — nothing new from this task's files.

- [ ] **Step 4: Commit**

```bash
git add src/pages/BankLinkCallbackPage.tsx src/App.tsx
git commit -m "Add bank-link callback page and route"
```

---

## Task 10: Replace "Create Account" with "Connect a bank" on the Dashboard, remove manual-entry modals

**Files:**
- Modify: `src/pages/DashboardPage.tsx`
- Delete: `src/components/CreateAccountModal.tsx`
- Delete: `src/components/AddTransactionModal.tsx`

**Interfaces:**
- Consumes: `bankLinkApi.startLink` from Task 8.

- [ ] **Step 1: Rewrite `DashboardPage.tsx`**

```tsx
import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Landmark, Wallet } from 'lucide-react'
import { dashboardApi } from '../api/dashboard'
import { bankLinkApi } from '../api/bankLink'
import type { AccountViewModel, TransactionViewModel } from '../types/api'
import { StatCard } from '../components/ui/StatCard'
import { Card, CardHeader } from '../components/ui/Card'
import { Button } from '../components/ui/Button'
import { EmptyState } from '../components/ui/EmptyState'
import { SkeletonCard } from '../components/ui/Skeleton'

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount)
}

function formatDate(iso: string) {
  return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric' }).format(new Date(iso))
}

function TransactionRow({ transaction }: { transaction: TransactionViewModel }) {
  const isExpense = transaction.direction === 'Expense'

  return (
    <li className="flex items-center justify-between rounded-md px-3 py-2 text-sm">
      <div>
        <p className="font-medium text-text-primary">{transaction.description}</p>
        <p className="text-xs text-text-muted">
          {transaction.category || 'Uncategorized'} · {formatDate(transaction.occurredAtUtc)}
        </p>
      </div>
      <span className={`tabular-figure font-medium ${isExpense ? 'text-text-primary' : 'text-status-good'}`}>
        {isExpense ? '−' : '+'}
        {formatCurrency(transaction.amount)}
      </span>
    </li>
  )
}

function AccountCard({ account }: { account: AccountViewModel }) {
  return (
    <Card hoverElevate>
      <div className="flex items-baseline justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">{account.type}</p>
          <h2 className="font-display text-lg text-text-primary">{account.nickname}</h2>
        </div>
        <p className="tabular-figure font-display text-2xl text-text-primary">{formatCurrency(account.balance)}</p>
      </div>

      <ul className="mt-4 divide-y divide-border">
        {account.recentTransactions.length === 0 ? (
          <li className="py-4 text-sm text-text-muted">No transactions yet.</li>
        ) : (
          account.recentTransactions.map((t) => <TransactionRow key={t.id} transaction={t} />)
        )}
      </ul>
    </Card>
  )
}

export function DashboardPage() {
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['dashboard'],
    queryFn: dashboardApi.get,
  })

  const summary = useMemo(() => {
    const accounts = data?.accounts ?? []
    const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0)
    return { totalBalance }
  }, [data])

  async function handleConnectBank() {
    const { linkUrl } = await bankLinkApi.startLink()
    window.location.href = linkUrl
  }

  if (isLoading) {
    return (
      <div>
        <div className="h-8 w-48 animate-pulse rounded bg-surface-sunken" />
        <div className="mt-6 grid gap-4 sm:grid-cols-2">
          <StatCard label="" value="" isLoading />
        </div>
        <div className="mt-6 grid gap-5 md:grid-cols-2">
          <SkeletonCard />
          <SkeletonCard />
        </div>
      </div>
    )
  }

  if (isError || !data) {
    return (
      <EmptyState
        title="Couldn't load your dashboard"
        description="Something went wrong fetching your accounts."
        action={<Button onClick={() => refetch()}>Try again</Button>}
      />
    )
  }

  return (
    <div>
      <CardHeader title="Your accounts" description="Every account you've connected, balance, and recent activity." />

      <div className="grid gap-4 sm:grid-cols-2">
        <StatCard label="Total balance" value={formatCurrency(summary.totalBalance)} icon={<Wallet className="h-4 w-4" />} />
      </div>

      <div className="mt-6 grid gap-5 md:grid-cols-2">
        {data.accounts.map((account) => (
          <AccountCard key={account.id} account={account} />
        ))}

        <button
          type="button"
          onClick={handleConnectBank}
          className="flex min-h-32 items-center justify-center gap-2 rounded-xl border border-dashed border-border-strong text-sm font-medium text-ft-blue hover:bg-surface-elevated"
        >
          <Landmark className="h-4 w-4" />
          Connect a bank
        </button>
      </div>
    </div>
  )
}
```

Note: `onAddTransaction` and the "Add transaction" button are gone — transactions only ever arrive via sync now, there's no manual add path.

- [ ] **Step 2: Delete the manual-entry modals**

```bash
git rm src/components/CreateAccountModal.tsx src/components/AddTransactionModal.tsx
```

- [ ] **Step 3: Build to verify it compiles**

Run: `npm run build` from the `FinTrackPrime` frontend repo root.
Expected: build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/pages/DashboardPage.tsx
git commit -m "Replace manual account/transaction entry with Connect a bank on Dashboard"
```

---

## Task 11: End-to-end manual verification against Testbank

**Files:** none (verification only).

- [ ] **Step 1: Run both apps locally**

Backend: `dotnet run --project src/FinTrackPrime.WebApi`
Frontend: `npm run dev` from the `FinTrackPrime` repo root.

- [ ] **Step 2: Register a fresh test user in the app and reach the Dashboard**

Confirm it shows zero accounts and only the "Connect a bank" tile (no leftover "Create an account" affordance).

- [ ] **Step 3: Click "Connect a bank"**

Confirm the browser navigates to Finverse's Link UI. Pick **Testbank**, log in with its documented sandbox test credentials, and complete the flow.

- [ ] **Step 4: Confirm the callback completes**

Confirm you land back on `/dashboard` (via `/bank-link/callback`) and see the Checking, Statement Savings, and Credit Card accounts from Testbank — **not** the Bitcoin/FX/Ledger ones.

- [ ] **Step 5: Confirm transaction data looks right**

Open one account and confirm transaction directions match sign: the Checking account's `+523.00 HKD` FPS transfer shows as income (green, `+`), and expenses like the Starbucks charge show as expense (`−`). Confirm the Credit Card's `-1,833.22 HKD` balance displays correctly as a negative balance.

- [ ] **Step 6: Confirm sync doesn't duplicate**

Trigger a second sync (call `POST /api/bank-link/sync`, e.g. via Swagger while authenticated) and confirm the transaction counts on the dashboard don't double.

- [ ] **Step 7: Document the result**

Note in your own tracking (not part of this repo) whether Steps 2–6 all passed, and if `ExchangeCodeAsync`/`GetAccountsAsync`/`GetTransactionsAsync` needed field-name corrections from what Task 5 guessed — update `FinverseClient.cs` and commit a fix if so.
