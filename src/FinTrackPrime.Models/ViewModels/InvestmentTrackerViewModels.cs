using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    public class InvestmentHoldingViewModel
    {
        public Guid Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Shares { get; set; }
        public decimal CostBasisPerShare { get; set; }
        public decimal CurrentPricePerShare { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal GainLoss { get; set; }
        public decimal AllocationPercent { get; set; }
    }

    public class CreateInvestmentHoldingRequest
    {
        [Required, MaxLength(12)]
        public string Symbol { get; set; } = string.Empty;

        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue)]
        public decimal Shares { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CostBasisPerShare { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CurrentPricePerShare { get; set; }
    }

    public class UpdateInvestmentHoldingRequest
    {
        [Range(0.0001, double.MaxValue)]
        public decimal Shares { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CostBasisPerShare { get; set; }

        // The field a user is expected to update most often: whatever
        // they last checked the position was worth.
        [Range(0, double.MaxValue)]
        public decimal CurrentPricePerShare { get; set; }
    }

    public class InvestmentPortfolioViewModel
    {
        public List<InvestmentHoldingViewModel> Holdings { get; set; } = new();
        public decimal TotalCostBasis { get; set; }
        public decimal TotalCurrentValue { get; set; }
        public decimal TotalGainLoss { get; set; }
    }
}
