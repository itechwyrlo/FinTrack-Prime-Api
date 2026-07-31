using System;
using System.Collections.Generic;
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

        public GeneratedAccessToken GenerateToken(User user, IEnumerable<PremiumTool> unlockedTools)
        {
            var jwtSection = _config.GetSection("Jwt");
            var keyBytes = Encoding.UTF8.GetBytes(jwtSection["Key"]!);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, user.Email),
                new("fullName", user.FullName),
            };

            // One claim per unlocked tool, only added when actually
            // unlocked. RequireLoanCalculator (etc.) policies check for
            // the presence of these, so a tool the user hasn't bought
            // simply has no claim, rather than a claim set to "False".
            foreach (var tool in unlockedTools)
            {
                claims.Add(new Claim($"unlock:{tool}", "True"));
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
