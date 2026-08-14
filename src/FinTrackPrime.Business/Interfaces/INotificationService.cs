using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FinTrackPrime.Models.ViewModels;

namespace FinTrackPrime.Business.Interfaces
{
    public interface INotificationService
    {
        Task<NotificationListViewModel> GetNotificationsAsync(Guid userId, int page, int pageSize);

        // Throws InvalidOperationException if the notification doesn't
        // exist or belongs to a different user.
        Task MarkReadAsync(Guid userId, Guid notificationId);

        Task MarkAllReadAsync(Guid userId);

        // Evaluates this user's Expense transactions that don't already
        // have an UnusualSpend notification against the flat-threshold
        // rule, inserts a Notification for each match, and returns the
        // ones just created — so the caller (SpendMonitorService, in
        // WebApi) can push them over SignalR. Assumes the caller has
        // already synced this user's transactions; this method only
        // reads what's already in the database.
        Task<List<NotificationViewModel>> CreateSpendNotificationsAsync(Guid userId);
    }
}
