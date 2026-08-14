using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    // Routes each request to a canned response by URL path instead of a
    // real socket, same technique used to test any typed-HttpClient class
    // without a network call. Path-based (not "last response") because
    // FinverseClient methods like GenerateLinkTokenAsync now make more
    // than one call (a customer_token fetch, then the real request).
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode StatusCode, string Body)> _responsesByPath;
        public Dictionary<string, string?> RequestBodiesByPath { get; } = new();

        public FakeHttpMessageHandler(Dictionary<string, (HttpStatusCode, string)> responsesByPath)
        {
            _responsesByPath = responsesByPath;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestBodiesByPath[path] = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (!_responsesByPath.TryGetValue(path, out var response))
            {
                throw new InvalidOperationException($"FakeHttpMessageHandler has no response configured for {path}");
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body),
            };
        }
    }

    public class FinverseClientTests
    {
        private static IConfiguration BuildConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Finverse:ClientId"] = "test-client-id",
                    ["Finverse:ClientSecret"] = "test-client-secret",
                })
                .Build();

        [Fact]
        public async Task GenerateLinkTokenAsync_SendsExpectedRequestFields()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/auth/customer/token"] = (HttpStatusCode.OK, "{\"access_token\":\"ct-789\"}"),
                ["/link/token"] = (HttpStatusCode.OK,
                    "{\"access_token\":\"lt-123\",\"link_url\":\"https://link.finverse.net/lt-123\"}"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());
            var userId = Guid.NewGuid();

            var result = await client.GenerateLinkTokenAsync(userId, "https://developer.prod.finverse.net/sink");

            Assert.Equal("lt-123", result.LinkToken);
            Assert.Equal("https://link.finverse.net/lt-123", result.LinkUrl);
            var linkTokenRequestBody = handler.RequestBodiesByPath["/link/token"];
            Assert.Contains("\"client_id\":\"test-client-id\"", linkTokenRequestBody);
            Assert.Contains("\"redirect_uri\":\"https://developer.prod.finverse.net/sink\"", linkTokenRequestBody);
            Assert.Contains($"\"user_id\":\"{userId}\"", linkTokenRequestBody);
        }

        [Fact]
        public async Task ExchangeLinkCodeAsync_SendsFormEncodedBodyAndReturnsAccessToken()
        {
            // Confirmed against real Finverse errors: this endpoint wants
            // a form-urlencoded body (not JSON), including client_id,
            // code, and redirect_uri — each was a separate confirmed
            // "X in formData is required" 400 before being added.
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/auth/customer/token"] = (HttpStatusCode.OK, "{\"access_token\":\"ct-789\"}"),
                ["/auth/token"] = (HttpStatusCode.OK, "{\"access_token\":\"at-456\"}"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var token = await client.ExchangeLinkCodeAsync("link-code-abc", "https://developer.prod.finverse.net/sink");

            Assert.Equal("at-456", token);
            var requestBody = handler.RequestBodiesByPath["/auth/token"];
            Assert.Contains("client_id=test-client-id", requestBody);
            Assert.Contains("client_secret=test-client-secret", requestBody);
            Assert.Contains("grant_type=authorization_code", requestBody);
            Assert.Contains("code=link-code-abc", requestBody);
            Assert.Contains("redirect_uri=", requestBody);
        }

        [Fact]
        public async Task GetAccountsAsync_ParsesAccountList()
        {
            // Confirmed against docs.finverse.com's real GET /accounts
            // example response (account_type/balance are nested objects,
            // field names are account_name/account_currency).
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/accounts"] = (HttpStatusCode.OK, """
                {
                  "accounts": [
                    {
                      "account_id": "acc-1",
                      "account_name": "HKD Checking",
                      "account_currency": "HKD",
                      "account_type": { "type": "DEPOSIT", "subtype": "CHECKING" },
                      "balance": { "currency": "HKD", "raw": "70013.12", "value": 70013.12 }
                    },
                    {
                      "account_id": "acc-2",
                      "account_name": "HKD Credit Card",
                      "account_currency": "HKD",
                      "account_type": { "type": "CARD", "subtype": "CREDIT_CARD" },
                      "balance": { "currency": "HKD", "raw": "-1833.22", "value": -1833.22 }
                    }
                  ]
                }
                """),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var accounts = await client.GetAccountsAsync("at-456");

            Assert.Equal(2, accounts.Count);
            Assert.Equal("acc-1", accounts[0].ExternalAccountId);
            Assert.Equal("HKD Checking", accounts[0].AccountName);
            Assert.Equal("CHECKING", accounts[0].AccountType);
            Assert.Equal("HKD", accounts[0].Currency);
            Assert.Equal(70013.12m, accounts[0].Balance);
            Assert.Equal(-1833.22m, accounts[1].Balance);
        }

        [Fact]
        public async Task GetTransactionsAsync_ParsesTransactionList()
        {
            // Confirmed against docs.finverse.com's real GET
            // /transactions example: the real endpoint is
            // /transactions/{account_id}, and "amount" is a nested object
            // (amount.value), not a flat number. Second transaction here
            // has no "description" key at all (not just a null value) —
            // docs explicitly say some bank transactions omit it — to
            // exercise the defensive TryGetProperty path.
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/transactions/acc-1"] = (HttpStatusCode.OK, """
                {
                  "transactions": [
                    {
                      "transaction_id": "txn-1",
                      "description": "BAT STARBUCKS@CITY SI NG 25MAY",
                      "amount": { "currency": "HKD", "raw": "-40.00", "value": -40.00 },
                      "posted_date": "2023-06-30"
                    },
                    {
                      "transaction_id": "txn-2",
                      "amount": { "currency": "HKD", "raw": "100.00", "value": 100.00 },
                      "posted_date": "2023-07-01"
                    }
                  ]
                }
                """),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.prod.finverse.net") };
            var client = new FinverseClient(httpClient, BuildConfig());

            var transactions = await client.GetTransactionsAsync("at-456", "acc-1");

            Assert.Equal(2, transactions.Count);
            Assert.Equal("txn-1", transactions[0].ExternalTransactionId);
            Assert.Equal("BAT STARBUCKS@CITY SI NG 25MAY", transactions[0].Description);
            Assert.Equal(-40.00m, transactions[0].Amount);
            Assert.Equal(new DateTime(2023, 6, 30), transactions[0].PostedAtUtc);
            Assert.Equal("txn-2", transactions[1].ExternalTransactionId);
            Assert.Equal(string.Empty, transactions[1].Description);
            Assert.Equal(100.00m, transactions[1].Amount);
        }
    }
}
