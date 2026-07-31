using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IInvestmentTrackerService
    {
        Task<InvestmentPortfolioViewModel> GetPortfolioAsync(Guid userId);
        Task<InvestmentHoldingViewModel> AddHoldingAsync(Guid userId, CreateInvestmentHoldingRequest request);
        Task<InvestmentHoldingViewModel> UpdateHoldingAsync(Guid userId, Guid holdingId, UpdateInvestmentHoldingRequest request);
        Task RemoveHoldingAsync(Guid userId, Guid holdingId);
    }
}
