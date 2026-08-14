# Loan Calculator: bank-offered interest rate + amortization methods

**Date:** 2026-08-10
**Status:** Draft — pending review
**Repos affected:** `FinTrack-Prime-Api` (backend), `FinTrackPrime` (frontend, `C:\Users\Wyrlo\projects\FinTrackPrime`)

## Background

This spec now covers two related changes to the Loan Calculator, designed together since they land in the same request/response shapes:

1. **Bank-offered rate.** Today `AnnualInterestRatePercent` is a free-typed field (0–100%) the customer edits directly — the calculator has no concept of what the bank actually charges. The bank wants its own actual, current rate applied, with the customer unable to change it.
2. **Amortization methods.** Today the calculator only implements one repayment method (constant payment every period — the "Equal"/annuity system). The bank wants to offer the standard set of amortization methods a real loan product catalog would (imagine this system belongs to a bank like JPMorgan): equal payments, fixed principal, an interest-only grace period, and an interest-only-with-balloon-payment structure.

### Bank rate

Real bank rates differ by loan purpose, so this is implemented per loan type (Mortgage / Auto Loan / Student Loan / Personal Loan / Other), reusing the `LiabilityType` enum introduced in the Financial Statement work rather than a parallel enum — the type of loan a customer calculates here is the same taxonomy as the type of liability they'd later track on their Financial Statement.

### Amortization methods

