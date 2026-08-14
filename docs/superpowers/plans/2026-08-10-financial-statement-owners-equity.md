# Financial Statement: typed Assets/Liabilities + Owner's Equity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Financial Statement premium tool typed Assets/Liabilities (with manual asset entry, mirroring the existing manual liability entry) and a third "Owner's Equity" figure, matching the bank's requested 3-column balance-sheet layout.

**Architecture:** Backend adds an `Asset` entity (mirrors the existing `Liability` entity) and `AssetType`/`LiabilityType` enums, tags every line the service already produces with a `Type`, and renames `NetWorth` to `OwnersEquity` (same arithmetic, `TotalAssets - TotalLiabilities`). Grouping/subtotals by `Type` happen client-side from flat, typed lists — no new nested API shape. Frontend restructures the page to 3 columns and adds a manual-asset form mirroring the existing manual-liability form.

**Tech Stack:** ASP.NET Core 10 / EF Core (SQL Server) backend, xunit + Moq + EF InMemory for backend tests. React + TypeScript + TanStack Query frontend — **no frontend test framework exists in this project today**; frontend tasks below are implementation + manual browser verification, not TDD. Introducing a frontend test framework is out of scope for this feature.

## Global Constraints

- Manual asset `Type` must be `RealEstate`, `Vehicle`, or `Other` — `Cash`/`Investment` are sync-only and rejected with `InvalidOperationException` → 400, same error-handling pattern already used by `PremiumAccessService`/`AuthService` (throw in the service, `catch (InvalidOperationException) → BadRequest` in the controller).
- Manual liability `Type` must not be `CreditCard` — that's sync-only, rejected the same way.
- `Name` fields: `[Required, MaxLength(120)]`. `Amount` fields: `[Range(0, double.MaxValue)]`. (Matches existing `CreateLiabilityRequest` validation exactly.)
- This project is pre-launch: the only migration on disk (`20260806231359_InitialMigration`) is still uncommitted (git status shows it as `??`). Per the established pattern from this session's earlier premium-unlock work, schema changes get hand-edited directly into that same migration + its `.Designer.cs` + `FinTrackDbContextModelSnapshot.cs`, rather than adding a new migration on top. **Do not run `dotnet ef migrations add`** — hand-edit, matching the file's existing structure exactly.
- Enum values serialize as their C# name in PascalCase (`JsonStringEnumConverter`, already configured in `Program.cs`); property names serialize camelCase (ASP.NET Core default) — e.g. the JSON property is `type`, and its value is the literal string `"RealEstate"`. Frontend TypeScript types must match both exactly.

---

## Task 1: `AssetType`/`LiabilityType` enums, `Asset` entity, `Liability.Type`

**Files:**
- Create: `src/FinTrackPrime.Models/Entities/AssetType.cs`
- Create: `src/FinTrackPrime.Models/Entities/LiabilityType.cs`
- Create: `src/FinTrackPrime.Models/Entities/Asset.cs`
- Modify: `src/FinTrackPrime.Models/Entities/Liability.cs`

**Interfaces:**
- Produces: `AssetType` enum (`Cash, Investment, RealEstate, Vehicle, Other`), `LiabilityType` enum (`CreditCard, Mortgage, AutoLoan, StudentLoan, PersonalLoan, Other`), `Asset` entity (`Id, UserId, User?, Name, Type, Amount`), `Liability.Type` property — all consumed by Tasks 2–7.

This is pure data-shape work (enums and POCOs have no behavior to unit test); verification is a successful build, not a test run.

- [ ] **Step 1: Create the two enums**

`src/FinTrackPrime.Models/Entities/AssetType.cs`:
```csharp
namespace FinTrackPrime.Models.Entities
{
    // Cash and Investment are system-assigned — they only ever come from
    // synced Accounts/InvestmentHoldings and are never offered as a
    // choice when a user manually adds an asset (see
    // FinancialStatementService.AddAssetAsync). RealEstate, Vehicle, and
    // Other are the only types a manual Asset row can have.
    public enum AssetType
    {
        Cash,
        Investment,
        RealEstate,
        Vehicle,
        Other,
    }
}
```

`src/FinTrackPrime.Models/Entities/LiabilityType.cs`:
```csharp
namespace FinTrackPrime.Models.Entities
{
    // CreditCard is system-assigned — it only ever comes from synced
    // CreditCard Accounts and is never offered as a choice when a user
    // manually adds a liability (see
    // FinancialStatementService.AddLiabilityAsync).
    public enum LiabilityType
    {
        CreditCard,
        Mortgage,
        AutoLoan,
        StudentLoan,
        PersonalLoan,
        Other,
    }
}
```

- [ ] **Step 2: Create the `Asset` entity**

`src/FinTrackPrime.Models/Entities/Asset.cs`:
```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    // Manually entered, same as Liability — there's no feed for real
    // estate, vehicles, or other personal property. Type is always
    // RealEstate, Vehicle, or Other; Cash/Investment assets come from
    // Accounts/InvestmentHoldings instead and never get a row here.
    public class Asset
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public AssetType Type { get; set; }
        public decimal Amount { get; set; }
    }
}
```

- [ ] **Step 3: Add `Type` to `Liability`**

Modify `src/FinTrackPrime.Models/Entities/Liability.cs` — full file becomes:
```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    // Manually entered, same as investment holdings. There's no
    // liabilities feed anywhere in this system; the user states what
    // they owe. Type is never CreditCard — that comes from a synced
    // CreditCard Account instead and never gets a row here.
    public class Liability
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public LiabilityType Type { get; set; }
        public decimal Amount { get; set; }
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds (nothing references `Liability.Type` yet, so no downstream break at this point — `FinTrackDbContext`'s `OnModelCreating` doesn't reference the new `Asset` type yet either, so it still compiles unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/AssetType.cs src/FinTrackPrime.Models/Entities/LiabilityType.cs src/FinTrackPrime.Models/Entities/Asset.cs src/FinTrackPrime.Models/Entities/Liability.cs
git commit -m "feat: add AssetType/LiabilityType enums, Asset entity, Liability.Type"
```

---

## Task 2: `FinTrackDbContext` — `DbSet<Asset>` and model config

