using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record AppraisalQuestionDto(int QuestionId, int SortOrder, string? Section, string Text, string Kind, bool IsRequired);

public record CycleCountsDto(int Eligible, int NotStarted, int Draft, int Submitted, int Reviewed);

/// <summary>State: Draft (not published), Scheduled (opens later), Open, Closed.</summary>
public record AppraisalCycleDto(
    int CycleId,
    string Name,
    string? Description,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly OpensOn,
    DateOnly ClosesOn,
    DateOnly? ReviewDueOn,
    bool IsPublished,
    string State,
    IReadOnlyList<AppraisalQuestionDto> Questions,
    CycleCountsDto Counts);

public class CycleRequest
{
    [Required, MaxLength(150)] public string Name { get; set; } = default!;
    [MaxLength(2000)] public string? Description { get; set; }
    [Required] public DateOnly PeriodFrom { get; set; }
    [Required] public DateOnly PeriodTo { get; set; }
    /// <summary>First day employees can fill the form.</summary>
    [Required] public DateOnly OpensOn { get; set; }
    /// <summary>Last day employees can submit.</summary>
    [Required] public DateOnly ClosesOn { get; set; }
    public DateOnly? ReviewDueOn { get; set; }
    /// <summary>On create: copy the form from this cycle (otherwise a standard form is used).</summary>
    public int? CopyQuestionsFromCycleId { get; set; }
}

public class QuestionRequest
{
    [MaxLength(100)] public string? Section { get; set; }
    [Required, MaxLength(500)] public string Text { get; set; } = default!;
    [Required, RegularExpression("^(Rating|Text)$")] public string Kind { get; set; } = "Rating";
    public bool IsRequired { get; set; } = true;
}

public record ProgressRowDto(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    string Status,
    int? AppraisalId,
    string? ReviewerName,
    DateTime? SubmittedAt,
    decimal? FinalRating,
    /// <summary>Feedback dated in the review period.</summary>
    int Appreciations = 0,
    int Complaints = 0);

public record MyCycleDto(
    int CycleId,
    string Name,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly OpensOn,
    DateOnly ClosesOn,
    string State,
    string MyStatus,
    decimal? FinalRating);

public record AppraisalAnswerDto(int QuestionId, int? SelfRating, string? SelfComment, int? ReviewerRating, string? ReviewerComment);

/// <summary>The appraisal form as seen by the employee (own) or a reviewer.</summary>
public record AppraisalFormDto(
    int? AppraisalId,
    int CycleId,
    string CycleName,
    string? CycleDescription,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly OpensOn,
    DateOnly ClosesOn,
    string CycleState,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string Designation,
    string Department,
    string Status,
    bool CanEditSelf,
    bool CanReview,
    bool ShowReview,          // reviewer part visible (to the employee only once reviewed)
    IReadOnlyList<AppraisalQuestionDto> Questions,
    IReadOnlyList<AppraisalAnswerDto> Answers,
    string? SelfSummary,
    string? ReviewerSummary,
    decimal? FinalRating,
    decimal? RecommendedHikePercent,   // never sent to the employee
    string? ReviewerName,
    DateTime? SubmittedAt,
    DateTime? ReviewedAt,
    /// <summary>Appreciations and complaints dated in the review period (the employee only sees shared ones).</summary>
    FeedbackPeriodDto? Feedback = null);

public class AnswerInput
{
    [Range(1, int.MaxValue)] public int QuestionId { get; set; }
    [Range(1, 5)] public int? Rating { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
}

public class SelfAppraisalRequest
{
    public List<AnswerInput> Answers { get; set; } = [];
    [MaxLength(4000)] public string? SelfSummary { get; set; }
    /// <summary>false = save draft, true = submit to the reviewer.</summary>
    public bool Submit { get; set; }
}

public class ReviewRequest
{
    public List<AnswerInput> Answers { get; set; } = [];
    [MaxLength(4000)] public string? ReviewerSummary { get; set; }
    [Range(1, 5)] public decimal? FinalRating { get; set; }
    [Range(0, 100)] public decimal? RecommendedHikePercent { get; set; }
    /// <summary>false = save progress, true = complete the review (the employee is notified).</summary>
    public bool Complete { get; set; }
}

public record ReviewListItemDto(
    int AppraisalId,
    int CycleId,
    string CycleName,
    int EmployeeId,
    string EmployeeName,
    string Designation,
    string Status,
    DateTime? SubmittedAt,
    decimal? FinalRating);

public record EmailMessageDto(
    long EmailId,
    string ToAddress,
    string? ToName,
    string Subject,
    string Category,
    string Status,
    DateTime NotBefore,
    DateTime CreatedAt,
    DateTime? SentAt,
    int Attempts,
    string? LastError);

public record EmailStatusDto(bool SmtpConfigured, string FromAddress, int Pending, int Failed);
