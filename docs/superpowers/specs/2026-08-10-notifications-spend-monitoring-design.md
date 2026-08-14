# Notifications: infrastructure, SignalR delivery, background spend monitoring

**Date:** 2026-08-10
**Status:** Draft — pending review
**Repos affected:** `FinTrack-Prime-Api` (backend), `FinTrackPrime` (frontend, `C:\Users\Wyrlo\projects\FinTrackPrime`)

## Background

Nothing about notifications exists in this app today beyond a dead UI shell: `TopNav.tsx` renders a bell icon with a `DropdownMenu` whose `items` are a hardcoded empty array and whose `header` is a static `<EmptyState title="No notifications yet" />` — it has never been wired to any data. There is no `Notification` entity, no notifications API, no SignalR, and no recurring background job anywhere in the backend; the only thing that currently happens automatically-on-a-schedule in this app is nothing — `BankLinkService.SyncAsync` only ever runs when a signed-in user clicks "Sync accounts" or completes a bank-link flow.

This spec covers the foundational piece: a real `Notification` model, SignalR-based real-time delivery, and a background job that periodically re-syncs every linked user and flags unusually large spend. **Security-event notifications** (new bank connected/disconnected, sign-in from an unrecognized device) are an explicit follow-on, out of scope here, reusing this same infrastructure once it exists.

### Finverse sandbox research

