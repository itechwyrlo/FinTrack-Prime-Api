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
    // Separate from DashboardController on purpose: that one is
    // read-only (the dashboard screen's single GET), this one is where
    // the user's own input actually enters the system.
    [ApiController]
    [Route("api/accounts")]
    [Authorize]
    public class AccountsController : ControllerBase
    {
        private readonly IAccountService _accountService;

        public AccountsController(IAccountService accountService)
        {
            _accountService = accountService;
        }

        [HttpPost]
        public async Task<ActionResult<AccountViewModel>> CreateAccount(CreateAccountRequest request)
        {
            var account = await _accountService.CreateAccountAsync(GetUserId(), request);
            return Ok(account);
        }

        [HttpPost("{accountId:guid}/transactions")]
        public async Task<ActionResult<TransactionViewModel>> AddTransaction(
            Guid accountId, CreateTransactionRequest request)
        {
            try
            {
                var transaction = await _accountService.AddTransactionAsync(GetUserId(), accountId, request);
                return Ok(transaction);
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