**Files:**
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Consumes: `Asset`, `AssetType`, `Liability.Type` from Task 1.
- Produces: `FinTrackDbContext.Assets` (`DbSet<Asset>`), consumed by Task 6 (service) and Task 3 (migration must match this shape).

- [ ] **Step 1: Add the `DbSet`**

In `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`, add alongside the existing `PremiumPurchases` line:
```csharp
        public DbSet<PremiumPurchase> PremiumPurchases => Set<PremiumPurchase>();
        public DbSet<Asset> Assets => Set<Asset>();
```

- [ ] **Step 2: Add `OnModelCreating` config for `Asset`, mirroring the existing `Liability` block**

Add this block immediately after the existing `modelBuilder.Entity<Liability>(...)` block (find it — it's the one with `entity.Property(l => l.Name).HasMaxLength(120).IsRequired();`):
```csharp
            modelBuilder.Entity<Asset>(entity =>
            {
                entity.Property(a => a.Name).HasMaxLength(120).IsRequired();
                entity.Property(a => a.Amount).HasColumnType("decimal(18,2)");
                entity.HasOne(a => a.User)
                      .WithMany()
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

- [ ] **Step 3: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs
git commit -m "feat: register Asset entity with FinTrackDbContext"
```

---

## Task 3: Hand-edit the migration (add `Assets` table, add `Liabilities.Type` column)

**Files:**
- Modify: `src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.cs`
- Modify: `src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.Designer.cs`
- Modify: `src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: shape from Tasks 1–2.
- Produces: `Assets` table (`Id, UserId, Name, Type, Amount`, unique-per-row-not-required, FK cascade to `Users`, index on `UserId`) and `Liabilities.Type` column, both queryable by Task 6's service.

- [ ] **Step 1: Add the `Type` column to the existing `Liabilities` `CreateTable` block**

In `20260806231359_InitialMigration.cs`, find the `CreateTable(name: "Liabilities", ...)` block and change:
```csharp
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
```
to:
```csharp
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
```
(Only the `Liabilities` table — do not touch `PremiumPurchases`, `Accounts`, or any other `CreateTable` block.)

- [ ] **Step 2: Add a new `CreateTable` block for `Assets`, right after the `Liabilities` block**

```csharp
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
```

- [ ] **Step 3: Add the `Assets.UserId` index**

Add alongside the existing `IX_Liabilities_UserId` index (same block of `CreateIndex` calls, keep alphabetical order like the rest of the file — insert right before `IX_LinkedInstitutions_UserId`):
```csharp
            migrationBuilder.CreateIndex(
                name: "IX_Assets_UserId",
                table: "Assets",
                column: "UserId");
```

- [ ] **Step 4: Add `Assets` to `Down()`**

In the same file's `Down(MigrationBuilder migrationBuilder)` method, add `DropTable(name: "Assets")` alongside the existing `DropTable(name: "Liabilities")` — keep alphabetical order (Assets comes before BudgetCategories):
```csharp
            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "BudgetCategories");
```

- [ ] **Step 5: Update `20260806231359_InitialMigration.Designer.cs`**

Find `modelBuilder.Entity("FinTrackPrime.Models.Entities.Liability", b => {...})` (property block) and add a `Type` property, matching the pattern of every other `int`-backed enum property in this file:
```csharp
                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(120)
                        .HasColumnType("nvarchar(120)");

                    b.Property<int>("Type")
                        .HasColumnType("int");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uniqueidentifier");
```

Add a new entity block for `Asset` immediately after the `Liability` property block closes (`b.ToTable("Liabilities"); });`):
```csharp
            modelBuilder.Entity("FinTrackPrime.Models.Entities.Asset", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uniqueidentifier");

                    b.Property<decimal>("Amount")
                        .HasColumnType("decimal(18,2)");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(120)
                        .HasColumnType("nvarchar(120)");

                    b.Property<int>("Type")
                        .HasColumnType("int");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uniqueidentifier");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.ToTable("Assets");
                });
