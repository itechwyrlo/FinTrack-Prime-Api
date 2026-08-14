using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class LiabilityViewModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public LiabilityType Type { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class CreateLiabilityRequest
    {
        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public LiabilityType Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }
    }

    public class AssetLineViewModel
    {
        // Null for synced lines (Cash from an Account, Investment from a
        // holding) — those aren't removable. Set for manual Asset rows,
        // which are.
        public Guid? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public AssetType Type { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class CreateAssetRequest
    {
        [Required, MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public AssetType Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Amount { get; set; }
    }

    // One currency's worth of the statement — same shape as the
    // top-level FinancialStatementViewModel below minus GeneratedAtUtc
    // (that's a whole-statement concept, not per-currency), mirroring
    // CashFlowByCurrencyViewModel.
    public class FinancialStatementByCurrencyViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal OwnersEquity { get; set; }
    }

    // A simple personal balance sheet: everything owned, everything
    // owed, and the difference — now bucketed per currency, same
    // principle CashFlowViewModel already uses. Currency/Assets/
    // TotalAssets/Liabilities/TotalLiabilities/OwnersEquity below are
    // whichever currency has the most combined asset+liability lines;
    // every other currency present is in OtherCurrencies, never blended
    // into this one.
    public class FinancialStatementViewModel
    {
        public string Currency { get; set; } = string.Empty;
        public List<AssetLineViewModel> Assets { get; set; } = new();
        public decimal TotalAssets { get; set; }
        public List<LiabilityViewModel> Liabilities { get; set; } = new();
        public decimal TotalLiabilities { get; set; }
        public decimal OwnersEquity { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public List<FinancialStatementByCurrencyViewModel> OtherCurrencies { get; set; } = new();
    }
}
