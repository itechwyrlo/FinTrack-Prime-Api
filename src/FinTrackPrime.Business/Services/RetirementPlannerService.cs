using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.Entities;
using FinTrackPrime.Models.Persistence;
using FinTrackPrime.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace FinTrackPrime.Business.Services
{
    public class RetirementPlannerService : IRetirementPlannerService
    {
        private readonly FinTrackDbContext _db;

        public RetirementPlannerService(FinTrackDbContext db)
        {
            _db = db;
        }

        public async Task<RetirementPlanViewModel> GetPlanAsync(Guid userId)
        {
            var plan = await _db.RetirementPlans.FirstOrDefaultAsync(p => p.UserId == userId);

            // No saved plan yet: project a reasonable starter scenario
            // without writing anything, so browsing the screen doesn't
            // silently create a row.
            if (plan is null)
            {
                return BuildViewModel(new RetirementPlan
                {
                    CurrentAge = 30,
                    RetirementAge = 65,
                    CurrentSavings = 0m,
                    MonthlyContribution = 300m,
                    AnnualReturnRatePercent = 6m,
                });
            }

            return BuildViewModel(plan);
        }

        public async Task<RetirementPlanViewModel> SavePlanAsync(Guid userId, RetirementPlanInputRequest request)
        {
            if (request.RetirementAge <= request.CurrentAge)
            {
                throw new InvalidOperationException("Retirement age must be after current age.");
            }

            var plan = await _db.RetirementPlans.FirstOrDefaultAsync(p => p.UserId == userId);

            if (plan is null)
            {
                plan = new RetirementPlan { Id = Guid.NewGuid(), UserId = userId };
                _db.RetirementPlans.Add(plan);
            }

            plan.CurrentAge = request.CurrentAge;
            plan.RetirementAge = request.RetirementAge;
            plan.CurrentSavings = request.CurrentSavings;
            plan.MonthlyContribution = request.MonthlyContribution;
            plan.AnnualReturnRatePercent = request.AnnualReturnRatePercent;

            await _db.SaveChangesAsync();

            return BuildViewModel(plan);
        }

        private static RetirementPlanViewModel BuildViewModel(RetirementPlan plan)
        {
            var monthlyRate = (plan.AnnualReturnRatePercent / 100m) / 12m;
            var totalMonths = (plan.RetirementAge - plan.CurrentAge) * 12;

            var projection = new List<RetirementProjectionPointViewModel>
            {
                new() { Age = plan.CurrentAge, ProjectedBalance = Math.Round(plan.CurrentSavings, 2) },
            };

            var balance = plan.CurrentSavings;

            for (var month = 1; month <= totalMonths; month++)
            {
                balance += balance * monthlyRate;
                balance += plan.MonthlyContribution;

                // Record a point once per full year, so the chart has one
                // entry per age rather than 12 nearly-identical points.
                if (month % 12 == 0)
                {
                    projection.Add(new RetirementProjectionPointViewModel
                    {
                        Age = plan.CurrentAge + (month / 12),
                        ProjectedBalance = Math.Round(balance, 2),
                    });
                }
            }

            return new RetirementPlanViewModel
            {
                CurrentAge = plan.CurrentAge,
                RetirementAge = plan.RetirementAge,
                CurrentSavings = plan.CurrentSavings,
                MonthlyContribution = plan.MonthlyContribution,
                AnnualReturnRatePercent = plan.AnnualReturnRatePercent,
                ProjectedBalanceAtRetirement = Math.Round(balance, 2),
                Projection = projection,
            };
        }
    }
}
