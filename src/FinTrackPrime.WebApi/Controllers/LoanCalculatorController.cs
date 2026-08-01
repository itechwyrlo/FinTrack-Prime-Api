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
    [Route("api/loan-calculator")]
    [Authorize(Policy = "RequireLoanCalculator")]
    public class LoanCalculatorController : ControllerBase
    {
        private readonly ILoanCalculatorService _loanCalculatorService;

        public LoanCalculatorController(ILoanCalculatorService loanCalculatorService)
        {
            _loanCalculatorService = loanCalculatorService;
        }

        // Stateless: nothing about a calculation is tied to the user or
        // saved, so this is a POST-as-compute rather than a resource.
        [HttpPost("calculate")]
        public ActionResult<LoanCalculationResultViewModel> Calculate(LoanCalculationRequest request)
        {
            var result = _loanCalculatorService.Calculate(request);
            return Ok(result);
        }

        // Also a POST-as-compute: the result depends on the caller's
        // tracked income/expenses/liabilities, but nothing about the
        // affordability check itself is saved.
        [HttpPost("affordability")]
        public async Task<ActionResult<LoanAffordabilityResultViewModel>> CheckAffordability(LoanAffordabilityRequest request)
        {
            var result = await _loanCalculatorService.CheckAffordabilityAsync(GetUserId(), request);
            return Ok(result);
        }

        private Guid GetUserId()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
