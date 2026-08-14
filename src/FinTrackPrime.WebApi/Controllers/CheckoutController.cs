using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/checkout")]
    [Authorize]
    public class CheckoutController : ControllerBase
    {
        private readonly IPremiumAccessService _premiumAccessService;

        public CheckoutController(IPremiumAccessService premiumAccessService)
        {
            _premiumAccessService = premiumAccessService;
        }

        [HttpGet("status")]
        public async Task<ActionResult<PremiumStatusViewModel>> GetStatus()
        {
            var status = await _premiumAccessService.GetStatusAsync(GetUserId());
            return Ok(status);
        }

        // Called after the frontend's PayPal button reports approval.
        // Nothing is trusted from that report except the order id; this
        // endpoint re-checks the order with PayPal before granting
        // anything. A successful call unlocks every premium tool at
        // once, since this is the one purchase that covers all of them.
        [HttpPost("verify")]
        public async Task<ActionResult<AuthResponse>> Verify(VerifyPurchaseRequest request)
        {
            try
            {
                var result = await _premiumAccessService.VerifyAndGrantAsync(
                    GetUserId(), request.PayPalOrderId);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private Guid GetUserId()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}