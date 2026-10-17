using HrPayroll.BuildingBlocks.Directory;
using HrPayroll.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.BuildingBlocks.Data;

internal static class MessagingModel
{
    public static void Map(ModelBuilder b)
    {
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Topic).IsUnicode(false).HasMaxLength(200);
            e.Property(x => x.MessageKey).HasMaxLength(200);
            e.Property(x => x.EventType).IsUnicode(false).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(1000);
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).IsUnicode(false).HasMaxLength(100);
        });
    }
}

/// <summary>
/// Maps the directory tables. The owner's tables generate their keys; a copy stores the owner's keys as they are and
/// leaves out what it must not hold (password hashes everywhere, bank / PAN details outside Payroll, the logo).
/// </summary>
internal static class DirectoryModelBuilder
{
    public static void Map(ModelBuilder b, DirectoryModel d)
    {
        b.Entity<Role>(e =>
        {
            e.ToTable("Roles");
            e.Property(x => x.RoleId).ValueGeneratedNever();   // not an identity column, even in Identity
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("RolePermissions");
            e.HasKey(x => new { x.RoleId, x.Permission });
        });

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Employee).WithOne(x => x.User).HasForeignKey<User>(x => x.EmployeeId);
            if (d.OwnsUsers)
            {
                e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            }
            else
            {
                e.Property(x => x.UserId).ValueGeneratedNever();
                e.Ignore(x => x.PasswordHash);
            }
        });

        b.Entity<Department>(e =>
        {
            e.ToTable("Departments");
            if (!d.OwnsEmployees) e.Property(x => x.DepartmentId).ValueGeneratedNever();
        });

        b.Entity<Employee>(e =>
        {
            e.ToTable("Employees");
            e.Property(x => x.Gender).IsFixedLength().IsUnicode(false).HasMaxLength(1);
            e.Property(x => x.BloodGroup).IsUnicode(false).HasMaxLength(3);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId);
            e.Ignore(x => x.FullName);
            if (d.OwnsEmployees)
            {
                e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            }
            else
            {
                e.Property(x => x.EmployeeId).ValueGeneratedNever();
                if (!d.IncludeFinancialDetails)
                {
                    e.Ignore(x => x.PanNumber);
                    e.Ignore(x => x.BankName);
                    e.Ignore(x => x.BankAccountNo);
                    e.Ignore(x => x.IfscCode);
                }
            }
        });

        b.Entity<Holiday>(e =>
        {
            e.ToTable("Holidays");
            if (!d.OwnsEmployees) e.Property(x => x.HolidayId).ValueGeneratedNever();
        });

        b.Entity<CompanyProfile>(e =>
        {
            e.ToTable("CompanyProfile");
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).ValueGeneratedNever();
            if (!d.OwnsEmployees)
            {
                e.Ignore(x => x.Logo);
                e.Ignore(x => x.LogoContentType);
            }
        });
    }
}

public static class LeaveRecordModel
{
    /// <summary>Maps the copy of leave requests (Attendance, Payroll).</summary>
    public static void MapLeaveRecords(this ModelBuilder b) =>
        b.Entity<LeaveRecord>(e =>
        {
            e.ToTable("LeaveRecords");
            e.HasKey(x => x.LeaveRequestId);
            e.Property(x => x.LeaveRequestId).ValueGeneratedNever();
            e.Property(x => x.TotalDays).HasPrecision(5, 1);
        });
}
