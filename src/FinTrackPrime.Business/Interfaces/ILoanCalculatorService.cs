using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface ILoanCalculatorService
    {
        // No Task/async here on purpose: this is CPU-only math, nothing
        // to await. Nothing about a loan calculation is saved; the
        // request carries everything the calculation needs.
        LoanCalculationResultViewModel Calculate(LoanCalculationRequest request);

        // Unlike Calculate, this reads the user's own tracked income,
        // budgeted expenses, and liabilities to judge a proposed loan
        // against their real finances instead of numbers re-typed into
        // the request.
        Task<LoanAffordabilityResultViewModel> CheckAffordabilityAsync(Guid userId, LoanAffordabilityRequest request);
    }
}
