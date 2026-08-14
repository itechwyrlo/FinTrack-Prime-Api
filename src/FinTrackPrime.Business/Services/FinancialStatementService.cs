using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace FinTrackPrime.Business.Services
{
    public class FinancialStatementService : IFinancialStatementService
    {
        private readonly FinTrackDbContext _db;

        public FinancialStatementService(FinTrackDbContext db)
        {
            _db = db;
        }

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

        public async Task<AssetLineViewModel> AddAssetAsync(Guid userId, CreateAssetRequest request)
        {
            if (request.Type is AssetType.Cash or AssetType.Investment or AssetType.Crypto)
            {
                throw new InvalidOperationException(
                    "Type must be RealEstate, Vehicle, or Other — Cash, Investment, and Crypto assets come from linked accounts.");
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

            return new AssetLineViewModel { Id = asset.Id, Label = asset.Name, Type = asset.Type, Currency = string.Empty, Amount = asset.Amount };
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

            return new LiabilityViewModel { Id = liability.Id, Name = liability.Name, Type = liability.Type, Currency = string.Empty, Amount = liability.Amount };
        }

        public async Task RemoveLiabilityAsync(Guid userId, Guid liabilityId)
        {
            var liability = await _db.Liabilities
                .FirstOrDefaultAsync(l => l.Id == liabilityId && l.UserId == userId);

            if (liability is null)
            {
                throw new InvalidOperationException("Liability not found.");
            }

            _db.Liabilities.Remove(liability);
            await _db.SaveChangesAsync();
        }
    }
}
