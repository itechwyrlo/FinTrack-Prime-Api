using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IBudgetPlannerService
    {
        Task<BudgetPlannerViewModel> GetPlannerAsync(Guid userId);
        Task<BudgetCategoryViewModel> AddCategoryAsync(Guid userId, CreateBudgetCategoryRequest request);
        Task<BudgetCategoryViewModel> UpdateCategoryAsync(Guid userId, Guid categoryId, UpdateBudgetCategoryRequest request);
        Task RemoveCategoryAsync(Guid userId, Guid categoryId);

        // Called once, right after registration, so a new user's planner
        // isn't empty on first login. Mirrors the starter categories the
        // original frontend-only demo shipped with.
        Task SeedDefaultCategoriesAsync(Guid userId);
    }
}
