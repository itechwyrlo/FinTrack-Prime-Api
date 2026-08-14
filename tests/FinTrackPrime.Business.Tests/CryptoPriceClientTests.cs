using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class CryptoPriceClientTests
    {
        [Fact]
        public async Task GetFiatEquivalentAsync_ReturnsAmountTimesUnitPrice()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/api/v3/simple/price"] = (HttpStatusCode.OK, "{\"bitcoin\":{\"usd\":65000.00}}"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            var result = await client.GetFiatEquivalentAsync("BTC", 0.5m, "USD");

            Assert.Equal(32500.00m, result);
        }

        [Fact]
        public async Task GetFiatEquivalentAsync_ThrowsForUnrecognizedCryptoCurrency()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>());
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFiatEquivalentAsync("DOGE", 100m, "USD"));
        }

        [Fact]
        public async Task GetFiatEquivalentAsync_ThrowsOnFailedRequest()
        {
            var handler = new FakeHttpMessageHandler(new Dictionary<string, (HttpStatusCode, string)>
            {
                ["/api/v3/simple/price"] = (HttpStatusCode.InternalServerError, "server error"),
            });
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") };
            var client = new CryptoPriceClient(httpClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFiatEquivalentAsync("BTC", 1m, "USD"));
        }
    }
}
