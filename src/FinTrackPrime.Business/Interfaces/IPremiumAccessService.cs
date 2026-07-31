using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IPremiumAccessService
    {
        // Verifies the PayPal order directly with PayPal, checks it
        // hasn't already been used and that the user doesn't already
        // own this specific tool, and only then unlocks that one tool
        // and returns a fresh JWT reflecting it. Throws
        // InvalidOperationException with a user-facing message on any
        // failure (unpaid order, wrong amount, already-used order,
        // already-owned tool).
        Task<AuthResponse> VerifyAndGrantAsync(Guid userId, string paypalOrderId, PremiumTool tool);

        Task<PremiumStatusViewModel> GetStatusAsync(Guid userId);
    }
}