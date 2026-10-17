using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Attendance.Data;

/// <summary>
/// HrPayroll_Attendance: check-in / check-out, work logs, attendance areas and rules, registered phones, registered faces
/// and selfies - plus copies of the directory and of leave requests (to show leave on calendars).
/// </summary>
public class AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();
    public DbSet<WorkLog> WorkLogs => Set<WorkLog>();
    public DbSet<AttendanceLocation> AttendanceLocations => Set<AttendanceLocation>();
    public DbSet<AttendanceLocationEmployee> AttendanceLocationEmployees => Set<AttendanceLocationEmployee>();
    public DbSet<AttendanceSettings> AttendanceSettings => Set<AttendanceSettings>();
    public DbSet<AttendanceDevice> AttendanceDevices => Set<AttendanceDevice>();
    public DbSet<EmployeeFace> EmployeeFaces => Set<EmployeeFace>();
    public DbSet<AttendanceSelfie> AttendanceSelfies => Set<AttendanceSelfie>();
    public DbSet<LeaveRecord> LeaveRecords => Set<LeaveRecord>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.MapLeaveRecords();

        b.Entity<AttendanceRecord>(e =>
        {
            e.ToTable("Attendance");
            e.HasKey(x => x.AttendanceId);
            e.Property(x => x.CheckInLatitude).HasPrecision(9, 6);
            e.Property(x => x.CheckInLongitude).HasPrecision(9, 6);
            e.Property(x => x.CheckOutLatitude).HasPrecision(9, 6);
            e.Property(x => x.CheckOutLongitude).HasPrecision(9, 6);
            e.Property(x => x.CheckInFaceScore).HasPrecision(4, 3);
            e.Property(x => x.CheckOutFaceScore).HasPrecision(4, 3);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<AttendanceLocation>(e =>
        {
            e.ToTable("AttendanceLocations");
            e.HasKey(x => x.LocationId);
            e.Property(x => x.Latitude).HasPrecision(9, 6);
            e.Property(x => x.Longitude).HasPrecision(9, 6);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasMany(x => x.Employees).WithOne().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AttendanceLocationEmployee>(e =>
        {
            e.ToTable("AttendanceLocationEmployees");
            e.HasKey(x => new { x.LocationId, x.EmployeeId });
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<AttendanceSettings>(e =>
        {
            e.ToTable("AttendanceSettings");
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<AttendanceDevice>(e =>
        {
            e.ToTable("AttendanceDevices");
            e.HasKey(x => x.DeviceId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });

        b.Entity<EmployeeFace>(e =>
        {
            e.ToTable("EmployeeFaces");
            e.HasKey(x => x.EmployeeId);
            e.Property(x => x.EmployeeId).ValueGeneratedNever();
            e.Property(x => x.IdPhotoScore).HasPrecision(4, 3);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedByUserId);
        });

        b.Entity<AttendanceSelfie>(e =>
        {
            e.ToTable("AttendanceSelfies");
            e.HasKey(x => new { x.AttendanceId, x.Kind });
            e.Property(x => x.Kind).IsUnicode(false).HasMaxLength(3);
            e.Property(x => x.Score).HasPrecision(4, 3);
        });

        b.Entity<WorkLog>(e =>
        {
            e.ToTable("WorkLogs");
            e.Property(x => x.Hours).HasPrecision(4, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        });
    }
}