```

Then find the second `modelBuilder.Entity("FinTrackPrime.Models.Entities.Liability", b => { b.HasOne(...) ... })` block (the relationships section, further down the file) and add a matching `Asset` relationship block right after it closes:
```csharp
            modelBuilder.Entity("FinTrackPrime.Models.Entities.Asset", b =>
                {
                    b.HasOne("FinTrackPrime.Models.Entities.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });
```

- [ ] **Step 6: Apply the identical edit to `FinTrackDbContextModelSnapshot.cs`**

This file mirrors `.Designer.cs` exactly for this purpose — apply the same three edits (Liability's `Type` property, the new `Asset` property block, the new `Asset` relationship block) at the equivalent locations in `FinTrackDbContextModelSnapshot.cs`.

- [ ] **Step 7: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.cs src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.Designer.cs src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs
git commit -m "feat: add Assets table and Liabilities.Type column to InitialMigration"
```

---

## Task 4: `FinancialStatementViewModels.cs` — typed shapes, `OwnersEquity` rename

**Files:**
- Modify: `src/FinTrackPrime.Models/ViewModels/FinancialStatementViewModels.cs`

**Interfaces:**
- Consumes: `AssetType`, `LiabilityType` from Task 1.
- Produces: `AssetLineViewModel { Id?, Label, Type, Amount }`, `CreateAssetRequest { Name, Type, Amount }`, `LiabilityViewModel { Id, Name, Type, Amount }`, `CreateLiabilityRequest { Name, Type, Amount }`, `FinancialStatementViewModel { Assets, TotalAssets, Liabilities, TotalLiabilities, OwnersEquity, GeneratedAtUtc }` — all consumed by Task 5 (interface), Task 6 (service), Task 7 (controller).

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/FinTrackPrime.Models/ViewModels/FinancialStatementViewModels.cs`:
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

    // A simple personal balance sheet: everything owned, everything
    // owed, and the difference. Built entirely from data already in the
    // system (accounts, investment holdings) plus user-entered assets
    // and liabilities, not a separate ledger of its own.
    //
    // OwnersEquity is TotalAssets - TotalLiabilities — for an individual
    // or self-employed client this is the same number as "net worth";
    // "Owner's Equity" is just the accounting term for it, used here to
    // match the bank's requested Assets/Liabilities/Owner's-Equity
    // presentation.
    public class FinancialStatementViewModel
    {
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal OwnersEquity { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/FinancialStatementViewModels.cs
git commit -m "feat: type FinancialStatement view models, rename NetWorth to OwnersEquity"
```

---

## Task 5: `IFinancialStatementService` — add `AddAssetAsync`/`RemoveAssetAsync`

**Files:**
- Modify: `src/FinTrackPrime.Business/Interfaces/IFinancialStatementService.cs`

**Interfaces:**
- Consumes: view models from Task 4.
- Produces: interface signatures consumed by Task 6 (implementation) and Task 7 (controller).

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/FinTrackPrime.Business/Interfaces/IFinancialStatementService.cs`:
```csharp
using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IFinancialStatementService
    {
        // Assembled fresh on every call from Accounts, Investment
        // Holdings, and manually entered Assets/Liabilities; nothing
        // about the statement itself is stored.
        Task<FinancialStatementViewModel> GetStatementAsync(Guid userId);

        // Type must be RealEstate, Vehicle, or Other — throws
        // InvalidOperationException for Cash/Investment, which are
        // sync-only.
        Task<AssetLineViewModel> AddAssetAsync(Guid userId, CreateAssetRequest request);
        Task RemoveAssetAsync(Guid userId, Guid assetId);

        // Type must not be CreditCard — throws InvalidOperationException,
        // since that's sync-only.
        Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request);
        Task RemoveLiabilityAsync(Guid userId, Guid liabilityId);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Business/FinTrackPrime.Business.csproj`
Expected: **fails** — `FinancialStatementService` no longer satisfies `IFinancialStatementService` (missing `AddAssetAsync`/`RemoveAssetAsync`). This is expected; Task 6 fixes it.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/IFinancialStatementService.cs
git commit -m "feat: add AddAssetAsync/RemoveAssetAsync to IFinancialStatementService"
```

---

## Task 6: `FinancialStatementService` — typed grouping, manual assets, type validation

**Files:**
- Modify: `src/FinTrackPrime.Business/Services/FinancialStatementService.cs`
- Test: `tests/FinTrackPrime.Business.Tests/FinancialStatementServiceTests.cs` (new)

**Interfaces:**
- Consumes: `IFinancialStatementService` (Task 5), all view models (Task 4), `Asset`/`AssetType`/`LiabilityType` (Task 1), `FinTrackDbContext.Assets` (Task 2).
- Produces: full `IFinancialStatementService` implementation, consumed by Task 7 (controller, already wired via DI in `Program.cs` — no change needed there since it's already registered as `IFinancialStatementService → FinancialStatementService`).

This service has no existing test coverage (`tests/FinTrackPrime.Business.Tests/` has no `FinancialStatementServiceTests.cs` today) — this task adds coverage for the new/changed behavior only (typing, manual-asset add/remove, liability-type validation), not a full regression suite for the pre-existing untested logic (e.g. `Unsupported`-account exclusion).

### Part A: `GetStatementAsync` typing

- [ ] **Step 1: Write the failing tests**

Create `tests/FinTrackPrime.Business.Tests/FinancialStatementServiceTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class FinancialStatementServiceTests
    {
        private static FinTrackDbContext BuildDb()
        {
            var options = new DbContextOptionsBuilder<FinTrackDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new FinTrackDbContext(options);
        }

        private static async Task<Guid> SeedUserAsync(FinTrackDbContext db)
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = $"{userId}@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return userId;
        }

        [Fact]
        public async Task GetStatementAsync_TagsAccountAndHoldingLinesWithCashAndInvestmentTypes()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "Checking", Type = AccountType.Checking,
                Balance = 1000m, Currency = "USD", ExternalAccountId = "acc-1", Institution = "Test Bank", CreatedAtUtc = DateTime.UtcNow,
            });
            db.InvestmentHoldings.Add(new InvestmentHolding
            {
                Id = Guid.NewGuid(), UserId = userId, Symbol = "VTI", Name = "Vanguard Total Market",
                Shares = 10m, CostBasisPerShare = 200m, CurrentPricePerShare = 220m,
            });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            Assert.Contains(statement.Assets, a => a.Label == "Checking" && a.Type == AssetType.Cash && a.Amount == 1000m);
            Assert.Contains(statement.Assets, a => a.Label == "VTI holding" && a.Type == AssetType.Investment && a.Amount == 2200m);
        }

        [Fact]
        public async Task GetStatementAsync_IncludesManualAssetsWithTheirOwnTypeAndId()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            var assetId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = assetId, UserId = userId, Name = "Home", Type = AssetType.RealEstate, Amount = 350000m });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            var line = Assert.Single(statement.Assets);
            Assert.Equal(assetId, line.Id);
            Assert.Equal("Home", line.Label);
            Assert.Equal(AssetType.RealEstate, line.Type);
            Assert.Equal(350000m, line.Amount);
        }

        [Fact]
        public async Task GetStatementAsync_TagsCreditCardAccountsAndManualLiabilitiesWithTheirType()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "Visa", Type = AccountType.CreditCard,
                Balance = -500m, Currency = "USD", ExternalAccountId = "acc-2", Institution = "Test Bank", CreatedAtUtc = DateTime.UtcNow,
            });
            db.Liabilities.Add(new Liability { Id = Guid.NewGuid(), UserId = userId, Name = "House loan", Type = LiabilityType.Mortgage, Amount = 200000m });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            Assert.Contains(statement.Liabilities, l => l.Name == "Visa" && l.Type == LiabilityType.CreditCard && l.Amount == 500m);
            Assert.Contains(statement.Liabilities, l => l.Name == "House loan" && l.Type == LiabilityType.Mortgage && l.Amount == 200000m);
        }

        [Fact]
        public async Task GetStatementAsync_ComputesOwnersEquityAsAssetsMinusLiabilities()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Assets.Add(new Asset { Id = Guid.NewGuid(), UserId = userId, Name = "Car", Type = AssetType.Vehicle, Amount = 15000m });
            db.Liabilities.Add(new Liability { Id = Guid.NewGuid(), UserId = userId, Name = "Auto loan", Type = LiabilityType.AutoLoan, Amount = 9000m });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            var statement = await service.GetStatementAsync(userId);

            Assert.Equal(15000m, statement.TotalAssets);
            Assert.Equal(9000m, statement.TotalLiabilities);
            Assert.Equal(6000m, statement.OwnersEquity);
        }
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter FinancialStatementServiceTests`
Expected: does not build (`FinancialStatementService` doesn't implement `AddAssetAsync`/`RemoveAssetAsync` yet — same failure as Task 5 Step 2, now surfaced by the test project).

- [ ] **Step 3: Implement `GetStatementAsync`**

In `src/FinTrackPrime.Business/Services/FinancialStatementService.cs`, replace the `GetStatementAsync` method body:
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

            // Unsupported accounts (see AccountType.Unsupported — Bitcoin,
            // FX wallets, etc.) are excluded here rather than counted as an
            // asset: their Balance is in whatever unit Finverse reported
            // (BTC, USD, ...), and this app has no conversion rate to turn
            // that into a number that's safe to add alongside HKD accounts.
            var nonCreditAccounts = accounts
                .Where(a => a.Type != AccountType.CreditCard && a.Type != AccountType.Unsupported)
                .ToList();

            var assets = nonCreditAccounts
                .Select(a => new AssetLineViewModel { Id = null, Label = a.Nickname, Type = AssetType.Cash, Amount = a.Balance })
                .Concat(holdings.Select(h => new AssetLineViewModel
                {
                    Id = null,
                    Label = $"{h.Symbol} holding",
                    Type = AssetType.Investment,
                    Amount = h.Shares * h.CurrentPricePerShare,
                }))
                .Concat(manualAssets.Select(a => new AssetLineViewModel
                {
                    Id = a.Id,
                    Label = a.Name,
                    Type = a.Type,
                    Amount = a.Amount,
                }))
                .OrderByDescending(a => a.Amount)
                .ToList();

            var totalAssets = assets.Sum(a => a.Amount);

            var allLiabilities = liabilities
                .Select(l => new LiabilityViewModel { Id = l.Id, Name = l.Name, Type = l.Type, Amount = l.Amount })
                .Concat(creditCardAccounts.Select(a => new LiabilityViewModel
                {
                    Id = a.Id,
                    Name = a.Nickname,
                    Type = LiabilityType.CreditCard,
                    Amount = Math.Abs(a.Balance),
                }))
                .OrderByDescending(l => l.Amount)
                .ToList();

            var totalLiabilities = allLiabilities.Sum(l => l.Amount);

            return new FinancialStatementViewModel
            {
                Assets = assets,
                TotalAssets = totalAssets,
                Liabilities = allLiabilities,
                TotalLiabilities = totalLiabilities,
                OwnersEquity = totalAssets - totalLiabilities,
                GeneratedAtUtc = DateTime.UtcNow,
            };
        }
```
(This drops the old `.OrderByDescending(...)` calls on the raw `liabilities`/`manualAssets` queries before mapping — ordering now happens once, after mapping, same as `assets` already did. No behavior change to the final order.)

Note: this method alone won't make the project buildable yet — `AddAssetAsync`/`RemoveAssetAsync` still don't exist (Part B). Steps 4 below run only after Part B is also done.

### Part B: `AddAssetAsync` / `RemoveAssetAsync`

- [ ] **Step 4: Add tests for the new methods, in the same test file**

Append to `FinancialStatementServiceTests`:
```csharp
        [Theory]
        [InlineData(AssetType.Cash)]
        [InlineData(AssetType.Investment)]
        public async Task AddAssetAsync_RejectsSyncOnlyTypes(AssetType syncOnlyType)
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var service = new FinancialStatementService(db);

            var request = new CreateAssetRequest { Name = "Something", Type = syncOnlyType, Amount = 100m };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAssetAsync(userId, request));
        }

        [Fact]
        public async Task AddAssetAsync_PersistsAndReturnsTheNewAsset()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var service = new FinancialStatementService(db);

            var result = await service.AddAssetAsync(userId, new CreateAssetRequest { Name = "Home", Type = AssetType.RealEstate, Amount = 350000m });

            Assert.NotNull(result.Id);
            Assert.Equal("Home", result.Label);
            Assert.Equal(AssetType.RealEstate, result.Type);
            Assert.Equal(350000m, result.Amount);
            Assert.Equal(1, await db.Assets.CountAsync(a => a.UserId == userId));
        }

        [Fact]
        public async Task RemoveAssetAsync_DeletesTheAsset()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var assetId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = assetId, UserId = userId, Name = "Car", Type = AssetType.Vehicle, Amount = 15000m });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);
            await service.RemoveAssetAsync(userId, assetId);

            Assert.Equal(0, await db.Assets.CountAsync());
        }

        [Fact]
        public async Task RemoveAssetAsync_ThrowsWhenAssetBelongsToAnotherUser()
        {
            await using var db = BuildDb();
            var owner = await SeedUserAsync(db);
            var otherUser = await SeedUserAsync(db);
            var assetId = Guid.NewGuid();
            db.Assets.Add(new Asset { Id = assetId, UserId = owner, Name = "Car", Type = AssetType.Vehicle, Amount = 15000m });
            await db.SaveChangesAsync();

            var service = new FinancialStatementService(db);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAssetAsync(otherUser, assetId));
        }
