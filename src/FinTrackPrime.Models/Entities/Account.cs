using System;
using System.Collections.Generic;

namespace FinTrackPrime.Models.Entities
{
    public enum AccountType
    {
        Checking,
        Savings
    }

    // A mock bank account for demo purposes. Balances here are
    // illustrative, not connected to any real banking rail.
    public class Account
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Nickname { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public decimal Balance { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
}
