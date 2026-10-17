namespace HrPayroll.Payroll.Entities;

public static class DocumentTypes
{
    public const string OfferLetter = "OfferLetter";
    public const string RelievingLetter = "RelievingLetter";
    public const string Form16 = "Form16";
    public const string PromotionLetter = "PromotionLetter";
}

/// <summary>A document released to an employee. <see cref="Data"/> is a JSON snapshot of its contents.</summary>
public class EmployeeDocument
{
    public int DocumentId { get; set; }
    public int EmployeeId { get; set; }
    public string DocumentType { get; set; } = default!;
    public int? FinancialYear { get; set; }
    public string Title { get; set; } = default!;
    public DateOnly IssueDate { get; set; }
    public string Data { get; set; } = default!;
    public DateTime ReleasedOn { get; set; }
    public int? ReleasedByUserId { get; set; }

    public Employee Employee { get; set; } = default!;
    public User? ReleasedBy { get; set; }
}

/// <summary>
/// A financial year (April - March) that has been closed: its payroll and salary revisions can no longer change
/// until it is reopened. <see cref="FinancialYear"/> is the first calendar year (2025 = FY 2025-26).
/// </summary>
public class ClosedFinancialYear
{
    public int FinancialYear { get; set; }
    public DateTime ClosedAt { get; set; }
    public int ClosedByUserId { get; set; }
    public string? Notes { get; set; }

    public User ClosedBy { get; set; } = default!;
}
