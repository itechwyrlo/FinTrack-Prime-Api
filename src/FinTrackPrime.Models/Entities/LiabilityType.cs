namespace FinTrackPrime.Models.Entities
{
    // CreditCard is system-assigned — it only ever comes from synced
    // CreditCard Accounts and is never offered as a choice when a user
    // manually adds a liability (see
    // FinancialStatementService.AddLiabilityAsync).
    public enum LiabilityType
    {
        CreditCard,
        Mortgage,
        AutoLoan,
        StudentLoan,
        PersonalLoan,
        Other,
    }
}
