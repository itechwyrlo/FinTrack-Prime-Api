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
    [Route("api/[controller]")]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly IAccountService _accountService;

        public DashboardController(IAccountService accountService)
        {
            _accountService = accountService;
        }

        [HttpGet]
        public async Task<ActionResult<DashboardViewModel>> Get()
        {
            var userId = GetUserIdFromToken();
            var dashboard = await _accountService.GetDashboardAsync(userId);
            return Ok(dashboard);
        }

        // The JWT's "sub" claim carries the user id (set in AuthService).
        // Every authenticated controller reads it the same way, so a
        // user can only ever see their own data.
        private Guid GetUserIdFromToken()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
