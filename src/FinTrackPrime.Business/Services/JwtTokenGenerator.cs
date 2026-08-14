using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace FinTrackPrime.Business.Services
{
    public class JwtTokenGenerator : IJwtTokenGenerator
    {
        private const int DefaultAccessTokenMinutes = 15;

        private readonly IConfiguration _config;

        public JwtTokenGenerator(IConfiguration config)
        {
            _config = config;
        }

        public GeneratedAccessToken GenerateToken(User user, bool premiumUnlocked)
        {
            var jwtSection = _config.GetSection("Jwt");
            var keyBytes = Encoding.UTF8.GetBytes(jwtSection["Key"]!);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, user.Email),
                new("fullName", user.FullName),
            };

            // Only added when actually unlocked. The RequirePremium
            // policy checks for the presence of this claim, so a user
            // who hasn't bought premium simply has no claim, rather than
            // a claim set to "False".
            if (premiumUnlocked)
            {
                claims.Add(new Claim("unlock:premium", "True"));
            }

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);

            var accessTokenMinutes = jwtSection.GetValue<int?>("AccessTokenMinutes") ?? DefaultAccessTokenMinutes;
            var expiresAtUtc = DateTime.UtcNow.AddMinutes(accessTokenMinutes);

            var token = new JwtSecurityToken(
                issuer: jwtSection["Issuer"],
                audience: jwtSection["Audience"],
                claims: claims,
                expires: expiresAtUtc,
                signingCredentials: credentials);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
            return new GeneratedAccessToken(tokenString, expiresAtUtc);
        }
    }
}
