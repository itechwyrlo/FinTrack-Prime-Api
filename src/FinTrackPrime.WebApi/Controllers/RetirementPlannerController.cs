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
    [Route("api/retirement-planner")]
    [Authorize(Policy = "RequireRetirementPlanner")]
    public class RetirementPlannerController : ControllerBase
    {
        private readonly IRetirementPlannerService _retirementPlannerService;

        public RetirementPlannerController(IRetirementPlannerService retirementPlannerService)
        {
            _retirementPlannerService = retirementPlannerService;
        }

        [HttpGet]
        public async Task<ActionResult<RetirementPlanViewModel>> Get()
        {
            var plan = await _retirementPlannerService.GetPlanAsync(GetUserId());
            return Ok(plan);
        }

        [HttpPut]
        public async Task<ActionResult<RetirementPlanViewModel>> Save(RetirementPlanInputRequest request)
        {
            try
            {
                var plan = await _retirementPlannerService.SavePlanAsync(GetUserId(), request);
                return Ok(plan);
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
