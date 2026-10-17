namespace HrPayroll.Performance.Entities;

public static class WorkTaskStatus
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string OnHold = "OnHold";
    public const string Completed = "Completed";
    public static readonly string[] All = [NotStarted, InProgress, OnHold, Completed];
}

/// <summary>
/// A task a team lead / manager assigns to someone in their team. The assignee can only report progress
/// (status and percentage); every report is kept in <see cref="WorkTaskUpdate"/>.
/// </summary>
public class WorkTask
{
    public int TaskId { get; set; }
    public string Title { get; set; } = default!;
    public string? Description { get; set; }
    public int AssigneeEmployeeId { get; set; }
    public int AssignedByUserId { get; set; }
    public string Priority { get; set; } = "Medium";
    public DateOnly StartDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string Status { get; set; } = WorkTaskStatus.NotStarted;
    public int PercentComplete { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Employee Assignee { get; set; } = default!;
    public User AssignedBy { get; set; } = default!;

    /// <summary>Human-readable task id, e.g. TSK-00042.</summary>
    public static string Code(int id) => $"TSK-{id:D5}";
}

public class WorkTaskUpdate
{
    public int UpdateId { get; set; }
    public int TaskId { get; set; }
    public DateOnly UpdateDate { get; set; }
    public string Status { get; set; } = default!;
    public int PercentComplete { get; set; }
    public string? Comment { get; set; }
    public int UpdatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User UpdatedBy { get; set; } = default!;
}
