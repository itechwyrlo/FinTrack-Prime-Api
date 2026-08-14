namespace FinTrackPrime.Models.Entities
{
    // A request-time choice, not bank-managed data — no dedicated table,
    // unlike LoanRate.
    public enum AmortizationMethod
    {
        Equal,
        FixedPrincipal,
        GracePeriod,
        Balloon,
    }
}
