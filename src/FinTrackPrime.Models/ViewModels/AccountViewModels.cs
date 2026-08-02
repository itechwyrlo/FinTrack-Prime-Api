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
        public List<TransactionViewModel> RecentTransactions { get; set; } = new();
    }

    // The full payload for the account dashboard screen: every account
    // the user owns, each with its own recent transaction list.
    public class DashboardViewModel
    {
        public List<AccountViewModel> Accounts { get; set; } = new();
    }

    public class CreateAccountRequest
    {
        [Required, MaxLength(80)]
        public string Nickname { get; set; } = string.Empty;

        [Required]
        public AccountType Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal StartingBalance { get; set; }
    }

    public class CreateTransactionRequest
    {
        [Required, MaxLength(120)]
        public string Description { get; set; } = string.Empty;

        [Required, MaxLength(80)]
        public string Category { get; set; } = string.Empty;

        [Range(0.01, double.MaxValue)]
        public decimal Amount { get; set; }

        [Required]
        public TransactionDirection Direction { get; set; }
    }
}