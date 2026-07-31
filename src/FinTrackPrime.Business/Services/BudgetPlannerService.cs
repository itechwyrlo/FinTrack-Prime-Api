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
    public class BudgetPlannerService : IBudgetPlannerService
    {
        private readonly FinTrackDbContext _db;

        public BudgetPlannerService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<BudgetPlannerViewModel> GetPlannerAsync(Guid userId)
        {
            var categories = await _db.BudgetCategories
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.Type)
                .ThenBy(c => c.Name)
                .ToListAsync();

            var totalIncome = categories
                .Where(c => c.Type == BudgetCategoryType.Income)
                .Sum(c => c.PlannedAmount);

            var totalExpenses = categories
                .Where(c => c.Type == BudgetCategoryType.Expense)
                .Sum(c => c.PlannedAmount);

            return new BudgetPlannerViewModel
            {
                Categories = categories.Select(ToViewModel).ToList(),
                TotalPlannedIncome = totalIncome,
                TotalPlannedExpenses = totalExpenses,
                PlannedNet = totalIncome - totalExpenses,
            };
        }

        public async Task<BudgetCategoryViewModel> AddCategoryAsync(Guid userId, CreateBudgetCategoryRequest request)
        {
            var category = new BudgetCategory
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = request.Name.Trim(),
                Type = request.Type,
                PlannedAmount = 0m,
            };

            _db.BudgetCategories.Add(category);
            await _db.SaveChangesAsync();

            return ToViewModel(category);
        }

        public async Task<BudgetCategoryViewModel> UpdateCategoryAsync(
            Guid userId, Guid categoryId, UpdateBudgetCategoryRequest request)
        {
            var category = await FindOwnedCategoryAsync(userId, categoryId);

            category.Name = request.Name.Trim();
            category.PlannedAmount = request.PlannedAmount;
            await _db.SaveChangesAsync();

            return ToViewModel(category);
        }

        public async Task RemoveCategoryAsync(Guid userId, Guid categoryId)
        {
            var category = await FindOwnedCategoryAsync(userId, categoryId);

            _db.BudgetCategories.Remove(category);
            await _db.SaveChangesAsync();
        }

        public async Task SeedDefaultCategoriesAsync(Guid userId)
        {
            var defaults = new[]
            {
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Salary", Type = BudgetCategoryType.Income, PlannedAmount = 0m },
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Housing", Type = BudgetCategoryType.Expense, PlannedAmount = 0m },
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Utilities", Type = BudgetCategoryType.Expense, PlannedAmount = 0m },
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Groceries", Type = BudgetCategoryType.Expense, PlannedAmount = 0m },
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Transportation", Type = BudgetCategoryType.Expense, PlannedAmount = 0m },
                new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Savings", Type = BudgetCategoryType.Expense, PlannedAmount = 0m },
            };

            _db.BudgetCategories.AddRange(defaults);
            await _db.SaveChangesAsync();
        }

        // Ownership check lives in one place: every mutating call goes
        // through this, so a user can never edit or delete someone else's
        // category by guessing an id.
        private async Task<BudgetCategory> FindOwnedCategoryAsync(Guid userId, Guid categoryId)
        {
            var category = await _db.BudgetCategories
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.UserId == userId);

            if (category is null)
            {
                throw new InvalidOperationException("Budget category not found.");
            }

            return category;
        }

        private static BudgetCategoryViewModel ToViewModel(BudgetCategory category) => new()
        {
            Id = category.Id,
            Name = category.Name,
            Type = category.Type,
            PlannedAmount = category.PlannedAmount,
        };
    }
}
