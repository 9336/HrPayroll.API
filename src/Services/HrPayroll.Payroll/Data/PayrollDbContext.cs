using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Data;

/// <summary>
/// HrPayroll_Payroll: salary structures, payslips, released documents (letters, Form 16), promotions and closed
/// financial years - plus copies of the directory (with PAN / bank details, for payslips and Form 16) and of leave
/// requests (unpaid leave in the payroll run). Salary revisions are published (hr.salaries).
/// </summary>
public class PayrollDbContext(DbContextOptions<PayrollDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new(IncludeFinancialDetails: true);

    protected override DirectoryModel Directory => Layout;

    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<ClosedFinancialYear> ClosedFinancialYears => Set<ClosedFinancialYear>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<LeaveRecord> LeaveRecords => Set<LeaveRecord>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.MapLeaveRecords();

        b.Entity<SalaryStructure>(e =>
        {
            e.ToTable("SalaryStructures");
            foreach (var p in new[] { nameof(SalaryStructure.BasicPay), nameof(SalaryStructure.Hra),
                         nameof(SalaryStructure.ConveyanceAllowance), nameof(SalaryStructure.MedicalAllowance),
                         nameof(SalaryStructure.SpecialAllowance), nameof(SalaryStructure.ProfessionalTax),
                         nameof(SalaryStructure.MonthlyTds) })
                e.Property<decimal>(p).HasPrecision(12, 2);
            e.Property(x => x.PfPercent).HasPrecision(5, 2);
            e.Property(x => x.GrossMonthly).HasPrecision(12, 2)
                .HasComputedColumnSql("[BasicPay] + [Hra] + [ConveyanceAllowance] + [MedicalAllowance] + [SpecialAllowance]", stored: true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.Ignore(x => x.Gross);
        });

        b.Entity<Payslip>(e =>
        {
            e.ToTable("Payslips");
            foreach (var p in typeof(Payslip).GetProperties().Where(p => p.PropertyType == typeof(decimal)))
                e.Property(p.Name).HasPrecision(12, 2);
            e.Property(x => x.LopDays).HasPrecision(4, 1);
            e.Property(x => x.PaidDays).HasPrecision(4, 1);
            e.Property(x => x.GeneratedOn).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.SalaryStructure).WithMany().HasForeignKey(x => x.SalaryStructureId);
        });

        b.Entity<EmployeeDocument>(e =>
        {
            e.ToTable("EmployeeDocuments");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.ReleasedOn).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.ReleasedBy).WithMany().HasForeignKey(x => x.ReleasedByUserId);
        });

        b.Entity<ClosedFinancialYear>(e =>
        {
            e.ToTable("ClosedFinancialYears");
            e.HasKey(x => x.FinancialYear);
            e.Property(x => x.FinancialYear).ValueGeneratedNever();
            e.HasOne(x => x.ClosedBy).WithMany().HasForeignKey(x => x.ClosedByUserId);
        });

        b.Entity<Promotion>(e =>
        {
            e.ToTable("Promotions");
            e.HasKey(x => x.PromotionId);
            e.Property(x => x.IncrementPercent).HasPrecision(5, 2);
            e.Property(x => x.OldMonthlyGross).HasPrecision(12, 2);
            e.Property(x => x.NewMonthlyGross).HasPrecision(12, 2);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.OldDepartment).WithMany().HasForeignKey(x => x.OldDepartmentId);
            e.HasOne(x => x.NewDepartment).WithMany().HasForeignKey(x => x.NewDepartmentId);
            e.HasOne(x => x.OldManager).WithMany().HasForeignKey(x => x.OldManagerId);
            e.HasOne(x => x.NewManager).WithMany().HasForeignKey(x => x.NewManagerId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId);
        });
    }

    // ------------------------------------------------------------------ Published: salary revisions
    protected override bool Publishes(Type t) => t == typeof(SalaryStructure);

    protected override Task<IEnumerable<OutboxEvent>> EventsAfterSaveAsync(
        IReadOnlyList<(object Entity, EntityState State)> changes, CancellationToken ct) =>
        Task.FromResult(changes.Where(c => c.Entity is SalaryStructure)
            .Select(c => SalaryEvent((SalaryStructure)c.Entity, deleted: c.State == EntityState.Deleted)));

    public static OutboxEvent SalaryEvent(SalaryStructure s, bool deleted) => new(Topics.Salaries, s.SalaryStructureId.ToString(),
        new SalaryStructureChanged(s.SalaryStructureId, s.EmployeeId, s.EffectiveFrom, s.Gross, deleted));
}
