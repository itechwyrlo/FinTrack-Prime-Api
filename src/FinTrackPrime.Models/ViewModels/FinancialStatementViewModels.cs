using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    public class LiabilityViewModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class CreateLiabilityRequest
    {
        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }
    }

    public class AssetLineViewModel
    {
        public string Label { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    // A simple personal balance sheet: everything owned, everything
    // owed, and the difference. Built entirely from data already in the
    // system (accounts, investment holdings) plus user-entered
    // liabilities, not a separate ledger of its own.
    public class FinancialStatementViewModel
    {
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal NetWorth { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
