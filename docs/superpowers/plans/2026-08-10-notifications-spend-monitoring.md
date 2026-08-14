# Notifications: Infrastructure, SignalR Delivery, Background Spend Monitoring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first real notification system for this app — a persistent `Notification` record, SignalR-based real-time delivery, and a recurring background job that re-syncs every linked user and flags unusually large spend — replacing today's dead, hardcoded-empty bell in `TopNav.tsx`.

**Architecture:** `FinTrackPrime.Business` gets a new `INotificationService` (pure data/business logic: list, mark-read, and the flat-threshold spend-detection rule — no SignalR or hosting types, keeping it unit-testable the same way every other service in this app is). `FinTrackPrime.WebApi` (the only project with the ASP.NET Core framework reference needed for SignalR/hosting) gets a `NotificationsHub` and a `SpendMonitorService : BackgroundService` that orchestrates: re-sync via the existing `IBankLinkService`, ask `INotificationService` to create any new spend notifications, then push each one to that user's SignalR group. The frontend gets one `HubConnection` per session (JWT passed via SignalR's `accessTokenFactory`, since a WebSocket handshake can't carry an `Authorization` header) and wires the already-existing (currently empty) notification bell to real data.

**Tech Stack:** ASP.NET Core 10 (SignalR ships in the shared framework already referenced by `FinTrackPrime.WebApi`'s `Microsoft.NET.Sdk.Web` — no new backend NuGet package needed) / EF Core (SQL Server), xunit + EF InMemory for backend tests. React + TypeScript + TanStack Query + `@microsoft/signalr` (new frontend dependency) — **no frontend test framework exists in this project**; frontend tasks are implementation + manual browser verification, matching the precedent set by the two most recent features in this app.

## Global Constraints

- `Notification.RelatedTransactionId` has a **database-enforced** filtered unique index (SQL Server `WHERE [RelatedTransactionId] IS NOT NULL`, same pattern as `User.GoogleId`) — this is what guarantees a transaction never gets two spend notifications, not just an application-level check.
- Every `NotificationsController`/`INotificationService` read/write is scoped to the caller's own `UserId` from the JWT — same ownership-check pattern already used by `FinancialStatementController`/`CheckoutController` etc.
- `SpendMonitorService` isolates each user's work in its own try/catch (one user's Finverse failure must not stop the job for the rest) — same isolation principle `BankLinkService.SyncAsync` already applies per-institution.
- This project is pre-launch: the only migration on disk (`20260806231359_InitialMigration`) is still uncommitted. Per the established pattern from earlier this session, schema changes get hand-edited directly into that same migration + its `.Designer.cs` + `FinTrackDbContextModelSnapshot.cs`. **Do not run `dotnet ef migrations add`.**
- `SpendMonitoring:FlatThresholdUsd` ($1,000) and `SpendMonitoring:IncomePercentThreshold` (50%) and `SpendMonitoring:IntervalHours` (4) are illustrative placeholders per the spec's open questions — implement them as config values (so they're a config change, not a code change, to update later), not hardcoded numbers.
- Enum values serialize as their C# name in PascalCase (`JsonStringEnumConverter`, already configured); property names serialize camelCase (ASP.NET Core default).

---

## Task 1: `Notification` entity, `NotificationType` enum, `SpendMonitoring` config

**Files:**
- Create: `src/FinTrackPrime.Models/Entities/NotificationType.cs`
- Create: `src/FinTrackPrime.Models/Entities/Notification.cs`
- Modify: `src/FinTrackPrime.WebApi/appsettings.json`

**Interfaces:**
- Produces: `NotificationType` enum, `Notification` entity, `SpendMonitoring:*` config keys — consumed by every later task.

- [ ] **Step 1: Create the enum**

`src/FinTrackPrime.Models/Entities/NotificationType.cs`:
```csharp
namespace FinTrackPrime.Models.Entities
{
    // More values (BankConnected, BankDisconnected, NewDeviceSignIn, ...)
    // arrive with the security-events follow-on feature — not part of
    // this one.
    public enum NotificationType
    {
        UnusualSpend,
    }
}
```

- [ ] **Step 2: Create the entity**

`src/FinTrackPrime.Models/Entities/Notification.cs`:
```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    public class Notification
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public NotificationType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        // Set for UnusualSpend, null for any notification type not tied
        // to one specific transaction. A filtered unique index on this
        // column (see FinTrackDbContext) guarantees a transaction never
        // gets flagged twice, even across separate SpendMonitorService
        // runs.
        public Guid? RelatedTransactionId { get; set; }

        public bool IsRead { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
```

- [ ] **Step 3: Add the `SpendMonitoring` config section**

In `src/FinTrackPrime.WebApi/appsettings.json`, add alongside the existing `Premium` section:
```json
  "SpendMonitoring": {
    "IntervalHours": 4,
    "FlatThresholdUsd": "1000.00",
    "IncomePercentThreshold": "50"
  },
```

- [ ] **Step 4: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/NotificationType.cs src/FinTrackPrime.Models/Entities/Notification.cs src/FinTrackPrime.WebApi/appsettings.json
git commit -m "feat: add Notification entity, NotificationType enum, SpendMonitoring config"
```

---

## Task 2: `FinTrackDbContext` — `DbSet<Notification>` and model config

**Files:**
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Consumes: `Notification`, `NotificationType` from Task 1.
- Produces: `FinTrackDbContext.Notifications` (`DbSet<Notification>`), consumed by Task 3 (migration must match) and Task 5 (service).

- [ ] **Step 1: Add the `DbSet`**

Add alongside the existing `LinkedInstitutions` line:
```csharp
        public DbSet<LinkedInstitution> LinkedInstitutions => Set<LinkedInstitution>();
        public DbSet<Notification> Notifications => Set<Notification>();
