using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

// ---------------------------------------------------------------- Jobs
public record StageCountDto(string Stage, int Count);

public record JobDto(
    int JobId,
    string Title,
    int DepartmentId,
    string Department,
    string? Location,
    string EmploymentType,
    int Openings,
    decimal? ExperienceMin,
    decimal? ExperienceMax,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? Description,
    string? Skills,
    string Status,
    int? HiringManagerEmployeeId,
    string? HiringManager,
    DateOnly? TargetDate,
    DateTime CreatedAt,
    int Applicants,
    int Active,
    int Hired,
    IReadOnlyList<StageCountDto> Stages);

public class JobRequest
{
    [Required, MaxLength(150)] public string Title { get; set; } = default!;
    [Range(1, int.MaxValue)] public int DepartmentId { get; set; }
    [MaxLength(100)] public string? Location { get; set; }
    [Required, RegularExpression("^(FullTime|PartTime|Contract|Intern)$")] public string EmploymentType { get; set; } = "FullTime";
    [Range(1, 500)] public int Openings { get; set; } = 1;
    [Range(0, 50)] public decimal? ExperienceMin { get; set; }
    [Range(0, 50)] public decimal? ExperienceMax { get; set; }
    [Range(0, 100_000_000)] public decimal? SalaryMin { get; set; }
    [Range(0, 100_000_000)] public decimal? SalaryMax { get; set; }
    [MaxLength(4000)] public string? Description { get; set; }
    [MaxLength(500)] public string? Skills { get; set; }
    [Required, RegularExpression("^(Draft|Open|OnHold|Closed)$")] public string Status { get; set; } = "Draft";
    [Range(1, int.MaxValue)] public int? HiringManagerEmployeeId { get; set; }
    public DateOnly? TargetDate { get; set; }
}

// ---------------------------------------------------------------- Candidates
public record CandidateListItemDto(
    int CandidateId,
    string FullName,
    string Email,
    string? Phone,
    string? CurrentTitle,
    string? CurrentCompany,
    decimal? ExperienceYears,
    string Source,
    bool HasResume,
    DateTime CreatedAt,
    string? LatestJob,
    string? LatestStage);

public class CandidateRequest
{
    [Required, MaxLength(150)] public string FullName { get; set; } = default!;
    [Required, EmailAddress, MaxLength(200)] public string Email { get; set; } = default!;
    [RegularExpression(@"^[0-9+\- ]{7,20}$", ErrorMessage = "Enter a valid phone number.")] public string? Phone { get; set; }
    [MaxLength(150)] public string? CurrentCompany { get; set; }
    [MaxLength(150)] public string? CurrentTitle { get; set; }
    [Range(0, 60)] public decimal? ExperienceYears { get; set; }
    [Range(0, 100_000_000)] public decimal? CurrentCtc { get; set; }
    [Range(0, 100_000_000)] public decimal? ExpectedCtc { get; set; }
    [Range(0, 365)] public int? NoticePeriodDays { get; set; }
    [MaxLength(100)] public string? Location { get; set; }
    [Required, RegularExpression("^(Referral|LinkedIn|JobPortal|Website|Agency|WalkIn|Campus|Other)$")] public string Source { get; set; } = "Other";
    [Range(1, int.MaxValue)] public int? ReferredByEmployeeId { get; set; }
    [MaxLength(500)] public string? Skills { get; set; }
    [Url, MaxLength(300)] public string? LinkedInUrl { get; set; }
    /// <summary>On create: also apply the candidate to this job.</summary>
    public int? ApplyToJobId { get; set; }
}

public record ApplicationDto(
    int ApplicationId,
    int JobId,
    string JobTitle,
    string JobStatus,
    int CandidateId,
    string CandidateName,
    string Stage,
    DateTime StageChangedAt,
    string? RejectionReason,
    decimal? OfferedCtc,
    DateOnly? JoiningDate,
    int? HiredEmployeeId,
    DateTime CreatedAt,
    int Interviews,
    decimal? AvgRating);

