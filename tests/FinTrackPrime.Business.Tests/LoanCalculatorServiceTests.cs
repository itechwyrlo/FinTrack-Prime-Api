using System;
using System.Linq;
using System.Threading.Tasks;
using FinTrackPrime.Business.Services;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinTrackPrime.Business.Tests
{
    public class LoanCalculatorServiceTests
    {
        private static FinTrackDbContext BuildDb()
        {
            var options = new DbContextOptionsBuilder<FinTrackDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new FinTrackDbContext(options);
        }

        private static async Task SeedRateAsync(FinTrackDbContext db, LiabilityType type, decimal annualRatePercent)
        {
            db.LoanRates.Add(new LoanRate { Id = Guid.NewGuid(), Type = type, AnnualRatePercent = annualRatePercent, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        private static async Task<Guid> SeedUserAsync(FinTrackDbContext db)
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new User { Id = userId, Email = $"{userId}@test.com", FullName = "Test User", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return userId;
        }

        [Fact]
        public async Task GetRatesAsync_ReturnsAllSeededRates()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 6.50m);
            await SeedRateAsync(db, LiabilityType.AutoLoan, 7.25m);

            var service = new LoanCalculatorService(db);
            var rates = await service.GetRatesAsync();

            Assert.Equal(2, rates.Count);
            Assert.Contains(rates, r => r.Type == LiabilityType.Mortgage && r.AnnualRatePercent == 6.50m);
        }

        [Fact]
        public async Task CalculateAsync_ThrowsWhenLoanTypeHasNoRate()
        {
            await using var db = BuildDb();
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest { PrincipalAmount = 10000m, LoanType = LiabilityType.Mortgage, TermMonths = 12 };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateAsync(request));
        }

        [Fact]
        public async Task CalculateAsync_Equal_ProducesAConstantPaymentThatFullyPaysOffTheLoan()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 10000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 12, Method = AmortizationMethod.Equal,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.Equal(result.Schedule[0].PaymentAmount, result.Schedule[^1].PaymentAmount);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
            Assert.Equal(12m, result.AppliedAnnualInterestRatePercent);
            Assert.True(result.TotalInterestPaid > 0m);
        }

        [Fact]
        public async Task CalculateAsync_FixedPrincipal_PaymentDeclinesAndPrincipalPortionStaysConstant()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.AutoLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.AutoLoan, TermMonths = 12, Method = AmortizationMethod.FixedPrincipal,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.True(result.Schedule[0].PaymentAmount > result.Schedule[^1].PaymentAmount);
            Assert.All(result.Schedule, row => Assert.Equal(1000m, row.PrincipalPaid));
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CalculateAsync_GracePeriod_IsInterestOnlyDuringGraceThenAmortizesTheRest()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.Mortgage, TermMonths = 12,
                Method = AmortizationMethod.GracePeriod, GracePeriodMonths = 3,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(12, result.Schedule.Count);
            Assert.All(result.Schedule.Take(3), row =>
            {
                Assert.Equal(0m, row.PrincipalPaid);
                Assert.Equal(12000m, row.RemainingBalance);
            });
            Assert.True(result.Schedule[3].PrincipalPaid > 0m);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CalculateAsync_RejectsGracePeriodMonthsOutOfRange()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.Mortgage, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 12000m, LoanType = LiabilityType.Mortgage, TermMonths = 12,
                Method = AmortizationMethod.GracePeriod, GracePeriodMonths = 12,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateAsync(request));
        }

        [Fact]
        public async Task CalculateAsync_Balloon_IsInterestOnlyUntilTheFinalMonthThenPaysTheBalloon()
        {
            await using var db = BuildDb();
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            var service = new LoanCalculatorService(db);

            var request = new LoanCalculationRequest
            {
                PrincipalAmount = 5000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 6, Method = AmortizationMethod.Balloon,
            };

            var result = await service.CalculateAsync(request);

            Assert.Equal(6, result.Schedule.Count);
            Assert.All(result.Schedule.Take(5), row =>
            {
                Assert.Equal(0m, row.PrincipalPaid);
                Assert.Equal(5000m, row.RemainingBalance);
            });
            Assert.Equal(5000m, result.Schedule[^1].PrincipalPaid);
            Assert.Equal(0m, result.Schedule[^1].RemainingBalance);
        }

        [Fact]
        public async Task CheckAffordabilityAsync_UsesFirstPeriodPaymentForBalloon()
        {
            await using var db = BuildDb();
            var userId = await SeedUserAsync(db);
            await SeedRateAsync(db, LiabilityType.PersonalLoan, 12m);
            db.BudgetCategories.Add(new BudgetCategory { Id = Guid.NewGuid(), UserId = userId, Name = "Salary", Type = BudgetCategoryType.Income, PlannedAmount = 5000m });
            await db.SaveChangesAsync();

            var service = new LoanCalculatorService(db);
            var request = new LoanAffordabilityRequest
            {
                PrincipalAmount = 5000m, LoanType = LiabilityType.PersonalLoan, TermMonths = 6, Method = AmortizationMethod.Balloon,
            };

            var result = await service.CheckAffordabilityAsync(userId, request);

            // Interest-only first payment: 5000 * (12% / 12) = 50, far
            // below what a fully-amortized 6-month loan would require —
            // confirms the affordability check used the first period's
            // payment, not a full-amortization figure.
            Assert.Equal(50m, result.ProposedMonthlyPayment);
        }
    }
}
