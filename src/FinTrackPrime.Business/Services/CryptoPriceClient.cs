using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;

namespace FinTrackPrime.Business.Services
{
    // Talks to CoinGecko's free /simple/price endpoint directly (no API
    // key). Registered with a typed HttpClient (see Program.cs) whose
    // BaseAddress must end with a trailing slash — the /api/v3 path
    // segment would otherwise be silently dropped when combined with a
    // relative request path (a classic .NET HttpClient gotcha).
    public class CryptoPriceClient : ICryptoPriceClient
    {
        // The only crypto currency Testbank has actually surfaced so
        // far — extend alongside BankLinkService.KnownCryptoCurrencies
        // if a new one is ever seen. CoinGecko's API expects full coin
        // ids, not ticker symbols.
        private static readonly Dictionary<string, string> CoinGeckoIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BTC"] = "bitcoin",
        };

        private readonly HttpClient _httpClient;

        public CryptoPriceClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<decimal> GetFiatEquivalentAsync(string cryptoCurrency, decimal amount, string fiatCurrency)
        {
            if (!CoinGeckoIds.TryGetValue(cryptoCurrency, out var coinId))
            {
                throw new InvalidOperationException($"Unrecognized crypto currency: {cryptoCurrency}.");
            }

            var fiatLower = fiatCurrency.ToLowerInvariant();

            // No leading slash — BaseAddress carries the /api/v3 segment
            // and must be combined with a relative (not rooted) path.
            using var response = await _httpClient.GetAsync($"simple/price?ids={coinId}&vs_currencies={fiatLower}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"CoinGecko price request failed ({(int)response.StatusCode}): {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var unitPrice = doc.RootElement.GetProperty(coinId).GetProperty(fiatLower).GetDecimal();

            return Math.Round(unitPrice * amount, 2);
        }
    }
}
