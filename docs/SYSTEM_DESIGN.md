# FinTrack Prime — System Design Documentation

- **Project Name:** FinTrack Prime
- **Course Name:** [Insert Course/Capstone Name]
- **Repositories covered:** `FinTrack-Prime-Api` (backend, this repo) and `FinTrackPrime` (frontend, separate repo)

---

## 1. Introduction & Problem Statement

FinTrack Prime is a personal finance web application that lets a user track
bank accounts and transactions, plan a budget, see their income/expense cash
flow, and — behind a one-time PayPal purchase per tool — use four advanced
financial planning tools: a loan calculator, an investment portfolio tracker,
a retirement planner, and a personal financial statement (net worth)
generator.

**Target user:** an individual who wants a single dashboard for everyday
budgeting (free tier) with the option to unlock deeper financial-planning
tools without a recurring subscription — a pay-once-per-tool model rather
than an all-or-nothing paywall.

**Core problem it solves:** most budgeting apps either give away nothing
without a subscription or dump every feature on a new user at once. FinTrack
Prime separates day-to-day budgeting (free) from advanced planning tools
(individually unlockable), and — critically — makes sure "the user paid" is
verified independently by the server rather than trusted from the browser.

---

## 2. System Design & User Interface

### 2.1 User flow

1. **Landing (`/`)** — redirects to `/dashboard` if authenticated, otherwise
   `/login`.
2. **Auth (`/login`, `/register`)** — email/password or Google Sign-In. A
   successful login gets a short-lived (15 min) access token in memory plus
   an HttpOnly refresh cookie; the app silently refreshes the access token
   on load and ~60 seconds before it expires, so the user is never bounced
   to `/login` mid-session.
3. **`AppLayout`** (Sidebar + TopNav) wraps every authenticated page:
   `/dashboard`, `/budget-planner`, `/cash-flow`, `/upgrade` are open to any
   logged-in user.
4. **Premium tools** — `/loan-calculator`, `/investment-tracker`,
   `/retirement-planner`, `/financial-statement` are each wrapped in a
   `PremiumRoute` that checks `user.unlockedTools`. If the tool isn't
   unlocked, the user is redirected to `/upgrade?tool=X`.
5. **`/upgrade`** — renders a PayPal Buttons widget per locked tool. On
   approval, the frontend captures the order, then calls the backend to
   verify it; a successful verify returns a new token with the tool
   unlocked, and the user is routed into the tool immediately.

### 2.2 Front-end layout & design choices

- **Design tokens over ad-hoc styling** — Tailwind v4's `@theme` block in
  `src/index.css` defines the palette (`--color-ft-navy`, `--color-ft-gold`,
  status and chart-series colors) once, so every page/chart draws from the
  same source instead of hardcoded hex values.
- **Headless components, one design system** — the ~24 primitives in
  `components/ui/` wrap Radix UI (Dialog, Select, Tabs, Toast, Tooltip,
  etc.), giving accessible keyboard/focus behavior for free while keeping a
  consistent look across all ten pages.
- **Route-level access control as components, not page-level checks** —
  `ProtectedRoute` and `PremiumRoute` are the *only* places auth/entitlement
  logic lives on the client; individual pages don't re-check it.
- **Installable but data-safe PWA** — `vite-plugin-pwa` precaches the app
  shell for offline load, but `/api/*` is explicitly configured
  `NetworkOnly` so account balances and transactions are never served from a
  stale cache.

### 2.3 Directory structure

**Backend** (`FinTrack-Prime-Api`) — Clean Architecture, three projects:

```
src/
├── FinTrackPrime.Models/       # entities, EF Core DbContext, view models (DTOs)
│   ├── Entities/                # User, Account, Transaction, BudgetCategory, ...
│   ├── ViewModels/               # request/response DTOs, one file per feature
│   ├── Persistence/              # FinTrackDbContext.cs
│   └── Migrations/               # EF Core migrations
├── FinTrackPrime.Business/     # business logic — one interface + service per feature
│   ├── Interfaces/
│   └── Services/                 # AuthService, JwtTokenGenerator, PayPalClient, ...
└── FinTrackPrime.WebApi/       # HTTP layer
    ├── Controllers/              # 10 controllers, one per feature
    ├── Auth/                     # RefreshTokenCookie.cs
    └── Program.cs                # DI, JWT auth, CORS, authorization policies
```

**Frontend** (`FinTrackPrime`):

```
src/
├── pages/          # one page per route (Dashboard, BudgetPlanner, CashFlow,
│                    #  Upgrade, and the 4 premium-tool pages)
├── components/
│   └── ui/          # ~24 Radix-based primitives (Button, Modal, Table, Tabs, Toast, ...)
├── api/             # one module per backend feature; client.ts holds the
│                    #  shared axios instance + auth interceptors
├── context/         # AuthContext, ThemeContext
├── hooks/           # usePayPalScript, useInstallPrompt, ...
├── config/          # navConfig.ts — single source of truth for sidebar/nav
└── types/           # api.ts — shared API response types
```

---

## 3. Tech Stack & Implementation Details

