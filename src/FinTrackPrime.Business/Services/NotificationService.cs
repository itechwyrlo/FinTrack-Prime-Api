using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class NotificationService : INotificationService
    {
        private readonly FinTrackDbContext _db;
        private readonly IConfiguration _config;

        public NotificationService(FinTrackDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        public async Task<NotificationListViewModel> GetNotificationsAsync(Guid userId, int page, int pageSize)
        {
            var query = _db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAtUtc);

            var totalCount = await query.CountAsync();
            var unreadCount = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(n => new NotificationViewModel
                {
                    Id = n.Id,
                    Type = n.Type,
                    Title = n.Title,
                    Message = n.Message,
                    RelatedTransactionId = n.RelatedTransactionId,
                    IsRead = n.IsRead,
                    CreatedAtUtc = n.CreatedAtUtc,
                })
                .ToListAsync();

            return new NotificationListViewModel
            {
                Items = items,
                UnreadCount = unreadCount,
                HasMore = page * pageSize < totalCount,
            };
        }

        public async Task MarkReadAsync(Guid userId, Guid notificationId)
        {
            var notification = await _db.Notifications
                .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

            if (notification is null)
            {
                throw new InvalidOperationException("Notification not found.");
            }

            notification.IsRead = true;
            await _db.SaveChangesAsync();
        }

        public async Task MarkAllReadAsync(Guid userId)
        {
            var unread = await _db.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ToListAsync();

            foreach (var notification in unread)
            {
                notification.IsRead = true;
            }

            await _db.SaveChangesAsync();
        }

        public async Task<List<NotificationViewModel>> CreateSpendNotificationsAsync(Guid userId)
        {
            var flatThreshold = decimal.Parse(_config["SpendMonitoring:FlatThresholdUsd"] ?? "1000.00");
            var incomePercentThreshold = decimal.Parse(_config["SpendMonitoring:IncomePercentThreshold"] ?? "50");

            var monthlyIncome = await _db.BudgetCategories
                .Where(c => c.UserId == userId && c.Type == BudgetCategoryType.Income)
                .SumAsync(c => c.PlannedAmount);

            // Only an Expense can be "unusual spend" — Income and Transfer
            // rows are excluded outright, same distinction Cash Flow
            // already draws.
            var candidateTransactions = await _db.Transactions
                .Where(t => t.Account!.UserId == userId && t.Direction == TransactionDirection.Expense)
                .Where(t => !_db.Notifications.Any(n => n.RelatedTransactionId == t.Id))
                .ToListAsync();

            var created = new List<NotificationViewModel>();

            foreach (var transaction in candidateTransactions)
            {
                var overFlatThreshold = transaction.Amount >= flatThreshold;
                var overIncomeThreshold = monthlyIncome > 0
                    && transaction.Amount >= monthlyIncome * (incomePercentThreshold / 100m);

                if (!overFlatThreshold && !overIncomeThreshold)
                {
                    continue;
                }

                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Type = NotificationType.UnusualSpend,
                    Title = "Unusual spend detected",
                    Message = $"A {transaction.Amount:C} charge (\"{transaction.Description}\") is larger than your usual spending.",
                    RelatedTransactionId = transaction.Id,
                    IsRead = false,
                    CreatedAtUtc = DateTime.UtcNow,
                };

                _db.Notifications.Add(notification);

                created.Add(new NotificationViewModel
                {
                    Id = notification.Id,
                    Type = notification.Type,
                    Title = notification.Title,
                    Message = notification.Message,
                    RelatedTransactionId = notification.RelatedTransactionId,
                    IsRead = notification.IsRead,
                    CreatedAtUtc = notification.CreatedAtUtc,
                });
            }

            if (created.Count > 0)
            {
                await _db.SaveChangesAsync();
            }

            return created;
        }
    }
}
