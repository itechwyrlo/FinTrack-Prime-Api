using System;

namespace FinTrackPrime.Models.Entities
{
    public enum BudgetCategoryType
    {
        Income,
        Expense
    }

    // A single line in a user's budget plan: "Groceries, expense, $400
    // planned per month." Separate from Transaction on purpose: this is
    // what the user plans to spend, not a record of what they did spend.
    public class BudgetCategory
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public BudgetCategoryType Type { get; set; }
        public decimal PlannedAmount { get; set; }
    }
}
