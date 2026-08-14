using System;

namespace FinTrackPrime.Models.Entities
{
    // Manually entered, same as investment holdings. There's no
    // liabilities feed anywhere in this system; the user states what
    // they owe. Type is never CreditCard — that comes from a synced
    // CreditCard Account instead and never gets a row here.
    public class Liability
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public LiabilityType Type { get; set; }
        public decimal Amount { get; set; }
    }
}