```

- [ ] **Step 5: Implement `AddAssetAsync` and `RemoveAssetAsync`**

Add these methods to `FinancialStatementService`, right after `GetStatementAsync`:
```csharp
        public async Task<AssetLineViewModel> AddAssetAsync(Guid userId, CreateAssetRequest request)
        {
            if (request.Type is AssetType.Cash or AssetType.Investment)
            {
                throw new InvalidOperationException(
                    "Type must be RealEstate, Vehicle, or Other — Cash and Investment assets come from linked accounts.");
            }

            var asset = new Asset
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = request.Name.Trim(),
                Type = request.Type,
                Amount = request.Amount,
            };

            _db.Assets.Add(asset);
            await _db.SaveChangesAsync();

            return new AssetLineViewModel { Id = asset.Id, Label = asset.Name, Type = asset.Type, Amount = asset.Amount };
        }

        public async Task RemoveAssetAsync(Guid userId, Guid assetId)
        {
            var asset = await _db.Assets
                .FirstOrDefaultAsync(a => a.Id == assetId && a.UserId == userId);

            if (asset is null)
            {
                throw new InvalidOperationException("Asset not found.");
            }

            _db.Assets.Remove(asset);
            await _db.SaveChangesAsync();
        }
```

### Part C: `AddLiabilityAsync` type validation

- [ ] **Step 6: Add tests for liability type handling**

Append to `FinancialStatementServiceTests`:
```csharp
        [Fact]
        public async Task AddLiabilityAsync_RejectsCreditCardType()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var service = new FinancialStatementService(db);

            var request = new CreateLiabilityRequest { Name = "Something", Type = LiabilityType.CreditCard, Amount = 100m };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddLiabilityAsync(userId, request));
        }

        [Fact]
        public async Task AddLiabilityAsync_PersistsTheChosenType()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var service = new FinancialStatementService(db);

            var result = await service.AddLiabilityAsync(userId, new CreateLiabilityRequest { Name = "Student loan", Type = LiabilityType.StudentLoan, Amount = 12000m });

            Assert.Equal(LiabilityType.StudentLoan, result.Type);
            var stored = await db.Liabilities.SingleAsync(l => l.Id == result.Id);
            Assert.Equal(LiabilityType.StudentLoan, stored.Type);
        }
