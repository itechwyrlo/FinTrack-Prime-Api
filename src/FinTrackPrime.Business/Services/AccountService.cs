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
                        })
                        .ToList(),
                });
            }

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
            };

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
            };
        }
    }
}