```

- [ ] **Step 2: Add `OnModelCreating` config**

Add this block anywhere after the `LinkedInstitution` block:
```csharp
            modelBuilder.Entity<Notification>(entity =>
            {
                entity.Property(n => n.Title).HasMaxLength(200).IsRequired();
                entity.Property(n => n.Message).HasMaxLength(1000).IsRequired();
                entity.HasIndex(n => new { n.UserId, n.CreatedAtUtc });

                // Filtered index: most rows have RelatedTransactionId ==
                // null (any future non-spend notification type), and SQL
                // Server would otherwise reject a plain unique index once
                // more than one NULL exists — same pattern as
                // User.GoogleId above.
                entity.HasIndex(n => n.RelatedTransactionId)
                      .IsUnique()
                      .HasFilter("[RelatedTransactionId] IS NOT NULL");

                entity.HasOne(n => n.User)
                      .WithMany()
                      .HasForeignKey(n => n.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
```

- [ ] **Step 3: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs
git commit -m "feat: register Notification entity with FinTrackDbContext"
```

---

## Task 3: Hand-edit the migration (add `Notifications` table)

**Files:**
- Modify: `src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.cs`
- Modify: `src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.Designer.cs`
- Modify: `src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: shape from Tasks 1–2.
- Produces: `Notifications` table, queryable by Task 5's service.

- [ ] **Step 1: Add the `CreateTable` block**

In `20260806231359_InitialMigration.cs`, add this immediately after the `CreateTable(name: "LinkedInstitutions", ...)` block:
```csharp
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RelatedTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
```

- [ ] **Step 2: Add the indexes**

Add alongside the other `CreateIndex` calls, right before `IX_PremiumPurchases_PayPalOrderId`:
```csharp
            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RelatedTransactionId",
                table: "Notifications",
                column: "RelatedTransactionId",
                unique: true,
                filter: "[RelatedTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAtUtc" });
```

- [ ] **Step 3: Add `Notifications` to `Down()`**

Add alongside the existing `DropTable(name: "LinkedInstitutions")`, keeping alphabetical order (Notifications comes right after LinkedInstitutions, before PremiumPurchases):
```csharp
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PremiumPurchases");
```

- [ ] **Step 4: Update `20260806231359_InitialMigration.Designer.cs`**

Add a new entity property block immediately after the `LinkedInstitution` block closes (`b.ToTable("LinkedInstitutions"); });`) and before `modelBuilder.Entity("FinTrackPrime.Models.Entities.PremiumPurchase", b =>`:
```csharp
            modelBuilder.Entity("FinTrackPrime.Models.Entities.Notification", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTime>("CreatedAtUtc")
                        .HasColumnType("datetime2");

                    b.Property<bool>("IsRead")
                        .HasColumnType("bit");

                    b.Property<string>("Message")
                        .IsRequired()
                        .HasMaxLength(1000)
                        .HasColumnType("nvarchar(1000)");

                    b.Property<Guid?>("RelatedTransactionId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("nvarchar(200)");

                    b.Property<int>("Type")
                        .HasColumnType("int");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uniqueidentifier");

                    b.HasKey("Id");

                    b.HasIndex("RelatedTransactionId")
                        .IsUnique()
                        .HasFilter("[RelatedTransactionId] IS NOT NULL");

                    b.HasIndex("UserId", "CreatedAtUtc");

                    b.ToTable("Notifications");
                });
```

Then add the matching relationship block immediately after the `LinkedInstitution` relationship block closes (the second occurrence, further down the file — `b.Navigation("User"); });` right after `.WithMany("LinkedInstitutions")`) and before `modelBuilder.Entity("FinTrackPrime.Models.Entities.PremiumPurchase", b => { b.HasOne(...`:
```csharp
            modelBuilder.Entity("FinTrackPrime.Models.Entities.Notification", b =>
                {
                    b.HasOne("FinTrackPrime.Models.Entities.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });
```

- [ ] **Step 5: Apply the identical edit to `FinTrackDbContextModelSnapshot.cs`**

Same two blocks, same relative position (after `LinkedInstitution`, before `PremiumPurchase`, in both the property section and the relationship section).

- [ ] **Step 6: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.cs src/FinTrackPrime.Models/Migrations/20260806231359_InitialMigration.Designer.cs src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs
git commit -m "feat: add Notifications table to InitialMigration"
```

---

## Task 4: `NotificationViewModels.cs`

**Files:**
- Create: `src/FinTrackPrime.Models/ViewModels/NotificationViewModels.cs`

**Interfaces:**
- Consumes: `NotificationType` from Task 1.
- Produces: `NotificationViewModel`, `NotificationListViewModel` — consumed by Task 5 (service), Task 6 (controller), Task 8 (background service, for the SignalR payload shape).

- [ ] **Step 1: Create the file**

```csharp
using System;
using System.Collections.Generic;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class NotificationViewModel
    {
        public Guid Id { get; set; }
        public NotificationType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Guid? RelatedTransactionId { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public class NotificationListViewModel
    {
        public List<NotificationViewModel> Items { get; set; } = new();
        public int UnreadCount { get; set; }
        public bool HasMore { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/NotificationViewModels.cs
git commit -m "feat: add Notification view models"
```

---

## Task 5: `INotificationService` / `NotificationService` — list, mark-read, spend detection

**Files:**
- Create: `src/FinTrackPrime.Business/Interfaces/INotificationService.cs`
- Create: `src/FinTrackPrime.Business/Services/NotificationService.cs`
- Test: `tests/FinTrackPrime.Business.Tests/NotificationServiceTests.cs` (new)

**Interfaces:**
- Consumes: `Notification`/`NotificationType` (Task 1), `FinTrackDbContext.Notifications` (Task 2), view models (Task 4), `BudgetCategory`/`Transaction`/`TransactionDirection` (existing entities), `IConfiguration` (for `SpendMonitoring:*`).
- Produces: `INotificationService`, consumed by Task 6 (controller) and Task 8 (background service). **No SignalR/hosting dependency anywhere in this task** — that stays entirely in WebApi (Task 7/8).

### Part A: `GetNotificationsAsync` / `MarkReadAsync` / `MarkAllReadAsync`

- [ ] **Step 1: Write the failing tests**

Create `tests/FinTrackPrime.Business.Tests/NotificationServiceTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class NotificationServiceTests
    {
        private static FinTrackDbContext BuildDb()
        {
            var options = new DbContextOptionsBuilder<FinTrackDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new FinTrackDbContext(options);
        }

        private static IConfiguration BuildConfig(string flatThresholdUsd = "1000.00", string incomePercentThreshold = "50")
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["SpendMonitoring:FlatThresholdUsd"] = flatThresholdUsd,
                    ["SpendMonitoring:IncomePercentThreshold"] = incomePercentThreshold,
                })
                .Build();
        }

        private static async Task<Guid> SeedUserAsync(FinTrackDbContext db)
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = $"{userId}@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return userId;
        }

        [Fact]
        public async Task GetNotificationsAsync_ReturnsNewestFirstWithUnreadCount()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            db.Notifications.AddRange(
                new Notification { Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.UnusualSpend, Title = "Old", Message = "m", IsRead = true, CreatedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new Notification { Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.UnusualSpend, Title = "New", Message = "m", IsRead = false, CreatedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            var result = await service.GetNotificationsAsync(userId, page: 1, pageSize: 20);

            Assert.Equal("New", result.Items[0].Title);
            Assert.Equal("Old", result.Items[1].Title);
            Assert.Equal(1, result.UnreadCount);
            Assert.False(result.HasMore);
        }

        [Fact]
        public async Task GetNotificationsAsync_SetsHasMoreWhenAnotherPageExists()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);

            for (var i = 0; i < 3; i++)
            {
                db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.UnusualSpend, Title = $"N{i}", Message = "m", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i) });
            }
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            var result = await service.GetNotificationsAsync(userId, page: 1, pageSize: 2);

            Assert.Equal(2, result.Items.Count);
            Assert.True(result.HasMore);
        }

        [Fact]
        public async Task MarkReadAsync_SetsIsReadTrue()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var notificationId = Guid.NewGuid();
            db.Notifications.Add(new Notification { Id = notificationId, UserId = userId, Type = NotificationType.UnusualSpend, Title = "N", Message = "m", IsRead = false, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            await service.MarkReadAsync(userId, notificationId);

            var stored = await db.Notifications.SingleAsync(n => n.Id == notificationId);
            Assert.True(stored.IsRead);
        }

        [Fact]
        public async Task MarkReadAsync_ThrowsWhenNotificationBelongsToAnotherUser()
        {
            await using var db = BuildDb();
            var owner = await SeedUserAsync(db);
            var otherUser = await SeedUserAsync(db);
            var notificationId = Guid.NewGuid();
            db.Notifications.Add(new Notification { Id = notificationId, UserId = owner, Type = NotificationType.UnusualSpend, Title = "N", Message = "m", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkReadAsync(otherUser, notificationId));
        }

        [Fact]
        public async Task MarkAllReadAsync_MarksEveryUnreadNotificationForThatUserOnly()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var otherUser = await SeedUserAsync(db);
            db.Notifications.AddRange(
                new Notification { Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.UnusualSpend, Title = "A", Message = "m", IsRead = false, CreatedAtUtc = DateTime.UtcNow },
                new Notification { Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.UnusualSpend, Title = "B", Message = "m", IsRead = false, CreatedAtUtc = DateTime.UtcNow },
                new Notification { Id = Guid.NewGuid(), UserId = otherUser, Type = NotificationType.UnusualSpend, Title = "C", Message = "m", IsRead = false, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            await service.MarkAllReadAsync(userId);

            Assert.All(await db.Notifications.Where(n => n.UserId == userId).ToListAsync(), n => Assert.True(n.IsRead));
            Assert.False(await db.Notifications.Where(n => n.UserId == otherUser).Select(n => n.IsRead).SingleAsync());
        }
    }
}
```

- [ ] **Step 2: Run the tests, confirm they fail**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter NotificationServiceTests`
Expected: does not build — `NotificationService` doesn't exist yet.

- [ ] **Step 3: Create `INotificationService`**

`src/FinTrackPrime.Business/Interfaces/INotificationService.cs`:
```csharp
using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface INotificationService
    {
        Task<NotificationListViewModel> GetNotificationsAsync(Guid userId, int page, int pageSize);

        // Throws InvalidOperationException if the notification doesn't
        // exist or belongs to a different user.
        Task MarkReadAsync(Guid userId, Guid notificationId);

        Task MarkAllReadAsync(Guid userId);

        // Evaluates this user's Expense transactions that don't already
        // have an UnusualSpend notification against the flat-threshold
        // rule, inserts a Notification for each match, and returns the
        // ones just created — so the caller (SpendMonitorService, in
        // WebApi) can push them over SignalR. Assumes the caller has
        // already synced this user's transactions; this method only
        // reads what's already in the database.
        Task<System.Collections.Generic.List<NotificationViewModel>> CreateSpendNotificationsAsync(Guid userId);
    }
}
```

- [ ] **Step 4: Implement `NotificationService` (list/mark-read parts)**

`src/FinTrackPrime.Business/Services/NotificationService.cs` (Part B below adds `CreateSpendNotificationsAsync` to this same file):
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class NotificationService : INotificationService
    {
        private readonly FinTrackDbContext _db;
        private readonly IConfiguration _config;

        public NotificationService(FinTrackDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        public async Task<NotificationListViewModel> GetNotificationsAsync(Guid userId, int page, int pageSize)
        {
            var query = _db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAtUtc);

            var totalCount = await query.CountAsync();
            var unreadCount = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(n => new NotificationViewModel
                {
                    Id = n.Id,
                    Type = n.Type,
                    Title = n.Title,
                    Message = n.Message,
                    RelatedTransactionId = n.RelatedTransactionId,
                    IsRead = n.IsRead,
                    CreatedAtUtc = n.CreatedAtUtc,
                })
                .ToListAsync();

            return new NotificationListViewModel
            {
                Items = items,
                UnreadCount = unreadCount,
                HasMore = page * pageSize < totalCount,
            };
        }

        public async Task MarkReadAsync(Guid userId, Guid notificationId)
        {
            var notification = await _db.Notifications
                .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

            if (notification is null)
            {
                throw new InvalidOperationException("Notification not found.");
            }

            notification.IsRead = true;
            await _db.SaveChangesAsync();
        }

        public async Task MarkAllReadAsync(Guid userId)
        {
            var unread = await _db.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ToListAsync();

            foreach (var notification in unread)
            {
                notification.IsRead = true;
            }

            await _db.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 5: Run the Part A tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter NotificationServiceTests`
Expected: the 5 Part A tests PASS; `CreateSpendNotificationsAsync` tests (Part B, not written yet) don't exist yet so nothing else runs.

### Part B: `CreateSpendNotificationsAsync`

- [ ] **Step 6: Add the failing tests**

Append to `NotificationServiceTests`:
```csharp
        private static async Task<Account> SeedCheckingAccountAsync(FinTrackDbContext db, Guid userId)
        {
            var account = new Account
            {
                Id = Guid.NewGuid(), UserId = userId, Nickname = "Checking", Type = AccountType.Checking,
                Balance = 5000m, Currency = "USD", ExternalAccountId = $"acc-{Guid.NewGuid()}", Institution = "Testbank", CreatedAtUtc = DateTime.UtcNow,
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return account;
        }

        [Fact]
        public async Task CreateSpendNotificationsAsync_FlagsTransactionOverFlatThreshold()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var account = await SeedCheckingAccountAsync(db, userId);
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), AccountId = account.Id, ExternalTransactionId = "t-1", Description = "Large purchase",
                Category = "Shopping", Amount = 1500m, Direction = TransactionDirection.Expense, OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig(flatThresholdUsd: "1000.00", incomePercentThreshold: "50"));
            var created = await service.CreateSpendNotificationsAsync(userId);

            var notification = Assert.Single(created);
            Assert.Equal(NotificationType.UnusualSpend, notification.Type);
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));
        }

        [Fact]
        public async Task CreateSpendNotificationsAsync_FlagsTransactionOverIncomePercentThreshold_WhenIncomeTracked()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var account = await SeedCheckingAccountAsync(db, userId);
            db.BudgetCategories.Add(new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Salary", Type = BudgetCategoryType.Income, PlannedAmount = 1000m });
            db.Transactions.Add(new Transaction
            {
                // 60% of $1000 income — below the $1000 flat threshold but above the 50% income threshold.
                Id = Guid.NewGuid(), AccountId = account.Id, ExternalTransactionId = "t-2", Description = "Big expense",
                Category = "Travel", Amount = 600m, Direction = TransactionDirection.Expense, OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig(flatThresholdUsd: "1000.00", incomePercentThreshold: "50"));
            var created = await service.CreateSpendNotificationsAsync(userId);

            Assert.Single(created);
        }

        [Fact]
        public async Task CreateSpendNotificationsAsync_DoesNotFlagBelowBothThresholdsWithNoIncomeTracked()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var account = await SeedCheckingAccountAsync(db, userId);
            db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(), AccountId = account.Id, ExternalTransactionId = "t-3", Description = "Groceries",
                Category = "Groceries", Amount = 80m, Direction = TransactionDirection.Expense, OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            var created = await service.CreateSpendNotificationsAsync(userId);

            Assert.Empty(created);
        }

        [Fact]
        public async Task CreateSpendNotificationsAsync_IgnoresIncomeAndTransferTransactions()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var account = await SeedCheckingAccountAsync(db, userId);
            db.Transactions.AddRange(
                new Transaction { Id = Guid.NewGuid(), AccountId = account.Id, ExternalTransactionId = "t-4", Description = "Paycheck", Category = "Salary", Amount = 5000m, Direction = TransactionDirection.Income, OccurredAtUtc = DateTime.UtcNow },
                new Transaction { Id = Guid.NewGuid(), AccountId = account.Id, ExternalTransactionId = "t-5", Description = "Card payment", Category = "", Amount = 2000m, Direction = TransactionDirection.Transfer, OccurredAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            var created = await service.CreateSpendNotificationsAsync(userId);

            Assert.Empty(created);
        }

        [Fact]
        public async Task CreateSpendNotificationsAsync_DoesNotDuplicateForAnAlreadyFlaggedTransaction()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            var account = await SeedCheckingAccountAsync(db, userId);
            var transactionId = Guid.NewGuid();
            db.Transactions.Add(new Transaction
            {
                Id = transactionId, AccountId = account.Id, ExternalTransactionId = "t-6", Description = "Large purchase",
                Category = "Shopping", Amount = 1500m, Direction = TransactionDirection.Expense, OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new NotificationService(db, BuildConfig());
            var firstRun = await service.CreateSpendNotificationsAsync(userId);
            var secondRun = await service.CreateSpendNotificationsAsync(userId);

            Assert.Single(firstRun);
            Assert.Empty(secondRun);
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.RelatedTransactionId == transactionId));
        }
```

- [ ] **Step 7: Run the tests, confirm they fail**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter NotificationServiceTests`
Expected: does not build — `CreateSpendNotificationsAsync` doesn't exist on `NotificationService` yet (interface declares it, implementation doesn't).

- [ ] **Step 8: Implement `CreateSpendNotificationsAsync`**

Add this method to `NotificationService`, after `MarkAllReadAsync`:
```csharp
        public async Task<List<NotificationViewModel>> CreateSpendNotificationsAsync(Guid userId)
        {
            var flatThreshold = decimal.Parse(_config["SpendMonitoring:FlatThresholdUsd"] ?? "1000.00");
            var incomePercentThreshold = decimal.Parse(_config["SpendMonitoring:IncomePercentThreshold"] ?? "50");

            var monthlyIncome = await _db.BudgetCategories
                .Where(c => c.UserId == userId && c.Type == BudgetCategoryType.Income)
                .SumAsync(c => c.PlannedAmount);

            // Only an Expense can be "unusual spend" — Income and Transfer
            // rows are excluded outright, same distinction Cash Flow
            // already draws.
            var candidateTransactions = await _db.Transactions
                .Where(t => t.Account!.UserId == userId && t.Direction == TransactionDirection.Expense)
                .Where(t => !_db.Notifications.Any(n => n.RelatedTransactionId == t.Id))
                .ToListAsync();

            var created = new List<NotificationViewModel>();

            foreach (var transaction in candidateTransactions)
            {
                var overFlatThreshold = transaction.Amount >= flatThreshold;
                var overIncomeThreshold = monthlyIncome > 0
                    && transaction.Amount >= monthlyIncome * (incomePercentThreshold / 100m);

                if (!overFlatThreshold && !overIncomeThreshold)
                {
                    continue;
                }

                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Type = NotificationType.UnusualSpend,
                    Title = "Unusual spend detected",
                    Message = $"A {transaction.Amount:C} charge (\"{transaction.Description}\") is larger than your usual spending.",
                    RelatedTransactionId = transaction.Id,
                    IsRead = false,
                    CreatedAtUtc = DateTime.UtcNow,
                };

                _db.Notifications.Add(notification);

                created.Add(new NotificationViewModel
                {
                    Id = notification.Id,
                    Type = notification.Type,
                    Title = notification.Title,
                    Message = notification.Message,
                    RelatedTransactionId = notification.RelatedTransactionId,
                    IsRead = notification.IsRead,
                    CreatedAtUtc = notification.CreatedAtUtc,
                });
            }

            if (created.Count > 0)
            {
                await _db.SaveChangesAsync();
            }

            return created;
        }
```

Note: `t.Account!.UserId == userId` requires `Transaction.Account` to be loaded/queryable via navigation — this works via EF Core's query translation against the FK (`AccountId`) without needing an explicit `.Include`, same as other cross-entity `Where` clauses already in this codebase (e.g. `FinancialStatementService`'s liability/account queries).

- [ ] **Step 9: Run all the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter NotificationServiceTests`
Expected: PASS, all 10 tests (5 from Part A, 5 from Part B).

- [ ] **Step 10: Run the full test project to confirm nothing else broke**

Run: `dotnet test tests/FinTrackPrime.Business.Tests`
Expected: PASS, every test in the project.

- [ ] **Step 11: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/INotificationService.cs src/FinTrackPrime.Business/Services/NotificationService.cs tests/FinTrackPrime.Business.Tests/NotificationServiceTests.cs
git commit -m "feat: add NotificationService (list, mark-read, spend detection)"
```

---

## Task 6: `NotificationsController`

**Files:**
- Create: `src/FinTrackPrime.WebApi/Controllers/NotificationsController.cs`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`

**Interfaces:**
- Consumes: `INotificationService` (Task 5), view models (Task 4).
- Produces: `GET/POST /api/notifications[...]`, consumed by Task 10 (frontend API client).

No controller-level tests exist anywhere in this project — verification is a build plus manual smoke check, same as `FinancialStatementController`/`CheckoutController`.

- [ ] **Step 1: Register `INotificationService` in DI**

In `Program.cs`, add alongside the other `AddScoped` calls:
```csharp
builder.Services.AddScoped<IFinancialStatementService, FinancialStatementService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
```

- [ ] **Step 2: Create the controller**

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
    [Route("api/notifications")]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationService _notificationService;

        public NotificationsController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        [HttpGet]
        public async Task<ActionResult<NotificationListViewModel>> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var result = await _notificationService.GetNotificationsAsync(GetUserId(), page, pageSize);
            return Ok(result);
        }

        [HttpPost("{notificationId:guid}/read")]
        public async Task<IActionResult> MarkRead(Guid notificationId)
        {
            try
            {
                await _notificationService.MarkReadAsync(GetUserId(), notificationId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllRead()
        {
            await _notificationService.MarkAllReadAsync(GetUserId());
            return NoContent();
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

- [ ] **Step 3: Build**

Run: `dotnet build FinTrackPrime.sln`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.WebApi/Controllers/NotificationsController.cs src/FinTrackPrime.WebApi/Program.cs
git commit -m "feat: add NotificationsController"
```

---

## Task 7: `NotificationsHub` and JWT-over-WebSocket auth

**Files:**
- Create: `src/FinTrackPrime.WebApi/Hubs/NotificationsHub.cs`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`

**Interfaces:**
- Produces: `NotificationsHub` at `/hubs/notifications`, and `IHubContext<NotificationsHub>` (an ASP.NET Core built-in service, available once `AddSignalR()`/`MapHub` are registered) — consumed by Task 8 (background service).

- [ ] **Step 1: Create the Hub**

```csharp
using System;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FinTrackPrime.WebApi.Hubs
{
    // One group per user (named after their UserId) — this is the only
    // addressing scheme in use. A connection is never added to any group
    // but its own, so a push to Clients.Group(userId) can only ever
    // reach that one user's own connections.
    [Authorize]
    public class NotificationsHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GetUserId().ToString());
            await base.OnConnectedAsync();
        }

        private Guid GetUserId()
        {
            var subClaim = Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
```

- [ ] **Step 2: Add SignalR + the JWT-over-query-string adaptation to `Program.cs`**

Add `builder.Services.AddSignalR();` right after `builder.Services.AddDataProtection();`:
```csharp
// Encrypts LinkedInstitution.AccessToken at rest (see BankLinkService).
builder.Services.AddDataProtection();

builder.Services.AddSignalR();
```

Modify the existing `.AddJwtBearer(options => { ... })` call to add `Events` — a browser can't set an `Authorization` header on a WebSocket handshake, so the SignalR JS client sends the token as an `access_token` query parameter instead; this reads it back out **only** for hub requests, every other endpoint's existing header-based flow is untouched:
```csharp
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });
```

Add the using directive at the top of the file:
```csharp
using FinTrackPrime.WebApi.Hubs;
```

Add the hub mapping right after `app.MapControllers();`:
```csharp
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");
```

- [ ] **Step 3: Build**

Run: `dotnet build FinTrackPrime.sln`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.WebApi/Hubs/NotificationsHub.cs src/FinTrackPrime.WebApi/Program.cs
git commit -m "feat: add NotificationsHub with JWT-over-query-string auth for WebSocket handshakes"
```

---

## Task 8: `SpendMonitorService` background job

**Files:**
- Create: `src/FinTrackPrime.WebApi/BackgroundServices/SpendMonitorService.cs`
- Modify: `src/FinTrackPrime.WebApi/Program.cs`

**Interfaces:**
- Consumes: `IBankLinkService.SyncAsync` (existing), `INotificationService.CreateSpendNotificationsAsync` (Task 5), `IHubContext<NotificationsHub>` (Task 7).
- Produces: the running background job — nothing downstream depends on this task; it's the orchestration endpoint of the whole feature.

- [ ] **Step 1: Create the background service**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTrackPrime.WebApi.BackgroundServices
{
    // Runs on a timer (SpendMonitoring:IntervalHours), re-syncing every
    // user who has at least one linked bank and flagging unusually large
    // spend. One user's failure (an expired Finverse token, a transient
    // outage) is caught and logged, not allowed to stop the run for
    // everyone else — same isolation principle BankLinkService.SyncAsync
    // already applies per-institution, one level up.
    public class SpendMonitorService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SpendMonitorService> _logger;

        public SpendMonitorService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<SpendMonitorService> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalHours = _config.GetValue<double?>("SpendMonitoring:IntervalHours") ?? 4;
            using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

            do
            {
                await RunOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTrackDbContext>();

            var userIds = await db.LinkedInstitutions
                .Select(i => i.UserId)
                .Distinct()
                .ToListAsync(stoppingToken);

            foreach (var userId in userIds)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await ProcessUserAsync(userId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SpendMonitorService failed for user {UserId}", userId);
                }
            }
        }

        private async Task ProcessUserAsync(Guid userId, CancellationToken stoppingToken)
        {
            // Fresh scope per user: keeps one user's tracked EF Core
            // entities from bleeding into the next, same reason
            // BankLinkService.SyncAsync clears its ChangeTracker between
            // institutions.
            using var userScope = _scopeFactory.CreateScope();
            var bankLinkService = userScope.ServiceProvider.GetRequiredService<IBankLinkService>();
            var notificationService = userScope.ServiceProvider.GetRequiredService<INotificationService>();
            var hubContext = userScope.ServiceProvider.GetRequiredService<IHubContext<NotificationsHub>>();

            await bankLinkService.SyncAsync(userId);
            var created = await notificationService.CreateSpendNotificationsAsync(userId);

            foreach (var notification in created)
            {
                await hubContext.Clients.Group(userId.ToString()).SendAsync("ReceiveNotification", notification, stoppingToken);
            }
        }
    }
}
```

- [ ] **Step 2: Register the hosted service**

In `Program.cs`, add alongside the other service registrations — `INotificationService` itself was already registered in Task 6 Step 1, only the hosted-service line is new here:
```csharp
builder.Services.AddHostedService<FinTrackPrime.WebApi.BackgroundServices.SpendMonitorService>();
```

- [ ] **Step 3: Build**

Run: `dotnet build FinTrackPrime.sln`
Expected: succeeds.

- [ ] **Step 4: Manual verification**

Run the API locally (`dotnet run --project src/FinTrackPrime.WebApi`), sign in as a test user with at least one linked Testbank institution and a large enough transaction (or an income category set up so the percent-of-income rule can trip on a smaller one), and either wait out the configured interval or temporarily set `SpendMonitoring:IntervalHours` very low (e.g. `0.01`) in `appsettings.Development.json` to see a run happen quickly. Confirm a row appears in the `Notifications` table and a `GET /api/notifications` call returns it.

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.WebApi/BackgroundServices/SpendMonitorService.cs src/FinTrackPrime.WebApi/Program.cs
git commit -m "feat: add SpendMonitorService background job"
```

---

## Task 9: Frontend types (`types/api.ts`)

**Files:**
- Modify: `src/types/api.ts` (in `C:\Users\Wyrlo\projects\FinTrackPrime`)

**Interfaces:**
- Produces: `NotificationType`, `NotificationViewModel`, `NotificationListViewModel` — consumed by Task 10 (API client) and Task 12 (TopNav).

- [ ] **Step 1: Add the types**

Append to `src/types/api.ts`:
```ts
// More values (BankConnected, BankDisconnected, NewDeviceSignIn, ...)
// arrive with the security-events follow-on feature.
export type NotificationType = 'UnusualSpend'

export interface NotificationViewModel {
  id: string
  type: NotificationType
  title: string
  message: string
  relatedTransactionId?: string
  isRead: boolean
  createdAtUtc: string
}

export interface NotificationListViewModel {
  items: NotificationViewModel[]
  unreadCount: number
  hasMore: boolean
}
```

- [ ] **Step 2: Commit**

```bash
git add src/types/api.ts
git commit -m "feat: add Notification types"
```

---

## Task 10: Frontend API client (`api/notifications.ts`)

**Files:**
- Create: `src/api/notifications.ts`

**Interfaces:**
- Consumes: types from Task 9.
- Produces: `notificationsApi.list/markRead/markAllRead` — consumed by Task 12.

- [ ] **Step 1: Create the file**

```ts
import { apiClient } from './client'
import type { NotificationListViewModel } from '../types/api'

export const notificationsApi = {
  list: async (page = 1, pageSize = 20): Promise<NotificationListViewModel> => {
    const { data } = await apiClient.get<NotificationListViewModel>('/api/notifications', {
      params: { page, pageSize },
    })
    return data
  },
  markRead: async (notificationId: string): Promise<void> => {
    await apiClient.post(`/api/notifications/${notificationId}/read`)
  },
  markAllRead: async (): Promise<void> => {
    await apiClient.post('/api/notifications/read-all')
  },
}
```

- [ ] **Step 2: Commit**

```bash
git add src/api/notifications.ts
git commit -m "feat: add notificationsApi client"
```

---

## Task 11: SignalR connection hook (`hooks/useNotificationsHub.ts`)

**Files:**
- Modify: `package.json` (add `@microsoft/signalr`)
- Create: `src/hooks/useNotificationsHub.ts`
- Modify: `src/components/AppLayout.tsx`

**Interfaces:**
- Consumes: `authSession` (existing, for the in-memory access token), `useToast` (existing), `notificationsApi` is NOT called here — this hook only manages the live connection and query-cache invalidation.
- Produces: `useNotificationsHub()` — consumed by Task 12 indirectly (it's mounted once in `AppLayout`, and its invalidation of the `['notifications']` query is what `TopNav`'s notification list re-reads).

- [ ] **Step 1: Add the SignalR client dependency**

Run: `npm install @microsoft/signalr` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)

- [ ] **Step 2: Create the hook**

```ts
import { useEffect } from 'react'
import { HubConnectionBuilder, HttpTransportType, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { authSession } from '../api/authSession'
import { useToast } from '../components/ui/Toast'
import type { NotificationViewModel } from '../types/api'

const HUB_URL = `${import.meta.env.VITE_API_BASE_URL ?? ''}/hubs/notifications`

/**
 * One SignalR connection per authenticated session. accessTokenFactory is
 * called by the SignalR client on every (re)connect attempt, so a token
 * refreshed mid-session (see api/client.ts's response interceptor) is
 * picked up automatically without this hook needing to know that
 * happened.
 */
export function useNotificationsHub(isAuthenticated: boolean) {
  const queryClient = useQueryClient()
  const { toast } = useToast()

  useEffect(() => {
    if (!isAuthenticated) {
      return
    }

    let connection: HubConnection | null = new HubConnectionBuilder()
      .withUrl(HUB_URL, {
        accessTokenFactory: () => authSession.get()?.token ?? '',
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('ReceiveNotification', (notification: NotificationViewModel) => {
      toast({ title: notification.title, description: notification.message, variant: 'warning' })
      queryClient.invalidateQueries({ queryKey: ['notifications'] })
    })

    connection.start().catch(() => {
      // Best-effort: a failed connection just means no live push this
      // session. GET /api/notifications (polled on mount, on window
      // focus, and by TanStack Query's own defaults) still surfaces
      // anything the user missed.
    })

    return () => {
      connection?.stop()
      connection = null
    }
  }, [isAuthenticated, queryClient, toast])
}
```

- [ ] **Step 3: Mount the hook in `AppLayout.tsx`**

In `src/components/AppLayout.tsx`, add:
```tsx
import { useNotificationsHub } from '../hooks/useNotificationsHub'
```
and, inside the `AppLayout` component body (right after the existing `useQuery` for `dashboard`):
```tsx
  useNotificationsHub(true)
```
(`AppLayout` only ever renders inside `<ProtectedRoute>`, per `App.tsx`'s route tree — reaching this component already implies `isAuthenticated`, so this is always `true` here; the parameter exists so the hook itself stays testable/reusable independent of that routing guarantee.)

- [ ] **Step 4: Build**

Run: `npm run build` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: succeeds, no TypeScript errors.

- [ ] **Step 5: Commit**

```bash
git add package.json package-lock.json src/hooks/useNotificationsHub.ts src/components/AppLayout.tsx
git commit -m "feat: add SignalR notifications hub connection"
```

---

## Task 12: Wire `TopNav.tsx`'s notification bell to real data

**Files:**
- Create: `src/components/NotificationList.tsx`
- Modify: `src/components/TopNav.tsx`

**Interfaces:**
- Consumes: `notificationsApi` (Task 10), `NotificationViewModel`/`NotificationListViewModel` (Task 9).
- Produces: the rendered notification bell — last task, nothing downstream depends on it.

- [ ] **Step 1: Create `NotificationList.tsx`**

```tsx
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle } from 'lucide-react'
import { notificationsApi } from '../api/notifications'
import { Button } from './ui/Button'
import { EmptyState } from './ui/EmptyState'
import { Spinner } from './ui/Spinner'

function formatRelativeTime(iso: string) {
  const diffMs = Date.now() - new Date(iso).getTime()
  const diffMinutes = Math.round(diffMs / 60_000)
  if (diffMinutes < 1) return 'Just now'
  if (diffMinutes < 60) return `${diffMinutes}m ago`
  const diffHours = Math.round(diffMinutes / 60)
  if (diffHours < 24) return `${diffHours}h ago`
  return `${Math.round(diffHours / 24)}d ago`
}

/** Rendered inside TopNav's Notifications DropdownMenu `header` slot — that
 * slot is a plain content area (not the `items` list, which is only a flat
 * list of one-line actions), so it can host this full interactive list. */
export function NotificationList() {
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({
    queryKey: ['notifications'],
    queryFn: () => notificationsApi.list(),
  })

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['notifications'] })

  if (isLoading) {
    return (
      <p className="flex items-center gap-2 py-4 text-sm text-text-muted">
        <Spinner size="sm" /> Loading…
      </p>
    )
  }

  if (!data || data.items.length === 0) {
    return <EmptyState title="No notifications yet" />
  }

  return (
    <div className="w-72">
      <div className="flex items-center justify-between pb-2">
        <p className="text-xs font-semibold uppercase tracking-wide text-text-secondary">Notifications</p>
        {data.unreadCount > 0 && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => notificationsApi.markAllRead().then(invalidate)}
          >
            Mark all read
          </Button>
        )}
      </div>
      <ul className="max-h-80 space-y-1 overflow-y-auto">
        {data.items.map((notification) => (
          <li key={notification.id}>
            <button
              type="button"
              onClick={() => {
                if (!notification.isRead) {
                  notificationsApi.markRead(notification.id).then(invalidate)
                }
              }}
              className="flex w-full items-start gap-2 rounded-md px-2 py-2 text-left text-sm hover:bg-surface-sunken"
            >
              <AlertTriangle
                className={`mt-0.5 h-4 w-4 shrink-0 ${notification.isRead ? 'text-text-muted' : 'text-status-warning'}`}
              />
              <span className="min-w-0 flex-1">
                <span className={`block truncate ${notification.isRead ? 'text-text-secondary' : 'font-medium text-text-primary'}`}>
                  {notification.title}
                </span>
                <span className="block truncate text-xs text-text-muted">{notification.message}</span>
                <span className="block text-xs text-text-muted">{formatRelativeTime(notification.createdAtUtc)}</span>
              </span>
              {!notification.isRead && <span className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-status-warning" />}
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}
```

- [ ] **Step 2: Wire it into `TopNav.tsx`**

In `src/components/TopNav.tsx`, add the import:
```ts
import { NotificationList } from './NotificationList'
```
and add a query for the unread count (for the badge), right after the existing `searchOpen` state:
```ts
  const { data: notifications } = useQuery({ queryKey: ['notifications'], queryFn: () => notificationsApi.list() })
```
(add the matching imports: `import { useQuery } from '@tanstack/react-query'` and `import { notificationsApi } from '../api/notifications'`).

Replace the existing Notifications `DropdownMenu`:
```tsx
        <DropdownMenu
          trigger={<IconButton icon={<Bell className="h-4 w-4" />} label="Notifications" variant="ghost" />}
          items={[]}
          header={<EmptyState title="No notifications yet" />}
        />
```
with:
```tsx
        <DropdownMenu
          trigger={
            <span className="relative inline-flex">
              <IconButton icon={<Bell className="h-4 w-4" />} label="Notifications" variant="ghost" />
              {(notifications?.unreadCount ?? 0) > 0 && (
                <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-status-warning px-1 text-[10px] font-semibold text-white">
                  {notifications!.unreadCount > 9 ? '9+' : notifications!.unreadCount}
                </span>
              )}
            </span>
          }
          items={[]}
          header={<NotificationList />}
        />
```

`EmptyState` stays imported (still used by `NotificationList.tsx` and possibly elsewhere in this file) — remove it from `TopNav.tsx`'s own imports only if it's no longer referenced there directly after this change.

- [ ] **Step 3: Build**

Run: `npm run build`
Expected: succeeds, no TypeScript errors.

- [ ] **Step 4: Manual verification**

Run `npm run dev`, sign in, open the notification bell — confirm it shows real data (or the "No notifications yet" empty state), the unread badge count matches, clicking an unread notification marks it read (badge count drops), "Mark all read" clears the badge, and — with the background job's interval temporarily lowered per Task 8's verification step — a toast appears live via SignalR without a manual refresh once a spend notification is created server-side.

- [ ] **Step 5: Commit**

```bash
git add src/components/NotificationList.tsx src/components/TopNav.tsx
git commit -m "feat: wire TopNav's notification bell to real data"
```

---

## Self-Review

**Spec coverage:**
- Persistent `Notification` record → Tasks 1–4. ✓
- SignalR real-time delivery, JWT-over-WebSocket auth adaptation → Task 7, frontend Task 11. ✓
- Background job re-syncing every linked user on a schedule → Task 8. ✓
- Flat-threshold + income-percent spend detection rule → Task 5 Part B. ✓
- Notification dedup (never flag the same transaction twice) → DB-enforced filtered unique index (Task 2/3) + `CreateSpendNotificationsAsync`'s `!_db.Notifications.Any(...)` filter (Task 5), tested explicitly. ✓
- `GET/POST` notifications API → Task 6. ✓
- Working notification bell in `TopNav.tsx` → Task 12. ✓
- Explicitly out of scope per the spec (security-event notifications, statistical detection, SignalR backplane) → untouched by every task above. ✓

**Placeholder scan:** no "TBD"/"add appropriate handling"/"similar to Task N" — every step has literal code or an exact command.

**Type consistency check:** `NotificationType`/`Notification`/`NotificationViewModel`/`NotificationListViewModel` field names match exactly across Task 1 (entity), Task 4 (view models), Task 5 (service + tests), Task 6 (controller), Task 9 (TS types), Task 10 (API client), Task 12 (`NotificationList.tsx` field usage: `title`/`message`/`isRead`/`createdAtUtc`/`relatedTransactionId`/`unreadCount`). `IHubContext<NotificationsHub>`/`Clients.Group(userId.ToString())`/`"ReceiveNotification"` event name used consistently between Task 8 (server push) and Task 11 (client `.on('ReceiveNotification', ...)`).
