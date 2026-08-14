using System;

namespace FinTrackPrime.Models.Entities
{
    // The one row that exists once a user has bought premium access — it
    // unlocks every premium tool at once, so there is at most one row
    // per user (enforced by a unique index on UserId). Storing
    // PayPalOrderId with its own unique index stops the same order from
    // being replayed to unlock a second account.
    public class PremiumPurchase
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string PayPalOrderId { get; set; } = string.Empty;
        public decimal AmountPaid { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime PurchasedAtUtc { get; set; }
    }
}
