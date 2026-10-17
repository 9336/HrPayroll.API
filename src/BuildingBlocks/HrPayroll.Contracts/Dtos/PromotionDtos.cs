using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record PromotionDto(
    int PromotionId,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    DateOnly EffectiveDate,
    string OldDesignation,
    string NewDesignation,
    string OldDepartment,
    string NewDepartment,
    string? OldManager,
    string? NewManager,
    decimal? IncrementPercent,
    decimal? OldMonthlyGross,
    decimal? NewMonthlyGross,
    DateOnly? SalaryEffectiveFrom,
    string? Remarks,
    string Status,
    int? DocumentId,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? AppliedAt);

/// <summary>An employee's current position, to pre-fill the promotion form.</summary>
public record PromotionContextDto(
    int EmployeeId,
    string EmployeeName,
    string Designation,
    int DepartmentId,
    string Department,
    int? ManagerId,
    string? ManagerName,
    DateOnly DateOfJoining,
    /// <summary>Only for users who can manage salaries.</summary>
    decimal? CurrentMonthlyGross,
    DateOnly? LastPromotionDate,
    string? LastPromotionTo,
    bool HasScheduledPromotion);

public class CreatePromotionRequest
{
    [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
    [Required, MaxLength(100)] public string NewDesignation { get; set; } = default!;
    /// <summary>Null = stays in the same department.</summary>
    [Range(1, int.MaxValue)] public int? NewDepartmentId { get; set; }
    /// <summary>Null = same reporting manager.</summary>
    [Range(1, int.MaxValue)] public int? NewManagerId { get; set; }
    [Required] public DateOnly EffectiveDate { get; set; }
    /// <summary>Salary increment in percent (needs Salaries: Add). Every pay component goes up by this much.</summary>
    [Range(0, 200)] public decimal? IncrementPercent { get; set; }
    [MaxLength(1000)] public string? Remarks { get; set; }
    /// <summary>Release a promotion letter to the employee (in My Documents).</summary>
    public bool IssueLetter { get; set; } = true;
}

public record PromotionLetterData(
    string EmployeeName,
    string EmployeeCode,
    string PreviousDesignation,
    string NewDesignation,
    string Department,
    string? PreviousDepartment,
    DateOnly EffectiveDate,
    string? ReportingTo,
    decimal? IncrementPercent,
    decimal? NewMonthlyGross,
    decimal? NewAnnualCtc,
    DateOnly? SalaryEffectiveFrom,
    string? Message);
