using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IFinancialStatementService
    {
        // Assembled fresh on every call from Accounts, Investment
        // Holdings, and manually entered Assets/Liabilities; nothing
        // about the statement itself is stored.
        Task<FinancialStatementViewModel> GetStatementAsync(Guid userId);

        // Type must be RealEstate, Vehicle, or Other — throws
        // InvalidOperationException for Cash/Investment, which are
        // sync-only.
        Task<AssetLineViewModel> AddAssetAsync(Guid userId, CreateAssetRequest request);
        Task RemoveAssetAsync(Guid userId, Guid assetId);

        // Type must not be CreditCard — throws InvalidOperationException,
        // since that's sync-only.
        Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request);
        Task RemoveLiabilityAsync(Guid userId, Guid liabilityId);
    }
}
