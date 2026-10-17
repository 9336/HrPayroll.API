namespace HrPayroll.Recruitment.Entities;

public static class TestStatus
{
    public const string Assigned = "Assigned";
    public const string InProgress = "InProgress";
    public const string Submitted = "Submitted";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}

/// <summary>A question bank ("branch") of multiple-choice questions, e.g. "Angular - Senior".</summary>
public class QuestionBank
{
    public int BankId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User CreatedBy { get; set; } = default!;
}

/// <summary>Multiple-choice question with 2-4 options (A-D) and one correct option.</summary>
public class BankQuestion
{
    public int QuestionId { get; set; }
    public int BankId { get; set; }
    public string Text { get; set; } = default!;
    public string OptionA { get; set; } = default!;
    public string OptionB { get; set; } = default!;
    public string? OptionC { get; set; }
    public string? OptionD { get; set; }
    /// <summary>"A", "B", "C" or "D".</summary>
    public string CorrectOption { get; set; } = "A";
    public string Difficulty { get; set; } = "Medium";
    public bool IsActive { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>An online test sent to a candidate for one of their applications.</summary>
public class CandidateTest
{
    public int TestId { get; set; }
    public int ApplicationId { get; set; }
    public int BankId { get; set; }
    /// <summary>Secret link token (the candidate has no login).</summary>
    public string Token { get; set; } = default!;
    public int QuestionCount { get; set; }
    public int DurationMinutes { get; set; }
    public int PassPercent { get; set; }
    /// <summary>Office local time; the test must be started before this.</summary>
    public DateTime ExpiresAt { get; set; }
    public string Status { get; set; } = TestStatus.Assigned;
    public DateTime? StartedAt { get; set; }
    public DateTime? DeadlineAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? CorrectAnswers { get; set; }
    public decimal? ScorePercent { get; set; }
    public bool? Passed { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public JobApplication Application { get; set; } = default!;
    public QuestionBank Bank { get; set; } = default!;
    public List<CandidateTestQuestion> Questions { get; set; } = [];
}

/// <summary>A question drawn for a test, in the order shown, with the candidate's answer.</summary>
public class CandidateTestQuestion
{
    public int TestId { get; set; }
    public int QuestionId { get; set; }
    public int SortOrder { get; set; }
    public string? SelectedOption { get; set; }
    public bool? IsCorrect { get; set; }

    public BankQuestion Question { get; set; } = default!;
}
