using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

/// <summary>One person in the company hierarchy. The client builds the tree from <see cref="ManagerId"/>.</summary>
public record OrgNodeDto(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    string Designation,
    string Department,
    string Email,
    int? ManagerId);

// ---------------------------------------------------------------- Company master
public record CompanyDto(
    string Name,
    string? LegalName,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PinCode,
    string? Phone,
    string? Email,
    string? Website,
    string? Pan,
    string? Tan,
    string? Gstin,
    string? SignatoryName,
    string? SignatoryDesignation,
    string? LogoDataUrl,   // logo as a data: URL, null when none is uploaded
    DateTime? UpdatedAt);

public class UpdateCompanyRequest
{
    [Required, MaxLength(150)] public string Name { get; set; } = default!;
    [MaxLength(200)] public string? LegalName { get; set; }
    [MaxLength(200)] public string? AddressLine1 { get; set; }
    [MaxLength(200)] public string? AddressLine2 { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(100)] public string? State { get; set; }
    [RegularExpression("^[1-9][0-9]{5}$", ErrorMessage = "PIN code must be 6 digits.")]
    public string? PinCode { get; set; }
    [Phone, MaxLength(20)] public string? Phone { get; set; }
    [EmailAddress, MaxLength(200)] public string? Email { get; set; }
    [MaxLength(200)] public string? Website { get; set; }
    [RegularExpression("^[A-Z]{5}[0-9]{4}[A-Z]$", ErrorMessage = "Invalid company PAN (e.g. AACCA1234F).")]
    public string? Pan { get; set; }
    [RegularExpression("^[A-Z]{4}[0-9]{5}[A-Z]$", ErrorMessage = "Invalid TAN (e.g. BLRA12345B).")]
    public string? Tan { get; set; }
    [RegularExpression("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", ErrorMessage = "Invalid GSTIN.")]
    public string? Gstin { get; set; }
    [MaxLength(150)] public string? SignatoryName { get; set; }
    [MaxLength(100)] public string? SignatoryDesignation { get; set; }
}
