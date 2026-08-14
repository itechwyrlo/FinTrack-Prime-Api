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
                    Currency = account.Currency,
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
    }
}