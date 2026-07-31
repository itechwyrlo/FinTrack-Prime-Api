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
    [Route("api/financial-statement")]
    [Authorize(Policy = "RequireFinancialStatement")]
    public class FinancialStatementController : ControllerBase
    {
        private readonly IFinancialStatementService _financialStatementService;

        public FinancialStatementController(IFinancialStatementService financialStatementService)
        {
            _financialStatementService = financialStatementService;
        }

        [HttpGet]
        public async Task<ActionResult<FinancialStatementViewModel>> Get()
        {
            var statement = await _financialStatementService.GetStatementAsync(GetUserId());
            return Ok(statement);
        }

        [HttpPost("liabilities")]
        public async Task<ActionResult<LiabilityViewModel>> AddLiability(CreateLiabilityRequest request)
        {
            var liability = await _financialStatementService.AddLiabilityAsync(GetUserId(), request);
            return Ok(liability);
        }

        [HttpDelete("liabilities/{liabilityId:guid}")]
        public async Task<IActionResult> RemoveLiability(Guid liabilityId)
        {
            try
            {
                await _financialStatementService.RemoveLiabilityAsync(GetUserId(), liabilityId);
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
