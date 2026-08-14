using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/bank-link")]
    [Authorize]
    public class BankLinkController : ControllerBase
    {
        private readonly IBankLinkService _bankLinkService;
        private readonly IConfiguration _config;

        public BankLinkController(IBankLinkService bankLinkService, IConfiguration config)
        {
            _bankLinkService = bankLinkService;
            _config = config;
        }

        [HttpPost("token")]
        public async Task<ActionResult<StartLinkResponse>> StartLink()
        {
            try
            {
                var redirectUri = _config["Finverse:RedirectUri"]!;
                var linkUrl = await _bankLinkService.StartLinkAsync(GetUserId(), redirectUri);
                return Ok(new StartLinkResponse { LinkUrl = linkUrl });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("complete")]
        public async Task<ActionResult<DashboardViewModel>> CompleteLink(CompleteLinkRequest request)
        {
            try
            {
                var dashboard = await _bankLinkService.CompleteLinkAsync(GetUserId(), request.LinkCode);
                return Ok(dashboard);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("sync")]
        public async Task<ActionResult<DashboardViewModel>> Sync()
        {
            var dashboard = await _bankLinkService.SyncAsync(GetUserId());
            return Ok(dashboard);
        }

        // Removes every linked institution (and its synced accounts/
        // transactions) for this user, so they can go through the Connect
        // flow again from scratch. Does not call Finverse's own unlink
        // API — this only clears this app's copy of the connection.
        [HttpDelete]
        public async Task<IActionResult> DisconnectAll()
        {
            await _bankLinkService.DisconnectAllAsync(GetUserId());
            return NoContent();
        }

        private Guid GetUserId()
        {
            var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