public record CandidateDetailDto(
    int CandidateId,
    string FullName,
    string Email,
    string? Phone,
    string? CurrentCompany,
    string? CurrentTitle,
    decimal? ExperienceYears,
    decimal? CurrentCtc,
    decimal? ExpectedCtc,
    int? NoticePeriodDays,
    string? Location,
    string Source,
    int? ReferredByEmployeeId,
    string? ReferredBy,
    string? Skills,
    string? LinkedInUrl,
    string? ResumeFileName,
    DateTime CreatedAt,
    IReadOnlyList<ApplicationDto> Applications,
    IReadOnlyList<InterviewDto> Interviews,
    IReadOnlyList<ActivityDto> Activities);

// ---------------------------------------------------------------- Pipeline
public class ApplyRequest
{
    [Range(1, int.MaxValue)] public int CandidateId { get; set; }
    [Range(1, int.MaxValue)] public int JobId { get; set; }
}

public class StageRequest
{
    [Required, RegularExpression("^(Applied|Screening|Interview|Offer|Hired|Rejected|Withdrawn)$")] public string Stage { get; set; } = default!;
    [MaxLength(500)] public string? Reason { get; set; }
    [Range(0, 100_000_000)] public decimal? OfferedCtc { get; set; }
    public DateOnly? JoiningDate { get; set; }
    /// <summary>Rejected only: email the candidate a polite rejection.</summary>
    public bool NotifyCandidate { get; set; }
}

public record LinkEmployeeRequest([Range(1, int.MaxValue)] int EmployeeId);

// ---------------------------------------------------------------- Interviews
public record InterviewDto(
    int InterviewId,
    int ApplicationId,
    int CandidateId,
    string CandidateName,
    string JobTitle,
    string Round,
    DateTime ScheduledAt,
    int DurationMinutes,
    string Mode,
    string? Location,
    int InterviewerEmployeeId,
    string Interviewer,
    string Status,
    int? Rating,
    string? Recommendation,
    string? Feedback,
    DateTime? FeedbackAt,
    bool HasResume);

public class InterviewRequest
{
    [Range(1, int.MaxValue)] public int ApplicationId { get; set; }
    [Required, MaxLength(100)] public string Round { get; set; } = default!;
    [Required] public DateTime ScheduledAt { get; set; }
    [Range(15, 480)] public int DurationMinutes { get; set; } = 60;
    [Required, RegularExpression("^(InPerson|Video|Phone)$")] public string Mode { get; set; } = "Video";
    [MaxLength(300)] public string? Location { get; set; }
    [Range(1, int.MaxValue)] public int InterviewerEmployeeId { get; set; }
    public bool NotifyCandidate { get; set; } = true;
}

/// <summary>Rating, recommendation and feedback are required unless the candidate did not show up.</summary>
public class FeedbackRequest
{
    [Range(1, 5)] public int? Rating { get; set; }
    [RegularExpression("^(StrongHire|Hire|NoHire|StrongNoHire)$")] public string? Recommendation { get; set; }
    [MaxLength(4000)] public string? Feedback { get; set; }
    /// <summary>Mark the candidate as a no-show instead of giving feedback.</summary>
    public bool NoShow { get; set; }
}

public record CancelInterviewRequest([MaxLength(300)] string? Reason, bool NotifyCandidate = true);

// ---------------------------------------------------------------- Activity
public record ActivityDto(
    int ActivityId,
    int CandidateId,
    string CandidateName,
    int? ApplicationId,
    string? JobTitle,
    string Type,
    string Summary,
    string? Details,
    DateOnly? FollowUpOn,
    bool IsDone,
    string? CreatedBy,
    DateTime CreatedAt);

public class ActivityRequest
{
    [Range(1, int.MaxValue)] public int CandidateId { get; set; }
    public int? ApplicationId { get; set; }
    [Required, RegularExpression("^(Note|Call|Email|Meeting|Task)$")] public string Type { get; set; } = "Note";
    [Required, MaxLength(200)] public string Summary { get; set; } = default!;
    [MaxLength(4000)] public string? Details { get; set; }
    public DateOnly? FollowUpOn { get; set; }
}

// ---------------------------------------------------------------- Dashboard
public record RecruitmentDashboardDto(
    int OpenJobs,
    int OpenPositions,
    int ActiveCandidates,
    int InterviewsThisWeek,
    int OffersOut,
    int HiresThisMonth,
    IReadOnlyList<StageCountDto> Pipeline,
    IReadOnlyList<InterviewDto> UpcomingInterviews,
    IReadOnlyList<ActivityDto> FollowUps,
    IReadOnlyList<ActivityDto> RecentActivity);
