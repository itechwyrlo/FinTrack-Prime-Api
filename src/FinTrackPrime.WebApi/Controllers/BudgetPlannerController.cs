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
    [Route("api/budget-planner")]
    [Authorize]
    public class BudgetPlannerController : ControllerBase
    {
        private readonly IBudgetPlannerService _budgetPlannerService;

        public BudgetPlannerController(IBudgetPlannerService budgetPlannerService)
        {
            _budgetPlannerService = budgetPlannerService;
        }

        [HttpGet]
        public async Task<ActionResult<BudgetPlannerViewModel>> Get()
        {
            var plan = await _budgetPlannerService.GetPlannerAsync(GetUserId());
            return Ok(plan);
        }

        [HttpPost]
        public async Task<ActionResult<BudgetCategoryViewModel>> Add(CreateBudgetCategoryRequest request)
        {
            var category = await _budgetPlannerService.AddCategoryAsync(GetUserId(), request);
            return Ok(category);
        }

        [HttpPut("{categoryId:guid}")]
        public async Task<ActionResult<BudgetCategoryViewModel>> Update(Guid categoryId, UpdateBudgetCategoryRequest request)
        {
            try
            {
                var category = await _budgetPlannerService.UpdateCategoryAsync(GetUserId(), categoryId, request);
                return Ok(category);
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpDelete("{categoryId:guid}")]
        public async Task<IActionResult> Remove(Guid categoryId)
        {
            try
            {
                await _budgetPlannerService.RemoveCategoryAsync(GetUserId(), categoryId);
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
