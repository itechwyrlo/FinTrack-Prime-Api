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
    public class InvestmentTrackerService : IInvestmentTrackerService
    {
        private readonly FinTrackDbContext _db;

        public InvestmentTrackerService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<InvestmentPortfolioViewModel> GetPortfolioAsync(Guid userId)
        {
            var holdings = await _db.InvestmentHoldings
                .Where(h => h.UserId == userId)
                .OrderByDescending(h => h.Shares * h.CurrentPricePerShare)
                .ToListAsync();

            var totalCurrentValue = holdings.Sum(h => h.Shares * h.CurrentPricePerShare);
            var totalCostBasis = holdings.Sum(h => h.Shares * h.CostBasisPerShare);

            return new InvestmentPortfolioViewModel
            {
                Holdings = holdings.Select(h => ToViewModel(h, totalCurrentValue)).ToList(),
                TotalCostBasis = totalCostBasis,
                TotalCurrentValue = totalCurrentValue,
                TotalGainLoss = totalCurrentValue - totalCostBasis,
            };
        }

        public async Task<InvestmentHoldingViewModel> AddHoldingAsync(Guid userId, CreateInvestmentHoldingRequest request)
        {
            var holding = new InvestmentHolding
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Symbol = request.Symbol.Trim().ToUpperInvariant(),
                Name = request.Name.Trim(),
                Shares = request.Shares,
                CostBasisPerShare = request.CostBasisPerShare,
                CurrentPricePerShare = request.CurrentPricePerShare,
            };

            _db.InvestmentHoldings.Add(holding);
            await _db.SaveChangesAsync();

            return ToViewModel(holding, holding.Shares * holding.CurrentPricePerShare);
        }

        public async Task<InvestmentHoldingViewModel> UpdateHoldingAsync(
            Guid userId, Guid holdingId, UpdateInvestmentHoldingRequest request)
        {
            var holding = await FindOwnedHoldingAsync(userId, holdingId);

            holding.Shares = request.Shares;
            holding.CostBasisPerShare = request.CostBasisPerShare;
            holding.CurrentPricePerShare = request.CurrentPricePerShare;
            await _db.SaveChangesAsync();

            var totalCurrentValue = await _db.InvestmentHoldings
                .Where(h => h.UserId == userId)
                .SumAsync(h => h.Shares * h.CurrentPricePerShare);

            return ToViewModel(holding, totalCurrentValue);
        }

        public async Task RemoveHoldingAsync(Guid userId, Guid holdingId)
        {
            var holding = await FindOwnedHoldingAsync(userId, holdingId);

            _db.InvestmentHoldings.Remove(holding);
            await _db.SaveChangesAsync();
        }

        private async Task<InvestmentHolding> FindOwnedHoldingAsync(Guid userId, Guid holdingId)
        {
            var holding = await _db.InvestmentHoldings
                .FirstOrDefaultAsync(h => h.Id == holdingId && h.UserId == userId);

            if (holding is null)
            {
                throw new InvalidOperationException("Investment holding not found.");
            }

            return holding;
        }

        private static InvestmentHoldingViewModel ToViewModel(InvestmentHolding holding, decimal portfolioTotal)
        {
            var currentValue = holding.Shares * holding.CurrentPricePerShare;
            var costBasisValue = holding.Shares * holding.CostBasisPerShare;

            return new InvestmentHoldingViewModel
            {
                Id = holding.Id,
                Symbol = holding.Symbol,
                Name = holding.Name,
                Shares = holding.Shares,
                CostBasisPerShare = holding.CostBasisPerShare,
                CurrentPricePerShare = holding.CurrentPricePerShare,
                CurrentValue = currentValue,
                GainLoss = currentValue - costBasisValue,
                AllocationPercent = portfolioTotal > 0 ? (currentValue / portfolioTotal) * 100m : 0m,
            };
        }
    }
}
