using System;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    // The frontend already completed the PayPal popup and has an approved
    // order id for the one-time premium purchase. This request asks the
    // backend to verify that order directly with PayPal before trusting
    // it, rather than trusting whatever the browser claims happened.
    public class VerifyPurchaseRequest
    {
        [Required]
        public string PayPalOrderId { get; set; } = string.Empty;
    }

    // IsUnlocked is false with PurchasedAtUtc null until the user
    // completes the one-time purchase; both are set together, once.
    public class PremiumStatusViewModel
    {
        public bool IsUnlocked { get; set; }
        public DateTime? PurchasedAtUtc { get; set; }
    }
}
