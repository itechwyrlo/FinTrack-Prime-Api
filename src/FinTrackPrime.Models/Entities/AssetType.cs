namespace FinTrackPrime.Models.Entities
{
    // Cash, Investment, and Crypto are system-assigned — they only ever
    // come from synced Accounts/InvestmentHoldings and are never offered
    // as a choice when a user manually adds an asset (see
    // FinancialStatementService.AddAssetAsync). RealEstate, Vehicle, and
    // Other are the only types a manual Asset row can have.
    public enum AssetType
    {
        Cash,
        Investment,
        RealEstate,
        Vehicle,
        Crypto,
        Other,
    }
}
