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
    [Route("api/cash-flow")]
    [Authorize]
    public class CashFlowController : ControllerBase
    {
        private readonly ICashFlowService _cashFlowService;

        public CashFlowController(ICashFlowService cashFlowService)
        {
            _cashFlowService = cashFlowService;
        }

        [HttpGet]
        public async Task<ActionResult<CashFlowViewModel>> Get()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userId = Guid.Parse(subClaim!);

            var cashFlow = await _cashFlowService.GetCashFlowAsync(userId);
            return Ok(cashFlow);
        }
    }
}
