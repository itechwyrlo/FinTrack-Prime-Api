using System;

namespace FinTrackPrime.Models.Entities
{
    // Bank-managed: one row per loan type, the rate the bank actually
    // offers for it. No admin UI exists anywhere in this app to edit
    // these — updated via a migration/seed/direct DB update, the same
    // way Premium:PriceUsd is updated today.
    public class LoanRate
    {
        public Guid Id { get; set; }

        // Mortgage | AutoLoan | StudentLoan | PersonalLoan | Other —
        // never CreditCard, that's not an amortized loan.
        public LiabilityType Type { get; set; }

        public decimal AnnualRatePercent { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
