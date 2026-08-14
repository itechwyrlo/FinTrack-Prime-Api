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
    public class CashFlowService : ICashFlowService
    {
        private readonly FinTrackDbContext _db;

        public CashFlowService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<CashFlowViewModel> GetCashFlowAsync(Guid userId)
        {
            // Include(Account) so t.Account.Currency below doesn't require
            // a second round trip per transaction.
            var transactions = await _db.Transactions
                .Include(t => t.Account)
                .Where(t => t.Account!.UserId == userId)
                .ToListAsync();

            // No FX conversion exists in this app (see Account.Currency),
            // so transactions are grouped per currency and summed only
            // within their own group — never blended across currencies.
            var byCurrency = transactions
                .GroupBy(t => t.Account!.Currency)
                .Select(g => Summarize(g.Key, g.ToList()))
                .OrderByDescending(c => c.TransactionCount)
                .ToList();

            // "Primary" = whichever currency has the most transactions.
            // Arbitrary but deterministic, and matches the common case of
            // one home-currency account plus a couple of foreign cards.
            var primary = byCurrency.FirstOrDefault()
                ?? Summarize(string.Empty, new List<Transaction>());

            return new CashFlowViewModel
            {
                Currency = primary.Currency,
                TotalIncome = primary.TotalIncome,
                TotalExpenses = primary.TotalExpenses,
                Net = primary.Net,
                ExpenseByCategory = primary.ExpenseByCategory,
                MonthlyTrend = primary.MonthlyTrend,
                OtherCurrencies = byCurrency
                    .Where(c => !ReferenceEquals(c, primary))
                    .Select(c => new CashFlowByCurrencyViewModel
                    {
                        Currency = c.Currency,
                        TotalIncome = c.TotalIncome,
                        TotalExpenses = c.TotalExpenses,
                        Net = c.Net,
                        ExpenseByCategory = c.ExpenseByCategory,
                        MonthlyTrend = c.MonthlyTrend,
                    })
                    .ToList(),
            };
        }

        // Everything CashFlowViewModel/CashFlowByCurrencyViewModel need,
        // shared between the primary currency and each entry in
        // OtherCurrencies so the two can't drift out of sync with each
        // other. TransactionCount is only used to pick the primary
        // currency above and isn't exposed on either view model.
        private sealed record CurrencySummary(
            string Currency,
            int TransactionCount,
            decimal TotalIncome,
            decimal TotalExpenses,
            decimal Net,
            List<CategoryAmountViewModel> ExpenseByCategory,
            List<MonthlyCashFlowViewModel> MonthlyTrend);

        private static CurrencySummary Summarize(string currency, List<Transaction> transactions)
        {
            var totalIncome = transactions
                .Where(t => t.Direction == TransactionDirection.Income)
                .Sum(t => t.Amount);

            var totalExpenses = transactions
                .Where(t => t.Direction == TransactionDirection.Expense)
                .Sum(t => t.Amount);

            var expenseByCategory = transactions
                .Where(t => t.Direction == TransactionDirection.Expense)
                .GroupBy(t => t.Category)
                .Select(g => new CategoryAmountViewModel
                {
                    Category = g.Key,
                    Amount = g.Sum(t => t.Amount),
                })
                .OrderByDescending(c => c.Amount)
                .ToList();

            var monthlyTrend = transactions
                .GroupBy(t => new { t.OccurredAtUtc.Year, t.OccurredAtUtc.Month })
                .Select(g => new MonthlyCashFlowViewModel
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    Income = g.Where(t => t.Direction == TransactionDirection.Income).Sum(t => t.Amount),
                    Expenses = g.Where(t => t.Direction == TransactionDirection.Expense).Sum(t => t.Amount),
                })
                .OrderBy(m => m.Year).ThenBy(m => m.Month)
                .ToList();

            return new CurrencySummary(
                currency,
                transactions.Count,
                totalIncome,
                totalExpenses,
                totalIncome - totalExpenses,
                expenseByCategory,
                monthlyTrend);
        }
    }
}
