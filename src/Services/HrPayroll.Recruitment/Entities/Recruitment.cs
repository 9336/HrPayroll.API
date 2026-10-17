namespace HrPayroll.Recruitment.Entities;

public static class JobStatus
{
    public const string Draft = "Draft";
    public const string Open = "Open";
    public const string OnHold = "OnHold";
    public const string Closed = "Closed";
    public static readonly string[] All = [Draft, Open, OnHold, Closed];
}

public static class ApplicationStage
{
    public const string Applied = "Applied";
    public const string Screening = "Screening";
    public const string Interview = "Interview";
    public const string Offer = "Offer";
    public const string Hired = "Hired";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    public static readonly string[] All = [Applied, Screening, Interview, Offer, Hired, Rejected, Withdrawn];
    public static readonly string[] Active = [Applied, Screening, Interview, Offer];
}

public static class InterviewStatus
{
    public const string Scheduled = "Scheduled";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string NoShow = "NoShow";
}

public static class ActivityType
{
    public const string Note = "Note";
    public const string Call = "Call";
    public const string Email = "Email";
    public const string Meeting = "Meeting";
    public const string Task = "Task";
    // logged automatically
    public const string Created = "Created";
    public const string Applied = "Applied";
    public const string StageChange = "StageChange";
    public const string Interview = "Interview";
    public const string Feedback = "Feedback";
    public const string Resume = "Resume";

    /// <summary>Types a recruiter can add by hand.</summary>
    public static readonly string[] Manual = [Note, Call, Email, Meeting, Task];
}

public class JobOpening
{
    public int JobId { get; set; }
    public string Title { get; set; } = default!;
    public int DepartmentId { get; set; }
    public string? Location { get; set; }
    public string EmploymentType { get; set; } = "FullTime";
    public int Openings { get; set; } = 1;
    public decimal? ExperienceMin { get; set; }
    public decimal? ExperienceMax { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string? Description { get; set; }
    public string? Skills { get; set; }
    public string Status { get; set; } = JobStatus.Draft;
    public int? HiringManagerEmployeeId { get; set; }
    public DateOnly? TargetDate { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Department Department { get; set; } = default!;
    public Employee? HiringManager { get; set; }
}

public class Candidate
{
    public int CandidateId { get; set; }
    public string FullName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
    public string? CurrentCompany { get; set; }
    public string? CurrentTitle { get; set; }
    public decimal? ExperienceYears { get; set; }
    public decimal? CurrentCtc { get; set; }
    public decimal? ExpectedCtc { get; set; }
    public int? NoticePeriodDays { get; set; }
    public string? Location { get; set; }
    public string Source { get; set; } = "Other";
    public int? ReferredByEmployeeId { get; set; }
    public string? Skills { get; set; }
    public string? LinkedInUrl { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Employee? ReferredBy { get; set; }
}

/// <summary>Resume file, kept apart so loading a candidate never loads the file.</summary>
public class CandidateResume
{
    public int CandidateId { get; set; }
    public byte[] Content { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public DateTime UploadedAt { get; set; }
}

/// <summary>A candidate applying to a job - moves through the hiring pipeline.</summary>
public class JobApplication
{
    public int ApplicationId { get; set; }
    public int JobId { get; set; }
    public int CandidateId { get; set; }
    public string Stage { get; set; } = ApplicationStage.Applied;
    public DateTime StageChangedAt { get; set; }
    public string? RejectionReason { get; set; }
    public decimal? OfferedCtc { get; set; }
    public DateOnly? JoiningDate { get; set; }
    public int? HiredEmployeeId { get; set; }
    public DateTime CreatedAt { get; set; }

    public JobOpening Job { get; set; } = default!;
    public Candidate Candidate { get; set; } = default!;
    public Employee? HiredEmployee { get; set; }
}

public class Interview
{
    public int InterviewId { get; set; }
    public int ApplicationId { get; set; }
    public string Round { get; set; } = default!;
    /// <summary>Office local time.</summary>
    public DateTime ScheduledAt { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Mode { get; set; } = "Video";
    public string? Location { get; set; }
    public int InterviewerEmployeeId { get; set; }
    public string Status { get; set; } = InterviewStatus.Scheduled;
    public byte? Rating { get; set; }
    public string? Recommendation { get; set; }
    public string? Feedback { get; set; }
    public DateTime? FeedbackAt { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public JobApplication Application { get; set; } = default!;
    public Employee Interviewer { get; set; } = default!;
}

/// <summary>Timeline entry for a candidate (manual note / call / task, or logged automatically).</summary>
public class RecruitmentActivity
{
    public int ActivityId { get; set; }
    public int CandidateId { get; set; }
    public int? ApplicationId { get; set; }
    public string Type { get; set; } = ActivityType.Note;
    public string Summary { get; set; } = default!;
    public string? Details { get; set; }
    public DateOnly? FollowUpOn { get; set; }
    public bool IsDone { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Candidate Candidate { get; set; } = default!;
    public JobApplication? Application { get; set; }
    public User? CreatedBy { get; set; }
}
