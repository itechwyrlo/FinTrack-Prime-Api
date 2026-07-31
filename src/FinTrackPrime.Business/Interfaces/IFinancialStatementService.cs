using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IFinancialStatementService
    {
        // Assembled fresh on every call from Accounts and Investment
        // Holdings; nothing about the statement itself is stored.
        Task<FinancialStatementViewModel> GetStatementAsync(Guid userId);

        Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request);
        Task RemoveLiabilityAsync(Guid userId, Guid liabilityId);
    }
}
