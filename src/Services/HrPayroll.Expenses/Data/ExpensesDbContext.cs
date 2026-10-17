using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Expenses.Data;

/// <summary>HrPayroll_Expenses: office expenses and categories, travel claims with their lines, receipts and timeline.</summary>
public class ExpensesDbContext(DbContextOptions<ExpensesDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<OfficeExpense> OfficeExpenses => Set<OfficeExpense>();
    public DbSet<TravelClaim> TravelClaims => Set<TravelClaim>();
    public DbSet<TravelClaimItem> TravelClaimItems => Set<TravelClaimItem>();
    public DbSet<TravelReceipt> TravelReceipts => Set<TravelReceipt>();
    public DbSet<TravelClaimEvent> TravelClaimEvents => Set<TravelClaimEvent>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ExpenseCategory>().ToTable("ExpenseCategories");

        b.Entity<OfficeExpense>(e =>
        {
            e.ToTable("OfficeExpenses");
            e.HasKey(x => x.ExpenseId);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.ExpenseCategoryId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId);
            e.HasOne(x => x.ActionedBy).WithMany().HasForeignKey(x => x.ActionedByUserId);
        });

        b.Entity<TravelClaim>(e =>
        {
            e.ToTable("TravelClaims");
            e.HasKey(x => x.ClaimId);
            e.Property(x => x.TotalAmount).HasPrecision(12, 2);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerEmployeeId);
            e.HasOne(x => x.ManagerActionBy).WithMany().HasForeignKey(x => x.ManagerActionByUserId);
            e.HasOne(x => x.AdminActionBy).WithMany().HasForeignKey(x => x.AdminActionByUserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PaidByUserId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TravelClaimItem>(e =>
        {
            e.ToTable("TravelClaimItems");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasOne(x => x.Receipt).WithOne().HasForeignKey<TravelReceipt>(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TravelReceipt>(e =>
        {
            e.ToTable("TravelReceipts");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.ContentType).IsUnicode(false).HasMaxLength(100);
        });

        b.Entity<TravelClaimEvent>(e =>
        {
            e.ToTable("TravelClaimEvents");
            e.HasKey(x => x.EventId);
            e.HasOne<TravelClaim>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.By).WithMany().HasForeignKey(x => x.ByUserId);
        });
    }
}
