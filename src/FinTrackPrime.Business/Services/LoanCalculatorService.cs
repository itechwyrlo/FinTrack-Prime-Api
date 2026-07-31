using System;
using System.Collections.Generic;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Services
{
    public class LoanCalculatorService : ILoanCalculatorService
    {
        // Safety cap so a pathological input (near-zero rate, huge term)
        // can't loop forever; a real loan schedule never needs this many
        // rows.
        private const int MaxScheduleMonths = 600;

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
    }
}
