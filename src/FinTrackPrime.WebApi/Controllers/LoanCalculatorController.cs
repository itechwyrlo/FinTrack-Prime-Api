using System;
using System.Collections.Generic;
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
    [Route("api/loan-calculator")]
    [Authorize(Policy = "RequirePremium")]
    public class LoanCalculatorController : ControllerBase
    {
        private readonly ILoanCalculatorService _loanCalculatorService;

        public LoanCalculatorController(ILoanCalculatorService loanCalculatorService)
        {
            _loanCalculatorService = loanCalculatorService;
        }

        [HttpGet("rates")]
        public async Task<ActionResult<List<LoanRateViewModel>>> GetRates()
        {
            var rates = await _loanCalculatorService.GetRatesAsync();
            return Ok(rates);
        }

        [HttpPost("calculate")]
        public async Task<ActionResult<LoanCalculationResultViewModel>> Calculate(LoanCalculationRequest request)
        {
            try
            {
                var result = await _loanCalculatorService.CalculateAsync(request);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Also a POST-as-compute: the result depends on the caller's
        // tracked income/expenses/liabilities, but nothing about the
        // affordability check itself is saved.
        [HttpPost("affordability")]
        public async Task<ActionResult<LoanAffordabilityResultViewModel>> CheckAffordability(LoanAffordabilityRequest request)
        {
            try
            {
                var result = await _loanCalculatorService.CheckAffordabilityAsync(GetUserId(), request);
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
