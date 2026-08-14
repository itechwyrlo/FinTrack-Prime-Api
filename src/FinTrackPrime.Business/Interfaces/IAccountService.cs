using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IAccountService
    {
        // Returns every account the user owns, each with its recent
        // transactions. This is the single call the dashboard screen
        // makes on load.
        Task<DashboardViewModel> GetDashboardAsync(Guid userId);
    }
}