namespace HrPayroll.BuildingBlocks.Directory;

// The company directory: employees, departments, holidays and the company master are owned by the Employees service;
// logins and roles by the Identity service. Every other service keeps a read-only copy of these tables in its own
// database, kept up to date from Kafka (see DirectoryReplicaHandlers), so it can join them locally.

public static class Roles
{
    public const string Admin = "Admin";
    public const string Employee = "Employee";
    public const string Hr = "HR";

    public const int AdminId = 1;
    public const int EmployeeId = 2;
    public const int HrId = 3;
}

public class Department
{
    public int DepartmentId { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

public class Employee
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = default!;
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
    public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateOnly DateOfJoining { get; set; }
    public int DepartmentId { get; set; }
    public string Designation { get; set; } = default!;
    /// <summary>Reporting manager (null for the top of the hierarchy).</summary>
    public int? ManagerId { get; set; }
    // Financial details - only in the Employees (owner) and Payroll copies.
    public string? PanNumber { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNo { get; set; }
    public string? IfscCode { get; set; }
    public string? BloodGroup { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Department Department { get; set; } = default!;
    public Employee? Manager { get; set; }
    public User? User { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}

public class Holiday
{
    public int HolidayId { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Name { get; set; } = default!;
}

/// <summary>Company master - a single row (<see cref="SingletonId"/>). The logo is only kept by the Employees service.</summary>
public class CompanyProfile
{
    public const int SingletonId = 1;

    public int CompanyId { get; set; } = SingletonId;
    public string Name { get; set; } = default!;
    public string? LegalName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PinCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Pan { get; set; }
    public string? Tan { get; set; }
    public string? Gstin { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryDesignation { get; set; }
    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class Role
{
    public int RoleId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>Built-in role (Admin / Employee): cannot be renamed or deleted.</summary>
    public bool IsSystem { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public int RoleId { get; set; }
    public string Permission { get; set; } = default!;
}

public class User
{
    public int UserId { get; set; }
    public string Username { get; set; } = default!;
    /// <summary>Only in the Identity service (never copied to other services).</summary>
    public string PasswordHash { get; set; } = default!;
    public int RoleId { get; set; }
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    /// <summary>When the user's last chat connection closed (UTC) - kept by the Chat service.</summary>
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Role Role { get; set; } = default!;
    public Employee? Employee { get; set; }
}

/// <summary>
/// Copy of a leave request kept by the services that need to know who is on leave (Attendance, Payroll). Owned by the
/// Leave service; filled from <see cref="HrPayroll.Contracts.Events.LeaveRequestChanged"/>.
/// </summary>
public class LeaveRecord
{
    public int LeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public int LeaveTypeId { get; set; }
    public string LeaveCode { get; set; } = default!;
    public string LeaveName { get; set; } = default!;
    public bool IsPaid { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public bool IsHalfDay { get; set; }
    public decimal TotalDays { get; set; }
    public string Status { get; set; } = default!;
}

public static class LeaveStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}
