using System;
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

        [Theory]
        [InlineData(AssetType.Cash)]
        [InlineData(AssetType.Investment)]
        [InlineData(AssetType.Crypto)]
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
    }
}
