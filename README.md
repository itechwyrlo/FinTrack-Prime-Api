# FinTrack Prime — Backend (Phase 1 + Phase 2 + Phase 3 + Phase 4)

Clean Architecture, three projects:

- **FinTrackPrime.Models** — entities (User, Account, Transaction), view models
  (DTOs), and the EF Core DbContext. This is the only project that knows
  about SQL Server directly.
- **FinTrackPrime.Business** — interfaces and their implementations
  (AuthService, AccountService). All business logic and password/JWT
  handling live here. Depends only on Models.
- **FinTrackPrime.WebApi** — controllers and Program.cs. Depends only on
  Business, and only through its interfaces.

## Important: not compiled in this environment

This was written and reviewed by hand, but not built with `dotnet build`.
The sandbox that produced this code can't reach NuGet, so packages were
never restored or verified here. Run a full build on your own machine
before trusting it.

## Setup

1. Install the .NET 8 SDK if you don't have it.
2. From the solution root:
   ```
   dotnet restore
   dotnet build
   ```
3. Update `src/FinTrackPrime.WebApi/appsettings.json`:
   - `ConnectionStrings:Default` — point this at your SQL Server instance.
   - `Jwt:Key` — replace the placeholder with a real random secret, at
     least 32 characters. Don't commit a real secret to git; move it to
     `appsettings.Development.json` or user-secrets instead.
4. Create the database:
   ```
   dotnet tool install --global dotnet-ef   (if you don't have it)
   cd src/FinTrackPrime.WebApi
   dotnet ef migrations add InitialCreate --project ../FinTrackPrime.Models --startup-project .
   dotnet ef database update --project ../FinTrackPrime.Models --startup-project .
   ```
5. Run it:
   ```
   dotnet run --project src/FinTrackPrime.WebApi
   ```
   Swagger UI opens at `/swagger` in development mode.

## What's implemented in this phase

- `POST /api/auth/register` — creates a user, seeds two mock accounts
  (Checking, Savings) with starter transactions, returns a JWT.
- `POST /api/auth/login` — returns a JWT for an existing user.
- `GET /api/dashboard` — authenticated. Returns every account the caller
  owns, each with its recent transactions.

## Phase 2 additions

- `GET /api/budget-planner` — the user's budget categories plus planned
  income/expense/net totals. New users get six starter categories
  (Salary, Housing, Utilities, Groceries, Transportation, Savings),
  seeded at registration alongside the mock accounts.
- `POST /api/budget-planner` — add a category (`{ name, type }`).
- `PUT /api/budget-planner/{categoryId}` — rename a category and/or set
  its planned amount.
- `DELETE /api/budget-planner/{categoryId}` — remove a category.
- `GET /api/cash-flow` — total income, total expenses, and net across
  every account the user owns, an expense breakdown by category, and a
  month-by-month income/expense trend, all built from the same
  Transaction data the dashboard uses. No separate cash-flow data model;
  it's a different view of the same transactions.

All budget-planner writes check that the category belongs to the calling
user before touching it, so no user can edit or delete another user's
data by guessing an id.

## Phase 3 additions: premium checkout / paywall

- `GET /api/checkout/status` — whether the caller has premium access, and
  when they purchased it, if they have.
- `POST /api/checkout/verify` — takes `{ "payPalOrderId": "..." }` from
  the frontend after its PayPal button reports approval, then:
  1. Rejects it immediately if that order id has already been used
     (unique index on `PremiumPurchases.PayPalOrderId`, checked before
     calling PayPal at all).
  2. Calls PayPal's REST API directly (`IPayPalClient` /
     `PayPalClient.cs`) to fetch the order's real status and amount.
     Nothing from the browser is trusted on its own.
  3. Rejects it if the order isn't `COMPLETED`, or the amount/currency
     don't match `Premium:PriceUsd` / `Premium:Currency` in
     appsettings.json.
  4. Only then records the purchase, sets `User.HasPremiumAccess = true`,
     and returns a **new JWT** with that claim updated. The old token
     kept saying `false` until it expired, so the frontend needs to swap
     in the new one right after a successful verify call.

This closes the gap flagged in the original project analysis: before,
"payment succeeded in the browser" and "the file is actually protected"
were never connected. Now, premium access is granted only after the
backend independently confirms the order with PayPal.

### PayPal setup needed

You'll need a PayPal Developer account and a Sandbox app to get a real
`ClientId` / `ClientSecret` for `appsettings.json`. `ApiBaseUrl` is
already set to PayPal's sandbox endpoint
(`https://api-m.sandbox.paypal.com`); swap it to
`https://api-m.paypal.com` for production.

## Phase 4 additions: the four premium tools

Every endpoint below requires `[Authorize(Policy = "RequirePremium")]`, a
policy checked against the `hasPremiumAccess` claim baked into the JWT at
login/register/verify time (see `Program.cs`, `JwtTokenGenerator.cs`).
A user without premium access gets a 403, not a 401, since they're
authenticated, just not entitled to this resource.

**Loan Calculator** (`api/loan-calculator`)
- `POST /calculate` — takes principal, annual rate, term in months, and
  an optional extra monthly payment. Returns the required monthly
  payment, total interest, total paid, payoff month count, and a full
  month-by-month amortization schedule. Stateless: nothing is saved,
  since a "what-if" calculator doesn't need to remember every scenario
  a user tries.

**Investment Portfolio Tracker** (`api/investment-tracker`)
- `GET /` — every holding plus current value, gain/loss, and allocation
  percentage of the whole portfolio.
- `POST /`, `PUT /{holdingId}`, `DELETE /{holdingId}` — add, edit, or
  remove a position. Prices are user-entered, same as the original
  frontend-only tracker; there's no live market data feed.

**Retirement Planner** (`api/retirement-planner`)
- `GET /` — the user's saved plan and its projection, or a reasonable
  starter scenario if they haven't saved one yet (nothing is written
  until they do).
- `PUT /` — save current age, retirement age, current savings, monthly
  contribution, and expected return, and get back a year-by-year
  projected balance up to retirement.

**Financial Statement generator** (`api/financial-statement`)
- `GET /` — a personal balance sheet: assets (every bank account balance
  plus every investment holding's current value), liabilities
  (user-entered), and net worth. Nothing here is a separate ledger; it's
  a computed view over data the other tools already collected.
- `POST /liabilities`, `DELETE /liabilities/{id}` — manage the
  liabilities side, since there's no automatic source for debts.

## What's now complete

All four phases from the original plan are built: authentication and
account dashboard, Budget Planner and Cash Flow Dashboard (free tier),
PayPal-verified premium checkout, and the four premium tools gated
behind it. Still outside this backend's scope: the React frontend
itself, and anything Prof's guide ends up asking for once you're through
it.

