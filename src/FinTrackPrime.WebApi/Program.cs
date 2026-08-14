using System.Text;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.WebApi.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------- Fail fast on missing production secrets ----------
// appsettings.json ships with these blank on purpose (nothing real is
// committed to source control). In every environment but local dev,
// they must come from the host's environment variables instead — if
// one is still blank here, the app would otherwise start "successfully"
// with a broken JWT signer or no database, and fail confusingly later
// on the first request instead of at boot.
if (!builder.Environment.IsDevelopment())
{
    var required = new (string Key, string Value)[]
    {
        ("ConnectionStrings:Default", builder.Configuration.GetConnectionString("Default") ?? ""),
        ("Jwt:Key", builder.Configuration["Jwt:Key"] ?? ""),
        ("PayPal:ClientId", builder.Configuration["PayPal:ClientId"] ?? ""),
        ("PayPal:ClientSecret", builder.Configuration["PayPal:ClientSecret"] ?? ""),
        ("Finverse:ClientId", builder.Configuration["Finverse:ClientId"] ?? ""),
        ("Finverse:ClientSecret", builder.Configuration["Finverse:ClientSecret"] ?? ""),
    };

    var missing = required.Where(r => string.IsNullOrWhiteSpace(r.Value)).Select(r => r.Key).ToList();
    if (builder.Configuration["Jwt:Key"] is { Length: > 0 and < 32 })
    {
        missing.Add("Jwt:Key (must be at least 32 characters)");
    }

    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            "Missing required configuration for a non-development environment: "
            + string.Join(", ", missing)
            + ". Set these as environment variables on the host (e.g. Jwt__Key, ConnectionStrings__Default) "
            + "rather than committing them to appsettings.json.");
    }
}

// ---------- Persistence (Models layer) ----------
builder.Services.AddDbContext<FinTrackDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// ---------- Business layer ----------
// Controllers depend on the interfaces only; swapping an implementation
// later never requires touching the WebApi layer.
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IBudgetPlannerService, BudgetPlannerService>();
builder.Services.AddScoped<ICashFlowService, CashFlowService>();
builder.Services.AddScoped<IPremiumAccessService, PremiumAccessService>();
builder.Services.AddScoped<IBankLinkService, BankLinkService>();
builder.Services.AddScoped<ILoanCalculatorService, LoanCalculatorService>();
builder.Services.AddScoped<IInvestmentTrackerService, InvestmentTrackerService>();
builder.Services.AddScoped<IRetirementPlannerService, RetirementPlannerService>();
builder.Services.AddScoped<IFinancialStatementService, FinancialStatementService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Typed HttpClient: BaseAddress comes from config so switching between
// PayPal's sandbox and live API is a config change, not a code change.
builder.Services.AddHttpClient<IPayPalClient, PayPalClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PayPal:ApiBaseUrl"]!);
});

builder.Services.AddHttpClient<IFinverseClient, FinverseClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Finverse:ApiBaseUrl"]!);
});

builder.Services.AddHttpClient<ICryptoPriceClient, CryptoPriceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["CoinGecko:ApiBaseUrl"]!);
});

// ---------- Authentication ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"]!;

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        };
        // A browser can't set an Authorization header on a WebSocket
        // handshake, so the SignalR JS client sends the token as an
        // access_token query param instead (accessTokenFactory) — this
        // reads it back out only for hub requests. Every other
        // endpoint's existing header-based Bearer flow is untouched.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Backed by the "unlock:premium" claim baked in at issuance (see
    // JwtTokenGenerator). All four premium tool controllers share this
    // one policy, since premium is a single all-tools purchase now
    // rather than something bought per tool.
    options.AddPolicy("RequirePremium", policy =>
        policy.RequireClaim("unlock:premium", true.ToString()));
});

// ---------- CORS ----------
// The React dev server and the deployed frontend both need to call this
// API from a different origin. Tighten AllowedOrigins in appsettings for
// production instead of widening this policy.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        // AllowCredentials is required for the browser to send/receive
        // the HttpOnly refresh-token cookie on cross-origin requests.
        // It only works paired with explicit WithOrigins (already the
        // case here) — CORS forbids combining it with AllowAnyOrigin.
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Encrypts LinkedInstitution.AccessToken at rest (see BankLinkService).
builder.Services.AddDataProtection();

builder.Services.AddSignalR();
builder.Services.AddHostedService<FinTrackPrime.WebApi.BackgroundServices.SpendMonitorService>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Without this, enums like BudgetCategoryType and
        // TransactionDirection serialize as plain numbers (0, 1), but
        // the frontend sends and expects them as text ("Expense",
        // "Income"). This makes both sides agree.
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors("FrontendPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");

app.Run();