using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per user, upserted rather than listed, since a user tunes
    // one retirement scenario at a time rather than keeping several.
    public class RetirementPlan
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public int CurrentAge { get; set; }
        public int RetirementAge { get; set; }
        public decimal CurrentSavings { get; set; }
        public decimal MonthlyContribution { get; set; }
        public decimal AnnualReturnRatePercent { get; set; }
    }
}
