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

        public LoanCalculationResultViewModel Calculate(LoanCalculationRequest request)
        {
            var monthlyRate = (request.AnnualInterestRatePercent / 100m) / 12m;
            var requiredPayment = CalculateRequiredMonthlyPayment(request.PrincipalAmount, monthlyRate, request.TermMonths);

            var schedule = new List<AmortizationRowViewModel>();
            var balance = request.PrincipalAmount;
            var totalInterest = 0m;
            var totalPaid = 0m;
            var month = 0;

            while (balance > 0.01m && month < MaxScheduleMonths)
            {
                month++;

                var interestForMonth = balance * monthlyRate;
                var basePayment = requiredPayment + request.ExtraMonthlyPayment;

                // The last payment only needs to cover what's left, not a
                // full payment, or the loan would go negative.
                var paymentForMonth = Math.Min(basePayment, balance + interestForMonth);
                var principalPaid = paymentForMonth - interestForMonth;

                balance -= principalPaid;
                totalInterest += interestForMonth;
                totalPaid += paymentForMonth;

                schedule.Add(new AmortizationRowViewModel
                {
                    Month = month,
                    PaymentAmount = Math.Round(paymentForMonth, 2),
                    PrincipalPaid = Math.Round(principalPaid, 2),
                    InterestPaid = Math.Round(interestForMonth, 2),
                    RemainingBalance = Math.Round(Math.Max(balance, 0), 2),
                });
            }

            return new LoanCalculationResultViewModel
            {
                RequiredMonthlyPayment = Math.Round(requiredPayment, 2),
                PayoffMonths = month,
                TotalInterestPaid = Math.Round(totalInterest, 2),
                TotalPaid = Math.Round(totalPaid, 2),
                Schedule = schedule,
            };
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

        public async Task<LoanAffordabilityResultViewModel> CheckAffordabilityAsync(Guid userId, LoanAffordabilityRequest request)
        {
            var monthlyRate = (request.AnnualInterestRatePercent / 100m) / 12m;
            var proposedPayment = CalculateRequiredMonthlyPayment(request.PrincipalAmount, monthlyRate, request.TermMonths);

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

        private static AffordabilityRating RateAffordability(decimal projectedDtiPercent)
        {
            if (projectedDtiPercent <= ComfortableDtiThreshold) return AffordabilityRating.Comfortable;
            if (projectedDtiPercent <= ManageableDtiThreshold) return AffordabilityRating.Manageable;
            if (projectedDtiPercent <= StretchedDtiThreshold) return AffordabilityRating.Stretched;
            return AffordabilityRating.NotRecommended;
        }
    }
}
