using System;
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

        public async Task<AuthResponse> VerifyAndGrantAsync(Guid userId, string paypalOrderId)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
                       ?? throw new InvalidOperationException("User not found.");

            var alreadyUnlocked = await _db.PremiumPurchases.AnyAsync(p => p.UserId == userId);
            if (alreadyUnlocked)
            {
                throw new InvalidOperationException("You already have premium access.");
            }

            // Block replay before calling PayPal at all: a used order id
            // is rejected the same way whether it belongs to this user or
            // another one.
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
                PayPalOrderId = paypalOrderId,
                AmountPaid = order.AmountValue,
                Currency = order.CurrencyCode,
                PurchasedAtUtc = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();

            // The old token doesn't know premium is unlocked until it
            // expires, so the frontend needs a fresh one immediately.
            var generatedAccessToken = _jwtTokenGenerator.GenerateToken(user, premiumUnlocked: true);

            return new AuthResponse
            {
                Token = generatedAccessToken.Token,
                AccessTokenExpiresAtUtc = generatedAccessToken.ExpiresAtUtc,
                FullName = user.FullName,
                Email = user.Email,
                PremiumUnlocked = true,
            };
        }

        public async Task<PremiumStatusViewModel> GetStatusAsync(Guid userId)
        {
            var purchase = await _db.PremiumPurchases.FirstOrDefaultAsync(p => p.UserId == userId);

            return new PremiumStatusViewModel
            {
                IsUnlocked = purchase is not null,
                PurchasedAtUtc = purchase?.PurchasedAtUtc,
            };
        }
    }
}
