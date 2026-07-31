using System;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface IAccountService
    {
        // Returns every account the user owns, each with its recent
        // transactions. This is the single call the dashboard screen
        // makes on load.
        Task<DashboardViewModel> GetDashboardAsync(Guid userId);

        // Real user input: creates one account the user actually asked
        // for, with the starting balance they entered. No fake data.
        Task<AccountViewModel> CreateAccountAsync(Guid userId, CreateAccountRequest request);

        // Real user input: records one transaction against an account
        // the user owns, and adjusts that account's balance by the
        // amount (added for income, subtracted for expense).
        Task<TransactionViewModel> AddTransactionAsync(
            Guid userId, Guid accountId, CreateTransactionRequest request);
    }
}