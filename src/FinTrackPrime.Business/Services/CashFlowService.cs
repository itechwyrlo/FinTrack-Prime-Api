using System;
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
            var transactions = await _db.Transactions
                .Where(t => t.Account!.UserId == userId)
                .ToListAsync();

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

            return new CashFlowViewModel
            {
                TotalIncome = totalIncome,
                TotalExpenses = totalExpenses,
                Net = totalIncome - totalExpenses,
                ExpenseByCategory = expenseByCategory,
                MonthlyTrend = monthlyTrend,
            };
        }
    }
}
