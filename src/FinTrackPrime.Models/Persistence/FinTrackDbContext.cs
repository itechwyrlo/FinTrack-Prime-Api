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
        public DbSet<InvestmentHolding> InvestmentHoldings => Set<InvestmentHolding>();
        public DbSet<RetirementPlan> RetirementPlans => Set<RetirementPlan>();
        public DbSet<Liability> Liabilities => Set<Liability>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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
                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.Property(t => t.Amount).HasColumnType("decimal(18,2)");
                entity.Property(t => t.Category).HasMaxLength(80);
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

            base.OnModelCreating(modelBuilder);
        }
    }
}
