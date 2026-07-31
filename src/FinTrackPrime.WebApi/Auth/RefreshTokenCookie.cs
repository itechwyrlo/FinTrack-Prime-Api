using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace FinTrackPrime.WebApi.Auth
{
    // Centralizes the refresh-token cookie's name and environment-aware
    // options so AuthController doesn't repeat this in every action.
    //
    // Development runs over plain http://localhost, where a Secure
    // cookie would be silently dropped by the browser, so it relaxes to
    // Secure=false/SameSite=Lax there. Anywhere else assumes real HTTPS
    // and uses Secure=true/SameSite=None, which also covers a deployed
    // frontend living on a different registrable domain than the API.
    public static class RefreshTokenCookie
    {
        public const string Name = "ftp_refresh";
        private const string CookiePath = "/api/auth";

        public static CookieOptions Build(IWebHostEnvironment env, DateTime expiresAtUtc)
        {
            var options = BaseOptions(env);
            options.Expires = expiresAtUtc;
            return options;
        }

        public static CookieOptions BuildForDelete(IWebHostEnvironment env)
        {
            return BaseOptions(env);
        }

        private static CookieOptions BaseOptions(IWebHostEnvironment env)
        {
            var isDevelopment = env.IsDevelopment();

            return new CookieOptions
            {
                HttpOnly = true,
                Secure = !isDevelopment,
                SameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.None,
                Path = CookiePath,
            };
        }
    }
}
