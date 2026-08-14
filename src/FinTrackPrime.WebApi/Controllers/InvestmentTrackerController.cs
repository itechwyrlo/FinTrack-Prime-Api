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
    [Route("api/investment-tracker")]
    [Authorize(Policy = "RequirePremium")]
    public class InvestmentTrackerController : ControllerBase
    {
        private readonly IInvestmentTrackerService _investmentTrackerService;

        public InvestmentTrackerController(IInvestmentTrackerService investmentTrackerService)
        {
            _investmentTrackerService = investmentTrackerService;
        }

        [HttpGet]
        public async Task<ActionResult<InvestmentPortfolioViewModel>> Get()
        {
            var portfolio = await _investmentTrackerService.GetPortfolioAsync(GetUserId());
            return Ok(portfolio);
        }

        [HttpPost]
        public async Task<ActionResult<InvestmentHoldingViewModel>> Add(CreateInvestmentHoldingRequest request)
        {
            var holding = await _investmentTrackerService.AddHoldingAsync(GetUserId(), request);
            return Ok(holding);
        }

        [HttpPut("{holdingId:guid}")]
        public async Task<ActionResult<InvestmentHoldingViewModel>> Update(Guid holdingId, UpdateInvestmentHoldingRequest request)
        {
            try
            {
                var holding = await _investmentTrackerService.UpdateHoldingAsync(GetUserId(), holdingId, request);
                return Ok(holding);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpDelete("{holdingId:guid}")]
        public async Task<IActionResult> Remove(Guid holdingId)
        {
            try
            {
                await _investmentTrackerService.RemoveHoldingAsync(GetUserId(), holdingId);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
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
