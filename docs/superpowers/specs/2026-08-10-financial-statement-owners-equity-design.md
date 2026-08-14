# Financial Statement: typed Assets/Liabilities + Owner's Equity column

**Date:** 2026-08-10
**Status:** Draft — pending review
**Repos affected:** `FinTrack-Prime-Api` (backend), `FinTrackPrime` (frontend, `C:\Users\Wyrlo\projects\FinTrackPrime`)

## Background

FinTrack Prime is being positioned as a white-label financial-tracking system a bank offers to its clients (retail and self-employed/small-business account holders). The existing "Financial Statement" premium tool is a simple two-column personal balance sheet: **Assets** (auto-derived from linked bank accounts + investment holdings) minus **Liabilities** (manually entered, untyped) equals a single **Net Worth** figure.

The bank wants the classic three-column presentation instead — **Assets / Liabilities / Owner's Equity** — with each side broken into standard accounting categories, matching the layout of a conventional chart of accounts.

### Why "Owner's Equity" instead of building out a real chart of accounts

Research into the accounting concepts behind the requested layout ([Corporate Finance Institute](https://corporatefinanceinstitute.com/resources/accounting/types-of-assets/), [AccountingTools](https://www.accountingtools.com/articles/types-of-assets.html), [Mars DD](https://learn.marsdd.com/article/liabilities-current-and-long-term/), [Bench Accounting](https://www.bench.co/blog/accounting/liabilities-in-accounting), [eFinanceManagement](https://efinancemanagement.com/financial-accounting/owners-equity), [Missouri LSF](https://lsfellowship.missouri.edu/article/owners-equity-vs-net-worth-key-differences-explained)) surfaced a key fact: **Owner's Equity is a business-accounting term; for an individual the identical formula (`Assets − Liabilities`) is called Net Worth.** The reference chart's equity column (Common Stock, Treasury Stock, Retained Earnings, Additional Paid-in Capital, Owner's Drawings) is a corporate/double-entry chart of accounts — none of those sub-accounts have a meaningful equivalent for a retail bank client, and building them out would mean adding real double-entry bookkeeping (a ledger, debit/credit postings) to an app that doesn't have one today.

**Decision:** Owner's Equity is implemented as a single derived line, `Assets − Liabilities`, using the exact arithmetic the app already does for Net Worth today — just relabeled and repositioned as the third column to match the requested layout. No stock/capital/retained-earnings sub-accounts. This keeps the statement accurate for every client type (individual or self-employed) without fabricating accounting concepts that don't apply to them.

### Why Assets gets typed *and* becomes user-editable

Today, Assets is 100% auto-derived (bank accounts + investment holdings) — there is no way for a user to add something that isn't linked, like a house or a car. Liabilities already supports manual add/remove. Typing the categories only has real value if a user can actually populate `RealEstate` / `Vehicle` / `Other` — so this spec adds a manual-asset capability mirroring the existing manual-liability one.

## Goals

- Categorize every asset and liability line with a `Type`.
- Let a user manually add/remove non-synced assets (real estate, vehicles, other personal property), same UX pattern as the existing liability add/remove.
- Let a user pick a `Type` when manually adding a liability (today it's untyped).
- Group both tables by `Type` with subtotals in the UI.
- Add an "Owner's Equity" column/figure to the statement, replacing "Net Worth" terminology.

## Non-goals

- No double-entry bookkeeping (no debit/credit ledger, no posting mechanics).
- No corporate equity sub-accounts (Common Stock, Treasury Stock, Retained Earnings, Additional Paid-in Capital, Owner's Drawings) — not meaningful for a retail/self-employed client and not requested once the Owner's Equity = Net Worth equivalence was confirmed.
- No change to how `Unsupported` Finverse accounts (crypto/FX wallets) are handled — they stay excluded from Assets, same as today. Folding them into an `Other` asset type would require a currency-conversion step this app doesn't have; that's a separate problem from this feature.
- No change to investment-holding or account-sync logic beyond tagging their existing lines with a `Type`.

## Data model (backend)

### New enums

```csharp
// FinTrackPrime.Models.Entities
public enum AssetType { Cash, Investment, RealEstate, Vehicle, Other }
public enum LiabilityType { CreditCard, Mortgage, AutoLoan, StudentLoan, PersonalLoan, Other }
```

`Cash`, `Investment`, and `CreditCard` are system-assigned — they only ever come from synced accounts/holdings and are never offered as a choice when a user manually adds an asset or liability. The remaining values are user-picked.

### New `Asset` entity (mirrors `Liability`)

```csharp
// FinTrackPrime.Models.Entities.Asset
public class Asset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string Name { get; set; } = string.Empty;
    public AssetType Type { get; set; }   // API-validated to RealEstate | Vehicle | Other
    public decimal Amount { get; set; }
}
```

New `DbSet<Asset> Assets` on `FinTrackDbContext`, new `Assets` table, `OnModelCreating` config matching the existing `Liability` block (max length on `Name`, `decimal(18,2)` on `Amount`, cascade-delete FK to `User`).

### `Liability` entity gains `Type`

```csharp
public LiabilityType Type { get; set; }   // API-validated to Mortgage | AutoLoan | StudentLoan | PersonalLoan | Other
```

### Migration

This project is still pre-launch (per the premium-unlock work done earlier this session, the team has been comfortable regenerating/hand-editing migrations rather than layering new ones on uncommitted schema). The implementation plan should confirm at plan time whether `Liabilities`/new `Assets` table changes land as a new migration or get folded into whatever's still uncommitted — not a design-level decision.

## API contract

### ViewModels

Flat lists stay flat — grouping/subtotals happen client-side from the `Type` field. No new nested "grouped" API shape; avoids maintaining the same subtotal logic in two places.

```csharp
public class AssetLineViewModel
{
    public Guid? Id { get; set; }     // null for synced lines (not removable); set for manual assets (removable)
    public string Label { get; set; } = string.Empty;
    public AssetType Type { get; set; }
    public decimal Amount { get; set; }
}

public class CreateAssetRequest
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public AssetType Type { get; set; }   // service rejects Cash/Investment — those are sync-only

    [Range(0, double.MaxValue)]
    public decimal Amount { get; set; }
}

public class LiabilityViewModel
{
    public Guid Id { get; set; }         // Account.Id for synced credit cards, Liability.Id for manual ones (unchanged from today)
    public string Name { get; set; } = string.Empty;
    public LiabilityType Type { get; set; }
    public decimal Amount { get; set; }
}

public class CreateLiabilityRequest
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public LiabilityType Type { get; set; }   // service rejects CreditCard — that's sync-only

    [Range(0, double.MaxValue)]
    public decimal Amount { get; set; }
}

// Renamed from NetWorth. Same arithmetic (TotalAssets - TotalLiabilities),
// relabeled to match the bank's 3-column Assets/Liabilities/Owner's-Equity
// presentation — for an individual or self-employed client this number
// *is* their net worth, "Owner's Equity" is just the accounting term for it.
public class FinancialStatementViewModel
{
    public List<AssetLineViewModel> Assets { get; set; } = new();
    public decimal TotalAssets { get; set; }
    public List<LiabilityViewModel> Liabilities { get; set; } = new();
    public decimal TotalLiabilities { get; set; }
    public decimal OwnersEquity { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
}
```

### Endpoints (`FinancialStatementController`, unchanged base route `api/financial-statement`, still behind `RequirePremium`)

| Method | Route | Body | Returns |
|---|---|---|---|
| GET | `/` | — | `FinancialStatementViewModel` |
| POST | `/assets` *(new)* | `CreateAssetRequest` | `AssetLineViewModel` |
| DELETE | `/assets/{assetId}` *(new)* | — | 204 / 404 |
| POST | `/liabilities` | `CreateLiabilityRequest` (now with `Type`) | `LiabilityViewModel` |
| DELETE | `/liabilities/{liabilityId}` | — | 204 / 404 (unchanged) |

### Service layer (`IFinancialStatementService`)

```csharp
Task<FinancialStatementViewModel> GetStatementAsync(Guid userId);
Task<AssetLineViewModel> AddAssetAsync(Guid userId, CreateAssetRequest request);      // new
Task RemoveAssetAsync(Guid userId, Guid assetId);                                     // new
Task<LiabilityViewModel> AddLiabilityAsync(Guid userId, CreateLiabilityRequest request);
Task RemoveLiabilityAsync(Guid userId, Guid liabilityId);
```

`GetStatementAsync` changes:
- Pull `Accounts` (non-CreditCard, non-Unsupported) → `AssetLineViewModel { Id: null, Type: Cash }`.
- Pull `InvestmentHoldings` → `AssetLineViewModel { Id: null, Type: Investment }`.
- Pull manual `Assets` rows → `AssetLineViewModel { Id: asset.Id, Type: asset.Type }`.
- Pull `CreditCard` accounts → `LiabilityViewModel { Id: account.Id, Type: CreditCard }` (unchanged from today, just tagged).
- Pull `Liabilities` rows → `LiabilityViewModel { Id: liability.Id, Type: liability.Type }`.
- `TotalAssets` / `TotalLiabilities` = sum as today.
- `OwnersEquity = TotalAssets - TotalLiabilities`.

`AddAssetAsync` / `AddLiabilityAsync` reject `Type` values reserved for sync-only lines (`Cash`/`Investment` for assets, `CreditCard` for liabilities) with a 400, same style as other validation failures in this codebase (`InvalidOperationException` → `BadRequest`).

## Frontend changes (`FinTrackPrime`)

### Types (`types/api.ts`)

- New `AssetType` / `LiabilityType` string unions, mirroring the backend enums (serialized as strings, same `JsonStringEnumConverter` pattern already in use).
- `AssetLineViewModel`: add `id?: string`, `type: AssetType`.
- `LiabilityViewModel`: add `type: LiabilityType`.
- `CreateAssetRequest` (new): `{ name, type, amount }`.
- `CreateLiabilityRequest`: add `type`.
- `FinancialStatementViewModel`: `netWorth` → `ownersEquity`.

### `api/financialStatement.ts`

Add `addAsset` / `removeAsset`, mirroring `addLiability` / `removeLiability`.

### `pages/FinancialStatementPage.tsx`

- Restructure to three columns: **Assets** / **Liabilities** / **Owner's Equity**, matching the reference layout.
- Assets and Liabilities tables group rows by `type` with a subtotal per group (computed client-side, e.g. via `reduce` — no new backend shape), rolling up to the same `totalAssets`/`totalLiabilities`.
- Owner's Equity renders as a single card/stat, `formatCurrency(data.ownersEquity)` — replaces today's "Net worth" `StatCard`.
- New "Add an asset" form, identical pattern to the existing "Add a liability" form: Name + Amount inputs, plus a `Type` `<select>` restricted to `RealEstate | Vehicle | Other`.
- "Add a liability" form gains a `Type` `<select>` restricted to `Mortgage | AutoLoan | StudentLoan | PersonalLoan | Other`.
- Manual rows (`id` present) get the existing ✕ remove button; synced rows (`id: null`) don't — same rule already in place for liabilities, now applied to assets too.
- The Assets-vs-Liabilities bar chart extends to three bars: Assets / Liabilities / Owner's Equity.

## Validation summary

| Rule | Enforced where |
|---|---|
| Manual asset `Type` must be `RealEstate`, `Vehicle`, or `Other` | Backend service (400 on violation); frontend `<select>` only offers these three |
| Manual liability `Type` must not be `CreditCard` | Backend service (400 on violation); frontend `<select>` excludes it |
| `Amount` ≥ 0 | Existing `[Range]` validation, unchanged |
| `Name` required, ≤ 120 chars | Existing `[Required, MaxLength]`, unchanged, now shared by both entities |

## Open questions for review

1. **Field naming**: is `OwnersEquity` (matching the bank's terminology) the right call for the API/UI, or should it stay `NetWorth` internally and only be *labeled* "Owner's Equity" in the UI? This spec assumes a full rename since the bank's ask was specifically for that terminology.
2. **Investment accounts vs. cash accounts**: today `AccountType` is `Checking | Savings | CreditCard | Unsupported` — both Checking and Savings map to `AssetType.Cash`. If the bank wants a finer split later (e.g. distinguishing Savings from Checking), that's a follow-on, not part of this spec.
3. Confirm the three manual-asset types (`RealEstate`, `Vehicle`, `Other`) and five manual-liability types (`Mortgage`, `AutoLoan`, `StudentLoan`, `PersonalLoan`, `Other`) are the right starter set for a retail/self-employed bank client — easy to extend the enum later if the bank wants more granularity (e.g. splitting `PersonalLoan` from a HELOC).
