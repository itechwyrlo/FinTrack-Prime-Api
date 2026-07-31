using System;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class BudgetCategoryViewModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public BudgetCategoryType Type { get; set; }
        public decimal PlannedAmount { get; set; }
    }

    public class CreateBudgetCategoryRequest
    {
        [Required, MaxLength(80)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public BudgetCategoryType Type { get; set; }
    }

    public class UpdateBudgetCategoryRequest
    {
        [Required, MaxLength(80)]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal PlannedAmount { get; set; }
    }

    // Planned totals alongside the category list, so the frontend doesn't
    // need to re-sum the list itself every time it renders.
    public class BudgetPlannerViewModel
    {
        public System.Collections.Generic.List<BudgetCategoryViewModel> Categories { get; set; } = new();
        public decimal TotalPlannedIncome { get; set; }
        public decimal TotalPlannedExpenses { get; set; }
        public decimal PlannedNet { get; set; }
    }
}
