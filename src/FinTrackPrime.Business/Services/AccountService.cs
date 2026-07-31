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
    public class AccountService : IAccountService
    {
        // A transaction needs to sit at least this many standard
        // deviations above the account's average expense to be flagged.
        // Two accounts of the same small size shouldn't flag every normal
        // purchase, so this is intentionally conservative.
        private const double UnusualStdDevThreshold = 2.0;

        // Below this many expense transactions, there isn't enough
        // history to call anything "unusual" yet.
        private const int MinTransactionsForFlagging = 4;

        private readonly FinTrackDbContext _db;

        public AccountService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<DashboardViewModel> GetDashboardAsync(Guid userId)
        {
            var accounts = await _db.Accounts
                .Where(a => a.UserId == userId)
                .Include(a => a.Transactions)
                .ToListAsync();

            var dashboard = new DashboardViewModel();

            foreach (var account in accounts)
            {
                RecomputeUnusualFlags(account.Transactions);

                dashboard.Accounts.Add(new AccountViewModel
                {
                    Id = account.Id,
                    Nickname = account.Nickname,
                    Type = account.Type,
                    Balance = account.Balance,
                    RecentTransactions = account.Transactions
                        .OrderByDescending(t => t.OccurredAtUtc)
                        .Take(25)
                        .Select(t => new TransactionViewModel
                        {
                            Id = t.Id,
                            Description = t.Description,
                            Category = t.Category,
                            Amount = t.Amount,
                            Direction = t.Direction,
                            OccurredAtUtc = t.OccurredAtUtc,
                            IsFlaggedUnusual = t.IsFlaggedUnusual,
                        })
                        .ToList(),
                });
            }

            // Flags were recalculated in memory above; persist them so the
            // next read (and any future reporting) sees the same result.
            await _db.SaveChangesAsync();

            return dashboard;
        }

        public async Task<AccountViewModel> CreateAccountAsync(Guid userId, CreateAccountRequest request)
        {
            var account = new Account
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Nickname = request.Nickname.Trim(),
                Type = request.Type,
                Balance = request.StartingBalance,
                CreatedAtUtc = DateTime.UtcNow,
            };

            _db.Accounts.Add(account);
            await _db.SaveChangesAsync();

            return new AccountViewModel
            {
                Id = account.Id,
                Nickname = account.Nickname,
                Type = account.Type,
                Balance = account.Balance,
                RecentTransactions = new List<TransactionViewModel>(),
            };
        }

        public async Task<TransactionViewModel> AddTransactionAsync(
            Guid userId, Guid accountId, CreateTransactionRequest request)
        {
            var account = await _db.Accounts
                .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId);

            if (account is null)
            {
                throw new InvalidOperationException("Account not found.");
            }

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Description = request.Description.Trim(),
                Category = request.Category.Trim(),
                Amount = request.Amount,
                Direction = request.Direction,
                OccurredAtUtc = DateTime.UtcNow,
                IsFlaggedUnusual = false,
            };

            // The balance reflects the transaction immediately; the
            // unusual-activity flag is recalculated separately, the next
            // time the dashboard is loaded, since it depends on the
            // account's whole history, not just this one entry.
            account.Balance += request.Direction == TransactionDirection.Income
                ? request.Amount
                : -request.Amount;

            _db.Transactions.Add(transaction);
            await _db.SaveChangesAsync();

            return new TransactionViewModel
            {
                Id = transaction.Id,
                Description = transaction.Description,
                Category = transaction.Category,
                Amount = transaction.Amount,
                Direction = transaction.Direction,
                OccurredAtUtc = transaction.OccurredAtUtc,
                IsFlaggedUnusual = transaction.IsFlaggedUnusual,
            };
        }

        // Rule-based outlier check, not machine learning: flags an expense
        // transaction when its amount sits more than UnusualStdDevThreshold
        // standard deviations above the account's own expense average.
        private static void RecomputeUnusualFlags(ICollection<Transaction> transactions)
        {
            var expenseAmounts = transactions
                .Where(t => t.Direction == TransactionDirection.Expense)
                .Select(t => (double)t.Amount)
                .ToList();

            foreach (var transaction in transactions)
            {
                transaction.IsFlaggedUnusual = false;
            }

            if (expenseAmounts.Count < MinTransactionsForFlagging)
            {
                return;
            }

            var mean = expenseAmounts.Average();
            var variance = expenseAmounts.Sum(a => Math.Pow(a - mean, 2)) / expenseAmounts.Count;
            var stdDev = Math.Sqrt(variance);

            if (stdDev == 0)
            {
                return;
            }

            var threshold = mean + (UnusualStdDevThreshold * stdDev);

            foreach (var transaction in transactions)
            {
                if (transaction.Direction == TransactionDirection.Expense && (double)transaction.Amount > threshold)
                {
                    transaction.IsFlaggedUnusual = true;
                }
            }
        }
    }
}