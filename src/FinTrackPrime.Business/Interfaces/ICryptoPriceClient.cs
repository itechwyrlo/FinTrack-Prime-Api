using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    public interface ICryptoPriceClient
    {
        // Throws InvalidOperationException on an unrecognized
        // cryptoCurrency or a failed API call — BankLinkService decides
        // how to handle that (keep the previous cached value).
        Task<decimal> GetFiatEquivalentAsync(string cryptoCurrency, decimal amount, string fiatCurrency);
    }
}
