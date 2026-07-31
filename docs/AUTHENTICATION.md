# FinTrackPrime — Authentication Flow Reference

This documents the current backend authentication system: register, login, Google login, refresh tokens, and logout. It reflects the recent migration `20260731091142_AdditionalFieldForAuth`, which added refresh-token rotation and Google login support.

## Endpoints

Base route: `/api/auth`

| Verb | Route | Auth required | Body | Success | Notes |
|---|---|---|---|---|---|
| POST | `/api/auth/register` | No | `RegisterRequest` | `200 OK` → `AuthResponse` | Sets `ftp_refresh` cookie. `409 Conflict` if email taken |
| POST | `/api/auth/login` | No | `LoginRequest` | `200 OK` → `AuthResponse` | Sets `ftp_refresh` cookie. `401` on bad credentials |
| POST | `/api/auth/google` | No | `GoogleLoginRequest` | `200 OK` → `AuthResponse` | Sets `ftp_refresh` cookie. `401` on invalid Google credential |
| POST | `/api/auth/refresh` | No (cookie) | *(empty)* | `200 OK` → `AuthResponse` | Reads/rotates `ftp_refresh` cookie. `401` if missing/invalid/expired/reused |
| POST | `/api/auth/logout` | No (cookie) | *(empty)* | `204 No Content` | Revokes current session, clears cookie |

The refresh token is **never** present in any JSON response body — only in the `ftp_refresh` HttpOnly cookie. `/refresh` and `/logout` take no request body at all; the token comes from the cookie automatically.

## Request DTOs

```csharp
public class RegisterRequest
{
    [Required, MaxLength(120)] public string FullName { get; set; }
    [Required, EmailAddress]   public string Email { get; set; }
    [Required, MinLength(8)]   public string Password { get; set; }
}

public class LoginRequest
{
    [Required, EmailAddress] public string Email { get; set; }
    [Required]                public string Password { get; set; }
}

public class GoogleLoginRequest
{
    [Required] public string IdToken { get; set; }
}
```

Validation failures produce the default ASP.NET Core `400` `ValidationProblemDetails`:
```json
{ "type": "...", "title": "One or more validation errors occurred.", "status": 400,
  "errors": { "Email": ["The Email field is required."] }, "traceId": "..." }
```

## Response shape — `AuthResponse`

Returned by register, login, google, and refresh (identical shape every time):

```json
{
  "token": "eyJhbGciOi...",
  "accessTokenExpiresAtUtc": "2026-07-31T09:26:42.123Z",
  "fullName": "Jane Doe",
  "email": "jane@example.com",
  "unlockedTools": ["LoanCalculator", "RetirementPlanner"]
}
```

- Field is `token`, **not** `accessToken`.
- `accessTokenExpiresAtUtc` is an absolute UTC ISO timestamp, **not** an `expiresIn` seconds duration.
- No `refreshToken` field — ever.
- `unlockedTools` is an array of enum names as strings (`LoanCalculator`, `InvestmentTracker`, `RetirementPlanner`, `FinancialStatement`).
- User data (`fullName`, `email`) is flattened into this object — there is no nested `user` object.

Auth-failure error shape (login/register/google/refresh business errors):
```json
{ "message": "Invalid email or password." }
```
Note this differs from the validation-error shape above — two distinct error formats exist depending on failure type.

## Token lifetimes

| Token | Lifetime | Source of truth |
|---|---|---|
| Access token (JWT) | **15 minutes** | `Jwt:AccessTokenMinutes` in `appsettings.json` |
| Refresh token | **30 days** | `RefreshToken:ExpiryDays` in `appsettings.json` |

JWT signing: symmetric HMAC-SHA256, `Issuer = FinTrackPrime.WebApi`, `Audience = FinTrackPrime.Client`. Claims include `sub` (user id), `email`, `fullName`, and one `unlock:{ToolName} = "True"` claim per unlocked premium tool.

## How the access token is sent/validated

Standard bearer auth — the client attaches:
```
Authorization: Bearer <token>
```
on every authenticated request. The server validates issuer, audience, lifetime, and signature via `AddJwtBearer`. It does **not** read the access token from a cookie.

## Refresh token mechanics

- Delivered only via an **HttpOnly cookie** named `ftp_refresh`, scoped to `Path=/api/auth` (browser will not attach it to any other route).
  - Development: `Secure=false`, `SameSite=Lax` (plain `http://localhost`).
  - Non-dev: `Secure=true`, `SameSite=None` (required for a cross-origin deployed frontend).
- Server stores only a SHA-256 hash of the token, never the raw value.
- **Rotation with reuse detection**: every refresh call invalidates the current token and issues a new one sharing the same `FamilyId`. If a token that was already rotated away is presented again (replay/theft), the **entire family is revoked** and `401 "Refresh token has already been used."` is returned — all sessions descended from that login are killed, forcing re-login.
- `/api/auth/logout` revokes only the current session's family and clears the cookie. It does not revoke other devices/sessions.
- Because the refresh token lives in a cookie, **cross-origin requests must be made with credentials included** (`fetch(..., { credentials: "include" })` or `axios.defaults.withCredentials = true`), and the backend CORS policy (`Cors:AllowedOrigins`) must list the exact frontend origin (default configured: `http://localhost:5173`) — `AllowCredentials()` is enabled server-side, which is incompatible with a wildcard `*` origin.

## Google login flow

The client, **not the backend**, talks to Google:

1. Client integrates Google Identity Services (GIS) in the browser and obtains a Google-signed **ID token** (JWT) from a successful Google sign-in (button or One Tap).
2. Client `POST`s `{ "idToken": "<google id token>" }` to `/api/auth/google`.
3. Backend validates the ID token itself against `Google:ClientId` (server-side config — **currently a placeholder value in `appsettings.json` and must be set to a real Google OAuth Web Client ID before this works**).
4. Backend matches/creates the user:
   - Match by `GoogleId` first.
   - If not found and Google reports the email as verified, fall back to matching by `Email` and link the existing account (`GoogleId` set on it).
   - Otherwise create a new Google-only account (no password hash/salt).
5. Response is the same `AuthResponse` shape as login/register, plus the `ftp_refresh` cookie.

There is no backend redirect/callback route — the client owns the entire Google OAuth popup/One-Tap UX and only ever sends the resulting ID token to `/api/auth/google`.

## What's new in the DB (relevant to auth)

- `Users.PasswordHash` / `Users.PasswordSalt` are now **nullable** — null for Google-only accounts.
- `Users.GoogleId` (nullable, unique when present).
- New `RefreshTokens` table, keyed by hashed token, with `FamilyId` for rotation chains.
- No `EmailConfirmed` field exists — there is no local email-confirmation flow.

## Known gaps to be aware of

- `Google:ClientId` in `appsettings.json` is a placeholder and must be replaced with the real client ID for Google login to function end-to-end.
- Error responses are inconsistent: `{ message }` for business-rule failures vs. ASP.NET's `ValidationProblemDetails` for model validation vs. an unshaped `500` for unhandled exceptions (no global exception handler is registered).
