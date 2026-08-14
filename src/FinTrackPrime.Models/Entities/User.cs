using System;
using System.Collections.Generic;

namespace FinTrackPrime.Models.Entities
{
    // A signed-up FinTrack Prime customer. Passwords are never stored in
    // plain text; only the hash and a per-user salt are persisted.
    //
    // PasswordHash/PasswordSalt are null for a Google-only account (no
    // local password ever set). GoogleId is null until the account has
    // signed in with Google at least once. A user can have both set: a
    // password account that later links Google via a matching email.
    //
    // Whether this user has premium access lives in PremiumPurchase, not
    // as a flag here: at most one row per user, added the moment they
    // complete the one-time purchase that unlocks every premium tool.
    public class User
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? PasswordHash { get; set; }
        public string? PasswordSalt { get; set; }
        public string? GoogleId { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Account> Accounts { get; set; } = new List<Account>();
        public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
        public ICollection<LinkedInstitution> LinkedInstitutions { get; set; } = new List<LinkedInstitution>();
    }
}