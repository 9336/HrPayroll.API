using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record FeedbackDto(
    int FeedbackId,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string Designation,
    string Kind,
    string Source,
    string GivenBy,
    string? Organisation,
    DateOnly EventDate,
    string Title,
    string Details,
    string? Severity,
    bool SharedWithEmployee,
    bool IsResolved,
    string? ResolutionNote,
    DateTime? ResolvedAt,
    string? ResolvedBy,
    string? EmployeeResponse,
    DateTime? RespondedAt,
    string? AttachmentName,
    string RecordedBy,
    DateTime CreatedAt,
    /// <summary>What the caller may do.</summary>
    bool CanEdit,
    bool CanRespond,
    bool CanDelete = false);

public class EmployeeFeedbackRequest
{
    [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
    [Required] public string Kind { get; set; } = default!;
    [Required] public string Source { get; set; } = default!;
    [Required, MaxLength(150)] public string GivenBy { get; set; } = default!;
    [MaxLength(150)] public string? Organisation { get; set; }
    [Required] public DateOnly EventDate { get; set; }
    [Required, MaxLength(200)] public string Title { get; set; } = default!;
    [Required, MaxLength(2000)] public string Details { get; set; } = default!;
    public string? Severity { get; set; }
    public bool SharedWithEmployee { get; set; }
}

public class FeedbackQuery
{
    public int? EmployeeId { get; set; }
    public string? Kind { get; set; }
    /// <summary>"Open" = unresolved complaints.</summary>
    public string? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Search { get; set; }
}

public record FeedbackNoteRequest([MaxLength(1000)] string? Text);

public record FeedbackSummaryDto(int Appreciations, int FromClients, int Complaints, int OpenComplaints, int SeriousComplaints);

/// <summary>Feedback list + counts for a period (e.g. an appraisal's review period).</summary>
public record FeedbackPeriodDto(FeedbackSummaryDto Summary, IReadOnlyList<FeedbackDto> Items);
