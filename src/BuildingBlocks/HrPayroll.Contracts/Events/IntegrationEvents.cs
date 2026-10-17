namespace HrPayroll.Contracts.Events;

/// <summary>
/// Kafka topics. Every message is keyed by the id of the record it is about, so all changes to one record land on the
/// same partition and are read in order.
/// </summary>
public static class Topics
{
    // ---- Company directory (owned by the Employees and Identity services, copied into every service's database)
    public const string Employees = "hr.employees";
    public const string Departments = "hr.departments";
    public const string Holidays = "hr.holidays";
    public const string Company = "hr.company";
    public const string Users = "hr.users";
    public const string Roles = "hr.roles";

    // ---- Business events
    public const string LeaveRequests = "hr.leave-requests";
    public const string Salaries = "hr.salaries";
    public const string Promotions = "hr.promotions";

    // ---- Commands to one service
    public const string LoginRequests = "hr.identity.login-requests";
    public const string Emails = "hr.notifications.emails";

    /// <summary>Directory topics: kept forever (log compaction keeps the latest message per key).</summary>
    public static readonly string[] Directory = [Employees, Departments, Holidays, Company, Users, Roles];

    public static readonly string[] All = [.. Directory, LeaveRequests, Salaries, Promotions, LoginRequests, Emails];

    /// <summary>Topics whose latest message per key is the current state (log-compacted).</summary>
    public static readonly IReadOnlySet<string> Compacted = new HashSet<string>([.. Directory, LeaveRequests, Salaries]);

    /// <summary>Messages a consumer could not process after several attempts go to "&lt;topic&gt;.dlq".</summary>
    public static string DeadLetter(string topic) => topic + ".dlq";
}

// ===================================================================== Company directory
/// <summary>An employee was added or changed (<see cref="IsNew"/> = just created). Published by the Employees service.</summary>
public record EmployeeUpserted(
    int EmployeeId, string EmployeeCode, string FirstName, string LastName, string Email, string? Phone, string? Gender,
    DateOnly? DateOfBirth, DateOnly DateOfJoining, int DepartmentId, string Designation, int? ManagerId,
    string? PanNumber, string? BankName, string? BankAccountNo, string? IfscCode,
    string? BloodGroup, string? EmergencyContactName, string? EmergencyContactPhone,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt, bool IsNew);

public record DepartmentUpserted(int DepartmentId, string Name, bool IsActive);

public record HolidayUpserted(int HolidayId, DateOnly HolidayDate, string Name);

public record HolidayDeleted(int HolidayId);

/// <summary>Company master without the logo (the logo is only served by the Employees service).</summary>
public record CompanyProfileUpdated(
    string Name, string? LegalName, string? AddressLine1, string? AddressLine2, string? City, string? State, string? PinCode,
    string? Phone, string? Email, string? Website, string? Pan, string? Tan, string? Gstin,
    string? SignatoryName, string? SignatoryDesignation, DateTime? UpdatedAt);

/// <summary>A login was added or changed (never carries the password). Published by the Identity service.</summary>
public record UserUpserted(int UserId, string Username, int RoleId, int? EmployeeId, bool IsActive, DateTime? LastLoginAt, DateTime CreatedAt);

/// <summary>A role and its permission codes (the built-in Admin role has every permission and lists none).</summary>
public record RoleUpserted(int RoleId, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions);

public record RoleDeleted(int RoleId);

// ===================================================================== Business events
/// <summary>A leave request was applied for, approved, rejected or cancelled. Published by the Leave service.</summary>
public record LeaveRequestChanged(
    int LeaveRequestId, int EmployeeId, int LeaveTypeId, string LeaveCode, string LeaveName, bool IsPaid,
    DateOnly FromDate, DateOnly ToDate, bool IsHalfDay, decimal TotalDays, string Status);

/// <summary>A salary revision was created or deleted. Published by the Payroll service.</summary>
public record SalaryStructureChanged(int SalaryStructureId, int EmployeeId, DateOnly EffectiveFrom, decimal GrossMonthly, bool Deleted);

/// <summary>
/// A promotion took effect: the Employees service updates the employee record. Only what the promotion changes is
/// set (null department = unchanged; <see cref="ManagerChanged"/> = false keeps the manager).
/// </summary>
public record PromotionApplied(int PromotionId, int EmployeeId, DateOnly EffectiveDate, string NewDesignation, int? NewDepartmentId,
    bool ManagerChanged, int? NewManagerId);

// ===================================================================== Commands
/// <summary>Create the login of a new employee (sent by the Employees service to the Identity service).</summary>
public record LoginRequested(int EmployeeId, string Username, string PasswordHash, int RoleId);

/// <summary>Send an email (any service -> Notifications service, which keeps the email log and sends it).</summary>
public record EmailRequested(string To, string? ToName, string Subject, string HtmlBody, string Category, DateTime NotBefore, string Source);
