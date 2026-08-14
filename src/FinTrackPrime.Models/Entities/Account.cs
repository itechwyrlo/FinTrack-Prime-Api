using System;
using System.Collections.Generic;

namespace FinTrackPrime.Models.Entities
{
    public enum AccountType
    {
        Checking,
        Savings,
        CreditCard,

        // A real fiat balance whose Finverse account_type.subtype isn't
        // one of the three above (e.g. a generic ledger account) — still
        // a real currency, safe to include everywhere via per-currency
        // bucketing (see FinancialStatementService/CashFlowService).
        Other,

        // A non-fiat balance (BTC, ...). FiatEquivalentValue/Currency
        // below carry the last successful conversion, refreshed each
        // sync — see BankLinkService.SyncInstitutionAsync.
        Crypto,

        // No usable currency at all. Narrower than it used to be: this
        // used to be the catch-all for anything unrecognized; now only
        // a defensive edge case (see BankLinkService.MapAccountType).
        Unsupported,
    }

    // A bank account linked via Finverse (or, historically, entered
    // manually before that path was removed). ExternalAccountId +
    // Institution identify which Finverse-linked account this row mirrors;
    // both are empty for any row created before linking existed.
    public class Account
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string Nickname { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public decimal Balance { get; set; }

        // Multi-currency accounts (Finverse's Testbank returns HKD, USD,
        // etc.) are stored as-is; FinancialStatementService/CashFlowService
        // bucket by this rather than converting between currencies.
        public string Currency { get; set; } = string.Empty;

        // Only populated for AccountType.Crypto — the last successful
        // conversion of Balance (in Currency) to a fiat value. A failed
        // price-feed call during sync leaves these as whatever they were
        // last time, rather than clearing them.
        public decimal? FiatEquivalentValue { get; set; }
        public string? FiatEquivalentCurrency { get; set; }
        public DateTime? PriceFetchedAtUtc { get; set; }

        public string ExternalAccountId { get; set; } = string.Empty;
        public string Institution { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }

        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
}
