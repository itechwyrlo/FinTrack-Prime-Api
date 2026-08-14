using FinTrackPrime.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinTrackPrime.Models.Persistence
{
    public class FinTrackDbContext : DbContext
    {
        public FinTrackDbContext(DbContextOptions<FinTrackDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<Transaction> Transactions => Set<Transaction>();
        public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
        public DbSet<PremiumPurchase> PremiumPurchases => Set<PremiumPurchase>();
        public DbSet<Asset> Assets => Set<Asset>();
        public DbSet<InvestmentHolding> InvestmentHoldings => Set<InvestmentHolding>();
        public DbSet<RetirementPlan> RetirementPlans => Set<RetirementPlan>();
        public DbSet<Liability> Liabilities => Set<Liability>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<LinkedInstitution> LinkedInstitutions => Set<LinkedInstitution>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<LoanRate> LoanRates => Set<LoanRate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(u => u.Email).IsUnique();
                entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
                entity.Property(u => u.FullName).HasMaxLength(120).IsRequired();
                entity.Property(u => u.PasswordHash).HasMaxLength(256);
                entity.Property(u => u.PasswordSalt).HasMaxLength(256);
                entity.Property(u => u.GoogleId).HasMaxLength(64);

                // Filtered index: most rows have GoogleId == null (password
                // accounts that never linked Google), and SQL Server would
                // otherwise reject a plain unique index once more than one
                // NULL exists.
                entity.HasIndex(u => u.GoogleId)
                      .IsUnique()
                      .HasFilter("[GoogleId] IS NOT NULL");
            });

            modelBuilder.Entity<RefreshToken>(entity =>
            {
                entity.Property(rt => rt.TokenHash).HasMaxLength(128).IsRequired();
                entity.HasIndex(rt => rt.TokenHash).IsUnique();
                entity.HasIndex(rt => rt.FamilyId);
                entity.HasOne(rt => rt.User)
                      .WithMany(u => u.RefreshTokens)
                      .HasForeignKey(rt => rt.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Account>(entity =>
            {
                entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
                entity.Property(a => a.Currency).HasMaxLength(8);
                entity.Property(a => a.FiatEquivalentValue).HasColumnType("decimal(18,2)");
                entity.Property(a => a.FiatEquivalentCurrency).HasMaxLength(8);
                entity.Property(a => a.ExternalAccountId).HasMaxLength(128);
                entity.Property(a => a.Institution).HasMaxLength(80);
                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.Property(t => t.Amount).HasColumnType("decimal(18,2)");
                entity.Property(t => t.Category).HasMaxLength(80);
                entity.Property(t => t.ExternalTransactionId).HasMaxLength(128);
                entity.HasIndex(t => new { t.AccountId, t.ExternalTransactionId });
                entity.HasOne(t => t.Account)
                      .WithMany(a => a.Transactions)
                      .HasForeignKey(t => t.AccountId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<BudgetCategory>(entity =>
            {
                entity.Property(b => b.PlannedAmount).HasColumnType("decimal(18,2)");
                entity.Property(b => b.Name).HasMaxLength(80).IsRequired();
                entity.HasOne(b => b.User)
                      .WithMany()
                      .HasForeignKey(b => b.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PremiumPurchase>(entity =>
            {
                // Unique, not just indexed: the database itself refuses a
                // second row for the same PayPal order, even if two
                // requests race each other.
                entity.HasIndex(p => p.PayPalOrderId).IsUnique();

                // Also unique: at most one purchase per user now that
                // premium is a single all-tools unlock rather than one
                // row per tool.
                entity.HasIndex(p => p.UserId).IsUnique();
                entity.Property(p => p.PayPalOrderId).HasMaxLength(64).IsRequired();
                entity.Property(p => p.AmountPaid).HasColumnType("decimal(18,2)");
                entity.Property(p => p.Currency).HasMaxLength(8);
                entity.HasOne(p => p.User)
                      .WithMany()
                      .HasForeignKey(p => p.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<InvestmentHolding>(entity =>
            {
                entity.Property(h => h.Symbol).HasMaxLength(12).IsRequired();
                entity.Property(h => h.Name).HasMaxLength(120).IsRequired();
                entity.Property(h => h.Shares).HasColumnType("decimal(18,4)");
                entity.Property(h => h.CostBasisPerShare).HasColumnType("decimal(18,4)");
                entity.Property(h => h.CurrentPricePerShare).HasColumnType("decimal(18,4)");
                entity.HasOne(h => h.User)
                      .WithMany()
                      .HasForeignKey(h => h.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RetirementPlan>(entity =>
            {
                // One plan per user: this is an upserted scenario, not a
                // list of saved plans.
                entity.HasIndex(p => p.UserId).IsUnique();
                entity.Property(p => p.CurrentSavings).HasColumnType("decimal(18,2)");
                entity.Property(p => p.MonthlyContribution).HasColumnType("decimal(18,2)");
                entity.Property(p => p.AnnualReturnRatePercent).HasColumnType("decimal(5,2)");
                entity.HasOne(p => p.User)
                      .WithMany()
                      .HasForeignKey(p => p.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Liability>(entity =>
            {
                entity.Property(l => l.Name).HasMaxLength(120).IsRequired();
                entity.Property(l => l.Amount).HasColumnType("decimal(18,2)");
                entity.HasOne(l => l.User)
                      .WithMany()
                      .HasForeignKey(l => l.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Asset>(entity =>
            {
                entity.Property(a => a.Name).HasMaxLength(120).IsRequired();
                entity.Property(a => a.Amount).HasColumnType("decimal(18,2)");
                entity.HasOne(a => a.User)
                      .WithMany()
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LinkedInstitution>(entity =>
            {
                entity.Property(l => l.Institution).HasMaxLength(80).IsRequired();
                entity.Property(l => l.AccessToken).HasMaxLength(2048).IsRequired();
                entity.HasOne(l => l.User)
                      .WithMany(u => u.LinkedInstitutions)
                      .HasForeignKey(l => l.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Notification>(entity =>
            {
                entity.Property(n => n.Title).HasMaxLength(200).IsRequired();
                entity.Property(n => n.Message).HasMaxLength(1000).IsRequired();
                entity.HasIndex(n => new { n.UserId, n.CreatedAtUtc });

                // Filtered index: most rows have RelatedTransactionId ==
                // null (any future non-spend notification type), and SQL
                // Server would otherwise reject a plain unique index once
                // more than one NULL exists — same pattern as
                // User.GoogleId above.
                entity.HasIndex(n => n.RelatedTransactionId)
                      .IsUnique()
                      .HasFilter("[RelatedTransactionId] IS NOT NULL");

                entity.HasOne(n => n.User)
                      .WithMany()
                      .HasForeignKey(n => n.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LoanRate>(entity =>
            {
                entity.Property(r => r.AnnualRatePercent).HasColumnType("decimal(5,2)");
                entity.HasIndex(r => r.Type).IsUnique();

                // Seeded via HasData, not a hand-written InsertData in some
                // migration's Up() — HasData ties this data to the model
                // itself, so `dotnet ef migrations add` regenerates it
                // automatically every time, even a from-scratch InitialMigration
                // regenerate. Ids/UpdatedAtUtc are fixed constants (not
                // Guid.NewGuid()/DateTime.UtcNow) because EF diffs HasData
                // snapshot-to-snapshot; a value that changes every run would
                // make EF think the seed data changed on every migration.
                //
                // Placeholder rates — replace with the bank's real figures
                // before this goes anywhere near production.
                entity.HasData(
                    new LoanRate { Id = Guid.Parse("8f14e45f-ceea-4b90-8f0a-000000000001"), Type = LiabilityType.Mortgage, AnnualRatePercent = 6.50m, UpdatedAtUtc = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc) },
                    new LoanRate { Id = Guid.Parse("8f14e45f-ceea-4b90-8f0a-000000000002"), Type = LiabilityType.AutoLoan, AnnualRatePercent = 7.25m, UpdatedAtUtc = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc) },
                    new LoanRate { Id = Guid.Parse("8f14e45f-ceea-4b90-8f0a-000000000003"), Type = LiabilityType.StudentLoan, AnnualRatePercent = 5.50m, UpdatedAtUtc = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc) },
                    new LoanRate { Id = Guid.Parse("8f14e45f-ceea-4b90-8f0a-000000000004"), Type = LiabilityType.PersonalLoan, AnnualRatePercent = 11.00m, UpdatedAtUtc = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc) },
                    new LoanRate { Id = Guid.Parse("8f14e45f-ceea-4b90-8f0a-000000000005"), Type = LiabilityType.Other, AnnualRatePercent = 9.00m, UpdatedAtUtc = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc) }
                );
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
