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
