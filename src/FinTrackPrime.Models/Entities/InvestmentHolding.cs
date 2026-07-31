using System;

namespace FinTrackPrime.Models.Entities
{
    // A manually entered position, not connected to a live market feed.
    // CurrentPricePerShare is whatever the user last entered, same as
    // the original frontend-only tracker.
    public class InvestmentHolding
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Shares { get; set; }
        public decimal CostBasisPerShare { get; set; }
        public decimal CurrentPricePerShare { get; set; }
    }
}
