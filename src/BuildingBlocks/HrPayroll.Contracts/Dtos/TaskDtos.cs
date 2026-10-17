using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record WorkTaskDto(
    int TaskId,
    string TaskCode,
    string Title,
    string? Description,
    int AssigneeEmployeeId,
    string AssigneeName,
    string AssigneeDesignation,
    string AssignedBy,
    string Priority,
    DateOnly StartDate,
    DateOnly DueDate,
    string Status,
    int PercentComplete,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateOnly? LastUpdateDate,
    bool UpdatedToday,
    bool IsOverdue,
    bool CanEdit,          // caller is the assigner's side (manager / tasks: Edit)
    bool CanReport,        // caller is the assignee
    bool CanDelete = false);

public record WorkTaskUpdateDto(int UpdateId, DateOnly UpdateDate, string Status, int PercentComplete, string? Comment, string UpdatedBy, DateTime CreatedAt);

public record WorkTaskDetailDto(WorkTaskDto Task, IReadOnlyList<WorkTaskUpdateDto> Updates);

/// <summary>Created / edited by a team lead or manager.</summary>
public class WorkTaskRequest
{
    [Required, MaxLength(200)] public string Title { get; set; } = default!;
    [MaxLength(2000)] public string? Description { get; set; }
    [Range(1, int.MaxValue)] public int AssigneeEmployeeId { get; set; }
    [Required, RegularExpression("^(Low|Medium|High|Critical)$")] public string Priority { get; set; } = "Medium";
    [Required] public DateOnly StartDate { get; set; }
    [Required] public DateOnly DueDate { get; set; }
}

/// <summary>The only thing an assignee can change: status and percentage (plus an optional comment).</summary>
public class TaskProgressRequest
{
    [Required, RegularExpression("^(NotStarted|InProgress|OnHold|Completed)$")] public string Status { get; set; } = default!;
    [Range(0, 100)] public int PercentComplete { get; set; }
    [MaxLength(1000)] public string? Comment { get; set; }
}

public record TaskQuery(int? AssigneeId, string? Status, DateOnly? DueFrom, DateOnly? DueTo, string? Search, bool NotUpdatedToday = false, bool OverdueOnly = false);

public record TaskSummaryDto(int Total, int NotStarted, int InProgress, int OnHold, int Completed, int Overdue, int NotUpdatedToday);

public record TeamTasksDto(TaskSummaryDto Summary, IReadOnlyList<WorkTaskDto> Tasks);
