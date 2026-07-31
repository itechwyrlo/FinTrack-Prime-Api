using System.Threading.Tasks;

namespace FinTrackPrime.Business.Interfaces
{
    // What this backend needs to know about a PayPal order, trimmed down
    // from PayPal's much larger response shape.
    public record PayPalOrderDetails(string Status, decimal AmountValue, string CurrencyCode);

    public interface IPayPalClient
    {
        // Calls PayPal directly (not the browser's word for it) to find
        // out what actually happened with an order. This is the fix for
        // the original build's biggest gap: payment and file delivery
        // were never actually connected server-side.
        Task<PayPalOrderDetails> GetOrderAsync(string paypalOrderId);
    }
}
