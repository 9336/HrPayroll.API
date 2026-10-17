using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Employees.Entities;

/// <summary>
/// Employee photo for the ID card (PNG / JPEG / WebP). Kept in its own table so loading an employee record never drags
/// the image bytes along. Other services (the selfie face check) read it through GET /internal/employees/{id}/photo.
/// </summary>
public class EmployeePhoto
{
    public int EmployeeId { get; set; }
    public byte[] Photo { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Copy of a salary revision (owned by the Payroll service, from hr.salaries) - only what the employee list needs:
/// the monthly gross in force today.
/// </summary>
public class SalarySnapshot
{
    public int SalaryStructureId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal GrossMonthly { get; set; }
}

/// <summary>Multipart form fields sent with an uploaded document.</summary>
public class EmployeeUploadForm
{
    [Required, MaxLength(60)] public string Category { get; set; } = default!;
    [MaxLength(150)] public string? Title { get; set; }
    [Required] public IFormFile File { get; set; } = default!;
}
