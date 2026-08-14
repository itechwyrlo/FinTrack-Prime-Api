using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    // A short-lived link_token + the browser URL to open it, returned by
    // Finverse's POST /link/token.
    public record FinverseLinkSession(string LinkToken, string LinkUrl);

    // AccountType here is Finverse's raw string (e.g. "checking",
    // "credit_card") — mapping to this app's own AccountType enum, and
    // filtering out unsupported types, happens in BankLinkService, not
    // here. This DTO is a direct mirror of what Finverse returns.
    public record FinverseAccountDto(
        string ExternalAccountId,
        string AccountName,
        string AccountType,
        string Currency,
        decimal Balance);

    // TransferHint carries whatever transfer-classification signal was
    // present in the transaction_details object, or null if Finverse gave
    // us nothing (per Finverse's own docs: "(BETA) ... most institutions
    // will not provide any values"). Checked against both the documented
    // field names (transaction_type/transfer_type) and Testbank's actual
    // field names (type/subtype), which don't match the documented ones —
    // confirmed against a real GET /transactions response, not assumed.
    public record FinverseTransactionDto(
        string ExternalTransactionId,
        string Description,
        decimal Amount,
        DateTime PostedAtUtc,
        string? TransferHint = null);

    public interface IFinverseClient
    {
        // Starts a Link session for one end user. redirectUri must be one
        // of the Callback URLs registered for this app in Finverse's API
        // Settings.
        Task<FinverseLinkSession> GenerateLinkTokenAsync(Guid userId, string redirectUri);

        // Exchanges the code Finverse's redirect handed back to the
        // frontend for a long-lived access token scoped to that one
        // linked institution. redirectUri must be the exact same value
        // passed to GenerateLinkTokenAsync for this session — Finverse
        // validates the two match (standard OAuth authorization-code
        // protection against interception).
        Task<string> ExchangeLinkCodeAsync(string linkCode, string redirectUri);

        Task<IReadOnlyList<FinverseAccountDto>> GetAccountsAsync(string accessToken);

        Task<IReadOnlyList<FinverseTransactionDto>> GetTransactionsAsync(
            string accessToken, string externalAccountId);
    }
}
