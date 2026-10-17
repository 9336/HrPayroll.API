using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Performance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Performance.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Performance.Services;

/// <summary>Who is acting: team leads / managers manage their reporting line; <see cref="All"/> = rights over everyone's tasks.</summary>
public record TaskActor(int UserId, int? EmployeeId, Rights All);

/// <summary>
/// Daily task management. Team leads / managers create tasks (with a Task ID) for people in their reporting
/// line; the assignee can only report status and percentage complete. Each report is kept as a dated update,
/// so managers can see daily progress and who has not updated today.
/// </summary>
public class TaskService(PerformanceDbContext db, OrgService org, EmailQueue mail, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Queries
    public async Task<List<WorkTaskDto>> GetMyTasksAsync(int employeeId, TaskActor actor, CancellationToken ct) =>
        await Project(db.WorkTasks.AsNoTracking().Where(t => t.AssigneeEmployeeId == employeeId)
                .OrderBy(t => t.Status == WorkTaskStatus.Completed).ThenBy(t => t.DueDate).ThenBy(t => t.TaskId), actor,
                await TeamAsync(actor, actor.All.Edit, ct), await TeamAsync(actor, actor.All.Delete, ct))
            .ToListAsync(ct);

    public async Task<TeamTasksDto> GetTeamTasksAsync(TaskActor actor, TaskQuery q, CancellationToken ct)
    {
        var team = await TeamAsync(actor, actor.All.View, ct);
        var today = Today;
        var query = db.WorkTasks.AsNoTracking();
        if (team is not null) query = query.Where(t => team.Contains(t.AssigneeEmployeeId));

        // Summary covers the whole team (before filters).
        var summary = await query.GroupBy(_ => 1).Select(g => new TaskSummaryDto(
            g.Count(),
            g.Count(t => t.Status == WorkTaskStatus.NotStarted),
            g.Count(t => t.Status == WorkTaskStatus.InProgress),
            g.Count(t => t.Status == WorkTaskStatus.OnHold),
            g.Count(t => t.Status == WorkTaskStatus.Completed),
            g.Count(t => t.Status != WorkTaskStatus.Completed && t.DueDate < today),
            g.Count(t => t.Status != WorkTaskStatus.Completed && t.StartDate <= today &&
                         !db.WorkTaskUpdates.Any(u => u.TaskId == t.TaskId && u.UpdateDate == today))))
            .FirstOrDefaultAsync(ct) ?? new TaskSummaryDto(0, 0, 0, 0, 0, 0, 0);

        if (q.AssigneeId is int a) query = query.Where(t => t.AssigneeEmployeeId == a);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(t => t.Status == q.Status);
        if (q.DueFrom is DateOnly from) query = query.Where(t => t.DueDate >= from);
        if (q.DueTo is DateOnly to) query = query.Where(t => t.DueDate <= to);
        if (q.OverdueOnly) query = query.Where(t => t.Status != WorkTaskStatus.Completed && t.DueDate < today);
        if (q.NotUpdatedToday)
            query = query.Where(t => t.Status != WorkTaskStatus.Completed && t.StartDate <= today &&
                                     !db.WorkTaskUpdates.Any(u => u.TaskId == t.TaskId && u.UpdateDate == today));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            var idSearch = ParseCode(s);
            query = query.Where(t => t.Title.Contains(s) || (idSearch != null && t.TaskId == idSearch));
        }

        var tasks = await Project(query.OrderBy(t => t.Status == WorkTaskStatus.Completed).ThenBy(t => t.DueDate).ThenBy(t => t.TaskId)
            .Take(500), actor, await TeamAsync(actor, actor.All.Edit, ct), await TeamAsync(actor, actor.All.Delete, ct)).ToListAsync(ct);
        return new TeamTasksDto(summary, tasks);
    }

    public async Task<WorkTaskDetailDto> GetAsync(int id, TaskActor actor, CancellationToken ct)
    {
        var task = await db.WorkTasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == id, ct) ?? throw new NotFoundException("Task not found.");
        var team = await TeamAsync(actor, actor.All.View, ct);
        if (task.AssigneeEmployeeId != actor.EmployeeId && !(team is null || team.Contains(task.AssigneeEmployeeId)))
            throw new ForbiddenException("This task is not yours or your team's.");
        var dto = await GetDtoAsync(id, actor, ct);
        var updates = await db.WorkTaskUpdates.AsNoTracking().Where(u => u.TaskId == id)
            .OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.UpdateId)
            .Select(u => new WorkTaskUpdateDto(u.UpdateId, u.UpdateDate, u.Status, u.PercentComplete, u.Comment,
                u.UpdatedBy.Employee != null ? u.UpdatedBy.Employee.FirstName + " " + u.UpdatedBy.Employee.LastName : u.UpdatedBy.Username,
                u.CreatedAt))
            .ToListAsync(ct);
        return new WorkTaskDetailDto(dto, updates);
    }

    /// <summary>People the caller can assign tasks to.</summary>
    public async Task<List<CalendarPersonDto>> GetAssignableAsync(TaskActor actor, CancellationToken ct)
    {
        var team = await TeamAsync(actor, actor.All.Add, ct);
        var q = db.Employees.AsNoTracking().Where(e => e.IsActive);
        if (team is not null) q = q.Where(e => team.Contains(e.EmployeeId));
        return await q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new CalendarPersonDto(e.EmployeeId, e.FirstName + " " + e.LastName, e.Designation, e.Department.Name))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ Manage (TL / manager)
    public async Task<WorkTaskDto> CreateAsync(TaskActor actor, WorkTaskRequest req, CancellationToken ct)
    {
        await EnsureCanAssignAsync(actor, req.AssigneeEmployeeId, actor.All.Add, ct);
        ValidateDates(req);
        var task = new WorkTask { AssignedByUserId = actor.UserId, Status = WorkTaskStatus.NotStarted, PercentComplete = 0 };
        Apply(task, req);
        db.WorkTasks.Add(task);
        await db.SaveChangesAsync(ct);
        await NotifyAssigneeAsync(task, reassigned: false, ct);
        await db.SaveChangesAsync(ct);
        return await GetDtoAsync(task.TaskId, actor, ct);
    }

    public async Task<WorkTaskDto> UpdateAsync(int id, TaskActor actor, WorkTaskRequest req, CancellationToken ct)
    {
        var task = await LoadManagedAsync(id, actor, actor.All.Edit, ct);
        await EnsureCanAssignAsync(actor, req.AssigneeEmployeeId, actor.All.Edit, ct);
        ValidateDates(req, task.StartDate);
        var reassigned = task.AssigneeEmployeeId != req.AssigneeEmployeeId;
        Apply(task, req);
        task.UpdatedAt = time.GetUtcNow().UtcDateTime;
        if (reassigned) await NotifyAssigneeAsync(task, reassigned: true, ct);
        await db.SaveChangesAsync(ct);
        return await GetDtoAsync(id, actor, ct);
    }

    public async Task DeleteAsync(int id, TaskActor actor, CancellationToken ct)
    {
        var task = await LoadManagedAsync(id, actor, actor.All.Delete, ct);
        db.WorkTasks.Remove(task);   // updates cascade
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Progress (assignee only)
    public async Task<WorkTaskDto> ReportProgressAsync(int id, TaskActor actor, TaskProgressRequest req, CancellationToken ct)
    {
        var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.TaskId == id, ct) ?? throw new NotFoundException("Task not found.");
        if (task.AssigneeEmployeeId != actor.EmployeeId) throw new ForbiddenException("Only the person the task is assigned to can update its progress.");

        // Keep status and percentage consistent.
        var status = req.Status;
        var percent = req.PercentComplete;
        if (status == WorkTaskStatus.Completed) percent = 100;
        else if (percent == 100) status = WorkTaskStatus.Completed;
        else if (status == WorkTaskStatus.NotStarted && percent > 0) status = WorkTaskStatus.InProgress;

        if (status == task.Status && percent == task.PercentComplete && string.IsNullOrWhiteSpace(req.Comment))
            throw new AppException("Nothing changed - update the status or percentage, or add a comment.");

        task.Status = status;
        task.PercentComplete = percent;
        task.UpdatedAt = time.GetUtcNow().UtcDateTime;
        task.CompletedAt = status == WorkTaskStatus.Completed ? task.CompletedAt ?? task.UpdatedAt : null;
        db.WorkTaskUpdates.Add(new WorkTaskUpdate
        {
            TaskId = id, UpdateDate = Today, Status = status, PercentComplete = percent,
            Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim(), UpdatedByUserId = actor.UserId,
        });
        await db.SaveChangesAsync(ct);
        return await GetDtoAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Helpers
    /// <summary>Employee ids the caller manages (null = everyone, when the role grants <paramref name="all"/>).</summary>
    private async Task<HashSet<int>?> TeamAsync(TaskActor actor, bool all, CancellationToken ct)
    {
        if (all) return null;
        return actor.EmployeeId is int me ? await org.GetSubordinateIdsAsync(me, ct) : [];
    }

    private async Task EnsureCanAssignAsync(TaskActor actor, int assigneeId, bool all, CancellationToken ct)
    {
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == assigneeId && e.IsActive, ct))
            throw new AppException("The assignee must be an active employee.");
        var team = await TeamAsync(actor, all, ct);
        if (team is not null && !team.Contains(assigneeId))
            throw new ForbiddenException("You can only assign tasks to people in your team.");
    }

    private async Task<WorkTask> LoadManagedAsync(int id, TaskActor actor, bool all, CancellationToken ct)
    {
        var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.TaskId == id, ct) ?? throw new NotFoundException("Task not found.");
        var team = await TeamAsync(actor, all, ct);
        if (team is not null && !team.Contains(task.AssigneeEmployeeId))
            throw new ForbiddenException("Only the team lead / manager of the assignee can change this task.");
        return task;
    }

    private void ValidateDates(WorkTaskRequest r, DateOnly? existingStart = null)
    {
        if (r.DueDate < r.StartDate) throw new AppException("Due date must be on or after the start date.");
        if (existingStart is null && r.DueDate < Today) throw new AppException("Due date cannot be in the past.");
    }

    private static void Apply(WorkTask t, WorkTaskRequest r)
    {
        t.Title = r.Title.Trim();
        t.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        t.AssigneeEmployeeId = r.AssigneeEmployeeId;
        t.Priority = r.Priority;
        t.StartDate = r.StartDate;
        t.DueDate = r.DueDate;
    }

    private async Task NotifyAssigneeAsync(WorkTask t, bool reassigned, CancellationToken ct)
    {
        var e = await db.Employees.Where(x => x.EmployeeId == t.AssigneeEmployeeId).Select(x => new { x.Email, x.FirstName, x.LastName }).FirstAsync(ct);
        var by = await db.Users.Where(u => u.UserId == t.AssignedByUserId)
            .Select(u => u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username).FirstAsync(ct);
        mail.Enqueue(e.Email, $"{e.FirstName} {e.LastName}", $"{WorkTask.Code(t.TaskId)}: {t.Title}",
            Layout("HR", reassigned ? "A task was reassigned to you" : "New task assigned to you",
                P($"Dear {e.FirstName},") +
                P($"{by} assigned you task {WorkTask.Code(t.TaskId)} - {t.Title} ({t.Priority} priority), due {t.DueDate:dd MMM yyyy}.") +
                (t.Description is null ? "" : P(t.Description)) +
                P("Please update its status and percentage complete every day."),
                "Open my tasks", $"{mail.AppUrl}/me/tasks"),
            "Task");
    }

    private async Task<WorkTaskDto> GetDtoAsync(int id, TaskActor actor, CancellationToken ct) =>
        await Project(db.WorkTasks.AsNoTracking().Where(t => t.TaskId == id), actor,
            await TeamAsync(actor, actor.All.Edit, ct), await TeamAsync(actor, actor.All.Delete, ct)).FirstAsync(ct);

    /// <summary><paramref name="editTeam"/> / <paramref name="deleteTeam"/>: whose tasks the caller may edit / delete (null = everyone).</summary>
    private IQueryable<WorkTaskDto> Project(IQueryable<WorkTask> q, TaskActor actor, HashSet<int>? editTeam, HashSet<int>? deleteTeam)
    {
        var today = Today;
        var me = actor.EmployeeId ?? 0;
        var all = editTeam is null;
        var teamIds = editTeam ?? [];
        var deleteAll = deleteTeam is null;
        var deleteIds = deleteTeam ?? [];
        return q.Select(t => new WorkTaskDto(
            t.TaskId, WorkTask.Code(t.TaskId),   // evaluated in memory (top-level projection)
            t.Title, t.Description, t.AssigneeEmployeeId, t.Assignee.FirstName + " " + t.Assignee.LastName, t.Assignee.Designation,
            t.AssignedBy.Employee != null ? t.AssignedBy.Employee.FirstName + " " + t.AssignedBy.Employee.LastName : t.AssignedBy.Username,
            t.Priority, t.StartDate, t.DueDate, t.Status, t.PercentComplete, t.CreatedAt, t.CompletedAt,
            db.WorkTaskUpdates.Where(u => u.TaskId == t.TaskId).Max(u => (DateOnly?)u.UpdateDate),
            db.WorkTaskUpdates.Any(u => u.TaskId == t.TaskId && u.UpdateDate == today),
            t.Status != WorkTaskStatus.Completed && t.DueDate < today,
            all || teamIds.Contains(t.AssigneeEmployeeId),
            t.AssigneeEmployeeId == me,
            deleteAll || deleteIds.Contains(t.AssigneeEmployeeId)));
    }

    /// <summary>"TSK-00042" or "42" -> 42.</summary>
    private static int? ParseCode(string s)
    {
        var digits = s.StartsWith("TSK-", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
        return int.TryParse(digits, out var n) ? n : null;
    }
}
