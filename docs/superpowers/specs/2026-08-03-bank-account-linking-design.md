# Bank Account Linking (Finverse) — Design

- **Date:** 2026-08-03
- **Status:** Approved for implementation planning

## Problem

Accounts and transactions today are entirely manual: `POST /api/accounts`
and `POST /api/accounts/{id}/transactions` let a user type in a nickname,
starting balance, and hand-enter every transaction (`AccountsController.cs`,
`AccountService.cs`). Nothing connects to a real bank. This replaces that
manual ledger with real (sandboxed) bank account linking via **Finverse**,
an account-aggregator that already has a live integration with Union Bank
of the Philippines, plus a generic "Testbank" sandbox institution usable
immediately without any bank approval process.

Correction to the existing README: it describes registration as seeding
two mock accounts with starter transactions. That is no longer true — the
current `AuthService.RegisterAsync` explicitly does not create any
accounts ("real financial data...only exist once the user actually creates
them"). So there is no seeding step to remove; this design only touches
the manual create/add-transaction path.

## Scope

- Linking **Testbank** (Finverse's own sandbox institution) end-to-end:
  Link UI → account/transaction pull → stored in this app's DB → shown on
  the existing Dashboard/Cash Flow/Budget Planner screens unchanged.
- Account types supported: **Checking, Savings, Credit Card** (the three
  that map to "debit or credit card" from the original ask). Finverse's
  Testbank also returns Bitcoin, FX, and Ledger account types — those are
  explicitly **out of scope** and filtered out at sync time.
- Sync is **pull-based** (triggered by user action / dashboard load), not
  webhook-based — there is no publicly reachable URL for Finverse to call
  back to in local dev.

**Explicitly out of scope for this pass:**
- Real Union Bank PH access. The Finverse app registered for this project
  defaults to "Test" status, which only allows linking Testbank / test
  cards. Real named institutions require Finverse to upgrade the app's
  tier — a request to `support@finverse.com`, not a code change. The
  design below works identically once that happens; only the institution
  picked in the Link UI changes.
- Webhook-based real-time sync (revisit once deployed with a public URL).
- Bitcoin/FX/Ledger account types.

## Architecture

Three steps, mirroring how the existing PayPal integration is
structured (`IPayPalClient` / `PayPalClient.cs` as the external-API
wrapper, a service that owns the business logic, a controller that's a
thin HTTP shell):

1. **Link** — frontend asks the backend to start a link session; backend
   calls Finverse's `POST /link/token` and returns the resulting
   `link_url` to the frontend, which opens Finverse's hosted Link UI. The
   user picks an institution and authenticates *with the bank*, not with
   FinTrack Prime — no card numbers, no bank passwords ever touch this
   backend.
2. **Exchange & initial sync** — Finverse redirects back to a frontend
   callback route with a code. The frontend hands that code to the
   backend, which exchanges it for an `access_token` (`POST /auth/token`),
   stores it, then immediately pulls that institution's accounts and
   transactions and writes them into this app's `Account`/`Transaction`
   tables.
3. **Ongoing sync** — a "Refresh" action (and/or dashboard load) re-calls
   Finverse's accounts/transactions endpoints for each linked institution
   and inserts anything not already present, keyed by external id.

## Data model changes

**`Account`** (`FinTrackPrime.Models/Entities/Account.cs`):
- Add `ExternalAccountId` (string) — Finverse's account id, used to match
  on sync instead of creating duplicates.
- Add `Institution` (string) — e.g. `"Testbank"`.
- `AccountType` enum gains `CreditCard` alongside the existing `Checking`,
  `Savings`.
- `Balance` is no longer user-set; it's overwritten on every sync from
  Finverse's reported balance (negative for credit-card-style debt, same
  convention Finverse itself uses).
- The class comment ("A mock bank account for demo purposes...") is now
  wrong and gets updated/removed.

**New `LinkedInstitution`** entity:
- `Id`, `UserId` (FK), `Institution` (string), `AccessToken` (string,
  encrypted at rest — same sensitivity as a password, this is a live
  credential to pull someone's financial data), `LinkedAtUtc`,
  `LastSyncedAtUtc`.
- One row per bank a user has connected. One institution can back
  multiple `Account` rows — Testbank returned 8 accounts total from a
  single login; of those, 3 are in scope (HKD Checking, HKD Statement
  Savings, HKD Credit Card) and the rest (USD FX, Bitcoin, HKD Ledger
  Account, and others not inspected) are filtered out at sync time per
  the account-type scope above.

**`Transaction`** (`FinTrackPrime.Models/Entities/Transaction.cs`):
- Add `ExternalTransactionId` (string) — dedupe key for re-sync.
- `Direction` is derived at sync time from the sign of Finverse's
  transaction amount: positive → `Income`, negative → `Expense`. This
  matches both the checking data (`+523.00 HKD` incoming transfer,
  `-40.00 HKD` Starbucks) and the credit card data (`+1,839.99 HKD`
  payment reducing the balance owed, `-233.00 HKD` a charge) we pulled
  from the sandbox.
- `Category` stays free text and is **not** populated from Finverse —
  it has no category field, only `Description` + `Amount` + posted date.
  Existing Budget Planner / Cash Flow category grouping is unaffected
  since it already treats this as free text; synced transactions will
  simply have an empty/default category until the user edits one (out of
  scope here whether that becomes a manual edit affordance or a future
  auto-categorization pass).

**Removed:**
- `CreateAccountRequest` / `CreateTransactionRequest` (`AccountViewModels.cs`)
  and both endpoints on `AccountsController.cs`.
- Frontend `CreateAccountModal.tsx` and `AddTransactionModal.tsx`.

## New backend surface

- `IFinverseClient` / `FinverseClient.cs` (`FinTrackPrime.Business`,
  same pattern as `IPayPalClient`/`PayPalClient.cs`): wraps
  `POST /link/token`, `POST /auth/token`, and the accounts/transactions
  read endpoints. Registered as a typed `HttpClient` in `Program.cs`,
  `BaseAddress` from `Finverse:ApiBaseUrl` config — same pattern as
  `PayPal:ApiBaseUrl` today.
- `BankLinkController` (`api/bank-link`), `[Authorize]`:
  - `POST /token` — starts a link session for the calling user.
  - `POST /complete` — exchanges the redirect code, performs initial sync.
  - `POST /sync` — re-syncs all of the calling user's linked institutions.
- Config (`appsettings.json` / user-secrets — **not committed in plaintext
  the way `PayPal:ClientSecret` currently is**; that's an existing gap in
  this repo, not something to repeat): `Finverse:ClientId`,
  `Finverse:ClientSecret`, `Finverse:ApiBaseUrl`, `Finverse:RedirectUri`.

## Frontend changes

- Replace "Create Account" entry point with "Connect a bank."
- New route (e.g. `/bank-link/callback`) to receive Finverse's redirect
  and call `POST /api/bank-link/complete`. This is the real callback URL
  to register in Finverse's API Settings alongside the placeholder sink
  URL used for testing so far.
- Dashboard, Cash Flow, and Budget Planner pages are unchanged — they
  already read `AccountViewModel`/`TransactionViewModel`, which barely
  change shape.

## Error handling

- Link session fails/is abandoned by the user: frontend shows a retry
  affordance; nothing is written to the DB (no `LinkedInstitution` row
  until exchange succeeds).
- Finverse API errors during sync (expired/revoked access token, Finverse
  outage): sync fails for that institution only, existing data stays as
  of last successful sync, `LastSyncedAtUtc` doesn't advance, surfaced to
  the frontend as a "couldn't refresh, showing data as of [time]" state
  rather than blocking the whole dashboard.
- An account type outside the supported three (Bitcoin/FX/Ledger) is
  silently skipped at sync time, not stored.

## Testing

- `FinverseClient` behind `IFinverseClient` so sync logic is unit-testable
  against a fake/mocked client, same as how `PremiumAccessService` is
  presumably tested against a fake `IPayPalClient`.
- Manual end-to-end test: link Testbank through the real sandbox, confirm
  Checking/Savings/Credit Card accounts and their transactions land in the
  dashboard with correct signs/directions; confirm a second sync doesn't
  duplicate transactions.
