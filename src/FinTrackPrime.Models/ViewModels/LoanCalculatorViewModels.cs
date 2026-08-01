using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    public class LoanCalculationRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Range(0, 100)]
        public decimal AnnualInterestRatePercent { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }

        // Optional. Applied to every payment, on top of the required
        // monthly payment, to show how much sooner the loan pays off.
        [Range(0, double.MaxValue)]
        public decimal ExtraMonthlyPayment { get; set; } = 0m;
    }

    public class AmortizationRowViewModel
    {
        public int Month { get; set; }
        public decimal PaymentAmount { get; set; }
        public decimal PrincipalPaid { get; set; }
        public decimal InterestPaid { get; set; }
        public decimal RemainingBalance { get; set; }
    }

    public class LoanCalculationResultViewModel
    {
        public decimal RequiredMonthlyPayment { get; set; }
        public int PayoffMonths { get; set; }
        public decimal TotalInterestPaid { get; set; }
        public decimal TotalPaid { get; set; }
        public List<AmortizationRowViewModel> Schedule { get; set; } = new();
    }

    // Same three inputs as LoanCalculationRequest; extra-payment payoff
    // acceleration isn't relevant to whether the loan fits the budget
    // today, so it's left out on purpose.
    public class LoanAffordabilityRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Range(0, 100)]
        public decimal AnnualInterestRatePercent { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }
    }

    // Unknown is the default (0) on purpose: with no income tracked yet
    // in Budget Planner, a ratio can't be computed, and the frontend
    // should prompt the user to add income rather than show a
    // misleading rating.
    public enum AffordabilityRating
    {
        Unknown,
        Comfortable,
        Manageable,
        Stretched,
        NotRecommended,
    }

    // Built entirely from data already in the system (BudgetCategories,
    // Liabilities), same as FinancialStatementViewModel, so the user
    // never re-enters income or debts they've already tracked elsewhere.
    public class LoanAffordabilityResultViewModel
    {
        public decimal ProposedMonthlyPayment { get; set; }
        public decimal MonthlyIncome { get; set; }
        public decimal ExistingMonthlyObligations { get; set; }
        public decimal TotalExistingLiabilities { get; set; }

        // Null when MonthlyIncome is 0 (no income category set up yet) —
        // dividing by zero would otherwise force a meaningless number.
        public decimal? CurrentDebtToIncomeRatioPercent { get; set; }
        public decimal? ProjectedDebtToIncomeRatioPercent { get; set; }
        public AffordabilityRating Rating { get; set; }
    }
}
