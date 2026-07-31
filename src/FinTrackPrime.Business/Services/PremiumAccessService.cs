using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class PremiumAccessService : IPremiumAccessService
    {
        private const string CompletedStatus = "COMPLETED";

        private readonly FinTrackDbContext _db;
        private readonly IPayPalClient _payPalClient;
        private readonly IJwtTokenGenerator _jwtTokenGenerator;
        private readonly IConfiguration _config;

        public PremiumAccessService(
            FinTrackDbContext db,
            IPayPalClient payPalClient,
            IJwtTokenGenerator jwtTokenGenerator,
            IConfiguration config)
        {
            _db = db;
            _payPalClient = payPalClient;
            _jwtTokenGenerator = jwtTokenGenerator;
            _config = config;
        }

        public async Task<AuthResponse> VerifyAndGrantAsync(Guid userId, string paypalOrderId, PremiumTool tool)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
                       ?? throw new InvalidOperationException("User not found.");

            var alreadyOwnsThisTool = await _db.PremiumPurchases
                .AnyAsync(p => p.UserId == userId && p.Tool == tool);
            if (alreadyOwnsThisTool)
            {
                throw new InvalidOperationException("You already have access to this tool.");
            }

            // Block replay before calling PayPal at all: a used order id
            // is rejected the same way whether it belongs to this user or
            // another one, and regardless of which tool it claims to be for.
            var orderAlreadyUsed = await _db.PremiumPurchases.AnyAsync(p => p.PayPalOrderId == paypalOrderId);
            if (orderAlreadyUsed)
            {
                throw new InvalidOperationException("This order has already been used.");
            }

            var order = await _payPalClient.GetOrderAsync(paypalOrderId);

            if (order.Status != CompletedStatus)
            {
                throw new InvalidOperationException($"Order is not completed (status: {order.Status}).");
            }

            // Same flat price for every tool right now. If tools ever
            // need different prices, this becomes a per-tool lookup
            // instead of one config value.
            var expectedPrice = decimal.Parse(_config["Premium:PriceUsd"] ?? "15.00");
            var expectedCurrency = _config["Premium:Currency"] ?? "USD";

            if (!string.Equals(order.CurrencyCode, expectedCurrency, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Order currency does not match the expected price.");
            }

            if (order.AmountValue < expectedPrice)
            {
                throw new InvalidOperationException("Order amount is less than the expected price.");
            }

            _db.PremiumPurchases.Add(new PremiumPurchase
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Tool = tool,
                PayPalOrderId = paypalOrderId,
                AmountPaid = order.AmountValue,
                Currency = order.CurrencyCode,
                PurchasedAtUtc = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();

            // The old token doesn't know about this tool until it
            // expires, so the frontend needs a fresh one immediately,
            // reflecting every tool the user owns now, this one included.
            var unlockedTools = await _db.PremiumPurchases
                .Where(p => p.UserId == user.Id)
                .Select(p => p.Tool)
                .ToListAsync();

            var generatedAccessToken = _jwtTokenGenerator.GenerateToken(user, unlockedTools);

            return new AuthResponse
            {
                Token = generatedAccessToken.Token,
                AccessTokenExpiresAtUtc = generatedAccessToken.ExpiresAtUtc,
                FullName = user.FullName,
                Email = user.Email,
                UnlockedTools = unlockedTools,
            };
        }

        public async Task<PremiumStatusViewModel> GetStatusAsync(Guid userId)
        {
            var purchases = await _db.PremiumPurchases
                .Where(p => p.UserId == userId)
                .OrderBy(p => p.PurchasedAtUtc)
                .ToListAsync();

            return new PremiumStatusViewModel
            {
                UnlockedTools = purchases
                    .Select(p => new UnlockedToolViewModel { Tool = p.Tool, PurchasedAtUtc = p.PurchasedAtUtc })
                    .ToList(),
            };
        }
    }
}