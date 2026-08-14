using System;

namespace FinTrackPrime.Models.Entities
{
    // Manually entered, same as Liability — there's no feed for real
    // estate, vehicles, or other personal property. Type is always
    // RealEstate, Vehicle, or Other; Cash/Investment assets come from
    // Accounts/InvestmentHoldings instead and never get a row here.
    public class Asset
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public AssetType Type { get; set; }
        public decimal Amount { get; set; }
    }
}