```

- [ ] **Step 7: Update `AddLiabilityAsync`**

Replace the existing `AddLiabilityAsync` method body:
```csharp
        public async Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request)
        {
            if (request.Type == LiabilityType.CreditCard)
            {
                throw new InvalidOperationException(
                    "Type must not be CreditCard — credit card liabilities come from linked accounts.");
            }

            var liability = new Liability
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = request.Name.Trim(),
                Type = request.Type,
                Amount = request.Amount,
            };

            _db.Liabilities.Add(liability);
            await _db.SaveChangesAsync();

            return new LiabilityViewModel { Id = liability.Id, Name = liability.Name, Type = liability.Type, Amount = liability.Amount };
        }
```
(`RemoveLiabilityAsync` is unchanged.)

- [ ] **Step 8: Run all the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter FinancialStatementServiceTests`
Expected: PASS (11 tests: 4 from Part A, 4 from Part B, 2 from Part C — wait, count: Part A has 4 `[Fact]`s, Part B has 1 `[Theory]` with 2 cases + 2 `[Fact]`s = 4 test executions, Part C has 2 `[Fact]`s. Total 10 test executions across 9 test methods.)

- [ ] **Step 9: Run the full test project to confirm nothing else broke**

Run: `dotnet test tests/FinTrackPrime.Business.Tests`
Expected: PASS (all tests, including pre-existing `BankLinkServiceTests`, `FinverseClientTests`, `SmokeTests`).

- [ ] **Step 10: Commit**

```bash
git add src/FinTrackPrime.Business/Services/FinancialStatementService.cs tests/FinTrackPrime.Business.Tests/FinancialStatementServiceTests.cs
git commit -m "feat: type FinancialStatementService lines, add manual asset support"
```

---

## Task 7: `FinancialStatementController` — asset endpoints, error handling

**Files:**
- Modify: `src/FinTrackPrime.WebApi/Controllers/FinancialStatementController.cs`

**Interfaces:**
- Consumes: `IFinancialStatementService.AddAssetAsync`/`RemoveAssetAsync` (Task 6), `CreateAssetRequest`/`AssetLineViewModel` (Task 4).
- Produces: `POST /api/financial-statement/assets`, `DELETE /api/financial-statement/assets/{assetId}` — consumed by Task 9 (frontend API client).

No controller-level tests exist anywhere in this project today (only the Business layer is tested) — this task's verification is a build plus a manual smoke check via Swagger, matching that existing convention.

- [ ] **Step 1: Rewrite the controller**

