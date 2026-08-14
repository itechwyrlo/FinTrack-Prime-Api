using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
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
                .AddInMemoryCollection(new Dictionary<string, string?>
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
    }
}
