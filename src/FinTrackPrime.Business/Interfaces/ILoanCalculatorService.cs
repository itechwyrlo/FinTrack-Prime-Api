using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    // No Task/async here on purpose: this is CPU-only math, nothing to
    // await. Nothing about a loan calculation is saved; the request
    // carries everything the calculation needs.
    public interface ILoanCalculatorService
    {
        LoanCalculationResultViewModel Calculate(LoanCalculationRequest request);
    }
}