Full replacement for `src/FinTrackPrime.WebApi/Controllers/FinancialStatementController.cs`:
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
    [Route("api/financial-statement")]
    [Authorize(Policy = "RequirePremium")]
    public class FinancialStatementController : ControllerBase
    {
        private readonly IFinancialStatementService _financialStatementService;

        public FinancialStatementController(IFinancialStatementService financialStatementService)
        {
            _financialStatementService = financialStatementService;
        }

        [HttpGet]
        public async Task<ActionResult<FinancialStatementViewModel>> Get()
        {
            var statement = await _financialStatementService.GetStatementAsync(GetUserId());
            return Ok(statement);
        }

        [HttpPost("assets")]
        public async Task<ActionResult<AssetLineViewModel>> AddAsset(CreateAssetRequest request)
        {
            try
            {
                var asset = await _financialStatementService.AddAssetAsync(GetUserId(), request);
                return Ok(asset);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("assets/{assetId:guid}")]
        public async Task<IActionResult> RemoveAsset(Guid assetId)
        {
            try
            {
                await _financialStatementService.RemoveAssetAsync(GetUserId(), assetId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPost("liabilities")]
        public async Task<ActionResult<LiabilityViewModel>> AddLiability(CreateLiabilityRequest request)
        {
            try
            {
                var liability = await _financialStatementService.AddLiabilityAsync(GetUserId(), request);
                return Ok(liability);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("liabilities/{liabilityId:guid}")]
        public async Task<IActionResult> RemoveLiability(Guid liabilityId)
        {
            try
            {
                await _financialStatementService.RemoveLiabilityAsync(GetUserId(), liabilityId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
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
(`AddLiability` now also wraps in try/catch — it didn't before Task 6 added the CreditCard-type rejection, and a validation failure there needs to reach the client as a 400, not an unhandled 500.)

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build FinTrackPrime.sln`
Expected: succeeds.

- [ ] **Step 3: Run the full backend test suite**

Run: `dotnet test`
Expected: PASS, all projects.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.WebApi/Controllers/FinancialStatementController.cs
git commit -m "feat: add asset endpoints to FinancialStatementController"
```

---

## Task 8: Frontend types (`types/api.ts`)

**Files:**
- Modify: `src/types/api.ts` (in `C:\Users\Wyrlo\projects\FinTrackPrime`)

**Interfaces:**
- Produces: `AssetType`, `LiabilityType`, updated `AssetLineViewModel`/`CreateAssetRequest`/`LiabilityViewModel`/`CreateLiabilityRequest`/`FinancialStatementViewModel` — consumed by Task 9 (API client) and Task 10 (page).

- [ ] **Step 1: Replace the Financial Statement type block**

In `src/types/api.ts`, find and replace:
```ts
export interface LiabilityViewModel {
  id: string
  name: string
  amount: number
}

export interface CreateLiabilityRequest {
  name: string
  amount: number
}

export interface AssetLineViewModel {
  label: string
  amount: number
}

export interface FinancialStatementViewModel {
  assets: AssetLineViewModel[]
  totalAssets: number
  liabilities: LiabilityViewModel[]
  totalLiabilities: number
  netWorth: number
  generatedAtUtc: string
}
```
with:
```ts
// Cash and Investment (assets) / CreditCard (liabilities) are
// system-assigned — they only ever come from synced accounts/holdings
// and are never offered as a choice in the "Add an asset"/"Add a
// liability" forms. See MANUAL_ASSET_TYPE_OPTIONS /
// MANUAL_LIABILITY_TYPE_OPTIONS in FinancialStatementPage.tsx.
export type AssetType = 'Cash' | 'Investment' | 'RealEstate' | 'Vehicle' | 'Other'
export type LiabilityType = 'CreditCard' | 'Mortgage' | 'AutoLoan' | 'StudentLoan' | 'PersonalLoan' | 'Other'

export interface LiabilityViewModel {
  id: string
  name: string
  type: LiabilityType
  amount: number
}

export interface CreateLiabilityRequest {
  name: string
  type: LiabilityType
  amount: number
}

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

- [ ] **Step 2: Verify no other file still references the old shape**

Run: `grep -rn "netWorth\|\.assets\[0\]\.label" src` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: no matches outside `FinancialStatementPage.tsx` (which Task 10 rewrites) — `netWorth` was only ever consumed there.

- [ ] **Step 3: Commit**

```bash
git add src/types/api.ts
git commit -m "feat: type Financial Statement API shapes, rename netWorth to ownersEquity"
```

---

## Task 9: Frontend API client (`api/financialStatement.ts`)

**Files:**
- Modify: `src/api/financialStatement.ts`

**Interfaces:**
- Consumes: types from Task 8.
- Produces: `financialStatementApi.addAsset`, `financialStatementApi.removeAsset` — consumed by Task 10.

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/api/financialStatement.ts`:
```ts
import { apiClient } from './client'
import type {
  CreateAssetRequest,
  CreateLiabilityRequest,
  AssetLineViewModel,
  FinancialStatementViewModel,
  LiabilityViewModel,
} from '../types/api'

export const financialStatementApi = {
  get: async (): Promise<FinancialStatementViewModel> => {
    const { data } = await apiClient.get<FinancialStatementViewModel>('/api/financial-statement')
    return data
  },
  addAsset: async (request: CreateAssetRequest): Promise<AssetLineViewModel> => {
    const { data } = await apiClient.post<AssetLineViewModel>('/api/financial-statement/assets', request)
    return data
  },
  removeAsset: async (assetId: string): Promise<void> => {
    await apiClient.delete(`/api/financial-statement/assets/${assetId}`)
  },
  addLiability: async (request: CreateLiabilityRequest): Promise<LiabilityViewModel> => {
    const { data } = await apiClient.post<LiabilityViewModel>(
      '/api/financial-statement/liabilities',
      request,
    )
    return data
  },
  removeLiability: async (liabilityId: string): Promise<void> => {
    await apiClient.delete(`/api/financial-statement/liabilities/${liabilityId}`)
  },
}
```

- [ ] **Step 2: Commit**

```bash
git add src/api/financialStatement.ts
git commit -m "feat: add addAsset/removeAsset to financialStatementApi"
```

---

## Task 10: `FinancialStatementPage.tsx` — 3-column layout, grouping, manual-asset form

**Files:**
- Modify: `src/pages/FinancialStatementPage.tsx`

**Interfaces:**
- Consumes: types (Task 8), `financialStatementApi` (Task 9), existing UI components (`Card`, `CardHeader`, `Input`, `Select`, `Button`, `IconButton`, `StatCard`, `Table`, `SkeletonCard` from `src/components/ui/`), `useDecimalInput` hook (unchanged).
- Produces: the rendered page — this is the last task, nothing downstream depends on it.

No frontend test framework exists — verification here is `npm run build` (catches TypeScript errors) plus a manual check in the browser (steps below).

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/pages/FinancialStatementPage.tsx`:
```tsx
import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { Plus, X } from 'lucide-react'
import { financialStatementApi } from '../api/financialStatement'
import { useDecimalInput } from '../hooks/useDecimalInput'
import type { AssetLineViewModel, AssetType, LiabilityType, LiabilityViewModel } from '../types/api'
import { Card, CardHeader } from '../components/ui/Card'
import { Input } from '../components/ui/Input'
import { Select, type SelectOption } from '../components/ui/Select'
import { Button } from '../components/ui/Button'
import { IconButton } from '../components/ui/IconButton'
import { StatCard } from '../components/ui/StatCard'
import { Table, type TableColumn } from '../components/ui/Table'
import { SkeletonCard } from '../components/ui/Skeleton'

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount)
}

// Display order + label for each Type's group heading. Cash/Investment/
// CreditCard groups only ever contain synced lines; the rest only ever
// contain manual ones.
const ASSET_TYPE_ORDER: { type: AssetType; label: string }[] = [
  { type: 'Cash', label: 'Cash & Bank Accounts' },
  { type: 'Investment', label: 'Investments' },
  { type: 'RealEstate', label: 'Real Estate' },
  { type: 'Vehicle', label: 'Vehicles' },
  { type: 'Other', label: 'Other Assets' },
]

const LIABILITY_TYPE_ORDER: { type: LiabilityType; label: string }[] = [
  { type: 'CreditCard', label: 'Credit Cards' },
  { type: 'Mortgage', label: 'Mortgages' },
  { type: 'AutoLoan', label: 'Auto Loans' },
  { type: 'StudentLoan', label: 'Student Loans' },
  { type: 'PersonalLoan', label: 'Personal Loans' },
  { type: 'Other', label: 'Other Liabilities' },
]

// Cash/Investment excluded — those come from linked accounts, never a
// user-picked value in the "Add an asset" form.
const MANUAL_ASSET_TYPE_OPTIONS: SelectOption[] = [
  { value: 'RealEstate', label: 'Real Estate' },
  { value: 'Vehicle', label: 'Vehicle' },
  { value: 'Other', label: 'Other' },
]

// CreditCard excluded — that comes from a linked account, never a
// user-picked value in the "Add a liability" form.
const MANUAL_LIABILITY_TYPE_OPTIONS: SelectOption[] = [
  { value: 'Mortgage', label: 'Mortgage' },
  { value: 'AutoLoan', label: 'Auto Loan' },
  { value: 'StudentLoan', label: 'Student Loan' },
  { value: 'PersonalLoan', label: 'Personal Loan' },
  { value: 'Other', label: 'Other' },
]

export function FinancialStatementPage() {
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({
    queryKey: ['financial-statement'],
    queryFn: financialStatementApi.get,
  })

  const [newAssetName, setNewAssetName] = useState('')
  const [newAssetType, setNewAssetType] = useState<AssetType>('RealEstate')
  const [newAssetAmount, setNewAssetAmount] = useState(0)
  const assetAmountInput = useDecimalInput({ value: newAssetAmount, onChange: setNewAssetAmount, decimals: 2 })

  const [newLiabilityName, setNewLiabilityName] = useState('')
  const [newLiabilityType, setNewLiabilityType] = useState<LiabilityType>('Mortgage')
  const [newLiabilityAmount, setNewLiabilityAmount] = useState(0)
  const liabilityAmountInput = useDecimalInput({ value: newLiabilityAmount, onChange: setNewLiabilityAmount, decimals: 2 })

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['financial-statement'] })

  const addAssetMutation = useMutation({
    mutationFn: financialStatementApi.addAsset,
    onSuccess: () => {
      setNewAssetName('')
      setNewAssetAmount(0)
      invalidate()
    },
  })

  const removeAssetMutation = useMutation({
    mutationFn: financialStatementApi.removeAsset,
    onSuccess: invalidate,
  })

  const addLiabilityMutation = useMutation({
    mutationFn: financialStatementApi.addLiability,
    onSuccess: () => {
      setNewLiabilityName('')
      setNewLiabilityAmount(0)
      invalidate()
    },
  })

  const removeLiabilityMutation = useMutation({
    mutationFn: financialStatementApi.removeLiability,
    onSuccess: invalidate,
  })

  const handleAddAsset = () => {
    if (!newAssetName.trim() || newAssetAmount <= 0) return
    addAssetMutation.mutate({ name: newAssetName.trim(), type: newAssetType, amount: newAssetAmount })
  }

  const handleAddLiability = () => {
    if (!newLiabilityName.trim() || newLiabilityAmount <= 0) return
    addLiabilityMutation.mutate({ name: newLiabilityName.trim(), type: newLiabilityType, amount: newLiabilityAmount })
  }

  if (isLoading || !data) {
    return (
      <div>
        <div className="h-8 w-56 animate-pulse rounded bg-surface-sunken" />
        <div className="mt-6 grid gap-5 lg:grid-cols-3">
          <SkeletonCard />
          <SkeletonCard />
          <SkeletonCard />
        </div>
      </div>
    )
  }

  const summaryChartData = [
    { name: 'Assets', amount: data.totalAssets },
    { name: 'Liabilities', amount: data.totalLiabilities },
    { name: "Owner's Equity", amount: data.ownersEquity },
  ]

  const assetColumns: TableColumn<AssetLineViewModel>[] = [
    { key: 'label', header: 'Asset', priority: 'high', render: (a) => a.label },
    {
      key: 'amount',
      header: 'Amount',
      priority: 'high',
      align: 'right',
      render: (a) => <span className="tabular-figure font-medium">{formatCurrency(a.amount)}</span>,
    },
    {
      key: 'remove',
      header: '',
      priority: 'high',
      align: 'right',
      render: (a) =>
        a.id && (
          <IconButton
            icon={<X className="h-3.5 w-3.5" />}
            label={`Remove ${a.label}`}
            variant="ghost"
            size="sm"
            onClick={() => removeAssetMutation.mutate(a.id!)}
            className="text-text-muted hover:text-status-critical"
          />
        ),
    },
  ]

  const liabilityColumns: TableColumn<LiabilityViewModel>[] = [
    { key: 'name', header: 'Liability', priority: 'high', render: (l) => l.name },
    {
      key: 'amount',
      header: 'Amount',
      priority: 'high',
      align: 'right',
      render: (l) => <span className="tabular-figure font-medium">{formatCurrency(l.amount)}</span>,
    },
    {
      key: 'remove',
      header: '',
      priority: 'high',
      align: 'right',
      render: (l) =>
        l.type !== 'CreditCard' && (
          <IconButton
            icon={<X className="h-3.5 w-3.5" />}
            label={`Remove ${l.name}`}
            variant="ghost"
            size="sm"
            onClick={() => removeLiabilityMutation.mutate(l.id)}
            className="text-text-muted hover:text-status-critical"
          />
        ),
    },
  ]

  const assetGroups = ASSET_TYPE_ORDER.map(({ type, label }) => ({
    type,
    label,
    lines: data.assets.filter((a) => a.type === type),
  })).filter((group) => group.lines.length > 0)

  const liabilityGroups = LIABILITY_TYPE_ORDER.map(({ type, label }) => ({
    type,
    label,
    lines: data.liabilities.filter((l) => l.type === type),
  })).filter((group) => group.lines.length > 0)

  return (
    <div>
      <CardHeader
        title="Financial Statement"
        description="Assets come from your accounts, investment holdings, and anything you add manually below. Liabilities are entered manually, alongside any linked credit cards."
      />

      <div className="mt-5 grid gap-5 lg:grid-cols-3">
        <div>
          <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">
            Assets — {formatCurrency(data.totalAssets)}
          </h2>

          {assetGroups.length === 0 && <Table columns={assetColumns} data={[]} keyExtractor={(a) => a.id ?? a.label} emptyMessage="No assets on file." />}

          {assetGroups.map((group) => (
            <div key={group.type} className="mb-4">
              <p className="mb-1.5 flex items-baseline justify-between text-sm font-medium text-text-secondary">
                <span>{group.label}</span>
                <span className="tabular-figure">{formatCurrency(group.lines.reduce((sum, a) => sum + a.amount, 0))}</span>
              </p>
              <Table columns={assetColumns} data={group.lines} keyExtractor={(a) => a.id ?? a.label} />
            </div>
          ))}

          <Card className="mt-3">
            <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Add an asset</p>
            <div className="flex flex-col gap-3">
              <Input label="Name" value={newAssetName} onChange={(e) => setNewAssetName(e.target.value)} placeholder="Home" />
              <Select
                label="Type"
                options={MANUAL_ASSET_TYPE_OPTIONS}
                value={newAssetType}
                onValueChange={(value) => setNewAssetType(value as AssetType)}
              />
              <Input label="Amount" variant="currency" {...assetAmountInput} />
              <Button leadingIcon={<Plus className="h-4 w-4" />} onClick={handleAddAsset} isLoading={addAssetMutation.isPending}>
                Add asset
              </Button>
            </div>
          </Card>
        </div>

        <div>
          <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">
            Liabilities — {formatCurrency(data.totalLiabilities)}
          </h2>

          {liabilityGroups.length === 0 && <Table columns={liabilityColumns} data={[]} keyExtractor={(l) => l.id} emptyMessage="No liabilities on file." />}

          {liabilityGroups.map((group) => (
            <div key={group.type} className="mb-4">
              <p className="mb-1.5 flex items-baseline justify-between text-sm font-medium text-text-secondary">
                <span>{group.label}</span>
                <span className="tabular-figure">{formatCurrency(group.lines.reduce((sum, l) => sum + l.amount, 0))}</span>
              </p>
              <Table columns={liabilityColumns} data={group.lines} keyExtractor={(l) => l.id} />
            </div>
          ))}

          <Card className="mt-3">
            <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Add a liability</p>
            <div className="flex flex-col gap-3">
              <Input label="Name" value={newLiabilityName} onChange={(e) => setNewLiabilityName(e.target.value)} placeholder="Auto loan" />
              <Select
                label="Type"
                options={MANUAL_LIABILITY_TYPE_OPTIONS}
                value={newLiabilityType}
                onValueChange={(value) => setNewLiabilityType(value as LiabilityType)}
              />
              <Input label="Amount" variant="currency" {...liabilityAmountInput} />
              <Button leadingIcon={<Plus className="h-4 w-4" />} onClick={handleAddLiability} isLoading={addLiabilityMutation.isPending}>
                Add liability
              </Button>
            </div>
          </Card>
        </div>

        <div>
          <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Owner's Equity</h2>
          <StatCard label="Assets − Liabilities" value={formatCurrency(data.ownersEquity)} className="text-center" />
          <p className="mt-3 text-xs text-text-muted">
            For an individual or self-employed account, Owner's Equity is what's left after everything owed is subtracted from everything owned — the
            same figure this statement used to call "Net Worth."
          </p>
        </div>
      </div>

      <Card className="mt-5">
        <h2 className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Assets vs. liabilities vs. owner's equity</h2>
        <div className="mt-3 h-40">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={summaryChartData} layout="vertical" margin={{ left: 16 }}>
              <CartesianGrid strokeDasharray="3 3" horizontal={false} stroke="var(--color-border)" />
              <XAxis type="number" hide />
              <YAxis type="category" dataKey="name" width={90} tick={{ fontSize: 12, fill: 'var(--color-text-secondary)' }} />
              <Tooltip formatter={(value) => formatCurrency(Number(value))} />
              <Bar dataKey="amount" radius={[0, 4, 4, 0]}>
                <Cell fill="var(--color-chart-diverging-positive)" />
                <Cell fill="var(--color-chart-diverging-negative)" />
                <Cell fill="var(--color-ft-gold)" />
              </Bar>
            </BarChart>
          </ResponsiveContainer>
        </div>
      </Card>
    </div>
  )
}
```

- [ ] **Step 2: Build**

Run: `npm run build` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: succeeds, no TypeScript errors.

- [ ] **Step 3: Manual verification**

Run: `npm run dev`, sign in as a premium-unlocked test user, navigate to Financial Statement. Confirm:
- Assets column groups by type (Cash/Investment always present if accounts/holdings exist; Real Estate/Vehicles/Other only appear once added).
- "Add an asset" creates a `RealEstate`/`Vehicle`/`Other` row, appears under the right group heading with a working ✕ remove button.
- "Add a liability" now has a Type picker; a synced credit-card liability row has no ✕ (not removable), a manually-added one does.
- Owner's Equity card shows `totalAssets − totalLiabilities`.
- The bottom bar chart shows three bars: Assets, Liabilities, Owner's Equity.

- [ ] **Step 4: Commit**

```bash
git add src/pages/FinancialStatementPage.tsx
git commit -m "feat: 3-column Assets/Liabilities/Owner's Equity layout with manual asset entry"
```

---

## Self-Review

**Spec coverage:**
- Typed Assets (Cash/Investment/RealEstate/Vehicle/Other) → Tasks 1, 4, 6, 8, 10. ✓
- Typed Liabilities (CreditCard/Mortgage/AutoLoan/StudentLoan/PersonalLoan/Other) → Tasks 1, 4, 6, 8, 10. ✓
- Manual asset add/remove → Tasks 1–3 (data model), 5–7 (backend), 9–10 (frontend). ✓
- Manual liability gains a `Type` picker → Tasks 4, 6, 10. ✓
- Type-grouped subtotals in the UI → Task 10. ✓
- `OwnersEquity` replacing `NetWorth` → Tasks 4, 6, 8, 10. ✓
- Sync-only types rejected on manual add (`Cash`/`Investment`/`CreditCard`) → Task 6 (service-level `InvalidOperationException`), Task 7 (400 response), Task 10 (`<select>` never offers them). ✓
- Explicitly out of scope per the spec (double-entry, corporate equity accounts, `Unsupported`/crypto accounts) → untouched by every task above. ✓

**Placeholder scan:** no "TBD"/"add appropriate handling"/"similar to Task N" — every step has literal code or an exact command.

**Type consistency check:** `AssetType`/`LiabilityType` enum member names match exactly across Task 1 (C# enum), Task 4 (view models), Task 6 (test assertions + service logic), Task 8 (TS union types), Task 10 (`ASSET_TYPE_ORDER`/`LIABILITY_TYPE_ORDER`/`MANUAL_*_OPTIONS` arrays). `FinancialStatementViewModel.OwnersEquity` (C#) / `ownersEquity` (TS) used consistently, no lingering `NetWorth`/`netWorth` reference anywhere in the plan.
