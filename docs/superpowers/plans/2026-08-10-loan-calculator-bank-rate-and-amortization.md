# Loan Calculator: Bank-Offered Rate + Amortization Methods — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Loan Calculator's free-typed interest rate with a bank-managed, read-only rate resolved by loan type, and add three new amortization methods (Fixed Principal, Grace Period, Balloon) alongside the existing Equal-payment method.

**Architecture:** New `LoanRate` entity (one row per `LiabilityType`, bank-managed, no admin UI) resolved server-side — the client can no longer supply or influence a rate, since `AnnualInterestRatePercent` is removed from both request DTOs entirely. `LoanCalculatorService` gains a `BuildSchedule` dispatcher with one private builder method per `AmortizationMethod`, sharing the existing `CalculateRequiredMonthlyPayment` helper and `MaxScheduleMonths` safety cap. Frontend replaces the rate `Input` with a Loan Type `Select` (read-only rate display) and adds an independent Amortization Method `Select`.

**Tech Stack:** ASP.NET Core 10 / EF Core (SQL Server), xunit + EF InMemory for backend tests. React + TypeScript + TanStack Query frontend — no frontend test framework exists in this project; frontend tasks are implementation + manual browser verification, matching precedent from the three prior features this session.

## Global Constraints

- **Migration approach has changed from earlier this session.** The three prior features hand-edited a single cumulative `InitialMigration` on the theory that it was still uncommitted and pre-launch. That approach led to running `dotnet ef migrations remove` at one point, which deleted the entire `Migrations` folder (migration + designer + snapshot all at once) and had to be recovered by regenerating from scratch. The migration on disk today (`20260810083912_InitialMigration`) is that freshly-regenerated baseline. **This plan adds a proper new incremental migration via `dotnet ef migrations add`, not another hand-edit of `InitialMigration`** — safer, and standard practice going forward.
- `AnnualInterestRatePercent` does not exist anywhere in `LoanCalculationRequest`/`LoanAffordabilityRequest` after this plan — there is no field for a client to send a rate through, even by calling the API directly with a raw HTTP client.
- `LoanRate` has no `UserId` — it's bank-wide data, not per-user, unlike every other new entity added this session.
- `ExtraMonthlyPayment` behaves identically across all four amortization methods: added to whatever base payment is due that period, always reducing principal. No per-method special-casing.
- Every amortization builder reuses the existing `MaxScheduleMonths` (600) safety cap and rounds every row to 2 decimals, matching today's `Calculate` method exactly.
- `RequiredMonthlyPayment` in the response means "first period's payment" for every method except `Equal` (where it's constant) — documented in the view model, not a behavior needing a flag.

---

## Task 1: `LoanRate` entity, `AmortizationMethod` enum

**Files:**
- Create: `src/FinTrackPrime.Models/Entities/LoanRate.cs`
- Create: `src/FinTrackPrime.Models/Entities/AmortizationMethod.cs`

**Interfaces:**
- Produces: `LoanRate` entity, `AmortizationMethod` enum — consumed by every later task.

- [ ] **Step 1: Create the entity**

`src/FinTrackPrime.Models/Entities/LoanRate.cs`:
```csharp
using System;

namespace FinTrackPrime.Models.Entities
{
    // Bank-managed: one row per loan type, the rate the bank actually
    // offers for it. No admin UI exists anywhere in this app to edit
    // these — updated via a migration/seed/direct DB update, the same
    // way Premium:PriceUsd is updated today.
    public class LoanRate
    {
        public Guid Id { get; set; }

        // Mortgage | AutoLoan | StudentLoan | PersonalLoan | Other —
        // never CreditCard, that's not an amortized loan.
        public LiabilityType Type { get; set; }

        public decimal AnnualRatePercent { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
```

- [ ] **Step 2: Create the enum**

`src/FinTrackPrime.Models/Entities/AmortizationMethod.cs`:
```csharp
namespace FinTrackPrime.Models.Entities
{
    // A request-time choice, not bank-managed data — no dedicated table,
    // unlike LoanRate.
    public enum AmortizationMethod
    {
        Equal,
        FixedPrincipal,
        GracePeriod,
        Balloon,
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Entities/LoanRate.cs src/FinTrackPrime.Models/Entities/AmortizationMethod.cs
git commit -m "feat: add LoanRate entity, AmortizationMethod enum"
```

---

## Task 2: `FinTrackDbContext` — `DbSet<LoanRate>` and model config

**Files:**
- Modify: `src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs`

**Interfaces:**
- Consumes: `LoanRate` from Task 1.
- Produces: `FinTrackDbContext.LoanRates` (`DbSet<LoanRate>`), consumed by Task 3 (migration) and Task 6 (service).

- [ ] **Step 1: Add the `DbSet`**

Add alongside the existing `Notifications` line:
```csharp
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<LoanRate> LoanRates => Set<LoanRate>();
```

- [ ] **Step 2: Add `OnModelCreating` config**

Add this block anywhere after the `Notification` block:
```csharp
            modelBuilder.Entity<LoanRate>(entity =>
            {
                entity.Property(r => r.AnnualRatePercent).HasColumnType("decimal(5,2)");
                entity.HasIndex(r => r.Type).IsUnique();
            });
```

- [ ] **Step 3: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Persistence/FinTrackDbContext.cs
git commit -m "feat: register LoanRate entity with FinTrackDbContext"
```

---

## Task 3: Migration — add the `LoanRates` table, with seed data

**Files:**
- Generate via CLI (do not hand-write): `src/FinTrackPrime.Models/Migrations/<timestamp>_AddLoanRatesTable.cs` and `.Designer.cs`
- Modify (auto-updated by the CLI): `src/FinTrackPrime.Models/Migrations/FinTrackDbContextModelSnapshot.cs`
- Modify (by hand, after generation): the generated `<timestamp>_AddLoanRatesTable.cs` — to add seed `InsertData` calls

**Interfaces:**
- Consumes: shape from Tasks 1–2.
- Produces: `LoanRates` table with 5 seeded rows, queryable by Task 6's service.

- [ ] **Step 1: Generate the migration**

Run from the repo root:
```bash
dotnet ef migrations add AddLoanRatesTable --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
```
Expected: creates a new `<timestamp>_AddLoanRatesTable.cs`/`.Designer.cs` pair and updates `FinTrackDbContextModelSnapshot.cs` — a `CreateTable(name: "LoanRates", ...)` with `Id`, `Type` (int), `AnnualRatePercent` (decimal(5,2)), `UpdatedAtUtc` (datetime2), plus a unique index on `Type`.

- [ ] **Step 2: Add seed data to the generated migration's `Up()` method**

Open the newly generated `<timestamp>_AddLoanRatesTable.cs`. Immediately after the `CreateTable`/`CreateIndex` calls for `LoanRates`, inside `Up(MigrationBuilder migrationBuilder)`, add — **placeholder rates, must be replaced with the bank's real figures before launch**:
```csharp
            migrationBuilder.InsertData(
                table: "LoanRates",
                columns: new[] { "Id", "Type", "AnnualRatePercent", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { Guid.NewGuid(), 1, 6.50m, DateTime.UtcNow },  // Mortgage
                    { Guid.NewGuid(), 2, 7.25m, DateTime.UtcNow },  // AutoLoan
                    { Guid.NewGuid(), 3, 5.50m, DateTime.UtcNow },  // StudentLoan
                    { Guid.NewGuid(), 4, 11.00m, DateTime.UtcNow }, // PersonalLoan
                    { Guid.NewGuid(), 5, 9.00m, DateTime.UtcNow },  // Other
                });
```
The integer values are `LiabilityType`'s underlying enum values in declaration order (`CreditCard = 0, Mortgage = 1, AutoLoan = 2, StudentLoan = 3, PersonalLoan = 4, Other = 5`) — confirm this against `src/FinTrackPrime.Models/Entities/LiabilityType.cs` before running, since `InsertData` writes raw integers, not enum names.

Also add the matching `DeleteData` (or leave `DropTable` alone — dropping the table already removes the seeded rows) in `Down()` — no action needed there, `DropTable(name: "LoanRates")` already covers it.

- [ ] **Step 3: Apply the migration**

Run:
```bash
dotnet ef database update --project src/FinTrackPrime.Models --startup-project src/FinTrackPrime.WebApi
```
Expected: `LoanRates` table created with 5 rows.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.Models/Migrations/
git commit -m "feat: add LoanRates table with seed data"
```

---

## Task 4: `LoanCalculatorViewModels.cs` — typed shapes, rate/method fields

**Files:**
- Modify: `src/FinTrackPrime.Models/ViewModels/LoanCalculatorViewModels.cs`

**Interfaces:**
- Consumes: `LiabilityType` (existing), `AmortizationMethod` (Task 1).
- Produces: `LoanRateViewModel`, updated `LoanCalculationRequest`/`LoanCalculationResultViewModel`/`LoanAffordabilityRequest` — consumed by Task 5 (interface), Task 6 (service), Task 7 (controller).

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/FinTrackPrime.Models/ViewModels/LoanCalculatorViewModels.cs`:
```csharp
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FinTrackPrime.Models.Entities;

namespace FinTrackPrime.Models.ViewModels
{
    public class LoanRateViewModel
    {
        public LiabilityType Type { get; set; }
        public decimal AnnualRatePercent { get; set; }
    }

    // AnnualInterestRatePercent does not exist here — the client can
    // never supply or influence a rate, even by calling the API
    // directly. LoanType drives which bank rate the server applies.
    public class LoanCalculationRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Required]
        public LiabilityType LoanType { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }

        // Optional. Applied to every payment, on top of whatever base
        // payment the chosen Method produces, to show how much sooner
        // the loan pays off. Behaves identically across all four
        // methods — no per-method special-casing.
        [Range(0, double.MaxValue)]
        public decimal ExtraMonthlyPayment { get; set; } = 0m;

        public AmortizationMethod Method { get; set; } = AmortizationMethod.Equal;

        // Required (and validated: 0 < value < TermMonths) only when
        // Method == GracePeriod; ignored for every other method.
        public int? GracePeriodMonths { get; set; }
    }

    public class AmortizationRowViewModel
    {
        public int Month { get; set; }
        public decimal PaymentAmount { get; set; }
        public decimal PrincipalPaid { get; set; }
        public decimal InterestPaid { get; set; }
        public decimal RemainingBalance { get; set; }
    }

    public class LoanCalculationResultViewModel
    {
        // The first period's payment for every method except Equal,
        // where it's constant across the whole schedule — see Schedule
        // for the full picture of how it changes.
        public decimal RequiredMonthlyPayment { get; set; }
        public int PayoffMonths { get; set; }
        public decimal TotalInterestPaid { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal AppliedAnnualInterestRatePercent { get; set; }
        public List<AmortizationRowViewModel> Schedule { get; set; } = new();
    }

    // Same shape as LoanCalculationRequest minus ExtraMonthlyPayment —
    // extra-payment payoff acceleration isn't relevant to whether the
    // loan fits the budget today, so it's left out on purpose.
    public class LoanAffordabilityRequest
    {
        [Range(0.01, double.MaxValue)]
        public decimal PrincipalAmount { get; set; }

        [Required]
        public LiabilityType LoanType { get; set; }

        [Range(1, 480)]
        public int TermMonths { get; set; }

        public AmortizationMethod Method { get; set; } = AmortizationMethod.Equal;

        public int? GracePeriodMonths { get; set; }
    }

    // Unknown is the default (0) on purpose: with no income tracked yet
    // in Budget Planner, a ratio can't be computed, and the frontend
    // should prompt the user to add income rather than show a
    // misleading rating.
    public enum AffordabilityRating
    {
        Unknown,
        Comfortable,
        Manageable,
        Stretched,
        NotRecommended,
    }

    // Built entirely from data already in the system (BudgetCategories,
    // Liabilities), same as FinancialStatementViewModel, so the user
    // never re-enters income or debts they've already tracked elsewhere.
    // ProposedMonthlyPayment is the resolved-rate, resolved-method first
    // period's payment (see LoanCalculationResultViewModel) — for
    // Balloon specifically, this ratio does not reflect the final lump
    // sum due at maturity, a known limitation of applying a
    // monthly-payment DTI ratio to a balloon loan.
    public class LoanAffordabilityResultViewModel
    {
        public decimal ProposedMonthlyPayment { get; set; }
        public decimal MonthlyIncome { get; set; }
        public decimal ExistingMonthlyObligations { get; set; }
        public decimal TotalExistingLiabilities { get; set; }

        // Null when MonthlyIncome is 0 (no income category set up yet) —
        // dividing by zero would otherwise force a meaningless number.
        public decimal? CurrentDebtToIncomeRatioPercent { get; set; }
        public decimal? ProjectedDebtToIncomeRatioPercent { get; set; }
        public AffordabilityRating Rating { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Models/FinTrackPrime.Models.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Models/ViewModels/LoanCalculatorViewModels.cs
git commit -m "feat: add LoanRateViewModel, Method/GracePeriodMonths to loan calculator requests"
```

---

## Task 5: `ILoanCalculatorService` — async `Calculate`, new `GetRatesAsync`

**Files:**
- Modify: `src/FinTrackPrime.Business/Interfaces/ILoanCalculatorService.cs`

**Interfaces:**
- Consumes: view models from Task 4.
- Produces: interface signatures consumed by Task 6 (implementation) and Task 7 (controller).

- [ ] **Step 1: Rewrite the file**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface ILoanCalculatorService
    {
        Task<List<LoanRateViewModel>> GetRatesAsync();

        // Now async: resolving the bank's rate for request.LoanType
        // requires a database lookup. Throws InvalidOperationException
        // if no LoanRate row exists for that type (defensive; shouldn't
        // trigger once seeded), or if Method == GracePeriod and
        // GracePeriodMonths is missing or out of range.
        Task<LoanCalculationResultViewModel> CalculateAsync(LoanCalculationRequest request);

        // Reads the user's own tracked income, budgeted expenses, and
        // liabilities to judge a proposed loan against their real
        // finances instead of numbers re-typed into the request. Same
        // rate/method resolution and validation as CalculateAsync.
        Task<LoanAffordabilityResultViewModel> CheckAffordabilityAsync(Guid userId, LoanAffordabilityRequest request);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/FinTrackPrime.Business/FinTrackPrime.Business.csproj`
Expected: **fails** — `LoanCalculatorService` no longer satisfies `ILoanCalculatorService`. Expected; Task 6 fixes it.

- [ ] **Step 3: Commit**

```bash
git add src/FinTrackPrime.Business/Interfaces/ILoanCalculatorService.cs
git commit -m "feat: make ILoanCalculatorService.Calculate async, add GetRatesAsync"
```

---

## Task 6: `LoanCalculatorService` — rate resolution + four amortization methods

**Files:**
- Modify: `src/FinTrackPrime.Business/Services/LoanCalculatorService.cs`
- Test: `tests/FinTrackPrime.Business.Tests/LoanCalculatorServiceTests.cs` (new)

**Interfaces:**
- Consumes: `ILoanCalculatorService` (Task 5), view models (Task 4), `LoanRate`/`AmortizationMethod`/`LiabilityType` (Task 1 + existing), `FinTrackDbContext.LoanRates` (Task 2).
- Produces: full `ILoanCalculatorService` implementation, consumed by Task 7 (controller; already wired via DI in `Program.cs` as `ILoanCalculatorService → LoanCalculatorService`, no change needed there).

This service has zero existing test coverage (`tests/FinTrackPrime.Business.Tests/` has no `LoanCalculatorServiceTests.cs` today) — this task adds coverage for both the pre-existing Equal-method behavior (as a regression check while it moves to the new dispatcher) and all three new methods.

### Part A: Rewrite the service

- [ ] **Step 1: Rewrite `LoanCalculatorService.cs`**

Full replacement:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace FinTrackPrime.Business.Services
{
    public class LoanCalculatorService : ILoanCalculatorService
    {
        // Safety cap so a pathological input (near-zero rate, huge term)
        // can't loop forever; a real loan schedule never needs this many
        // rows.
        private const int MaxScheduleMonths = 600;

        // Banking-standard debt-to-income bands: 36% is the traditional
        // "comfortable" ceiling, 43% is the qualified-mortgage limit most
        // US lenders use, 50% is widely treated as high-risk. These turn
        // a raw ratio into a rating a non-technical user can act on.
        private const decimal ComfortableDtiThreshold = 36m;
        private const decimal ManageableDtiThreshold = 43m;
        private const decimal StretchedDtiThreshold = 50m;

        private readonly FinTrackDbContext _db;

        public LoanCalculatorService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<List<LoanRateViewModel>> GetRatesAsync()
        {
            return await _db.LoanRates
                .Select(r => new LoanRateViewModel { Type = r.Type, AnnualRatePercent = r.AnnualRatePercent })
                .ToListAsync();
        }

        public async Task<LoanCalculationResultViewModel> CalculateAsync(LoanCalculationRequest request)
        {
            var annualRate = await ResolveRateAsync(request.LoanType);
            var monthlyRate = (annualRate / 100m) / 12m;

            ValidateGracePeriod(request.Method, request.GracePeriodMonths, request.TermMonths);

            var schedule = BuildSchedule(
                request.PrincipalAmount, monthlyRate, request.TermMonths, request.ExtraMonthlyPayment,
                request.Method, request.GracePeriodMonths);

            return new LoanCalculationResultViewModel
            {
                RequiredMonthlyPayment = schedule.Count > 0 ? schedule[0].PaymentAmount : 0m,
                PayoffMonths = schedule.Count,
                TotalInterestPaid = Math.Round(schedule.Sum(r => r.InterestPaid), 2),
                TotalPaid = Math.Round(schedule.Sum(r => r.PaymentAmount), 2),
                AppliedAnnualInterestRatePercent = annualRate,
                Schedule = schedule,
            };
        }

        public async Task<LoanAffordabilityResultViewModel> CheckAffordabilityAsync(Guid userId, LoanAffordabilityRequest request)
        {
            var annualRate = await ResolveRateAsync(request.LoanType);
            var monthlyRate = (annualRate / 100m) / 12m;

            ValidateGracePeriod(request.Method, request.GracePeriodMonths, request.TermMonths);

            // No ExtraMonthlyPayment on this request on purpose (see
            // LoanAffordabilityRequest) — 0m here.
            var schedule = BuildSchedule(request.PrincipalAmount, monthlyRate, request.TermMonths, 0m, request.Method, request.GracePeriodMonths);
            var proposedPayment = schedule.Count > 0 ? schedule[0].PaymentAmount : 0m;

            var categories = await _db.BudgetCategories
                .Where(c => c.UserId == userId)
                .ToListAsync();

            var monthlyIncome = categories
                .Where(c => c.Type == BudgetCategoryType.Income)
                .Sum(c => c.PlannedAmount);

            var existingObligations = categories
                .Where(c => c.Type == BudgetCategoryType.Expense)
                .Sum(c => c.PlannedAmount);

            var totalExistingLiabilities = await _db.Liabilities
                .Where(l => l.UserId == userId)
                .SumAsync(l => l.Amount);

            decimal? currentDti = null;
            decimal? projectedDti = null;
            var rating = AffordabilityRating.Unknown;

            // Only rate the loan if there's an actual income figure to
            // divide by; otherwise "0% DTI" would misleadingly read as
            // affordable.
            if (monthlyIncome > 0)
            {
                currentDti = Math.Round(existingObligations / monthlyIncome * 100m, 1);
                projectedDti = Math.Round((existingObligations + proposedPayment) / monthlyIncome * 100m, 1);
                rating = RateAffordability(projectedDti.Value);
            }

            return new LoanAffordabilityResultViewModel
            {
                ProposedMonthlyPayment = Math.Round(proposedPayment, 2),
                MonthlyIncome = Math.Round(monthlyIncome, 2),
                ExistingMonthlyObligations = Math.Round(existingObligations, 2),
                TotalExistingLiabilities = Math.Round(totalExistingLiabilities, 2),
                CurrentDebtToIncomeRatioPercent = currentDti,
                ProjectedDebtToIncomeRatioPercent = projectedDti,
                Rating = rating,
            };
        }

        private async Task<decimal> ResolveRateAsync(LiabilityType loanType)
        {
            var rate = await _db.LoanRates.FirstOrDefaultAsync(r => r.Type == loanType);
            if (rate is null)
            {
                throw new InvalidOperationException($"No rate is configured for loan type {loanType}.");
            }
            return rate.AnnualRatePercent;
        }

        private static void ValidateGracePeriod(AmortizationMethod method, int? gracePeriodMonths, int termMonths)
        {
            if (method != AmortizationMethod.GracePeriod)
            {
                return;
            }

            if (gracePeriodMonths is null || gracePeriodMonths <= 0 || gracePeriodMonths >= termMonths)
            {
                throw new InvalidOperationException("GracePeriodMonths must be greater than 0 and less than TermMonths.");
            }
        }

        private static List<AmortizationRowViewModel> BuildSchedule(
            decimal principal, decimal monthlyRate, int termMonths, decimal extraMonthlyPayment,
            AmortizationMethod method, int? gracePeriodMonths)
        {
            return method switch
            {
                AmortizationMethod.Equal => BuildEqualSchedule(principal, monthlyRate, termMonths, extraMonthlyPayment, termMonths),
                AmortizationMethod.FixedPrincipal => BuildFixedPrincipalSchedule(principal, monthlyRate, termMonths, extraMonthlyPayment),
                AmortizationMethod.GracePeriod => BuildGracePeriodSchedule(principal, monthlyRate, termMonths, extraMonthlyPayment, gracePeriodMonths!.Value),
                AmortizationMethod.Balloon => BuildBalloonSchedule(principal, monthlyRate, termMonths, extraMonthlyPayment),
                _ => throw new InvalidOperationException($"Unsupported amortization method: {method}"),
            };
        }

        // "French"/annuity system: constant total payment every period.
        // termMonthsForPaymentFormula and startMonth let
        // BuildGracePeriodSchedule reuse this for its post-grace phase,
        // continuing month numbering and computing the required payment
        // against the remaining term rather than the full one.
        private static List<AmortizationRowViewModel> BuildEqualSchedule(
            decimal principal, decimal monthlyRate, int termMonths, decimal extraMonthlyPayment,
            int termMonthsForPaymentFormula, int startMonth = 0)
        {
            var requiredPayment = CalculateRequiredMonthlyPayment(principal, monthlyRate, termMonthsForPaymentFormula);
            var schedule = new List<AmortizationRowViewModel>();
            var balance = principal;
            var month = startMonth;

            while (balance > 0.01m && month - startMonth < MaxScheduleMonths)
            {
                month++;

                var interestForMonth = balance * monthlyRate;
                var basePayment = requiredPayment + extraMonthlyPayment;

                // The last payment only needs to cover what's left, not a
                // full payment, or the loan would go negative.
                var paymentForMonth = Math.Min(basePayment, balance + interestForMonth);
                var principalPaid = paymentForMonth - interestForMonth;

                balance -= principalPaid;

                schedule.Add(new AmortizationRowViewModel
                {
                    Month = month,
                    PaymentAmount = Math.Round(paymentForMonth, 2),
                    PrincipalPaid = Math.Round(principalPaid, 2),
                    InterestPaid = Math.Round(interestForMonth, 2),
                    RemainingBalance = Math.Round(Math.Max(balance, 0), 2),
                });
            }

            return schedule;
        }

        // "German" system: constant principal portion every period;
        // total payment declines over time as the interest portion
        // shrinks.
        private static List<AmortizationRowViewModel> BuildFixedPrincipalSchedule(
            decimal principal, decimal monthlyRate, int termMonths, decimal extraMonthlyPayment)
        {
            var fixedPrincipal = principal / termMonths;
            var schedule = new List<AmortizationRowViewModel>();
            var balance = principal;
            var month = 0;

            while (balance > 0.01m && month < MaxScheduleMonths)
            {
                month++;

                var interestForMonth = balance * monthlyRate;
                var principalPaid = Math.Min(fixedPrincipal + extraMonthlyPayment, balance);
                var paymentForMonth = principalPaid + interestForMonth;

                balance -= principalPaid;

                schedule.Add(new AmortizationRowViewModel
                {
                    Month = month,
                    PaymentAmount = Math.Round(paymentForMonth, 2),
                    PrincipalPaid = Math.Round(principalPaid, 2),
                    InterestPaid = Math.Round(interestForMonth, 2),
                    RemainingBalance = Math.Round(Math.Max(balance, 0), 2),
                });
            }

            return schedule;
        }

        // Interest-only for gracePeriodMonths (principal untouched unless
        // ExtraMonthlyPayment reduces it early), then switches to the
        // Equal system for the remaining term, computed against the
        // balance and term remaining at that point.
        private static List<AmortizationRowViewModel> BuildGracePeriodSchedule(
            decimal principal, decimal monthlyRate, int termMonths, decimal extraMonthlyPayment, int gracePeriodMonths)
        {
            var schedule = new List<AmortizationRowViewModel>();
            var balance = principal;

            for (var month = 1; month <= gracePeriodMonths; month++)
            {
                var interestForMonth = balance * monthlyRate;
                var principalPaid = Math.Min(extraMonthlyPayment, balance);
                var paymentForMonth = interestForMonth + principalPaid;

                balance -= principalPaid;

                schedule.Add(new AmortizationRowViewModel
                {
                    Month = month,
                    PaymentAmount = Math.Round(paymentForMonth, 2),
                    PrincipalPaid = Math.Round(principalPaid, 2),
                    InterestPaid = Math.Round(interestForMonth, 2),
                    RemainingBalance = Math.Round(Math.Max(balance, 0), 2),
                });
            }

            var remainingTerm = termMonths - gracePeriodMonths;
            var equalPhase = BuildEqualSchedule(balance, monthlyRate, remainingTerm, extraMonthlyPayment, remainingTerm, startMonth: gracePeriodMonths);
            schedule.AddRange(equalPhase);

            return schedule;
        }

        // "Bullet" loan: interest-only every period except the last,
        // where the entire remaining principal is due as one lump sum
        // (the balloon) on top of that period's interest.
        // ExtraMonthlyPayment is still honored during the interest-only
        // phase, partially reducing the eventual balloon — a real,
        // common "partially amortizing balloon" variant, not a special
        // case to avoid.
        private static List<AmortizationRowViewModel> BuildBalloonSchedule(
            decimal principal, decimal monthlyRate, int termMonths, decimal extraMonthlyPayment)
        {
            var schedule = new List<AmortizationRowViewModel>();
            var balance = principal;
            var lastMonth = Math.Min(termMonths, MaxScheduleMonths);

            for (var month = 1; month <= lastMonth; month++)
            {
                var interestForMonth = balance * monthlyRate;
                decimal principalPaid;
                decimal paymentForMonth;

                if (month == termMonths)
                {
                    principalPaid = balance;
                    paymentForMonth = principalPaid + interestForMonth;
                }
                else
                {
                    principalPaid = Math.Min(extraMonthlyPayment, balance);
                    paymentForMonth = interestForMonth + principalPaid;
                }

                balance -= principalPaid;

                schedule.Add(new AmortizationRowViewModel
                {
                    Month = month,
                    PaymentAmount = Math.Round(paymentForMonth, 2),
                    PrincipalPaid = Math.Round(principalPaid, 2),
                    InterestPaid = Math.Round(interestForMonth, 2),
                    RemainingBalance = Math.Round(Math.Max(balance, 0), 2),
                });

                if (balance <= 0.01m)
                {
                    break;
                }
            }

            return schedule;
        }

        private static decimal CalculateRequiredMonthlyPayment(decimal principal, decimal monthlyRate, int termMonths)
        {
            if (monthlyRate == 0m)
            {
                return principal / termMonths;
            }

            var ratePow = Math.Pow((double)(1 + monthlyRate), termMonths);
            var factor = (decimal)ratePow;

            return principal * monthlyRate * factor / (factor - 1);
        }

        private static AffordabilityRating RateAffordability(decimal projectedDtiPercent)
        {
            if (projectedDtiPercent <= ComfortableDtiThreshold) return AffordabilityRating.Comfortable;
            if (projectedDtiPercent <= ManageableDtiThreshold) return AffordabilityRating.Manageable;
            if (projectedDtiPercent <= StretchedDtiThreshold) return AffordabilityRating.Stretched;
            return AffordabilityRating.NotRecommended;
        }
    }
}
```

### Part B: Tests

- [ ] **Step 2: Write the tests**

Create `tests/FinTrackPrime.Business.Tests/LoanCalculatorServiceTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class LoanCalculatorServiceTests
    {
        private static FinTrackDbContext BuildDb()
        {
            var options = new DbContextOptionsBuilder<FinTrackDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new FinTrackDbContext(options);
        }

        private static async Task SeedRateAsync(FinTrackDbContext db, LiabilityType type, decimal annualRatePercent)
        {
            db.LoanRates.Add(new LoanRate { Id = Guid.NewGuid(), Type = type, AnnualRatePercent = annualRatePercent, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        private static async Task<Guid> SeedUserAsync(FinTrackDbContext db)
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = $"{userId}@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return userId;
        }

        [Fact]
        public async Task GetRatesAsync_ReturnsAllSeededRates()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 6.50m);
            await SeedRateAsync(db, LiabilityType.AutoLoan, 7.25m);

            var service = new LoanCalculatorService(db);
            var rates = await service.GetRatesAsync();

            Assert.Equal(2, rates.Count);
            Assert.Contains(rates, r => r.Type == LiabilityType.Mortgage && r.AnnualRatePercent == 6.50m);
        }

        [Fact]
        public async Task CalculateAsync_ThrowsWhenLoanTypeHasNoRate()
        {
            await using var db = BuildDb();
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest { PrincipalAmount = 10000m, LoanType = LiabilityType.Mortgage, TermMonths = 12 };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateAsync(request));
        }

        [Fact]
        public async Task CalculateAsync_Equal_ProducesAConstantPaymentThatFullyPaysOffTheLoan()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 10000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 12, Method = AmortizationMethod.Equal,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.Equal(result.Schedule[0].PaymentAmount, result.Schedule[^1].PaymentAmount);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
            Assert.Equal(12m, result.AppliedAnnualInterestRatePercent);
            Assert.True(result.TotalInterestPaid > 0m);
        }

        [Fact]
        public async Task CalculateAsync_FixedPrincipal_PaymentDeclinesAndPrincipalPortionStaysConstant()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.AutoLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.AutoLoan, TermMonths = 12, Method = AmortizationMethod.FixedPrincipal,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.True(result.Schedule[0].PaymentAmount > result.Schedule[^1].PaymentAmount);
            Assert.All(result.Schedule, row => Assert.Equal(1000m, row.PrincipalPaid));
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CalculateAsync_GracePeriod_IsInterestOnlyDuringGraceThenAmortizesTheRest()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.Mortgage, TermMonths = 12,
                Method = AmortizationMethod.GracePeriod, GracePeriodMonths = 3,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.All(result.Schedule.Take(3), row =>
            {
                Assert.Equal(0m, row.PrincipalPaid);
                Assert.Equal(12000m, row.RemainingBalance);
            });
            Assert.True(result.Schedule[3].PrincipalPaid > 0m);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CalculateAsync_RejectsGracePeriodMonthsOutOfRange()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.Mortgage, TermMonths = 12,
                Method = AmortizationMethod.GracePeriod, GracePeriodMonths = 12,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateAsync(request));
        }

        [Fact]
        public async Task CalculateAsync_Balloon_IsInterestOnlyUntilTheFinalMonthThenPaysTheBalloon()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 5000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 6, Method = AmortizationMethod.Balloon,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(6, result.Schedule.Count);
            Assert.All(result.Schedule.Take(5), row =>
            {
                Assert.Equal(0m, row.PrincipalPaid);
                Assert.Equal(5000m, row.RemainingBalance);
            });
            Assert.Equal(5000m, result.Schedule[^1].PrincipalPaid);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CheckAffordabilityAsync_UsesFirstPeriodPaymentForBalloon()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            db.BudgetCategories.Add(new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Salary", Type = BudgetCategoryType.Income, PlannedAmount = 5000m });
            await db.SaveChangesAsync();

            var service = new LoanCalculatorService(db);
            var request = new LoanAffordabilityRequest
            {
                PrincipalAmount = 5000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 6, Method = AmortizationMethod.Balloon,
            };

            var result = await service.CheckAffordabilityAsync(userId, request);

            // Interest-only first payment: 5000 * (12% / 12) = 50, far
            // below what a fully-amortized 6-month loan would require —
            // confirms the affordability check used the first period's
            // payment, not a full-amortization figure.
            Assert.Equal(50m, result.ProposedMonthlyPayment);
        }
    }
}
```

- [ ] **Step 3: Run the tests, confirm they pass**

Run: `dotnet test tests/FinTrackPrime.Business.Tests --filter LoanCalculatorServiceTests`
Expected: PASS, all 8 tests.

- [ ] **Step 4: Run the full test project to confirm nothing else broke**

Run: `dotnet test tests/FinTrackPrime.Business.Tests`
Expected: PASS, every test in the project.

- [ ] **Step 5: Commit**

```bash
git add src/FinTrackPrime.Business/Services/LoanCalculatorService.cs tests/FinTrackPrime.Business.Tests/LoanCalculatorServiceTests.cs
git commit -m "feat: bank-rate resolution and four amortization methods in LoanCalculatorService"
```

---

## Task 7: `LoanCalculatorController` — async `Calculate`, new `/rates` endpoint, error handling

**Files:**
- Modify: `src/FinTrackPrime.WebApi/Controllers/LoanCalculatorController.cs`

**Interfaces:**
- Consumes: `ILoanCalculatorService` (Task 5/6), view models (Task 4).
- Produces: `GET /api/loan-calculator/rates`, updated `POST /calculate` and `POST /affordability` — consumed by Task 9 (frontend API client).

- [ ] **Step 1: Rewrite the controller**

```csharp
using System;
using System.Collections.Generic;
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
    [Route("api/loan-calculator")]
    [Authorize(Policy = "RequirePremium")]
    public class LoanCalculatorController : ControllerBase
    {
        private readonly ILoanCalculatorService _loanCalculatorService;

        public LoanCalculatorController(ILoanCalculatorService loanCalculatorService)
        {
            _loanCalculatorService = loanCalculatorService;
        }

        [HttpGet("rates")]
        public async Task<ActionResult<List<LoanRateViewModel>>> GetRates()
        {
            var rates = await _loanCalculatorService.GetRatesAsync();
            return Ok(rates);
        }

        [HttpPost("calculate")]
        public async Task<ActionResult<LoanCalculationResultViewModel>> Calculate(LoanCalculationRequest request)
        {
            try
            {
                var result = await _loanCalculatorService.CalculateAsync(request);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Also a POST-as-compute: the result depends on the caller's
        // tracked income/expenses/liabilities, but nothing about the
        // affordability check itself is saved.
        [HttpPost("affordability")]
        public async Task<ActionResult<LoanAffordabilityResultViewModel>> CheckAffordability(LoanAffordabilityRequest request)
        {
            try
            {
                var result = await _loanCalculatorService.CheckAffordabilityAsync(GetUserId(), request);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
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
```

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build FinTrackPrime.sln`
Expected: succeeds.

- [ ] **Step 3: Run the full backend test suite**

Run: `dotnet test`
Expected: PASS, all projects.

- [ ] **Step 4: Commit**

```bash
git add src/FinTrackPrime.WebApi/Controllers/LoanCalculatorController.cs
git commit -m "feat: add /rates endpoint, async Calculate, error handling to LoanCalculatorController"
```

---

## Task 8: Frontend types (`types/api.ts`)

**Files:**
- Modify: `src/types/api.ts` (in `C:\Users\Wyrlo\projects\FinTrackPrime`)

**Interfaces:**
- Produces: `AmortizationMethod`, `LoanRateViewModel`, updated `LoanCalculationRequest`/`LoanCalculationResultViewModel`/`LoanAffordabilityRequest` — consumed by Task 9 (API client) and Task 10 (page).

- [ ] **Step 1: Replace the loan calculator type block**

Find and replace:
```ts
export interface LoanCalculationRequest {
  principalAmount: number
  annualInterestRatePercent: number
  termMonths: number
  extraMonthlyPayment: number
}

export interface AmortizationRowViewModel {
  month: number
  paymentAmount: number
  principalPaid: number
  interestPaid: number
  remainingBalance: number
}

export interface LoanCalculationResultViewModel {
  requiredMonthlyPayment: number
  payoffMonths: number
  totalInterestPaid: number
  totalPaid: number
  schedule: AmortizationRowViewModel[]
}

export interface LoanAffordabilityRequest {
  principalAmount: number
  annualInterestRatePercent: number
  termMonths: number
}
```
with:
```ts
export type AmortizationMethod = 'Equal' | 'FixedPrincipal' | 'GracePeriod' | 'Balloon'

export interface LoanRateViewModel {
  type: LiabilityType
  annualRatePercent: number
}

// annualInterestRatePercent does not exist here — loanType drives which
// bank rate the server applies; the client can't supply or influence it.
export interface LoanCalculationRequest {
  principalAmount: number
  loanType: LiabilityType
  termMonths: number
  extraMonthlyPayment: number
  method: AmortizationMethod
  // Required only when method === 'GracePeriod'.
  gracePeriodMonths?: number
}

export interface AmortizationRowViewModel {
  month: number
  paymentAmount: number
  principalPaid: number
  interestPaid: number
  remainingBalance: number
}

export interface LoanCalculationResultViewModel {
  // First period's payment for every method except 'Equal', where it's
  // constant across the whole schedule.
  requiredMonthlyPayment: number
  payoffMonths: number
  totalInterestPaid: number
  totalPaid: number
  appliedAnnualInterestRatePercent: number
  schedule: AmortizationRowViewModel[]
}

export interface LoanAffordabilityRequest {
  principalAmount: number
  loanType: LiabilityType
  termMonths: number
  method: AmortizationMethod
  gracePeriodMonths?: number
}
```
(`LiabilityType` already exists in this file from the Financial Statement work — no new import needed, it's in the same module.)

- [ ] **Step 2: Verify no other file still references the old shape**

Run: `grep -rn "annualInterestRatePercent" src` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: no matches — it was only ever consumed by `LoanCalculatorPage.tsx`, which Task 10 rewrites.

- [ ] **Step 3: Commit**

```bash
git add src/types/api.ts
git commit -m "feat: type loan calculator requests with LoanType, Method, GracePeriodMonths"
```

---

## Task 9: Frontend API client (`api/loanCalculator.ts`)

**Files:**
- Modify: `src/api/loanCalculator.ts`

**Interfaces:**
- Consumes: types from Task 8.
- Produces: `loanCalculatorApi.getRates` — consumed by Task 10.

- [ ] **Step 1: Add `getRates`**

Full replacement:
```ts
import { apiClient } from './client'
import type {
  LoanAffordabilityRequest,
  LoanAffordabilityResultViewModel,
  LoanCalculationRequest,
  LoanCalculationResultViewModel,
  LoanRateViewModel,
} from '../types/api'

export const loanCalculatorApi = {
  getRates: async (): Promise<LoanRateViewModel[]> => {
    const { data } = await apiClient.get<LoanRateViewModel[]>('/api/loan-calculator/rates')
    return data
  },
  calculate: async (request: LoanCalculationRequest): Promise<LoanCalculationResultViewModel> => {
    const { data } = await apiClient.post<LoanCalculationResultViewModel>(
      '/api/loan-calculator/calculate',
      request,
    )
    return data
  },
  checkAffordability: async (
    request: LoanAffordabilityRequest,
  ): Promise<LoanAffordabilityResultViewModel> => {
    const { data } = await apiClient.post<LoanAffordabilityResultViewModel>(
      '/api/loan-calculator/affordability',
      request,
    )
    return data
  },
}
```

- [ ] **Step 2: Commit**

```bash
git add src/api/loanCalculator.ts
git commit -m "feat: add getRates to loanCalculatorApi"
```

---

## Task 10: `LoanCalculatorPage.tsx` — Loan Type + Method selectors, read-only rate

**Files:**
- Modify: `src/pages/LoanCalculatorPage.tsx`

**Interfaces:**
- Consumes: types (Task 8), `loanCalculatorApi` (Task 9), existing UI components (`Card`, `CardHeader`, `Input`, `Select`, `Button`, `Badge`, `EmptyState`, `StatCard`, `Spinner` from `src/components/ui/`).
- Produces: the rendered page — last task, nothing downstream depends on it.

No frontend test framework exists — verification is `npm run build` plus manual browser checks (steps below).

- [ ] **Step 1: Rewrite the file**

Full replacement for `src/pages/LoanCalculatorPage.tsx`:
```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { loanCalculatorApi } from '../api/loanCalculator'
import { useDebouncedCallback } from '../hooks/useDebouncedCallback'
import { useDecimalInput } from '../hooks/useDecimalInput'
import type { AffordabilityRating, AmortizationMethod, LiabilityType, LoanCalculationRequest } from '../types/api'
import { Badge } from '../components/ui/Badge'
import { Button } from '../components/ui/Button'
import { Card, CardHeader } from '../components/ui/Card'
import { EmptyState } from '../components/ui/EmptyState'
import { Input } from '../components/ui/Input'
import { Select, type SelectOption } from '../components/ui/Select'
import { StatCard } from '../components/ui/StatCard'
import { Spinner } from '../components/ui/Spinner'

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount)
}

function formatPercent(value: number) {
  return `${value.toFixed(1)}%`
}

const LOAN_TYPE_OPTIONS: SelectOption[] = [
  { value: 'Mortgage', label: 'Mortgage' },
  { value: 'AutoLoan', label: 'Auto Loan' },
  { value: 'StudentLoan', label: 'Student Loan' },
  { value: 'PersonalLoan', label: 'Personal Loan' },
  { value: 'Other', label: 'Other' },
]

const METHOD_OPTIONS: SelectOption[] = [
  { value: 'Equal', label: 'Fixed Equal Amortization Case' },
  { value: 'FixedPrincipal', label: 'Fixed Principal Amortization Case' },
  { value: 'GracePeriod', label: 'Fixed Equal Amortization Case with Grace Period' },
  { value: 'Balloon', label: 'Periodic Interest Payment, Balloon Payment at Maturity' },
]

const DEFAULT_REQUEST: LoanCalculationRequest = {
  principalAmount: 25000,
  loanType: 'PersonalLoan',
  termMonths: 60,
  extraMonthlyPayment: 0,
  method: 'Equal',
  gracePeriodMonths: undefined,
}

const RATING_BADGE_VARIANT: Record<AffordabilityRating, 'neutral' | 'good' | 'gold' | 'warning' | 'critical'> = {
  Unknown: 'neutral',
  Comfortable: 'good',
  Manageable: 'gold',
  Stretched: 'warning',
  NotRecommended: 'critical',
}

const RATING_LABEL: Record<AffordabilityRating, string> = {
  Unknown: 'Unknown',
  Comfortable: 'Comfortable',
  Manageable: 'Manageable',
  Stretched: 'Stretched',
  NotRecommended: 'Not recommended',
}

export function LoanCalculatorPage() {
  const navigate = useNavigate()
  const [request, setRequest] = useState<LoanCalculationRequest>(DEFAULT_REQUEST)

  const { data: rates } = useQuery({ queryKey: ['loan-rates'], queryFn: loanCalculatorApi.getRates })
  const currentRate = rates?.find((r) => r.type === request.loanType)?.annualRatePercent

  const mutation = useMutation({
    mutationFn: loanCalculatorApi.calculate,
  })

  const runCalculation = useDebouncedCallback((req: LoanCalculationRequest) => {
    mutation.mutate(req)
  }, 400)

  // Runs once on mount for the default scenario, then again every time
  // an input changes (debounced), so the results pane is never empty.
  useEffect(() => {
    runCalculation(request)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request])

  const affordabilityMutation = useMutation({
    mutationFn: loanCalculatorApi.checkAffordability,
  })

  const runAffordabilityCheck = useDebouncedCallback(
    (req: { principalAmount: number; loanType: LiabilityType; termMonths: number; method: AmortizationMethod; gracePeriodMonths?: number }) => {
      affordabilityMutation.mutate(req)
    },
    400,
  )

  // Mirrors the loan being calculated above, so affordability always reflects
  // the same principal/type/term/method the user is currently looking at.
  useEffect(() => {
    runAffordabilityCheck({
      principalAmount: request.principalAmount,
      loanType: request.loanType,
      termMonths: request.termMonths,
      method: request.method,
      gracePeriodMonths: request.gracePeriodMonths,
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request.principalAmount, request.loanType, request.termMonths, request.method, request.gracePeriodMonths])

  const updateField = <K extends keyof LoanCalculationRequest>(field: K, value: LoanCalculationRequest[K]) => {
    setRequest((prev) => ({ ...prev, [field]: value }))
  }

  const principalInput = useDecimalInput({
    value: request.principalAmount,
    onChange: (value) => updateField('principalAmount', value),
    decimals: 2,
  })
  const termInput = useDecimalInput({
    value: request.termMonths,
    onChange: (value) => updateField('termMonths', value),
    decimals: 0,
  })
  const extraPaymentInput = useDecimalInput({
    value: request.extraMonthlyPayment,
    onChange: (value) => updateField('extraMonthlyPayment', value),
    decimals: 2,
  })
  const gracePeriodInput = useDecimalInput({
    value: request.gracePeriodMonths ?? 0,
    onChange: (value) => updateField('gracePeriodMonths', value),
    decimals: 0,
  })

  const result = mutation.data
  const chartData = result?.schedule.map((row) => ({
    month: row.month,
    Balance: row.remainingBalance,
  }))

  const paymentLabel = request.method === 'Equal' ? 'Monthly payment' : 'First payment'

  return (
    <div>
      <CardHeader
        title="Loan Calculator"
        description="Pick a loan type and amortization method. The rate is set by the bank and can't be changed."
      />

      <div className="grid gap-5 lg:grid-cols-2">
        <Card className="space-y-4">
          <Input label="Loan amount" variant="currency" {...principalInput} />

          <Select
            label="Loan Type"
            options={LOAN_TYPE_OPTIONS}
            value={request.loanType}
            onValueChange={(value) => updateField('loanType', value as LiabilityType)}
          />

          <div>
            <p className="mb-1.5 text-sm font-medium text-text-primary">Bank rate</p>
            <div className="flex h-[42px] items-center rounded-lg border border-border-strong bg-surface-sunken px-3 text-sm text-text-secondary">
              {currentRate === undefined ? <Spinner size="sm" /> : formatPercent(currentRate)}
            </div>
          </div>

          <Select
            label="Type of Loan"
            options={METHOD_OPTIONS}
            value={request.method}
            onValueChange={(value) => updateField('method', value as AmortizationMethod)}
          />

          {request.method === 'GracePeriod' && (
            <Input
              label="Grace period (months)"
              {...gracePeriodInput}
              helperText="Interest-only for this many months before regular payments begin."
            />
          )}

          <Input label="Term (months)" {...termInput} />

          <Input
            label="Extra monthly payment"
            variant="currency"
            {...extraPaymentInput}
            helperText="Optional. See how much sooner extra payments pay off the loan."
          />
        </Card>

        <div className="space-y-5">
          <Card>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Results</h2>
            {result ? (
              <div className="mt-3 grid grid-cols-2 gap-3">
                <StatCard label={paymentLabel} value={formatCurrency(result.requiredMonthlyPayment)} />
                <StatCard label="Payoff time" value={`${result.payoffMonths} months`} />
                <StatCard label="Total interest" value={formatCurrency(result.totalInterestPaid)} />
                <StatCard label="Total paid" value={formatCurrency(result.totalPaid)} />
              </div>
            ) : (
              <p className="mt-3 flex items-center gap-2 text-sm text-text-muted">
                <Spinner size="sm" /> Calculating…
              </p>
            )}
            {result && (
              <p className="mt-2 text-xs text-text-muted">at {formatPercent(result.appliedAnnualInterestRatePercent)} APR</p>
            )}
          </Card>

          <Card>
            <h2 className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">Remaining balance over time</h2>
            <div className="mt-3 h-56">
              {chartData && chartData.length > 0 ? (
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={chartData}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" />
                    <XAxis
                      dataKey="month"
                      tick={{ fontSize: 12, fill: 'var(--color-text-secondary)' }}
                      label={{ value: 'Month', position: 'insideBottom', offset: -4, fontSize: 12, fill: 'var(--color-text-muted)' }}
                    />
                    <YAxis tick={{ fontSize: 12, fill: 'var(--color-text-secondary)' }} />
                    <Tooltip formatter={(value) => formatCurrency(Number(value))} labelFormatter={(m) => `Month ${m}`} />
                    <Line type="monotone" dataKey="Balance" stroke="var(--color-chart-sequential-500)" strokeWidth={2} dot={false} />
                  </LineChart>
                </ResponsiveContainer>
              ) : (
                <p className="flex items-center gap-2 text-sm text-text-muted">
                  <Spinner size="sm" /> Calculating…
                </p>
              )}
            </div>
          </Card>

          <Card>
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-xs font-semibold uppercase tracking-wide text-ft-gold-ink dark:text-ft-gold">
                Can you afford this loan?
              </h2>
              {affordabilityMutation.data && (
                <Badge variant={RATING_BADGE_VARIANT[affordabilityMutation.data.rating]}>
                  {RATING_LABEL[affordabilityMutation.data.rating]}
                </Badge>
              )}
            </div>

            {!affordabilityMutation.data ? (
              <p className="mt-3 flex items-center gap-2 text-sm text-text-muted">
                <Spinner size="sm" /> Checking…
              </p>
            ) : affordabilityMutation.data.rating === 'Unknown' ? (
              <div className="mt-3">
                <EmptyState
                  title="Add an income category to check affordability"
                  description="We use your Budget Planner income categories to estimate whether this loan fits your budget."
                  action={<Button onClick={() => navigate('/budget-planner')}>Go to Budget Planner</Button>}
                />
              </div>
            ) : (
              <div className="mt-3 grid grid-cols-2 gap-3">
                <StatCard label="Proposed payment" value={formatCurrency(affordabilityMutation.data.proposedMonthlyPayment)} />
                <StatCard label="Monthly income" value={formatCurrency(affordabilityMutation.data.monthlyIncome)} />
                <StatCard
                  label="Current debt-to-income"
                  value={
                    affordabilityMutation.data.currentDebtToIncomeRatioPercent === null
                      ? '—'
                      : formatPercent(affordabilityMutation.data.currentDebtToIncomeRatioPercent)
                  }
                />
                <StatCard
                  label="Projected debt-to-income"
                  value={
                    affordabilityMutation.data.projectedDebtToIncomeRatioPercent === null
                      ? '—'
                      : formatPercent(affordabilityMutation.data.projectedDebtToIncomeRatioPercent)
                  }
                />
              </div>
            )}
            {request.method === 'Balloon' && (
              <p className="mt-3 text-xs text-text-muted">
                This ratio is based on the interest-only payment above — it doesn't reflect the full balloon payment due at maturity.
              </p>
            )}
          </Card>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Build**

Run: `npm run build` (from `C:\Users\Wyrlo\projects\FinTrackPrime`)
Expected: succeeds, no TypeScript errors.

- [ ] **Step 3: Manual verification**

Run `npm run dev`, sign in as a premium-unlocked test user, navigate to Loan Calculator. Confirm:
- No "Annual interest rate" input exists anymore; a read-only "Bank rate" field shows a value that changes when Loan Type changes, without any way to type into it.
- Switching "Type of Loan" between all four options changes the Results card's numbers and the balance chart's shape (Equal: flat declining curve to zero; Fixed Principal: steeper decline than Equal; Grace Period: flat balance for the grace months then declining; Balloon: flat balance until the last point, then a vertical drop to zero).
- Selecting Grace Period reveals the "Grace period (months)" input; other methods don't show it.
- The affordability card shows the balloon caveat text only when Method is Balloon.

- [ ] **Step 4: Commit**

```bash
git add src/pages/LoanCalculatorPage.tsx
git commit -m "feat: Loan Type + Amortization Method selectors, read-only bank rate"
```

---

## Self-Review

**Spec coverage:**
- Bank-managed, read-only rate resolved by Loan Type → Tasks 1–4 (data model/contract), 6 (resolution + rejection of any client-supplied rate, since the field no longer exists), 10 (read-only UI). ✓
- Four amortization methods (Equal unchanged, FixedPrincipal, GracePeriod, Balloon) → Task 6, tested individually. ✓
- Weekly installments explicitly deferred → not present anywhere in this plan. ✓
- Loan Type and Amortization Method fully independent selectors → Task 10's two separate `Select` components, no cross-constraint logic anywhere. ✓
- `ExtraMonthlyPayment` behaves uniformly across methods → same field, same "added to base payment, reduces principal" logic in all four builders, no special-casing. ✓
- `RequiredMonthlyPayment` repurposed as first-period payment for non-Equal methods → documented in Task 4's view model comment, exercised by Task 6's Balloon/GracePeriod tests, surfaced in Task 10's `paymentLabel`. ✓
- Affordability check uses first-period payment, with a Balloon-specific UI caveat → Task 6 (`CheckAffordabilityAsync`), Task 10 (conditional caveat text). ✓
- Migration approach explicitly changed away from hand-editing, per the incident earlier this session → Task 3, called out in Global Constraints. ✓

**Placeholder scan:** no "TBD"/"add appropriate handling"/"similar to Task N" — every step has literal code or an exact command. The seed rate values are explicitly marked as placeholders needing real bank figures, which is a documented open question, not an omission.

**Type consistency check:** `AmortizationMethod` enum member names (`Equal`, `FixedPrincipal`, `GracePeriod`, `Balloon`) match exactly across Task 1 (C# enum), Task 4 (view models), Task 6 (service switch + tests), Task 8 (TS union type), Task 10 (`METHOD_OPTIONS` values). `LoanRateViewModel`/`GetRatesAsync`/`getRates` naming consistent Task 4 → 5 → 6 → 7 → 9 → 10. `GracePeriodMonths` (C#) / `gracePeriodMonths` (TS) used consistently, never renamed partway through.
