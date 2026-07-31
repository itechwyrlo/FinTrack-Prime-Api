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
}
