using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record LookupDto(int Id, string Name);

public record EmployeeListItemDto(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    string Email,
    string Department,
    string Designation,
    DateOnly DateOfJoining,
    bool IsActive,
    string? Username,
    decimal? CurrentGross);

public record EmployeeDto(
    int EmployeeId,
    string EmployeeCode,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string? Gender,
    DateOnly? DateOfBirth,
    DateOnly DateOfJoining,
    int DepartmentId,
    string Department,
    string Designation,
    string? PanNumber,
    string? BankName,
    string? BankAccountNo,
    string? IfscCode,
    bool IsActive,
    string? Username,
    string? Role,
    int? ManagerId,
    string? ManagerName,
    string? BloodGroup,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? PhotoDataUrl = null);

/// <summary>Everything printed on an employee ID card (company branding comes from the company master).</summary>
public record IdCardDto(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    string Designation,
    string Department,
    string? BloodGroup,
    string? Phone,
    string Email,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    DateOnly DateOfJoining,
    bool IsActive,
    string? PhotoDataUrl);

public class EmployeeUpsertRequest
{
    [Required, MaxLength(100)] public string FirstName { get; set; } = default!;
    [Required, MaxLength(100)] public string LastName { get; set; } = default!;
    [Required, EmailAddress, MaxLength(200)] public string Email { get; set; } = default!;
    [Phone, MaxLength(20)] public string? Phone { get; set; }
    [RegularExpression("^[MFO]$")] public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    [Required] public DateOnly DateOfJoining { get; set; }
    [Range(1, int.MaxValue)] public int DepartmentId { get; set; }
    [Required, MaxLength(100)] public string Designation { get; set; } = default!;
    /// <summary>Reporting manager; null for the top of the hierarchy.</summary>
    [Range(1, int.MaxValue)] public int? ManagerId { get; set; }
    [RegularExpression("^[A-Z]{5}[0-9]{4}[A-Z]$", ErrorMessage = "Invalid PAN format (e.g. ABCDE1234F).")]
    public string? PanNumber { get; set; }
    [MaxLength(100)] public string? BankName { get; set; }
    [MaxLength(30)] public string? BankAccountNo { get; set; }
    [RegularExpression("^[A-Z]{4}0[A-Z0-9]{6}$", ErrorMessage = "Invalid IFSC code.")]
    public string? IfscCode { get; set; }
    [RegularExpression("^(A|B|AB|O)[+-]$", ErrorMessage = "Blood group must be like A+, O- or AB+.")]
    public string? BloodGroup { get; set; }
    [MaxLength(100)] public string? EmergencyContactName { get; set; }
    [RegularExpression(@"^[0-9+\- ]{7,20}$", ErrorMessage = "Invalid emergency contact phone.")]
    public string? EmergencyContactPhone { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreateEmployeeRequest : EmployeeUpsertRequest
{
    /// <summary>Login to create for the employee.</summary>
    [Required, MaxLength(50), RegularExpression(@"^[a-zA-Z0-9._]+$")]
    public string Username { get; set; } = default!;
    [Required, MinLength(8), MaxLength(100)]
    public string InitialPassword { get; set; } = default!;
    /// <summary>Role of the new login. Anything other than Employee needs the roles.edit permission.</summary>
    [Range(1, int.MaxValue)]
    public int RoleId { get; set; } = 2;   // the built-in Employee role
}

public record ResetPasswordRequest([Required, MinLength(8), MaxLength(100)] string NewPassword);
