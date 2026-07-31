using System;

namespace FinTrackPrime.Models.Entities
{
    // Manually entered, same as investment holdings. There's no
    // liabilities feed anywhere in this system; the user states what
    // they owe.
    public class Liability
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }
}
