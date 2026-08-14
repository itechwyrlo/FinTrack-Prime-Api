using System;
using System.ComponentModel.DataAnnotations;

namespace FinTrackPrime.Models.ViewModels
{
    public class RegisterRequest
    {
        [Required, MaxLength(120)]
        public string FullName { get; set; } = string.Empty;

        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(8)]
        public string Password { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    // Body for POST /api/auth/google. IdToken is the Google-signed ID
    // token the frontend obtained from Google Identity Services — this
    // backend verifies it itself and never talks to Google directly.
    public class GoogleLoginRequest
    {
        [Required]
        public string IdToken { get; set; } = string.Empty;
    }

    // Returned on successful register, login, google login, refresh, or
    // premium verify. The frontend stores the token and sends it back as
    // a Bearer header on every request after. AccessTokenExpiresAtUtc
    // lets the client proactively refresh instead of waiting for a 401.
    // PremiumUnlocked is one flag covering every premium tool at once —
    // there's a single purchase, not one per tool. The refresh token
    // itself is never part of this shape, it only ever travels as an
    // HttpOnly cookie.
    public class AuthResponse
    {
        public string Token { get; set; } = string.Empty;
        public DateTime AccessTokenExpiresAtUtc { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool PremiumUnlocked { get; set; }
    }

    // Internal service -> controller handoff, produced by every
    // IAuthService method that issues tokens. Never serialized
    // directly: AuthController unpacks Response into the JSON body and
    // uses RefreshToken/RefreshTokenExpiresAtUtc to set the cookie.
    public class AuthResult
    {
        public AuthResponse Response { get; set; } = new();
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime RefreshTokenExpiresAtUtc { get; set; }
    }
}