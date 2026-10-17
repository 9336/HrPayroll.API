namespace HrPayroll.Payroll.Entities;

public static class PromotionStatus
{
    /// <summary>Effective date still ahead: the employee record changes on that day.</summary>
    public const string Scheduled = "Scheduled";
    public const string Applied = "Applied";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A promotion: new designation (and optionally department, reporting manager and a salary increment) from an
/// effective date. The before / after values are kept, so the history reads correctly later.
/// </summary>
public class Promotion
{
    public int PromotionId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string OldDesignation { get; set; } = default!;
    public string NewDesignation { get; set; } = default!;
    public int OldDepartmentId { get; set; }
    public int NewDepartmentId { get; set; }
    public int? OldManagerId { get; set; }
    public int? NewManagerId { get; set; }
    public decimal? IncrementPercent { get; set; }
    public decimal? OldMonthlyGross { get; set; }
    public decimal? NewMonthlyGross { get; set; }
    /// <summary>Salary revision created for the increment (effective from <see cref="SalaryEffectiveFrom"/>).</summary>
    public int? SalaryStructureId { get; set; }
    public DateOnly? SalaryEffectiveFrom { get; set; }
    public string? Remarks { get; set; }
    public string Status { get; set; } = PromotionStatus.Scheduled;
    public int? DocumentId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }

    public Employee Employee { get; set; } = default!;
    public Department OldDepartment { get; set; } = default!;
    public Department NewDepartment { get; set; } = default!;
    public Employee? OldManager { get; set; }
    public Employee? NewManager { get; set; }
    public User CreatedBy { get; set; } = default!;
}
