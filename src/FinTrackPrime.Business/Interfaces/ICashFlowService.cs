using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface ICashFlowService
    {
        // Reads across every account the user owns, since cash flow is a
        // whole-customer view, not a per-account one.
        Task<CashFlowViewModel> GetCashFlowAsync(Guid userId);
    }
}
