using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

// ---------------------------------------------------------------- Question banks (TL / manager)
public record QuestionBankDto(
    int BankId,
    string Name,
    string? Description,
    bool IsActive,
    int ActiveQuestions,
    int TotalQuestions,
    string CreatedBy,
    DateTime CreatedAt,
    bool CanEdit,
    bool CanDelete = false);

public class QuestionBankRequest
{
    [Required, MaxLength(150)] public string Name { get; set; } = default!;
    [MaxLength(1000)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public record BankQuestionDto(
    int QuestionId,
    string Text,
    string OptionA,
    string OptionB,
    string? OptionC,
    string? OptionD,
    string CorrectOption,
    string Difficulty,
    bool IsActive,
    int TimesUsed);

public class BankQuestionRequest
{
    [Required, MaxLength(1000)] public string Text { get; set; } = default!;
    [Required, MaxLength(300)] public string OptionA { get; set; } = default!;
    [Required, MaxLength(300)] public string OptionB { get; set; } = default!;
    [MaxLength(300)] public string? OptionC { get; set; }
    [MaxLength(300)] public string? OptionD { get; set; }
    [Required, RegularExpression("^[ABCD]$", ErrorMessage = "Correct option must be A, B, C or D.")] public string CorrectOption { get; set; } = "A";
    [Required, RegularExpression("^(Easy|Medium|Hard)$")] public string Difficulty { get; set; } = "Medium";
    public bool IsActive { get; set; } = true;
}

public record QuestionBankDetailDto(QuestionBankDto Bank, IReadOnlyList<BankQuestionDto> Questions);

// ---------------------------------------------------------------- Candidate tests (recruiter)
public class AssignTestRequest
{
    [Range(1, int.MaxValue)] public int ApplicationId { get; set; }
    [Range(1, int.MaxValue)] public int BankId { get; set; }
    [Range(5, 100)] public int QuestionCount { get; set; } = 50;
    [Range(10, 240)] public int DurationMinutes { get; set; } = 60;
    [Range(1, 100)] public int PassPercent { get; set; } = 60;
    /// <summary>How long the link stays valid (the test must be started before then).</summary>
    [Range(1, 30)] public int ExpiresInDays { get; set; } = 3;
    public bool NotifyCandidate { get; set; } = true;
}

public record CandidateTestDto(
    int TestId,
    int ApplicationId,
    string JobTitle,
    int CandidateId,
    string CandidateName,
    int BankId,
    string BankName,
    int QuestionCount,
    int DurationMinutes,
    int PassPercent,
    DateTime ExpiresAt,
    string Status,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    int? CorrectAnswers,
    decimal? ScorePercent,
    bool? Passed,
    int Answered,
    string TestUrl,
    DateTime CreatedAt);

public record TestResultQuestionDto(
    int Number,
    string Text,
    string OptionA,
    string OptionB,
    string? OptionC,
    string? OptionD,
    string CorrectOption,
    string? SelectedOption,
    bool? IsCorrect,
    string Difficulty);

public record CandidateTestResultDto(CandidateTestDto Test, IReadOnlyList<TestResultQuestionDto> Questions);

// ---------------------------------------------------------------- Public (candidate, no login)
public record PublicOptionDto(string Key, string Text);

/// <summary>Never contains the correct answer.</summary>
public record PublicQuestionDto(int QuestionId, int Number, string Text, IReadOnlyList<PublicOptionDto> Options, string? Selected);

public record PublicTestDto(
    string CandidateName,
    string JobTitle,
    string CompanyName,
    string TestName,
    int QuestionCount,
    int DurationMinutes,
    DateTime ExpiresAt,
    string Status,
    int? RemainingSeconds,
    IReadOnlyList<PublicQuestionDto>? Questions);

public class PublicAnswer
{
    [Range(1, int.MaxValue)] public int QuestionId { get; set; }
    [RegularExpression("^[ABCD]$")] public string? Option { get; set; }
}

public class SaveAnswersRequest
{
    [MaxLength(200)] public List<PublicAnswer> Answers { get; set; } = [];
}
