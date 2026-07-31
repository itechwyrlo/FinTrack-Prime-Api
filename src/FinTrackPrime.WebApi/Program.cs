using System.Text;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Persistence;
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
builder.Services.AddScoped<ILoanCalculatorService, LoanCalculatorService>();
builder.Services.AddScoped<IInvestmentTrackerService, InvestmentTrackerService>();
builder.Services.AddScoped<IRetirementPlannerService, RetirementPlannerService>();
builder.Services.AddScoped<IFinancialStatementService, FinancialStatementService>();

// Typed HttpClient: BaseAddress comes from config so switching between
// PayPal's sandbox and live API is a config change, not a code change.
builder.Services.AddHttpClient<IPayPalClient, PayPalClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PayPal:ApiBaseUrl"]!);
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
    });

builder.Services.AddAuthorization(options =>
{
    // Backed by the per-tool claims baked in at issuance (see
    // JwtTokenGenerator). Each of the four premium tool controllers
    // uses its own policy, since owning one tool says nothing about
    // owning the others.
    options.AddPolicy("RequireLoanCalculator", policy =>
        policy.RequireClaim("unlock:LoanCalculator", true.ToString()));
    options.AddPolicy("RequireInvestmentTracker", policy =>
        policy.RequireClaim("unlock:InvestmentTracker", true.ToString()));
    options.AddPolicy("RequireRetirementPlanner", policy =>
        policy.RequireClaim("unlock:RetirementPlanner", true.ToString()));
    options.AddPolicy("RequireFinancialStatement", policy =>
        policy.RequireClaim("unlock:FinancialStatement", true.ToString()));
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

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Without this, enums like PremiumTool, BudgetCategoryType, and
        // TransactionDirection serialize as plain numbers (0, 1), but
        // the frontend sends and expects them as text ("LoanCalculator",
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

app.Run();