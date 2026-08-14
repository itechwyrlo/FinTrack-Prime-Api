using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per bank a user has connected through Finverse. One
    // institution can back multiple Account rows (e.g. Testbank returns a
    // checking, a savings, and a credit card account from one login).
    // AccessToken is encrypted at rest via ASP.NET Core's Data Protection
    // API (see BankLinkService) — treat it with the same care as a
    // password, never log it.
    public class LinkedInstitution
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Institution { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;

        public DateTime LinkedAtUtc { get; set; }
        public DateTime? LastSyncedAtUtc { get; set; }
    }
}
