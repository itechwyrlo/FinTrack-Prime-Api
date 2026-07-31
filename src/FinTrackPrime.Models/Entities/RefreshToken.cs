using System;

namespace FinTrackPrime.Models.Entities
{
    // One row per issued refresh token. FamilyId is shared by every
    // token in one rotation chain (= one login session): a fresh login
    // starts a new family, and each rotation carries the family
    // forward. That is what makes reuse detection cheap (revoking a
    // family is one UPDATE) and lets a user hold independent sessions
    // on multiple devices without one login invalidating another.
    public class RefreshToken
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public Guid FamilyId { get; set; }

        // SHA-256 hash of the raw token, base64-encoded. The raw value
        // itself is never persisted, only handed to the client once as
        // the cookie value.
        public string TokenHash { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
    }
}
