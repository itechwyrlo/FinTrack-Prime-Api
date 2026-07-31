using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class AuthService : IAuthService
    {
        private const int SaltSizeBytes = 16;
        private const int HashSizeBytes = 32;
        private const int Pbkdf2Iterations = 100_000;
        private const int RefreshTokenBytes = 32;
        private const int DefaultRefreshTokenExpiryDays = 30;

        private readonly FinTrackDbContext _db;
        private readonly IJwtTokenGenerator _jwtTokenGenerator;
        private readonly IBudgetPlannerService _budgetPlannerService;
        private readonly IConfiguration _config;

        public AuthService(
            FinTrackDbContext db,
            IJwtTokenGenerator jwtTokenGenerator,
            IBudgetPlannerService budgetPlannerService,
            IConfiguration config)
        {
            _db = db;
            _jwtTokenGenerator = jwtTokenGenerator;
            _budgetPlannerService = budgetPlannerService;
            _config = config;
        }

        public async Task<AuthResult> RegisterAsync(RegisterRequest request)
        {
            var emailNormalized = request.Email.Trim().ToLowerInvariant();

            var alreadyExists = await _db.Users.AnyAsync(u => u.Email == emailNormalized);
            if (alreadyExists)
            {
                throw new InvalidOperationException("An account with this email already exists.");
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var hash = HashPassword(request.Password, salt);

            var user = new User
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName.Trim(),
                Email = emailNormalized,
                PasswordHash = Convert.ToBase64String(hash),
                PasswordSalt = Convert.ToBase64String(salt),
                CreatedAtUtc = DateTime.UtcNow,
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            // New users start with a budget plan template (just category
            // names, all amounts at 0) so Budget Planner isn't a blank
            // page. They do NOT get fake accounts or transactions,
            // those are real financial data and only exist once the
            // user actually creates them.
            await _budgetPlannerService.SeedDefaultCategoriesAsync(user.Id);

            // A brand-new user owns no premium tools yet, and this is a
            // fresh login session, so it starts its own refresh-token
            // family.
            return await IssueTokensAsync(user, new List<PremiumTool>(), Guid.NewGuid());
        }

        public async Task<AuthResult> LoginAsync(LoginRequest request)
        {
            var emailNormalized = request.Email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == emailNormalized);

            // A null PasswordHash means this account only ever signed up
            // via Google and has no local password to check. Same
            // generic message as "no such user" / "wrong password" on
            // purpose, so this endpoint can't be used to discover which
            // accounts exist or how they authenticate.
            if (user is null || user.PasswordHash is null || user.PasswordSalt is null
                || !VerifyPassword(request.Password, user.PasswordSalt, user.PasswordHash))
            {
                throw new InvalidOperationException("Invalid email or password.");
            }

            var unlockedTools = await GetUnlockedToolsAsync(user.Id);
            return await IssueTokensAsync(user, unlockedTools, Guid.NewGuid());
        }

        public async Task<AuthResult> LoginWithGoogleAsync(string googleIdToken)
        {
            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(googleIdToken, new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { _config["Google:ClientId"]! },
                });
            }
            catch (InvalidJwtException)
            {
                throw new InvalidOperationException("Invalid Google credential.");
            }

            if (string.IsNullOrWhiteSpace(payload.Email))
            {
                throw new InvalidOperationException("Invalid Google credential.");
            }

            var emailNormalized = payload.Email.Trim().ToLowerInvariant();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);
            if (user is null && payload.EmailVerified)
            {
                // Only trust an email match to link an existing password
                // account when Google has verified that email; otherwise
                // an attacker controlling an unverified hosted-domain
                // address could take over a victim's account by email.
                user = await _db.Users.FirstOrDefaultAsync(u => u.Email == emailNormalized);
            }

            var isNewUser = user is null;
            if (user is null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = string.IsNullOrWhiteSpace(payload.Name) ? emailNormalized : payload.Name,
                    Email = emailNormalized,
                    GoogleId = payload.Subject,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                _db.Users.Add(user);
            }
            else if (user.GoogleId is null)
            {
                // A password account signing in with Google for the
                // first time using the same email: link rather than
                // create a duplicate account.
                user.GoogleId = payload.Subject;
            }

            await _db.SaveChangesAsync();

            if (isNewUser)
            {
                await _budgetPlannerService.SeedDefaultCategoriesAsync(user.Id);
            }

            var unlockedTools = await GetUnlockedToolsAsync(user.Id);
            return await IssueTokensAsync(user, unlockedTools, Guid.NewGuid());
        }

        public async Task<AuthResult> RefreshAsync(string refreshToken)
        {
            var tokenHash = HashToken(refreshToken);
            var existing = await _db.RefreshTokens
                .Include(rt => rt.User)
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);

            if (existing is null)
            {
                throw new InvalidOperationException("Invalid refresh token.");
            }

            if (existing.RevokedAtUtc is not null)
            {
                // This exact token was already rotated away once before.
                // Being presented again means it's most likely a stolen
                // cookie being replayed after the legitimate client
                // already moved on — burn the whole session chain rather
                // than trusting only this one token.
                var liveFamilyTokens = await _db.RefreshTokens
                    .Where(rt => rt.FamilyId == existing.FamilyId && rt.RevokedAtUtc == null)
                    .ToListAsync();

                foreach (var token in liveFamilyTokens)
                {
                    token.RevokedAtUtc = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();
                throw new InvalidOperationException("Refresh token has already been used.");
            }

            if (existing.ExpiresAtUtc < DateTime.UtcNow)
            {
                throw new InvalidOperationException("Refresh token has expired.");
            }

            existing.RevokedAtUtc = DateTime.UtcNow;

            var user = existing.User!;
            var unlockedTools = await GetUnlockedToolsAsync(user.Id);

            // Same FamilyId: this is a rotation within the same session,
            // not a new login.
            return await IssueTokensAsync(user, unlockedTools, existing.FamilyId);
        }

        public async Task LogoutAsync(string refreshToken)
        {
            var tokenHash = HashToken(refreshToken);
            var existing = await _db.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash && rt.RevokedAtUtc == null);

            if (existing is not null)
            {
                existing.RevokedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        private async Task<AuthResult> IssueTokensAsync(User user, List<PremiumTool> unlockedTools, Guid familyId)
        {
            var generatedAccessToken = _jwtTokenGenerator.GenerateToken(user, unlockedTools);

            var rawRefreshToken = GenerateRawRefreshToken();
            var refreshTokenExpiryDays = _config.GetSection("RefreshToken").GetValue<int?>("ExpiryDays")
                                          ?? DefaultRefreshTokenExpiryDays;
            var refreshTokenExpiresAtUtc = DateTime.UtcNow.AddDays(refreshTokenExpiryDays);

            _db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                FamilyId = familyId,
                TokenHash = HashToken(rawRefreshToken),
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = refreshTokenExpiresAtUtc,
            });

            // Persists both the new row above and, when called from
            // RefreshAsync, the RevokedAtUtc change made to the token
            // being rotated away, since that entity is already tracked.
            await _db.SaveChangesAsync();

            return new AuthResult
            {
                Response = new AuthResponse
                {
                    Token = generatedAccessToken.Token,
                    AccessTokenExpiresAtUtc = generatedAccessToken.ExpiresAtUtc,
                    FullName = user.FullName,
                    Email = user.Email,
                    UnlockedTools = unlockedTools,
                },
                RefreshToken = rawRefreshToken,
                RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc,
            };
        }

        private async Task<List<PremiumTool>> GetUnlockedToolsAsync(Guid userId)
        {
            return await _db.PremiumPurchases
                .Where(p => p.UserId == userId)
                .Select(p => p.Tool)
                .ToListAsync();
        }

        private static string GenerateRawRefreshToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(RefreshTokenBytes);
            // Base64url (RFC 4648 §5): safe as a cookie value with no
            // extra escaping, unlike standard Base64's '+', '/', '='.
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static string HashToken(string rawToken)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
            return Convert.ToBase64String(bytes);
        }

        private static byte[] HashPassword(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        }

        private static bool VerifyPassword(string password, string storedSaltBase64, string storedHashBase64)
        {
            var salt = Convert.FromBase64String(storedSaltBase64);
            var expectedHash = Convert.FromBase64String(storedHashBase64);
            var actualHash = HashPassword(password, salt);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
    }
}