Finverse's Developer Portal issues sandbox ("Test app") credentials by default, and its Demo App lets a developer link a synthetic "Testbank" — which this codebase already integrates with (`BankLinkService.cs` hardcodes `"Testbank"` as the institution name, and comments there cite real observed sync behavior like `"Payment. Thank you"` credit-card payments). However, **Finverse does not publish Testbank's actual transaction dataset anywhere in its public docs** ([Finverse API Docs](https://docs.finverse.com/), [Finverse Data API](https://www.finverse.com/bank-data-api)) — what this codebase already knows about it was learned by directly linking the sandbox, not by reading documentation. There is no guarantee Testbank happens to contain a transaction large enough to trip a spend-detection rule.

**Consequence for this design:** detection must be a genuine, generically-applicable rule (see below) that works against whatever transactions actually arrive, not something tuned to a known fixture. Demonstrating this feature may require either waiting for Testbank's normal data to happen to cross the threshold, or (out of scope for this spec, but worth knowing) manually inserting a test `Transaction` row during a demo.

## Goals

- A persistent, per-user `Notification` record — not an ephemeral toast.
- Real-time delivery via SignalR, adapting this app's existing Bearer-token JWT auth to work over a WebSocket connection.
- A recurring background job that re-syncs every user with a linked bank on a schedule and flags transactions matching a flat-threshold "unusual spend" rule.
- A real `GET /api/notifications` API and a working notification bell in `TopNav.tsx`, replacing today's dead shell.

## Non-goals

- Security-event notifications (bank connected/disconnected, new-device sign-in) — explicit follow-on spec, reusing this infrastructure.
- Statistical/per-category anomaly detection (deviation from a user's own historical average) — flat threshold only, per the "no guaranteed sandbox data to build history from" constraint above. Worth revisiting once there's real usage data.
- A SignalR backplane (Redis/Azure SignalR) for multi-instance fan-out — this app runs as a single instance today; adding a backplane is a deployment-scale concern for later, not a design gap in this spec.
- Any change to `BankLinkService`'s existing sync logic itself — the background job calls it as-is.

## Data model (backend)

### New `Notification` entity

```csharp
// FinTrackPrime.Models.Entities.Notification
public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    // Set for UnusualSpend, null for any notification type that isn't
    // tied to one specific transaction. Lets the frontend deep-link to
    // it (e.g. highlight the row on the Dashboard).
    public Guid? RelatedTransactionId { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
```

```csharp
// FinTrackPrime.Models.Entities.NotificationType
public enum NotificationType
{
    UnusualSpend,
    // Security-event values (BankConnected, BankDisconnected,
    // NewDeviceSignIn, ...) are added by the follow-on spec, not here.
}
```

`DbSet<Notification>` on `FinTrackDbContext`. Indexes: `(UserId, CreatedAtUtc)` for the list query, and a unique index on `RelatedTransactionId` *where not null* (SQL Server filtered index, same pattern already used for `User.GoogleId`) — this is what makes the background job's dedup ("don't re-flag a transaction that already has a notification") a database-enforced guarantee, not just an application-level check that could race.

### Config (`appsettings.json`, same pattern as `Premium`)

```json
"SpendMonitoring": {
  "IntervalHours": 4,
  "FlatThresholdUsd": "1000.00",
  "IncomePercentThreshold": "50"
}
```

## Detection rule

A synced `Expense` transaction is flagged if **either**:
- `Amount >= SpendMonitoring:FlatThresholdUsd`, **or**
- the user has at least one `Income`-type `BudgetCategory` (same "only rate with real data" guard `LoanCalculatorService.CheckAffordabilityAsync` already uses) and `Amount >= (IncomePercentThreshold / 100) * totalMonthlyIncome`.

Each qualifying transaction produces exactly one `Notification` (`Type = UnusualSpend`, `RelatedTransactionId` set) — the filtered unique index prevents a second one on a later job run.

## Backend: background job

**`SpendMonitorService : BackgroundService`**, registered via `builder.Services.AddHostedService<SpendMonitorService>()`. Runs on a `PeriodicTimer` at `SpendMonitoring:IntervalHours`. Since it's a singleton but `FinTrackDbContext`/`BankLinkService`/etc. are all scoped, each tick opens a fresh scope via `IServiceScopeFactory`:

1. Query distinct `UserId` values from `LinkedInstitutions`.
2. For each user (own scope per user, so one user's failure — same as `BankLinkService.SyncAsync`'s existing per-institution isolation — can't take down the rest): call the scope's `IBankLinkService.SyncAsync(userId)` (reusing the exact same, already-tested Finverse sync path a manual "Sync accounts" click uses — not a parallel code path).
3. Load that user's `Expense` transactions with no existing `Notification` (`LEFT JOIN` / `Where(t => !_db.Notifications.Any(n => n.RelatedTransactionId == t.Id))`), apply the detection rule above.
4. For each match: insert the `Notification`, `SaveChangesAsync`, then push it over SignalR (below).

## Backend: SignalR delivery

**`NotificationsHub : Hub`** at `/hubs/notifications`, `[Authorize]`. `OnConnectedAsync` adds the connection to a group named after the caller's `UserId` claim (`Groups.AddToGroupAsync(Context.ConnectionId, userId)`) — this is the addressing scheme: one group per user, not a broadcast. `SpendMonitorService` (and the security-events follow-on, later) depend on `IHubContext<NotificationsHub>` to call `Clients.Group(userId).SendAsync("ReceiveNotification", dto)` right after each `Notification` row is inserted.

**JWT-over-WebSocket adaptation**: a browser can't set an `Authorization` header on a WebSocket handshake, so the SignalR JS client instead sends the token as an `access_token` query-string parameter (the standard SignalR+JWT pattern). `Program.cs`'s `AddJwtBearer(...)` call gains one addition — everywhere else in the app, the existing header-based flow is completely untouched:

```csharp
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters { /* unchanged */ };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        },
    };
});
```

`Program.cs` also needs `builder.Services.AddSignalR()` and `app.MapHub<NotificationsHub>("/hubs/notifications")`. CORS already supports this without changes — the existing `FrontendPolicy` already combines explicit `WithOrigins` with `AllowCredentials`, which is what SignalR's negotiate/handshake requests need.

## API

**New `NotificationsController`** (`api/notifications`, `[Authorize]` — no `RequirePremium`, spend/security alerts aren't a premium feature):

| Method | Route | Returns |
|---|---|---|
| GET | `/?page=1&pageSize=20` | `NotificationListViewModel { Items: List<NotificationViewModel>, UnreadCount, HasMore }` |
| POST | `/{id}/read` | 204 |
| POST | `/read-all` | 204 |

```csharp
public class NotificationViewModel
{
    public Guid Id { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Guid? RelatedTransactionId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
```

## Frontend

- **`types/api.ts`**: `NotificationType`, `NotificationViewModel`, `NotificationListViewModel`.
- **`api/notifications.ts`** (new): `list(page)`, `markRead(id)`, `markAllRead()`.
- **`hooks/useNotificationsHub.ts`** (new): opens one `@microsoft/signalr` `HubConnection` to `/hubs/notifications` per session, using `accessTokenFactory: () => authSession.get()?.token ?? ''` (mirrors how `apiClient`'s request interceptor already reads the in-memory token). On `ReceiveNotification`: fires a toast via the existing `useToast()` and invalidates the `['notifications']` query so the bell's list/badge refresh. Connection starts once the user is authenticated (mirrors `AuthProvider`'s existing initialization gating) and stops on logout.
- **`TopNav.tsx`**: the Notifications `DropdownMenu`'s `items` stay `[]` (that prop is a flat list of one-line clickable actions — not shaped for title+message+timestamp+read-state rows) but its `header` slot, currently a static `<EmptyState>`, becomes a new `<NotificationList />` component: fetches via `['notifications']`, renders each with an unread-state dot, marks read on click, "Mark all read" action, and keeps the existing `<EmptyState title="No notifications yet" />` as its own empty-list case. The bell `IconButton` itself gains a small unread-count badge.

## Validation / integrity summary

| Rule | Enforced where |
|---|---|
| A transaction gets at most one `UnusualSpend` notification | Filtered unique index on `Notification.RelatedTransactionId` (DB-enforced, not just app-level) |
| Only the notification's own owner can mark it read / see it in the list | `NotificationsController` filters every query by the caller's `UserId` from the JWT, same pattern as every other controller in this app |
| SignalR connections only receive their own user's notifications | Group-per-user addressing — a connection is never added to any group but its own |
| One user's sync failure doesn't block the job for other users | `SpendMonitorService` isolates each user's work in its own try/catch + scope, same isolation `BankLinkService.SyncAsync` already has per-institution |

## Open questions for review

1. **`FlatThresholdUsd`/`IncomePercentThreshold` values** ($1,000 / 50%) are illustrative placeholders, not the bank's real risk tolerance — confirm before anything resembling production.
2. **4-hour interval**: reasonable default balancing "feels live enough" against re-syncing every linked user's full Finverse data repeatedly — flag if the bank has a specific SLA or Finverse rate-limit number this should be tuned against instead.
3. **Notification retention**: no expiry/archival is specified here — notifications accumulate forever. Worth deciding later (e.g. auto-mark-read after N days) but not blocking for this spec.
