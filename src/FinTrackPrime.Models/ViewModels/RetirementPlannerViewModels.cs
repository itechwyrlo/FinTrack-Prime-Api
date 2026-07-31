using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    public class RetirementPlanInputRequest
    {
        [Range(1, 100)]
        public int CurrentAge { get; set; }

        [Range(1, 100)]
        public int RetirementAge { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CurrentSavings { get; set; }

        [Range(0, double.MaxValue)]
        public decimal MonthlyContribution { get; set; }

        [Range(0, 30)]
        public decimal AnnualReturnRatePercent { get; set; }
    }

    public class RetirementProjectionPointViewModel
    {
        public int Age { get; set; }
        public decimal ProjectedBalance { get; set; }
    }

    public class RetirementPlanViewModel
    {
        public int CurrentAge { get; set; }
        public int RetirementAge { get; set; }
        public decimal CurrentSavings { get; set; }
        public decimal MonthlyContribution { get; set; }
        public decimal AnnualReturnRatePercent { get; set; }
        public decimal ProjectedBalanceAtRetirement { get; set; }
        public List<RetirementProjectionPointViewModel> Projection { get; set; } = new();
    }
}
