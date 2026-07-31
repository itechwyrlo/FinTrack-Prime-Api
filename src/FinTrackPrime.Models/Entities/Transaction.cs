using System;

namespace FinTrackPrime.Models.Entities
{
    public enum TransactionDirection
    {
        Income,
        Expense
    }

    public class Transaction
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public Account? Account { get; set; }

        public string Description { get; set; } = string.Empty;

        // Free-text category ("Groceries", "Salary", "Utilities").
        // The Budget Planner and Cash Flow Dashboard both group by this
        // field, so it is the one thing that has to stay consistent.
        public string Category { get; set; } = string.Empty;

        public decimal Amount { get; set; }
        public TransactionDirection Direction { get; set; }
        public DateTime OccurredAtUtc { get; set; }

        // Set by the unusual-activity check (a simple rule, not machine
        // learning): true when an amount is a large outlier against the
        // account's recent history. Surfaced as a flag in the UI, not
        // acted on automatically.
        public bool IsFlaggedUnusual { get; set; }
    }
}
