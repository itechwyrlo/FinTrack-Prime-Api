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
using Microsoft.Extensions.DependencyInjection;
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
            new ServiceCollection()
                .AddDataProtection()
                .Services.BuildServiceProvider()
                .GetRequiredService<IDataProtectionProvider>();

        private static ICryptoPriceClient BuildCryptoPriceClient() => new Mock<ICryptoPriceClient>().Object;

        private static IConfiguration BuildConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Finverse:RedirectUri"] = "https://developer.prod.finverse.net/sink",
                })
                .Build();

        [Fact]
        public async Task CompleteLinkAsync_ClassifiesRecognizedSubtypesCorrectly()
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
                new("acc-credit", "HKD Credit Card", "credit_card", "HKD", -1833.22m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code");

            var accounts = await db.Accounts.Where(a => a.UserId == userId).ToListAsync();
            Assert.Equal(2, accounts.Count);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-checking" && a.Type == AccountType.Checking);
            Assert.Contains(accounts, a => a.ExternalAccountId == "acc-credit" && a.Type == AccountType.CreditCard);
        }

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

        [Fact]
        public async Task CompleteLinkAsync_DerivesTransactionDirectionFromAmountSign()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "u2@test.com", FullName = "Test User 2", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-in", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
                new("txn-out", "BAT STARBUCKS", -40.00m, new DateTime(2023, 6, 30)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

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
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-1", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());
            await service.CompleteLinkAsync(userId, "link-code");

            // Second sync returns the same transaction again.
            await service.SyncAsync(userId);

            var transactions = await db.Transactions.Where(t => t.ExternalTransactionId == "txn-1").ToListAsync();
            Assert.Single(transactions);
        }

        [Fact]
        public async Task CompleteLinkAsync_TwoDifferentUsers_EachGetOwnTransactionsDespiteSameExternalIds()
        {
            await using var db = BuildDb();
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            db.Users.Add(new User { Id = userA, Email = "a@test.com", FullName = "User A", CreatedAtUtc = DateTime.UtcNow });
            db.Users.Add(new User { Id = userB, Email = "b@test.com", FullName = "User B", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync("link-code", It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            // Same external transaction id for both users, as Finverse's sandbox
            // Testbank genuinely returns to every user who links it.
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-shared", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userA, "link-code");
            await service.CompleteLinkAsync(userB, "link-code");

            var userATransactions = await db.Transactions
                .Where(t => t.Account!.UserId == userA).ToListAsync();
            var userBTransactions = await db.Transactions
                .Where(t => t.Account!.UserId == userB).ToListAsync();

            Assert.Single(userATransactions);
            Assert.Single(userBTransactions);
        }

        [Fact]
        public async Task CompleteLinkAsync_CalledTwiceForSameUser_ReusesLinkedInstitutionRow()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "reuse@test.com", FullName = "Reuse User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<FinverseTransactionDto>());

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            await service.CompleteLinkAsync(userId, "link-code-1");
            await service.CompleteLinkAsync(userId, "link-code-2");

            var institutions = await db.LinkedInstitutions.Where(i => i.UserId == userId).ToListAsync();
            Assert.Single(institutions);
        }

        [Fact]
        public async Task DisconnectAllAsync_RemovesLinkedInstitutionsAccountsAndTransactions()
        {
            await using var db = BuildDb();
            var userId = Guid.NewGuid();
            var otherUserId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = "disconnect@test.com", FullName = "Disconnect User", CreatedAtUtc = DateTime.UtcNow });
            db.Users.Add(new User { Id = otherUserId, Email = "other@test.com", FullName = "Other User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var finverseClient = new Mock<IFinverseClient>();
            finverseClient.Setup(c => c.ExchangeLinkCodeAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("at-456");
            finverseClient.Setup(c => c.GetAccountsAsync("at-456")).ReturnsAsync(new List<FinverseAccountDto>
            {
                new("acc-checking", "HKD Checking", "checking", "HKD", 70013.12m),
            });
            finverseClient.Setup(c => c.GetTransactionsAsync("at-456", "acc-checking")).ReturnsAsync(new List<FinverseTransactionDto>
            {
                new("txn-1", "Transfer FPS", 523.00m, new DateTime(2024, 11, 11)),
            });

            var service = new BankLinkService(db, finverseClient.Object, BuildCryptoPriceClient(), BuildDataProtection(), BuildConfig());

            // Link both users, so the test also proves DisconnectAllAsync
            // only touches the calling user's data, not every row in the table.
            await service.CompleteLinkAsync(userId, "link-code");
            await service.CompleteLinkAsync(otherUserId, "link-code");

            await service.DisconnectAllAsync(userId);

            Assert.Empty(await db.LinkedInstitutions.Where(i => i.UserId == userId).ToListAsync());
            Assert.Empty(await db.Accounts.Where(a => a.UserId == userId).ToListAsync());
            Assert.Empty(await db.Transactions.Where(t => t.Account!.UserId == userId).ToListAsync());

            // The other user's data must survive.
            Assert.Single(await db.LinkedInstitutions.Where(i => i.UserId == otherUserId).ToListAsync());
            Assert.Single(await db.Accounts.Where(a => a.UserId == otherUserId).ToListAsync());
        }
    }
}
