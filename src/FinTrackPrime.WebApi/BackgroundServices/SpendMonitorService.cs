using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinTrackPrime.WebApi.BackgroundServices
{
    // Runs on a timer (SpendMonitoring:IntervalHours), re-syncing every
    // user who has at least one linked bank and flagging unusually large
    // spend. One user's failure (an expired Finverse token, a transient
    // outage) is caught and logged, not allowed to stop the run for
    // everyone else — same isolation principle BankLinkService.SyncAsync
    // already applies per-institution, one level up.
    public class SpendMonitorService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SpendMonitorService> _logger;

        public SpendMonitorService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<SpendMonitorService> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalHours = _config.GetValue<double?>("SpendMonitoring:IntervalHours") ?? 4;
            using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

            do
            {
                await RunOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTrackDbContext>();

            var userIds = await db.LinkedInstitutions
                .Select(i => i.UserId)
                .Distinct()
                .ToListAsync(stoppingToken);

            foreach (var userId in userIds)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await ProcessUserAsync(userId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SpendMonitorService failed for user {UserId}", userId);
                }
            }
        }

        private async Task ProcessUserAsync(Guid userId, CancellationToken stoppingToken)
        {
            // Fresh scope per user: keeps one user's tracked EF Core
            // entities from bleeding into the next, same reason
            // BankLinkService.SyncAsync clears its ChangeTracker between
            // institutions.
            using var userScope = _scopeFactory.CreateScope();
            var bankLinkService = userScope.ServiceProvider.GetRequiredService<IBankLinkService>();
            var notificationService = userScope.ServiceProvider.GetRequiredService<INotificationService>();
            var hubContext = userScope.ServiceProvider.GetRequiredService<IHubContext<NotificationsHub>>();

            await bankLinkService.SyncAsync(userId);
            var created = await notificationService.CreateSpendNotificationsAsync(userId);

            foreach (var notification in created)
            {
                await hubContext.Clients.Group(userId.ToString()).SendAsync("ReceiveNotification", notification, stoppingToken);
            }
        }
    }
}
