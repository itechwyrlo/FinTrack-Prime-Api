using System;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using FinTrackPrime.WebApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IWebHostEnvironment _env;

        public AuthController(IAuthService authService, IWebHostEnvironment env)
        {
            _authService = authService;
            _env = env;
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
        {
            try
            {
                var result = await _authService.RegisterAsync(request);
                SetRefreshCookie(result);
                return Ok(result.Response);
            }
            catch (InvalidOperationException ex)
            {
                // Expected validation failure (duplicate email), not a
                // server error, so this returns 409 rather than 500.
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
        {
            try
            {
                var result = await _authService.LoginAsync(request);
                SetRefreshCookie(result);
                return Ok(result.Response);
            }
            catch (InvalidOperationException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
        }

        [HttpPost("google")]
        public async Task<ActionResult<AuthResponse>> Google(GoogleLoginRequest request)
        {
            try
            {
                var result = await _authService.LoginWithGoogleAsync(request.IdToken);
                SetRefreshCookie(result);
                return Ok(result.Response);
            }
            catch (InvalidOperationException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
        }

        [HttpPost("refresh")]
        public async Task<ActionResult<AuthResponse>> Refresh()
        {
            var rawToken = Request.Cookies[RefreshTokenCookie.Name];
            if (string.IsNullOrEmpty(rawToken))
            {
                return Unauthorized(new { message = "No refresh token was provided." });
            }

            try
            {
                var result = await _authService.RefreshAsync(rawToken);
                SetRefreshCookie(result);
                return Ok(result.Response);
            }
            catch (InvalidOperationException ex)
            {
                // Any refresh failure (unknown/expired/reused token)
                // clears the cookie too, so the client doesn't keep
                // resending a dead cookie on every subsequent call.
                Response.Cookies.Delete(RefreshTokenCookie.Name, RefreshTokenCookie.BuildForDelete(_env));
                return Unauthorized(new { message = ex.Message });
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            var rawToken = Request.Cookies[RefreshTokenCookie.Name];
            if (!string.IsNullOrEmpty(rawToken))
            {
                await _authService.LogoutAsync(rawToken);
            }

            Response.Cookies.Delete(RefreshTokenCookie.Name, RefreshTokenCookie.BuildForDelete(_env));
            return NoContent();
        }

        private void SetRefreshCookie(AuthResult result)
        {
            Response.Cookies.Append(
                RefreshTokenCookie.Name,
                result.RefreshToken,
                RefreshTokenCookie.Build(_env, result.RefreshTokenExpiresAtUtc));
        }
    }
}
