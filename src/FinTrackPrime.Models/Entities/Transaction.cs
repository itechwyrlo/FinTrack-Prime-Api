using System;

namespace FinTrackPrime.Models.Entities
{
    public enum TransactionDirection
    {
        Income,
        Expense,

        // Money moving between the user's own accounts (a credit card
        // payment funded from Checking, an FPS/bank transfer) rather than
        // real income or spending. Excluded from Cash Flow's Income/Expense
        // sums on purpose — counting it either way double-counts the same
        // dollars against whatever account it left from/landed in.
        Transfer,
    }

    public class Transaction
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public Account? Account { get; set; }

        public string ExternalTransactionId { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        // Free-text category ("Groceries", "Salary", "Utilities").
        // The Budget Planner and Cash Flow Dashboard both group by this
        // field, so it is the one thing that has to stay consistent.
        public string Category { get; set; } = string.Empty;

        public decimal Amount { get; set; }
        public TransactionDirection Direction { get; set; }
        public DateTime OccurredAtUtc { get; set; }

    }
}
