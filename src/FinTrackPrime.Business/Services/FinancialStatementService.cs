using System;
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

            var liabilities = await _db.Liabilities
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.Amount)
                .ToListAsync();

            var assets = accounts
                .Select(a => new AssetLineViewModel { Label = a.Nickname, Amount = a.Balance })
                .Concat(holdings.Select(h => new AssetLineViewModel
                {
                    Label = $"{h.Symbol} holding",
                    Amount = h.Shares * h.CurrentPricePerShare,
                }))
                .OrderByDescending(a => a.Amount)
                .ToList();

            var totalAssets = assets.Sum(a => a.Amount);
            var totalLiabilities = liabilities.Sum(l => l.Amount);

            return new FinancialStatementViewModel
            {
                Assets = assets,
                TotalAssets = totalAssets,
                Liabilities = liabilities.Select(l => new LiabilityViewModel
                {
                    Id = l.Id,
                    Name = l.Name,
                    Amount = l.Amount,
                }).ToList(),
                TotalLiabilities = totalLiabilities,
                NetWorth = totalAssets - totalLiabilities,
                GeneratedAtUtc = DateTime.UtcNow,
            };
        }

        public async Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request)
        {
            var liability = new Liability
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = request.Name.Trim(),
                Amount = request.Amount,
            };

            _db.Liabilities.Add(liability);
            await _db.SaveChangesAsync();

            return new LiabilityViewModel { Id = liability.Id, Name = liability.Name, Amount = liability.Amount };
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
