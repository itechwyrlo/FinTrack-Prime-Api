using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per verified PayPal order, for one specific tool. Storing
    // PayPalOrderId with a unique index stops the same order from being
    // replayed to unlock anything twice, on the same account or a
    // different one. A user can have up to four rows, one per tool.
    public class PremiumPurchase
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public PremiumTool Tool { get; set; }
        public string PayPalOrderId { get; set; } = string.Empty;
        public decimal AmountPaid { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime PurchasedAtUtc { get; set; }
    }
}