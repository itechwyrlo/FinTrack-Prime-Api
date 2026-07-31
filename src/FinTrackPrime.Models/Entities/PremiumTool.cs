namespace FinTrackPrime.Models.Entities
{
    // One value per premium tool. Each is unlocked independently, a
    // PremiumPurchase row exists per tool a user has actually bought,
    // not one flag covering all four.
    public enum PremiumTool
    {
        LoanCalculator,
        InvestmentTracker,
        RetirementPlanner,
        FinancialStatement,
    }
}