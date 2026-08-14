using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    public class BankLinkService : IBankLinkService
    {
        // Converted-to value for any AccountType.Crypto account's cached
        // fiat equivalent — see MapAccountType and SyncInstitutionAsync.
        private const string TargetFiatCurrency = "USD";

        private readonly FinTrackDbContext _db;
        private readonly IFinverseClient _finverseClient;
        private readonly ICryptoPriceClient _cryptoPriceClient;
        private readonly IDataProtector _protector;
        private readonly IConfiguration _config;

        public BankLinkService(
            FinTrackDbContext db,
            IFinverseClient finverseClient,
            ICryptoPriceClient cryptoPriceClient,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration config)
        {
            _db = db;
            _finverseClient = finverseClient;
            _cryptoPriceClient = cryptoPriceClient;
            // A dedicated purpose string scopes this protector so it can
            // never accidentally decrypt data protected for another
            // purpose elsewhere in the app.
            _protector = dataProtectionProvider.CreateProtector("FinTrackPrime.LinkedInstitution.AccessToken");
            _config = config;
        }

        public async Task<string> StartLinkAsync(Guid userId, string redirectUri)
        {
            var session = await _finverseClient.GenerateLinkTokenAsync(userId, redirectUri);
            return session.LinkUrl;
        }

        public async Task<DashboardViewModel> CompleteLinkAsync(Guid userId, string linkCode)
        {
            // Must be the exact same value StartLinkAsync passed to
            // GenerateLinkTokenAsync for this session — Finverse validates
            // the two match.
            var redirectUri = _config["Finverse:RedirectUri"] ?? string.Empty;
            var accessToken = await _finverseClient.ExchangeLinkCodeAsync(linkCode, redirectUri);

            const string institutionName = "Testbank"; // VERIFY: Finverse's exchange
                                                        // response may include the
                                                        // institution name directly;
                                                        // if so, use that instead of
                                                        // hardcoding.

            var institution = await _db.LinkedInstitutions
                .FirstOrDefaultAsync(i => i.UserId == userId && i.Institution == institutionName);

            if (institution is null)
            {
                institution = new LinkedInstitution
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Institution = institutionName,
                    LinkedAtUtc = DateTime.UtcNow,
                };
                _db.LinkedInstitutions.Add(institution);
            }

            institution.AccessToken = _protector.Protect(accessToken);
            await _db.SaveChangesAsync();

            await SyncInstitutionAsync(userId, institution, accessToken);

            return await BuildDashboardAsync(userId);
        }

        public async Task<DashboardViewModel> SyncAsync(Guid userId)
        {
            var institutions = await _db.LinkedInstitutions
                .Where(i => i.UserId == userId)
                .ToListAsync();

            foreach (var institution in institutions)
            {
                try
                {
                    var accessToken = _protector.Unprotect(institution.AccessToken);
                    await SyncInstitutionAsync(userId, institution, accessToken);
                }
                catch (Exception)
                {
                    // One institution's Finverse call failing (expired
                    // token, outage) must not block syncing the others,
                    // or block the dashboard from loading at all — that
                    // institution's data just stays as of its last
                    // successful sync.

                    // Discard any partial, unsaved changes this institution's sync made
                    // before failing, so BuildDashboardAsync (which reads through the
                    // same tracked context) can't return phantom unsaved state, and so a
                    // LATER institution's SaveChangesAsync in this same loop can't
                    // accidentally flush this institution's partial work.
                    _db.ChangeTracker.Clear();
                }
            }

            return await BuildDashboardAsync(userId);
        }

        public async Task DisconnectAllAsync(Guid userId)
        {
            // Transactions are removed explicitly rather than relying on
            // the FK's cascade delete: that cascade is a real constraint
            // against SQL Server, but the EF Core InMemory provider used
            // in tests doesn't enforce database-level FK constraints —
            // only entities it has actually loaded get cascaded. Loading
            // and removing them here keeps this correct against either.
            var accounts = await _db.Accounts
                .Where(a => a.UserId == userId)
                .Include(a => a.Transactions)
                .ToListAsync();

            foreach (var account in accounts)
            {
                _db.Transactions.RemoveRange(account.Transactions);
            }
            _db.Accounts.RemoveRange(accounts);

            var institutions = await _db.LinkedInstitutions
                .Where(i => i.UserId == userId)
                .ToListAsync();
            _db.LinkedInstitutions.RemoveRange(institutions);

            await _db.SaveChangesAsync();
        }

        private async Task SyncInstitutionAsync(Guid userId, LinkedInstitution institution, string accessToken)
        {
            var finverseAccounts = await _finverseClient.GetAccountsAsync(accessToken);

            foreach (var finverseAccount in finverseAccounts)
            {
                var accountType = MapAccountType(finverseAccount.AccountType, finverseAccount.Currency);

                var account = await _db.Accounts.FirstOrDefaultAsync(
                    a => a.UserId == userId && a.ExternalAccountId == finverseAccount.ExternalAccountId);

                if (account is null)
                {
                    account = new Account
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        ExternalAccountId = finverseAccount.ExternalAccountId,
                        Institution = institution.Institution,
                        CreatedAtUtc = DateTime.UtcNow,
                    };
                    _db.Accounts.Add(account);
                }

                account.Nickname = finverseAccount.AccountName;
                account.Type = accountType;
                account.Currency = finverseAccount.Currency;
                account.Balance = finverseAccount.Balance;

                if (accountType == AccountType.Crypto)
                {
                    try
                    {
                        account.FiatEquivalentValue = await _cryptoPriceClient.GetFiatEquivalentAsync(
                            finverseAccount.Currency, finverseAccount.Balance, TargetFiatCurrency);
                        account.FiatEquivalentCurrency = TargetFiatCurrency;
                        account.PriceFetchedAtUtc = DateTime.UtcNow;
                    }
                    catch (Exception)
                    {
                        // Leave the previous cached value as-is — a stale
                        // price beats no price, and one account's
                        // price-feed hiccup must not block the rest of
                        // this institution's sync.
                    }
                }

                if (accountType == AccountType.Unsupported || accountType == AccountType.Crypto)
                {
                    continue; // balance/nickname (and, for Crypto, the fiat-equivalent) synced above; transactions are not.
                }

                var finverseTransactions = await _finverseClient.GetTransactionsAsync(
                    accessToken, finverseAccount.ExternalAccountId);

                foreach (var finverseTransaction in finverseTransactions)
                {
                    var alreadySynced = await _db.Transactions.AnyAsync(
                        t => t.AccountId == account.Id && t.ExternalTransactionId == finverseTransaction.ExternalTransactionId);
                    if (alreadySynced)
                    {
                        continue;
                    }

                    _db.Transactions.Add(new Transaction
                    {
                        Id = Guid.NewGuid(),
                        AccountId = account.Id,
                        ExternalTransactionId = finverseTransaction.ExternalTransactionId,
                        Description = finverseTransaction.Description,
                        Category = string.Empty, // Finverse has no category field.
                        Amount = Math.Abs(finverseTransaction.Amount),
                        Direction = ClassifyDirection(accountType, finverseTransaction),
                        OccurredAtUtc = finverseTransaction.PostedAtUtc,
                    });
                }
            }

            institution.LastSyncedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // Two independent transfer signals, checked before falling back to
        // the plain sign-of-amount rule Finverse's own docs describe
        // ("positive: money received; negative: money sent"). Confirmed
        // against a real Testbank sync where both cases actually occurred:
        //  - A credit card payment ("Payment. Thank you", amount > 0 on a
        //    CreditCard account) has no transaction_details at all, so it
        //    can only be caught by account type + sign.
        //  - An FPS transfer between people (amount > 0 on Checking) DID
        //    carry transaction_details with a "Transfer" hint, so that's
        //    checked too, for institutions/rails that do report it.
        // Neither signal is available for every transfer — e.g. an
        // internal bank transfer with no transaction_details and not on a
        // CreditCard account still falls through to Income/Expense. Known
        // gap, not solved here.
        private static TransactionDirection ClassifyDirection(
            AccountType accountType, FinverseTransactionDto transaction)
        {
            if (accountType == AccountType.CreditCard && transaction.Amount >= 0)
            {
                return TransactionDirection.Transfer;
            }

            if (transaction.TransferHint?.Contains("transfer", StringComparison.OrdinalIgnoreCase) == true)
            {
                return TransactionDirection.Transfer;
            }

            return transaction.Amount >= 0
                ? TransactionDirection.Income
                : TransactionDirection.Expense;
        }

        // The only crypto currency Testbank has actually surfaced so
        // far — extend alongside CryptoPriceClient.CoinGeckoIds if a new
        // one is ever seen.
        private static readonly HashSet<string> KnownCryptoCurrencies = new(StringComparer.OrdinalIgnoreCase) { "BTC" };

        // Detects crypto by currency code, not by guessing more Finverse
        // subtype strings — sidesteps the same uncertainty the old VERIFY
        // comment here used to flag. Anything with a recognized subtype
        // maps directly; anything else with a real (non-crypto) currency
        // becomes Other — a real balance in a real currency, no reason to
        // exclude it just because its subtype string is unrecognized.
        // Unsupported now only means "no usable currency at all."
        private static AccountType MapAccountType(string finverseAccountType, string currency)
        {
            switch (finverseAccountType.ToLowerInvariant())
            {
                case "checking":
                case "current":
                    return AccountType.Checking;
                case "savings":
                    return AccountType.Savings;
                case "credit_card":
                case "credit":
                    return AccountType.CreditCard;
            }

            if (string.IsNullOrWhiteSpace(currency))
            {
                return AccountType.Unsupported;
            }

            return KnownCryptoCurrencies.Contains(currency) ? AccountType.Crypto : AccountType.Other;
        }

        private async Task<DashboardViewModel> BuildDashboardAsync(Guid userId)
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
