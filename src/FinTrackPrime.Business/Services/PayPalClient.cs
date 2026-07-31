using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FinTrackPrime.Business.Services
{
    // Talks to PayPal's REST API directly. Registered with a typed
    // HttpClient (see Program.cs), so BaseAddress and lifetime are
    // handled by the DI container rather than this class.
    public class PayPalClient : IPayPalClient
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;

        public PayPalClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<PayPalOrderDetails> GetOrderAsync(string paypalOrderId)
        {
            var accessToken = await GetAccessTokenAsync();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/checkout/orders/{paypalOrderId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"PayPal order lookup failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.GetProperty("status").GetString() ?? string.Empty;

            // A completed order always has at least one purchase unit;
            // this only reads the first, since checkout here is always a
            // single line item (the premium bundle).
            var amountElement = root
                .GetProperty("purchase_units")[0]
                .GetProperty("amount");

            var amountValue = decimal.Parse(amountElement.GetProperty("value").GetString()!);
            var currencyCode = amountElement.GetProperty("currency_code").GetString() ?? string.Empty;

            return new PayPalOrderDetails(status, amountValue, currencyCode);
        }

        private async Task<string> GetAccessTokenAsync()
        {
            var clientId = _config["PayPal:ClientId"];
            var clientSecret = _config["PayPal:ClientSecret"];
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "client_credentials"),
            });

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"PayPal authentication failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString()!;
        }
    }
}
