using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class TransactionViewModel
    {
        public Guid Id { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public TransactionDirection Direction { get; set; }
        public DateTime OccurredAtUtc { get; set; }
    }

    public class AccountViewModel
    {
        public Guid Id { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public decimal Balance { get; set; }

        // Was missing entirely — the frontend had no way to know Balance
        // wasn't always in the same unit (HKD vs SGD vs, for an
        // Unsupported account, BTC/USD) and formatted every account as if
        // it were one fixed currency. See Account.Currency.
        public string Currency { get; set; } = string.Empty;

        public List<TransactionViewModel> RecentTransactions { get; set; } = new();
    }

    // The full payload for the account dashboard screen: every account
    // the user owns, each with its own recent transaction list.
    public class DashboardViewModel
    {
        public List<AccountViewModel> Accounts { get; set; } = new();
    }

    public class StartLinkResponse
    {
        public string LinkUrl { get; set; } = string.Empty;
    }

    public class CompleteLinkRequest
    {
        [Required]
        public string LinkCode { get; set; } = string.Empty;
    }
}