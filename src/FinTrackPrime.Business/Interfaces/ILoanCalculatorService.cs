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
