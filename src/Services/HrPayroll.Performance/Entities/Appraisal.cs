namespace HrPayroll.Performance.Entities;

public static class AppraisalStatus
{
    public const string NotStarted = "NotStarted";   // no row yet (reporting only)
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Reviewed = "Reviewed";
}

public static class QuestionKind
{
    public const string Rating = "Rating";   // 1-5 scale + optional comment
    public const string Text = "Text";       // written answer
}

/// <summary>An appraisal round. Employees can fill the form only between <see cref="OpensOn"/> and <see cref="ClosesOn"/>.</summary>
public class AppraisalCycle
{
    public int CycleId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public DateOnly OpensOn { get; set; }
    public DateOnly ClosesOn { get; set; }
    public DateOnly? ReviewDueOn { get; set; }
    public bool IsPublished { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<AppraisalQuestion> Questions { get; set; } = [];
}

public class AppraisalQuestion
{
    public int QuestionId { get; set; }
    public int CycleId { get; set; }
    public int SortOrder { get; set; }
    public string? Section { get; set; }
    public string Text { get; set; } = default!;
    public string Kind { get; set; } = QuestionKind.Rating;
    public bool IsRequired { get; set; } = true;
}

/// <summary>One employee's appraisal in a cycle (self-assessment + reviewer assessment).</summary>
public class Appraisal
{
    public int AppraisalId { get; set; }
    public int CycleId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>Reporting manager at the time of submission.</summary>
    public int? ReviewerEmployeeId { get; set; }
    public string Status { get; set; } = AppraisalStatus.Draft;
    public string? SelfSummary { get; set; }
    public string? ReviewerSummary { get; set; }
    public decimal? FinalRating { get; set; }
    public decimal? RecommendedHikePercent { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public AppraisalCycle Cycle { get; set; } = default!;
    public Employee Employee { get; set; } = default!;
    public Employee? Reviewer { get; set; }
    public User? ReviewedBy { get; set; }
    public List<AppraisalAnswer> Answers { get; set; } = [];
}

public class AppraisalAnswer
{
    public int AppraisalId { get; set; }
    public int QuestionId { get; set; }
    public byte? SelfRating { get; set; }
    public string? SelfComment { get; set; }
    public byte? ReviewerRating { get; set; }
    public string? ReviewerComment { get; set; }
}
