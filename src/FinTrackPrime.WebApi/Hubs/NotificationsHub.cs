using System;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FinTrackPrime.WebApi.Hubs
{
    // One group per user (named after their UserId) — this is the only
    // addressing scheme in use. A connection is never added to any group
    // but its own, so a push to Clients.Group(userId) can only ever
    // reach that one user's own connections.
    [Authorize]
    public class NotificationsHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GetUserId().ToString());
            await base.OnConnectedAsync();
        }

        private Guid GetUserId()
        {
            var subClaim = Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                            ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.Parse(subClaim!);
        }
    }
}
