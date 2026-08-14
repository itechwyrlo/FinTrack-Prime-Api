using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class LoanRateViewModel
    {
        public LiabilityType Type { get; set; }
        public decimal AnnualRatePercent { get; set; }
    }

    // AnnualInterestRatePercent does not exist here — the client can
    // never supply or influence a rate, even by calling the API
    // directly. LoanType drives which bank rate the server applies.
    public class LoanCalculationRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Required]
        public LiabilityType LoanType { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }

        // Optional. Applied to every payment, on top of whatever base
        // payment the chosen Method produces, to show how much sooner
        // the loan pays off. Behaves identically across all four
        // methods — no per-method special-casing.
        [Range(0, double.MaxValue)]
        public decimal ExtraMonthlyPayment { get; set; } = 0m;

        public AmortizationMethod Method { get; set; } = AmortizationMethod.Equal;

        // Required (and validated: 0 < value < TermMonths) only when
        // Method == GracePeriod; ignored for every other method.
        public int? GracePeriodMonths { get; set; }
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
        // The first period's payment for every method except Equal,
        // where it's constant across the whole schedule — see Schedule
        // for the full picture of how it changes.
        public decimal RequiredMonthlyPayment { get; set; }
        public int PayoffMonths { get; set; }
        public decimal TotalInterestPaid { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal AppliedAnnualInterestRatePercent { get; set; }
        public List<AmortizationRowViewModel> Schedule { get; set; } = new();
    }

    // Same shape as LoanCalculationRequest minus ExtraMonthlyPayment —
    // extra-payment payoff acceleration isn't relevant to whether the
    // loan fits the budget today, so it's left out on purpose.
    public class LoanAffordabilityRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Required]
        public LiabilityType LoanType { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }

        public AmortizationMethod Method { get; set; } = AmortizationMethod.Equal;

        public int? GracePeriodMonths { get; set; }
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
    // ProposedMonthlyPayment is the resolved-rate, resolved-method first
    // period's payment (see LoanCalculationResultViewModel) — for
    // Balloon specifically, this ratio does not reflect the final lump
    // sum due at maturity, a known limitation of applying a
    // monthly-payment DTI ratio to a balloon loan.
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
