using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    // Talks to Finverse's REST API directly. Registered with a typed
    // HttpClient (see Program.cs) whose BaseAddress is
    // Finverse:ApiBaseUrl, same pattern as PayPalClient.
    public class FinverseClient : IFinverseClient
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public FinverseClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<FinverseLinkSession> GenerateLinkTokenAsync(Guid userId, string redirectUri)
        {
            // /link/token requires a customer_token in the Authorization
            // header, on top of the body fields below — the two are
            // separate concerns: the header authenticates this call as
            // coming from your app, the body's client_id says which
            // Customer App the link session belongs to.
            var customerToken = await GetCustomerTokenAsync();

            // Field names/values confirmed against docs.finverse.com's
            // full /link/token reference (request parameters table, not
            // just the example curl snippet):
            // - response_mode is documented as always "form_post" — no
            //   alternative is accepted. An earlier attempt to use
            //   "query" instead (based on generic OAuth conventions, not
            //   Finverse's actual spec) was wrong and is reverted here.
            // - ui_mode defaults to "" (iframe behavior) when omitted,
            //   which silently discards the redirect_uri callback and
            //   expects the embedding page to catch a postMessage — not
            //   what an app doing a real full-page redirect (this one)
            //   wants. "auto_redirect" is the documented mode for exactly
            //   that case: after success/error, the UI automatically
            //   navigates to redirect_uri after ~1 second, no button
            //   click or iframe/postMessage handling required.
            var payload = new
            {
                client_id = _config["Finverse:ClientId"],
                redirect_uri = redirectUri,
                state = Guid.NewGuid().ToString("N"),
                user_id = userId.ToString(),
                grant_type = "client_credentials",
                response_mode = "form_post",
                response_type = "code",
                ui_mode = "auto_redirect",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/link/token")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse link/token request failed ({(int)response.StatusCode}): {body}");
            }

            // Confirmed against a real response: the field is actually
            // named "access_token" — "link_token" is just the docs'
            // prose nickname for it, same pattern as the customer_token
            // endpoint. link_url's name was already correct.
            return ParseResponse(body, root => new FinverseLinkSession(
                root.GetProperty("access_token").GetString() ?? string.Empty,
                root.GetProperty("link_url").GetString() ?? string.Empty));
        }

        public async Task<string> ExchangeLinkCodeAsync(string linkCode, string redirectUri)
        {
            // Sits in the same docs folder as /link/token, which requires
            // a customer_token — sending the same header here too.
            var customerToken = await GetCustomerTokenAsync();

            // Confirmed by real errors from Finverse (HTTP 400
            // INVALID_PARAMETER, first for a missing client_id then a
            // missing redirect_uri): unlike /link/token and
            // /auth/customer/token (both plain JSON), this endpoint wants
            // form-urlencoded body fields, not JSON, and redirect_uri must
            // be included and must match the value GenerateLinkTokenAsync
            // used to start this same session.
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", _config["Finverse:ClientId"] ?? string.Empty),
                    new KeyValuePair<string, string>("client_secret", _config["Finverse:ClientSecret"] ?? string.Empty),
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("code", linkCode),
                    new KeyValuePair<string, string>("redirect_uri", redirectUri),
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse auth/token exchange failed ({(int)response.StatusCode}): {body}");
            }

            return ParseResponse(body, root => root.GetProperty("access_token").GetString() ?? string.Empty);
        }

        // Authenticates this app (not any one end user) to Finverse.
        // Confirmed against docs.finverse.com's "Auth (Customer App)"
        // page: plain JSON body (not Basic auth/form-urlencoded), and the
        // response's token field is actually named "access_token" — the
        // docs just refer to that value by the nickname "customer_token"
        // in prose, it isn't the real JSON key.
        private async Task<string> GetCustomerTokenAsync()
        {
            var payload = new
            {
                client_id = _config["Finverse:ClientId"],
                client_secret = _config["Finverse:ClientSecret"],
                grant_type = "client_credentials",
            };

            using var response = await _httpClient.PostAsJsonAsync("/auth/customer/token", payload);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Finverse customer token request failed ({(int)response.StatusCode}): {body}");
            }

            return ParseResponse(body, root => root.GetProperty("access_token").GetString() ?? string.Empty);
        }

        public async Task<IReadOnlyList<FinverseAccountDto>> GetAccountsAsync(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/accounts");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse accounts request failed ({(int)response.StatusCode}): {body}");
            }

            return ParseResponse(body, root =>
            {
                var accounts = new List<FinverseAccountDto>();

                // Confirmed against docs.finverse.com's real GET /accounts
                // example: account_type and balance are nested objects,
                // not flat fields — account_type.subtype (CHECKING,
                // SAVINGS, CREDIT_CARD, ...) is what BankLinkService's
                // MapAccountType actually switches on, and balance.value
                // carries the signed amount (positive = asset, negative
                // = liability, exactly as we'd assumed).
                foreach (var element in root.GetProperty("accounts").EnumerateArray())
                {
                    accounts.Add(new FinverseAccountDto(
                        element.GetProperty("account_id").GetString() ?? string.Empty,
                        element.GetProperty("account_name").GetString() ?? string.Empty,
                        element.GetProperty("account_type").GetProperty("subtype").GetString() ?? string.Empty,
                        element.GetProperty("account_currency").GetString() ?? string.Empty,
                        element.GetProperty("balance").GetProperty("value").GetDecimal()));
                }

                return (IReadOnlyList<FinverseAccountDto>)accounts;
            });
        }

        public async Task<IReadOnlyList<FinverseTransactionDto>> GetTransactionsAsync(
            string accessToken, string externalAccountId)
        {
            // Confirmed against docs.finverse.com: the real per-account
            // endpoint is GET /transactions/{account_id}, not
            // /accounts/{id}/transactions. Pagination (offset/limit,
            // default 500/max 1000) exists but isn't handled here — out
            // of scope until an account is seen with more transactions
            // than one page returns.
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/transactions/{externalAccountId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Finverse transactions request failed ({(int)response.StatusCode}): {body}");
            }

            return ParseResponse(body, root =>
            {
                var transactions = new List<FinverseTransactionDto>();

                foreach (var element in root.GetProperty("transactions").EnumerateArray())
                {
                    // "description" is documented as optional — some bank
                    // transactions omit it entirely, not just leave it
                    // null — so this reads defensively instead of via
                    // GetProperty, which would throw if the key is absent.
                    var description = element.TryGetProperty("description", out var descriptionProp)
                        ? descriptionProp.GetString() ?? string.Empty
                        : string.Empty;

                    transactions.Add(new FinverseTransactionDto(
                        element.GetProperty("transaction_id").GetString() ?? string.Empty,
                        description,
                        // "amount" is a nested object here too, same
                        // convention as /accounts' "balance".
                        element.GetProperty("amount").GetProperty("value").GetDecimal(),
                        DateTime.Parse(
                            element.GetProperty("posted_date").GetString() ?? string.Empty,
                            CultureInfo.InvariantCulture),
                        ExtractTransferHint(element)));
                }

                return (IReadOnlyList<FinverseTransactionDto>)transactions;
            });
        }

        // transaction_details is null for most transactions (per Finverse's
        // docs, most institutions don't provide it at all) and is nullable
        // per-field even when present. The documented shape uses
        // transaction_type/transfer_type; Testbank's actual sandbox
        // responses instead use type/subtype — confirmed by inspecting a
        // real GET /transactions body, not the docs prose. Check both so a
        // real institution matching the documented shape still works.
        private static string? ExtractTransferHint(JsonElement transactionElement)
        {
            if (!transactionElement.TryGetProperty("transaction_details", out var details)
                || details.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var fieldName in new[] { "type", "transaction_type", "subtype", "transfer_type" })
            {
                if (details.TryGetProperty(fieldName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }

        // Every field-name guess we've made against Finverse's real API
        // has needed at least one correction once actually exercised
        // (customer_token vs access_token, link_token/link_url still
        // unconfirmed at the time this was written). A 200 OK response
        // whose shape doesn't match what we expect should surface the
        // raw body immediately instead of a bare KeyNotFoundException —
        // that's the difference between a one-round-trip fix and another
        // guessing loop.
        private static T ParseResponse<T>(string body, Func<JsonElement, T> parse)
        {
            using var doc = JsonDocument.Parse(body);

            try
            {
                return parse(doc.RootElement);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or JsonException)
            {
                throw new InvalidOperationException(
                    $"Finverse response didn't have the expected shape ({ex.GetType().Name}: {ex.Message}). Raw body: {body}", ex);
            }
        }
    }
}
