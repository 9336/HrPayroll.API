using HrPayroll.BuildingBlocks.Data;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Leave.Data;

/// <summary>
/// HrPayroll_Leave: leave types, balances, requests and the leave-year rollover log, plus a copy of the directory.
/// Every change to a leave request is published (hr.leave-requests) - Attendance and Payroll keep copies.
/// </summary>
public class LeaveDbContext(DbContextOptions<LeaveDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveYearRollover> LeaveYearRollovers => Set<LeaveYearRollover>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<LeaveType>(e =>
        {
            e.ToTable("LeaveTypes");
            e.Property(x => x.AnnualQuota).HasPrecision(5, 1);
            e.Property(x => x.MaxCarryForward).HasPrecision(5, 1);
            e.Property(x => x.ApplicableGender).IsFixedLength().IsUnicode(false).HasMaxLength(1);
        });

        b.Entity<LeaveBalance>(e =>
        {
            e.ToTable("LeaveBalances");
            e.Property(x => x.Allocated).HasPrecision(5, 1);
            e.Property(x => x.CarryForwarded).HasPrecision(5, 1);
            e.Property(x => x.Used).HasPrecision(5, 1);
            e.Property(x => x.Available).HasPrecision(5, 1)
                .HasComputedColumnSql("[Allocated] + [CarryForwarded] - [Used]", stored: true);
            e.HasOne(x => x.LeaveType).WithMany().HasForeignKey(x => x.LeaveTypeId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.Ignore(x => x.CurrentAvailable);
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.ToTable("LeaveRequests");
            e.Property(x => x.TotalDays).HasPrecision(5, 1);
            e.Property(x => x.AppliedOn).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.LeaveType).WithMany().HasForeignKey(x => x.LeaveTypeId);
            e.HasOne(x => x.ActionedBy).WithMany().HasForeignKey(x => x.ActionedByUserId);
        });

        b.Entity<LeaveYearRollover>(e =>
        {
            e.ToTable("LeaveYearRollovers");
            e.HasKey(x => x.RolloverId);
            e.Property(x => x.CarriedForwardDays).HasPrecision(10, 1);
            e.Property(x => x.LapsedDays).HasPrecision(10, 1);
            e.HasOne(x => x.RunBy).WithMany().HasForeignKey(x => x.RunByUserId);
        });
    }

    // ------------------------------------------------------------------ Published: leave request changes
    protected override bool Publishes(Type t) => t == typeof(LeaveRequest) || t == typeof(LeaveType);

    protected override async Task<IEnumerable<OutboxEvent>> EventsAfterSaveAsync(
        IReadOnlyList<(object Entity, EntityState State)> changes, CancellationToken ct)
    {
        var requests = changes.Select(c => c.Entity).OfType<LeaveRequest>().ToList();
        // A renamed leave type: the copies show its name, so its requests are published again.
        var renamedTypes = changes.Select(c => c.Entity).OfType<LeaveType>().Select(t => t.LeaveTypeId).ToList();
        if (renamedTypes.Count > 0)
            requests.AddRange(await LeaveRequests.AsNoTracking().Where(r => renamedTypes.Contains(r.LeaveTypeId)).ToListAsync(ct));
        var typeIds = requests.Select(r => r.LeaveTypeId).Distinct().ToList();
        var types = await LeaveTypes.AsNoTracking().Where(t => typeIds.Contains(t.LeaveTypeId)).ToDictionaryAsync(t => t.LeaveTypeId, ct);
        return requests.Select(r => LeaveEvent(r, types[r.LeaveTypeId]));
    }

    public static OutboxEvent LeaveEvent(LeaveRequest r, LeaveType t) => new(Topics.LeaveRequests, r.LeaveRequestId.ToString(),
        new LeaveRequestChanged(r.LeaveRequestId, r.EmployeeId, r.LeaveTypeId, t.Code, t.Name, t.IsPaid, r.FromDate, r.ToDate,
            r.IsHalfDay, r.TotalDays, r.Status));
}
