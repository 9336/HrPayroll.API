using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record ItRequestDto(
    int RequestId,
    string Code,
    string RequestType,
    string Title,
    string Details,
    string Priority,
    DateOnly? RequiredBy,
    string Status,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string Department,
    string? ApproverName,
    string? ApprovedByName,
    DateTime? ApprovedAt,
    string? ApprovalRemarks,
    int? AssignedToUserId,
    string? AssignedToName,
    string? ResolutionNotes,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record ItRequestEventDto(long EventId, string Action, string? Note, string ByName, DateTime At);

/// <summary>A request with its timeline, plus what the caller may do with it.</summary>
public record ItRequestDetailDto(
    ItRequestDto Request,
    List<ItRequestEventDto> Events,
    bool CanCancel,
    bool CanApprove,
    bool CanWork,
    bool CanComment);

public class CreateItRequest
{
    [Required] public string RequestType { get; set; } = default!;
    [Required, MaxLength(200)] public string Title { get; set; } = default!;
    [Required, MaxLength(2000)] public string Details { get; set; } = default!;
    [Required] public string Priority { get; set; } = "Medium";
    public DateOnly? RequiredBy { get; set; }
}

public record ItRemarksRequest([MaxLength(1000)] string? Remarks);

public record ItCommentRequest([Required, MaxLength(1000)] string Text);

/// <summary>Assign to an IT team member (null = yourself).</summary>
public record ItAssignRequest(int? UserId);

public class ItQueueQuery
{
    /// <summary>A status, "Open" (approved or in progress, the default) or "All".</summary>
    public string? Status { get; set; } = "Open";
    public string? Type { get; set; }
    public string? Search { get; set; }
    public bool MineOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public record ItDeskStatsDto(int PendingManagerApproval, int AwaitingIt, int InProgress, int AssignedToMe, int CompletedThisMonth);

/// <summary>Numbers for the sidebar badges.</summary>
public record ItCountsDto(int MyOpen, int AwaitingMyApproval, int ItQueue);

public record ItStaffDto(int UserId, string Name);
