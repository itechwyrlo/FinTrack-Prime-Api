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

    // Same shape as the top-level totals below, scoped to one currency.
    // FinTrack does no FX conversion (see Account.Currency), so a user
    // with accounts in more than one currency gets one of these per
    // currency instead of a single number that silently adds e.g. SGD to
    // HKD as if they were equal.
    public class CashFlowByCurrencyViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal Net { get; set; }
        public List<CategoryAmountViewModel> ExpenseByCategory { get; set; } = new();
        public List<MonthlyCashFlowViewModel> MonthlyTrend { get; set; } = new();
    }

    // Everything the Cash Flow Dashboard screen needs in one call. The
    // top-level totals cover the user's primary currency only (whichever
    // currency has the most transactions) so existing single-currency
    // callers keep working unchanged; any other currencies the user holds
    // are broken out in OtherCurrencies instead of being blended in.
    public class CashFlowViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal Net { get; set; }
        public List<CategoryAmountViewModel> ExpenseByCategory { get; set; } = new();
        public List<MonthlyCashFlowViewModel> MonthlyTrend { get; set; } = new();
        public List<CashFlowByCurrencyViewModel> OtherCurrencies { get; set; } = new();
    }
}
