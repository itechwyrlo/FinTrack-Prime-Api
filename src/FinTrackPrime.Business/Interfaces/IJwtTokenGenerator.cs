using System;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Business.Interfaces
{
    // Returned by GenerateToken: the signed JWT plus the exact UTC
    // instant it expires at, so callers can hand ExpiresAtUtc straight
    // to the client instead of recomputing it from config elsewhere.
    public record GeneratedAccessToken(string Token, DateTime ExpiresAtUtc);

    public interface IJwtTokenGenerator
    {
        // Every claim in the token (including whether premium is
        // unlocked) reflects the User's state at the moment this is
        // called. A new token has to be issued whenever that state
        // changes, or the old token keeps asserting the old value until
        // it expires.
        GeneratedAccessToken GenerateToken(User user, bool premiumUnlocked);
    }
}
