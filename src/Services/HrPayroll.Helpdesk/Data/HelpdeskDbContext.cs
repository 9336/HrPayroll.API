using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Helpdesk.Data;

/// <summary>HrPayroll_Helpdesk: IT service requests with their timeline, and the visitor desk.</summary>
public class HelpdeskDbContext(DbContextOptions<HelpdeskDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<ItRequest> ItRequests => Set<ItRequest>();
    public DbSet<ItRequestEvent> ItRequestEvents => Set<ItRequestEvent>();
    public DbSet<Visit> Visits => Set<Visit>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ItRequest>(e =>
        {
            e.ToTable("ItRequests");
            e.HasKey(x => x.RequestId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.Approver).WithMany().HasForeignKey(x => x.ApproverEmployeeId);
            e.HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedByUserId);
            e.HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToUserId);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ItRequestEvent>(e =>
        {
            e.ToTable("ItRequestEvents");
            e.HasKey(x => x.EventId);
            e.HasOne(x => x.By).WithMany().HasForeignKey(x => x.ByUserId);
        });

        b.Entity<Visit>(e =>
        {
            e.ToTable("Visits");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostEmployeeId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId);
        });
    }
}