| Layer | Technology | Why it was chosen |
|---|---|---|
| Backend API | ASP.NET Core 8 Web API | Built-in policy-based authorization maps cleanly onto "one claim per premium tool"; mature EF Core integration. |
| Database | SQL Server + EF Core | Relational FKs model the domain naturally (User → Accounts → Transactions, User → Holdings, etc.); migrations track schema changes. |
| Auth | JWT (15-min access token) + HttpOnly refresh cookie, rotation with family/reuse detection | Short-lived access tokens limit exposure if leaked; the refresh token is never readable by JS, so it isn't stealable via XSS. |
| Payments | PayPal REST API (server-verified) | The frontend cannot be trusted to say "I paid" — the backend independently re-checks the order with PayPal before granting access. |
| Frontend | React 19 + TypeScript + Vite | Type safety across ten feature pages; Vite's dev server keeps iteration fast. |
| Server state | TanStack Query | Handles caching/invalidation (e.g., re-fetching premium status right after checkout) without hand-rolled loading state. |
| UI | Radix UI + Tailwind v4 | Accessible unstyled primitives + utility-first styling driven by shared design tokens. |
| HTTP client | Axios with interceptors | Centralizes "attach the bearer token" and "silently refresh on 401" in one place instead of every call site. |
| PWA | vite-plugin-pwa | Installable app shell, explicitly excluding `/api/*` from caching so financial data is always live. |

### Critical feature: server-side PayPal verification

The single most important design decision in the app: the backend never
trusts the browser's "payment succeeded" message. `PremiumAccessService`
independently re-verifies the order with PayPal before granting anything
(`FinTrackPrime.Business/Services/PayPalClient.cs`,
`PremiumAccessService.cs`):

```csharp
// PremiumAccessService.VerifyAndGrantAsync (simplified)
if (await _db.PremiumPurchases.AnyAsync(p => p.UserId == userId && p.Tool == tool))
    throw new InvalidOperationException("You already have access to this tool.");
if (await _db.PremiumPurchases.AnyAsync(p => p.PayPalOrderId == paypalOrderId))
    throw new InvalidOperationException("This order has already been used.");

var order = await _payPalClient.GetOrderAsync(paypalOrderId);
if (order.Status != "COMPLETED") throw new InvalidOperationException(...);
if (!string.Equals(order.CurrencyCode, expectedCurrency, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Order currency does not match the expected price.");
if (order.AmountValue < expectedPrice) throw new InvalidOperationException(...);

_db.PremiumPurchases.Add(new PremiumPurchase { UserId, Tool, PayPalOrderId, AmountPaid = order.AmountValue, Currency = order.CurrencyCode, PurchasedAtUtc = DateTime.UtcNow });
await _db.SaveChangesAsync();
var token = _jwtTokenGenerator.GenerateToken(user, unlockedTools); // fresh JWT with the new unlock claim
```

Order: already-owns-tool check → order-id reuse check (backed by a DB unique
index as a hard backstop) → live PayPal order lookup → status check →
currency check → amount check → persist the purchase → reissue a JWT with
the updated `unlock:{Tool}` claim, since the old token still says `false`
until it naturally expires.

---

## 4. Challenges & Solutions (Learning Outcomes)

> The three items below are challenges the codebase's own design clearly
> had to solve, inferred from reading the implementation — swap in your own
> specific debugging story for each if you remember the details, since a
> grader will likely ask about them.

1. **Client-reported payment success can't be trusted.** A user could, in
   principle, fake a "PayPal approved" event in the browser. **Solution:**
   the backend independently calls PayPal's own Orders API and re-checks
   status, currency, and amount before granting anything — the browser's
   claim is only ever a hint to *which* order to check.
   **Skill learned:** never conflate "the client says it happened" with "it
   happened" for anything security- or money-relevant.

2. **A stateless JWT goes stale the moment access changes.** Since premium
   access is baked into JWT claims (`unlock:{Tool}`), a purchase made
   mid-session would otherwise leave the user holding a token that still
   says "not unlocked" until it expires. **Solution:** issue a brand-new JWT
   immediately after a successful purchase and have the frontend swap it
   into `AuthContext` right away, so `PremiumRoute` re-evaluates on the next
   render instead of waiting ~15 minutes.
   **Skill learned:** claims-based authorization needs an explicit
   invalidation/reissue path whenever the underlying entitlement can change
   during a live session.

3. **Refresh tokens are a long-lived secret; losing one is worse than
   losing an access token.** **Solution:** only a SHA-256 hash of the
   refresh token is stored (never the raw value), tokens are grouped by
   `FamilyId`, and reuse of an already-rotated token revokes the entire
   family — a signal that the token was stolen and replayed.
   **Skill learned:** rotation + reuse detection turns "a leaked refresh
   token" from a silent, permanent breach into a detectable, contained one.

---

## 5. Future Improvements

1. **Live market data for the Investment Tracker** — holding prices are
   currently manual entry; integrating a market-data API would make
   gain/loss figures real-time instead of only as fresh as the user's last
   update.
2. **Automated bank transaction import** (e.g. via Plaid or a similar
   aggregator) to replace manually entered/seeded transactions with a real
   cash-flow picture.
3. **Budget/bill push notifications** — the app is already an installable
   PWA; adding a push-capable service worker would let it alert users when
   they're over budget or a planned expense is due, closing the loop
   between the Budget Planner and real-time awareness.

