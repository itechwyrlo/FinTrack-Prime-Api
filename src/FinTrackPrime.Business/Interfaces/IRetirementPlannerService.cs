using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IRetirementPlannerService
    {
        // Returns a sensible starter plan if the user has never saved
        // one, so the screen is never empty on first visit.
        Task<RetirementPlanViewModel> GetPlanAsync(Guid userId);

        Task<RetirementPlanViewModel> SavePlanAsync(Guid userId, RetirementPlanInputRequest request);
    }
}