Research into standard loan amortization systems ([GraphCalc](https://www.graphcalc.com/amortization-schedule-for-equal-principle-payments-calculator/), [Calculator Soup](https://www.calculatorsoup.com/calculators/financial/amortization-equal-principal-payments-calculator.php), [Wikipedia — Amortizing loan](https://en.wikipedia.org/wiki/Amortizing_loan), [Omni Calculator — Balloon Payment](https://www.omnicalculator.com/finance/balloon-payment), [Crestmont Capital](https://www.crestmontcapital.com/blog/balloon-payments-vs-regular-amortization)) confirms four distinct, well-established methods map to what was requested:

| Requested label | Standard name | Mechanics |
|---|---|---|
| Fixed Equal Amortization Case | "French"/annuity system | Constant total payment every period; principal/interest mix shifts over time. **Already implemented** — this is exactly what `LoanCalculatorService.Calculate()` does today. |
| Fixed Principal Amortization Case | "German" system | Constant principal portion every period; total payment declines over time as the interest portion shrinks. |
| Fixed Equal Amortization Case with Grace Period | Interest-only grace period | An initial period of interest-only payments (no principal reduction), then switches to the Equal system for the remaining term. |
| Periodic Interest Payment, Balloon Payment at Maturity | "Bullet" loan | Interest-only every period except the last, where the entire remaining principal is due as one lump sum. |

A fifth requested option — weekly installments — is **explicitly deferred**: it changes the payment-frequency unit this calculator (and its affordability check, which compares to *monthly* income) assumes throughout, and deserves its own follow-on rather than being bundled in.

## Goals

- The customer picks a **Loan Type** (for the rate), not a rate.
- The rate applied to both the amortization calculation and the affordability check comes from a bank-managed rate table, resolved server-side — the client can never influence it, even by calling the API directly.
- The customer separately picks an **Amortization Method**, and the schedule is computed accordingly.
- Loan Type and Amortization Method are fully independent — any combination is valid (e.g. a Personal Loan can use Balloon, a Mortgage can use Grace Period).

## Non-goals

- No admin UI to edit rates — none exists anywhere in this app today (no staff/admin role, only customer `User` accounts). Rates are updated by whoever has backend access, via a migration/seed/direct DB update — the same way `Premium:PriceUsd` is updated today.
- No term-length-tiered pricing, no rate history/audit trail, no risk-based/credit-score-adjusted pricing — unchanged from the original bank-rate design.
- No weekly-installment method (see above — deferred).
- No constraint mapping between Loan Type and Amortization Method (e.g. "only Mortgages offer Grace Period") — every combination is offered to every loan type.

## Data model (backend)

### New `LoanRate` entity (unchanged from the original spec)

```csharp
// FinTrackPrime.Models.Entities.LoanRate
public class LoanRate
{
    public Guid Id { get; set; }
    public LiabilityType Type { get; set; }   // Mortgage | AutoLoan | StudentLoan | PersonalLoan | Other — never CreditCard, that's not an amortized loan
    public decimal AnnualRatePercent { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

`DbSet<LoanRate>` on `FinTrackDbContext`, unique index on `Type` (at most one active rate per loan type).

### Seed data (unchanged)

Inserted directly in the same still-uncommitted `InitialMigration` (via `migrationBuilder.InsertData`). **Placeholder values — must be replaced with the bank's real figures before launch:**

| Type | AnnualRatePercent |
|---|---|
| Mortgage | 6.50 |
| AutoLoan | 7.25 |
| PersonalLoan | 11.00 |
| StudentLoan | 5.50 |
| Other | 9.00 |

### New `AmortizationMethod` enum

```csharp
// FinTrackPrime.Models.Entities.AmortizationMethod
public enum AmortizationMethod
{
    Equal,
    FixedPrincipal,
    GracePeriod,
    Balloon,
}
```

No new table — this is a request-time choice, not bank-managed data.

## Amortization math per method

All four share the existing `MaxScheduleMonths` safety cap and round every row to 2 decimals, same as today's `Calculate` method.

- **Equal** (unchanged): `requiredPayment = P × r × (1+r)^n / ((1+r)^n − 1)` (today's `CalculateRequiredMonthlyPayment`), constant every period.
- **FixedPrincipal**: `fixedPrincipal = Principal / TermMonths`, constant every period. Each period: `interest = balance × rate`; `principalPaid = fixedPrincipal + extra`; `payment = principalPaid + interest`; payment declines over time as `interest` shrinks.
- **GracePeriod**: a new `GracePeriodMonths` request field. Months `1..GracePeriodMonths` are interest-only (`payment = balance × rate`; any `ExtraMonthlyPayment` still reduces principal early during this phase). At month `GracePeriodMonths + 1`, the Equal formula is recomputed against the *remaining* balance and *remaining* term (`TermMonths - GracePeriodMonths`), then proceeds exactly like Equal for the rest of the schedule.
- **Balloon**: months `1..TermMonths-1` are interest-only (`ExtraMonthlyPayment` still allowed, partially reducing the eventual balloon — a real, common "partially amortizing balloon" variant, not a special case to avoid). Month `TermMonths` pays the entire remaining balance plus that period's interest in one lump sum.

**`ExtraMonthlyPayment`** behaves uniformly across all four methods — added on top of whatever base payment is due that period, always reducing principal. No per-method special-casing needed.

**`RequiredMonthlyPayment`** in the response only has one clean meaning for Equal, where it's constant. For the other three methods, this field is repurposed to mean *the first period's payment* — the full picture (including how it changes) is already available via the existing `Schedule` list, which already returns a per-row `PaymentAmount`. No response shape growth needed just to represent a changing payment.

**Affordability check** (`CheckAffordabilityAsync`) uses the same *first period's payment* for its `ProposedMonthlyPayment`/DTI calculation, for the identical reason. This is a real, known limitation for Balloon specifically — a DTI ratio built from monthly payments doesn't reflect a lump sum due at maturity — but that's a property of DTI ratios applied to balloon loans in general, not something to solve algorithmically here; flagged as a UI-level caveat, not a math problem.

## API contract

### View models

```csharp
public class LoanRateViewModel
{
    public LiabilityType Type { get; set; }
    public decimal AnnualRatePercent { get; set; }
}

// AnnualInterestRatePercent is REMOVED — the client can no longer supply
// a rate. LoanType drives which bank rate the server applies. Method and
// GracePeriodMonths are new.
public class LoanCalculationRequest
{
    [Range(0.01, double.MaxValue)]
    public decimal PrincipalAmount { get; set; }

    [Required]
    public LiabilityType LoanType { get; set; }

    [Range(1, 480)]
    public int TermMonths { get; set; }

    [Range(0, double.MaxValue)]
    public decimal ExtraMonthlyPayment { get; set; } = 0m;

    public AmortizationMethod Method { get; set; } = AmortizationMethod.Equal;

    // Required (and validated: 0 < value < TermMonths) only when
    // Method == GracePeriod; ignored for every other method.
    public int? GracePeriodMonths { get; set; }
}

public class LoanCalculationResultViewModel
{
    public decimal RequiredMonthlyPayment { get; set; }   // first period's payment for non-Equal methods — see "Amortization math" above
    public int PayoffMonths { get; set; }
    public decimal TotalInterestPaid { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal AppliedAnnualInterestRatePercent { get; set; }   // echoes the bank rate actually used
    public List<AmortizationRowViewModel> Schedule { get; set; } = new();
}

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

// LoanAffordabilityResultViewModel unchanged in shape — ProposedMonthlyPayment
// is now the resolved-rate, resolved-method first-period payment.
```

### Endpoints (`LoanCalculatorController`, unchanged base route, still `RequirePremium`)

| Method | Route | Body | Returns |
|---|---|---|---|
| GET | `/rates` *(new)* | — | `List<LoanRateViewModel>` |
| POST | `/calculate` | `LoanCalculationRequest` (now `LoanType` + `Method` + `GracePeriodMonths`, no rate) | `LoanCalculationResultViewModel` (now includes applied rate) |
| POST | `/affordability` | `LoanAffordabilityRequest` (now `LoanType` + `Method` + `GracePeriodMonths`, no rate) | `LoanAffordabilityResultViewModel` (unchanged shape) |

### Service layer

`ILoanCalculatorService.Calculate` becomes `CalculateAsync` (needs DB access now) — looks up `LoanRate` by `request.LoanType`, throws `InvalidOperationException` if no rate row exists for that type (defensive guard; shouldn't trigger once seeded) or if `Method == GracePeriod` and `GracePeriodMonths` is missing/out of range, then builds the amortization schedule using the method-specific logic above. `CheckAffordabilityAsync` does the identical rate/method resolution instead of trusting a client-supplied rate. New `GetRatesAsync` returns all seeded rates.

## Frontend changes

### Types (`types/api.ts`)

- `AmortizationMethod` string union: `'Equal' | 'FixedPrincipal' | 'GracePeriod' | 'Balloon'`.
- `LoanCalculationRequest`/`LoanAffordabilityRequest`: `annualInterestRatePercent` → `loanType: LiabilityType`; add `method: AmortizationMethod` and `gracePeriodMonths?: number`.
- `LoanCalculationResultViewModel`: add `appliedAnnualInterestRatePercent: number`.
- New `LoanRateViewModel { type: LiabilityType, annualRatePercent: number }`.

### `api/loanCalculator.ts`

Add `getRates(): Promise<LoanRateViewModel[]>`.

### `pages/LoanCalculatorPage.tsx`

- The free-typed "Annual interest rate" `Input` is replaced by a **Loan Type `Select`** (Mortgage, Auto Loan, Student Loan, Personal Loan, Other — same restricted set as the Financial Statement's manual-liability Type picker). Next to it, a **read-only display** shows the bank's current rate for the selected type, resolved client-side from `getRates()` (fetched once, cached — no polling).
- A separate **Amortization Method `Select`** (Fixed Equal Amortization, Fixed Principal Amortization, Fixed Equal Amortization with Grace Period, Periodic Interest Payment with Balloon at Maturity — labels matching the reference tool's wording) — fully independent of the Loan Type picker.
- When Method is Grace Period, a **Grace period (months)** `Input` appears (hidden otherwise), feeding `gracePeriodMonths`.
- The Results card's payment stat gains a label change for non-Equal methods — "First payment" instead of "Monthly payment" — since the amount isn't constant; the balance chart (already rendered from `Schedule`) naturally shows the balloon/declining-payment shape without any chart-specific change.
- Both `calculate` and `checkAffordability` key off the same `loanType`/`method`/`gracePeriodMonths` state.

## Validation summary

| Rule | Enforced where |
|---|---|
| Client cannot supply or influence the interest rate | `AnnualInterestRatePercent` removed from both request DTOs entirely |
| `LoanType` must have a seeded `LoanRate` row | Service throws `InvalidOperationException` (defensive; shouldn't trigger once seeded) |
| `GracePeriodMonths` required and `0 < value < TermMonths` when `Method == GracePeriod` | Service throws `InvalidOperationException`; ignored/not required for other methods |
| `PrincipalAmount` > 0, `TermMonths` 1–480, `ExtraMonthlyPayment` ≥ 0 | Existing `[Range]` validation, unchanged |

## Open questions for review

1. **Seed values**: the five placeholder rates are illustrative, not real bank figures.
2. **`Other` loan type**: kept for consistency with the Financial Statement's liability types; confirm it's still wanted here.
3. **`InvalidOperationException` on missing rate**: defensive-only; flagging in case graceful degradation is preferred over an error.
4. **Balloon + DTI caveat**: the affordability check's ratio doesn't reflect a balloon's final lump sum — worth a UI disclaimer, not a math fix, but confirm that's an acceptable tradeoff rather than needing (e.g.) a separate "balloon amount" callout in the affordability card.
5. **Weekly installments**: deferred entirely from this spec, per your scope answer — follow-on work, not forgotten.
