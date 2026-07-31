using System.Collections.Generic;

namespace FinTrackPrime.Models.ViewModels
{
    public class CategoryAmountViewModel
    {
        public string Category { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    // One point on the monthly trend line. Year and Month are separate
    // ints (not a DateTime) since this is a calendar bucket, not a point
    // in time.
    public class MonthlyCashFlowViewModel
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal Income { get; set; }
        public decimal Expenses { get; set; }
    }

    // Everything the Cash Flow Dashboard screen needs in one call: totals
    // across all of the user's accounts, an expense breakdown by
    // category, and a month-by-month trend for the chart.
    public class CashFlowViewModel
    {
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal Net { get; set; }
        public List<CategoryAmountViewModel> ExpenseByCategory { get; set; } = new();
        public List<MonthlyCashFlowViewModel> MonthlyTrend { get; set; } = new();
    }
}
