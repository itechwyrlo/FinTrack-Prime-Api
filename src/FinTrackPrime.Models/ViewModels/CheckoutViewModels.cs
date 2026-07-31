using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    // The frontend already completed the PayPal popup and has an approved
    // order id, for one specific tool. This request asks the backend to
    // verify that order directly with PayPal before trusting it, rather
    // than trusting whatever the browser claims happened.
    public class VerifyPurchaseRequest
    {
        [Required]
        public string PayPalOrderId { get; set; } = string.Empty;

        [Required]
        public PremiumTool Tool { get; set; }
    }

    public class UnlockedToolViewModel
    {
        public PremiumTool Tool { get; set; }
        public DateTime PurchasedAtUtc { get; set; }
    }

    // One row per tool the user has actually bought. A tool with no
    // entry here is simply not unlocked yet.
    public class PremiumStatusViewModel
    {
        public List<UnlockedToolViewModel> UnlockedTools { get; set; } = new();
    }
}