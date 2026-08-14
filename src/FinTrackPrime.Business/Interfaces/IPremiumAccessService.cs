using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IPremiumAccessService
    {
        // Verifies the PayPal order directly with PayPal, checks it
        // hasn't already been used and that the user doesn't already
        // have premium access, and only then unlocks every premium tool
        // for that user and returns a fresh JWT reflecting it. Throws
        // InvalidOperationException with a user-facing message on any
        // failure (unpaid order, wrong amount, already-used order,
        // already unlocked).
        Task<AuthResponse> VerifyAndGrantAsync(Guid userId, string paypalOrderId);

        Task<PremiumStatusViewModel> GetStatusAsync(Guid userId);
    }
}
