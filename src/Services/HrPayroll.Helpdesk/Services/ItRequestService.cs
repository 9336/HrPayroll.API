using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Helpdesk.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Helpdesk.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Helpdesk.Services;

/// <summary>Who is acting: an employee (requester / manager), IT staff (it.manage) and / or an admin.</summary>
/// <summary>IsItStaff: works on requests (it.edit); CanViewAll: sees every request (it.view).</summary>
public record ItActor(int UserId, int? EmployeeId, bool IsItStaff, bool IsAdmin, bool CanViewAll = false);

/// <summary>
/// IT service requests. An employee raises a request; their reporting manager (or anyone above them, or an admin)
/// approves it; then the IT team picks it up, works on it and completes or rejects it. Every step is kept as a
/// timeline event and the people involved get an email.
/// </summary>
public class ItRequestService(HelpdeskDbContext db, OrgService org, EmailQueue mail, TimeProvider time)
{
    private const string MailCategory = "IT";
    public static readonly string[] Priorities = ["Low", "Medium", "High", "Urgent"];

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Requester
    public async Task<ItRequestDetailDto> CreateAsync(ItActor actor, CreateItRequest req, CancellationToken ct)
    {
        var employeeId = actor.EmployeeId ?? throw new ForbiddenException("Your login is not linked to an employee profile.");
        if (!ItRequestTypes.All.Contains(req.RequestType)) throw new AppException("Please choose what you need.");
        if (!Priorities.Contains(req.Priority)) throw new AppException("Please choose a priority.");
        if (req.RequiredBy is DateOnly by && by < Today) throw new AppException("'Needed by' cannot be in the past.");

        var me = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == employeeId)
                     .Select(e => new { e.FirstName, e.LastName, e.IsActive, e.ManagerId }).FirstOrDefaultAsync(ct)
                 ?? throw new NotFoundException("Employee not found.");
        if (!me.IsActive) throw new AppException("Inactive employees cannot raise requests.");
        var manager = me.ManagerId is int mid
            ? await db.Employees.AsNoTracking().Where(e => e.EmployeeId == mid && e.IsActive)
                .Select(e => new { e.EmployeeId, e.FirstName, e.LastName, e.Email }).FirstOrDefaultAsync(ct)
            : null;

        var now = UtcNow;
        var request = new ItRequest
        {
            EmployeeId = employeeId, RequestType = req.RequestType, Title = req.Title.Trim(), Details = req.Details.Trim(),
            Priority = req.Priority, RequiredBy = req.RequiredBy, CreatedAt = now,
            ApproverEmployeeId = manager?.EmployeeId,
            Status = manager is null ? ItRequestStatus.Approved : ItRequestStatus.PendingApproval,
        };
        request.Events.Add(Event(actor, "Submitted", manager is null ? null : $"Sent to {manager.FirstName} {manager.LastName} for approval.", now));
        if (manager is null)
        {
            request.ApprovedAt = now;
            request.Events.Add(Event(actor, "Approved", "No reporting manager - sent straight to the IT team.", now));
        }
        db.ItRequests.Add(request);
        await db.SaveChangesAsync(ct);

        var code = ItRequest.Code(request.RequestId);
        var who = $"{me.FirstName} {me.LastName}";
        if (manager is not null)
            mail.Enqueue(manager.Email, $"{manager.FirstName} {manager.LastName}", $"Approval needed: {code} {request.Title}",
                Layout(await CompanyNameAsync(ct), "An IT request needs your approval",
                    P($"Dear {manager.FirstName},") +
                    P($"{who} has raised IT request {code}: {request.Title} ({TypeLabel(request.RequestType)}, {request.Priority} priority).") +
                    P(request.Details) +
                    P("Please approve or reject it so the IT team can act on it."),
                    "Review request", $"{mail.AppUrl}/it-requests/approvals"), MailCategory);
        else
            await NotifyItTeamAsync(request, who, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(request.RequestId, actor, ct);
    }

    public async Task<List<ItRequestDto>> GetMineAsync(ItActor actor, CancellationToken ct)
    {
        var employeeId = actor.EmployeeId ?? throw new ForbiddenException("Your login is not linked to an employee profile.");
        return await Project(db.ItRequests.AsNoTracking().Where(r => r.EmployeeId == employeeId)
            .OrderByDescending(r => r.CreatedAt)).ToListAsync(ct);
    }

    public async Task<ItRequestDetailDto> CancelAsync(int id, ItActor actor, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        if (r.EmployeeId != actor.EmployeeId) throw new ForbiddenException("You can only cancel your own requests.");
        if (r.Status is not (ItRequestStatus.PendingApproval or ItRequestStatus.Approved))
            throw new ConflictException(r.Status == ItRequestStatus.InProgress
                ? "IT has already started on this request - please add a comment instead."
                : $"This request is already {Label(r.Status)}.");
        r.Status = ItRequestStatus.Cancelled;
        Touch(r, actor, "Cancelled", null);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Reporting manager
    /// <summary>Requests of everyone reporting to the caller (all requests for an admin).</summary>
    public async Task<List<ItRequestDto>> GetApprovalsAsync(ItActor actor, string? status, CancellationToken ct)
    {
        var q = db.ItRequests.AsNoTracking();
        if (!actor.IsAdmin)
        {
            if (actor.EmployeeId is not int me) return [];
            var team = await org.GetSubordinateIdsAsync(me, ct);
            q = q.Where(r => team.Contains(r.EmployeeId));
        }
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        return await Project(q.OrderByDescending(r => r.Status == ItRequestStatus.PendingApproval).ThenByDescending(r => r.CreatedAt).Take(300))
            .ToListAsync(ct);
    }

    public async Task<ItRequestDetailDto> ApproveAsync(int id, ItActor actor, string? remarks, CancellationToken ct)
    {
        var r = await LoadForApprovalAsync(id, actor, ct);
        r.Status = ItRequestStatus.Approved;
        r.ApprovedByUserId = actor.UserId;
        r.ApprovedAt = UtcNow;
        r.ApprovalRemarks = Clean(remarks);
        Touch(r, actor, "Approved", r.ApprovalRemarks);

        var requester = await RequesterAsync(r.EmployeeId, ct);
        mail.Enqueue(requester.Email, requester.Name, $"Approved: {ItRequest.Code(r.RequestId)} {r.Title}",
            Layout(await CompanyNameAsync(ct), "Your IT request was approved",
                P($"Dear {requester.FirstName},") +
                P($"Your request {ItRequest.Code(r.RequestId)} - {r.Title} has been approved by {await UserNameAsync(actor.UserId, ct)} and sent to the IT team.") +
                (r.ApprovalRemarks is null ? "" : P($"Remarks: {r.ApprovalRemarks}")),
                "Track my request", $"{mail.AppUrl}/me/it-requests"), MailCategory);
        await NotifyItTeamAsync(r, requester.Name, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<ItRequestDetailDto> RejectAsync(int id, ItActor actor, string? remarks, CancellationToken ct)
    {
        var reason = Clean(remarks) ?? throw new AppException("Please give a reason for rejecting the request.");
        var r = await LoadForApprovalAsync(id, actor, ct);
        r.Status = ItRequestStatus.Rejected;
        r.ApprovedByUserId = actor.UserId;
        r.ApprovedAt = UtcNow;
        r.ApprovalRemarks = reason;
        Touch(r, actor, "Rejected", reason);
        await MailRequesterAsync(r, "Your IT request was rejected",
            $"Your request {ItRequest.Code(r.RequestId)} - {r.Title} was rejected by your reporting manager ({await UserNameAsync(actor.UserId, ct)}).",
            reason, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ IT service desk
    public async Task<PagedResult<ItRequestDto>> GetQueueAsync(ItActor actor, ItQueueQuery query, CancellationToken ct)
    {
        var q = db.ItRequests.AsNoTracking();
        if (query.Status == "Open") q = q.Where(r => r.Status == ItRequestStatus.Approved || r.Status == ItRequestStatus.InProgress);
        else if (!string.IsNullOrWhiteSpace(query.Status) && query.Status != "All") q = q.Where(r => r.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Type)) q = q.Where(r => r.RequestType == query.Type);
        if (query.MineOnly) q = q.Where(r => r.AssignedToUserId == actor.UserId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            var digits = s.StartsWith("ITR-", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
            var idFromCode = int.TryParse(digits, out var n) ? n : (int?)null;
            q = q.Where(r => r.Title.Contains(s) || r.Details.Contains(s) || r.RequestId == idFromCode ||
                             (r.Employee.FirstName + " " + r.Employee.LastName).Contains(s) || r.Employee.EmployeeCode.Contains(s));
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(query.Page, 1);
        var total = await q.CountAsync(ct);
        // Most urgent first: waiting for IT, then by priority and age.
        var items = await Project(q
                .OrderBy(r => r.Status == ItRequestStatus.Approved ? 0 : r.Status == ItRequestStatus.InProgress ? 1 : 2)
                .ThenBy(r => r.Priority == "Urgent" ? 0 : r.Priority == "High" ? 1 : r.Priority == "Medium" ? 2 : 3)
                .ThenBy(r => r.Status == ItRequestStatus.Approved || r.Status == ItRequestStatus.InProgress ? r.CreatedAt : DateTime.MaxValue)
                .ThenByDescending(r => r.RequestId)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);
        return new PagedResult<ItRequestDto>(items, total, page, pageSize);
    }

    public async Task<ItDeskStatsDto> GetStatsAsync(ItActor actor, CancellationToken ct)
    {
        var monthStart = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var counts = await db.ItRequests.GroupBy(_ => 1).Select(g => new
        {
            Pending = g.Count(r => r.Status == ItRequestStatus.PendingApproval),
            Awaiting = g.Count(r => r.Status == ItRequestStatus.Approved),
            InProgress = g.Count(r => r.Status == ItRequestStatus.InProgress),
            Mine = g.Count(r => r.AssignedToUserId == actor.UserId && (r.Status == ItRequestStatus.Approved || r.Status == ItRequestStatus.InProgress)),
            Done = g.Count(r => r.Status == ItRequestStatus.Completed && r.CompletedAt >= monthStart),
        }).FirstOrDefaultAsync(ct);
        return new ItDeskStatsDto(counts?.Pending ?? 0, counts?.Awaiting ?? 0, counts?.InProgress ?? 0, counts?.Mine ?? 0, counts?.Done ?? 0);
    }

    public async Task<List<ItStaffDto>> GetStaffAsync(CancellationToken ct) =>
        (await ItStaffQuery().Select(u => new ItStaffDto(u.UserId, u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username))
            .ToListAsync(ct)).OrderBy(s => s.Name).ToList();

    public async Task<ItRequestDetailDto> AssignAsync(int id, ItActor actor, int? userId, CancellationToken ct)
    {
        var r = await LoadForItAsync(id, actor, ct);
        var target = userId ?? actor.UserId;
        if (!await ItStaffQuery().AnyAsync(u => u.UserId == target, ct)) throw new AppException("That person is not in the IT team.");
        if (r.AssignedToUserId == target) return await GetDetailAsync(id, actor, ct);
        r.AssignedToUserId = target;
        Touch(r, actor, "Assigned", $"Assigned to {await UserNameAsync(target, ct)}.");
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<ItRequestDetailDto> StartAsync(int id, ItActor actor, string? note, CancellationToken ct)
    {
        var r = await LoadForItAsync(id, actor, ct);
        if (r.Status != ItRequestStatus.Approved) throw new ConflictException("Work has already started on this request.");
        r.Status = ItRequestStatus.InProgress;
        r.AssignedToUserId ??= actor.UserId;
        Touch(r, actor, "Started", Clean(note));
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<ItRequestDetailDto> CompleteAsync(int id, ItActor actor, string? notes, CancellationToken ct)
    {
        var resolution = Clean(notes) ?? throw new AppException("Please describe what was done (e.g. laptop asset tag, software installed).");
        var r = await LoadForItAsync(id, actor, ct);
        r.Status = ItRequestStatus.Completed;
        r.AssignedToUserId ??= actor.UserId;
        r.ResolutionNotes = resolution;
        r.CompletedAt = UtcNow;
        Touch(r, actor, "Completed", resolution);
        await MailRequesterAsync(r, "Your IT request is complete",
            $"The IT team has completed your request {ItRequest.Code(r.RequestId)} - {r.Title}.", resolution, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<ItRequestDetailDto> ItRejectAsync(int id, ItActor actor, string? remarks, CancellationToken ct)
    {
        var reason = Clean(remarks) ?? throw new AppException("Please give a reason for rejecting the request.");
        var r = await LoadForItAsync(id, actor, ct);
        r.Status = ItRequestStatus.Rejected;
        r.ResolutionNotes = reason;
        r.CompletedAt = UtcNow;
        Touch(r, actor, "Rejected", reason);
        await MailRequesterAsync(r, "Your IT request was rejected",
            $"The IT team could not fulfil your request {ItRequest.Code(r.RequestId)} - {r.Title}.", reason, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Shared
    public async Task<ItRequestDetailDto> GetDetailAsync(int id, ItActor actor, CancellationToken ct)
    {
        var dto = await Project(db.ItRequests.AsNoTracking().Where(r => r.RequestId == id)).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("IT request not found.");
        var isRequester = dto.EmployeeId == actor.EmployeeId;
        var isManager = !isRequester && actor.EmployeeId is int me && await org.IsSubordinateAsync(me, dto.EmployeeId, ct);
        if (!isRequester && !isManager && !actor.IsItStaff && !actor.CanViewAll && !actor.IsAdmin)
            throw new ForbiddenException("You cannot view this request.");

        var events = await db.ItRequestEvents.AsNoTracking().Where(e => e.RequestId == id).OrderBy(e => e.EventId)
            .Select(e => new ItRequestEventDto(e.EventId, e.Action, e.Note,
                e.By.Employee != null ? e.By.Employee.FirstName + " " + e.By.Employee.LastName : e.By.Username, e.At))
            .ToListAsync(ct);
        var open = ItRequestStatus.Open.Contains(dto.Status);
        return new ItRequestDetailDto(dto, events,
            CanCancel: isRequester && dto.Status is ItRequestStatus.PendingApproval or ItRequestStatus.Approved,
            CanApprove: dto.Status == ItRequestStatus.PendingApproval && !isRequester && (isManager || actor.IsAdmin),
            CanWork: actor.IsItStaff && dto.Status is ItRequestStatus.Approved or ItRequestStatus.InProgress,
            CanComment: open);
    }

    public async Task<ItRequestDetailDto> CommentAsync(int id, ItActor actor, string text, CancellationToken ct)
    {
        var detail = await GetDetailAsync(id, actor, ct);   // also checks the caller may see it
        if (!detail.CanComment) throw new ConflictException("Comments are closed on this request.");
        var r = await LoadAsync(id, ct);
        Touch(r, actor, "Comment", text.Trim());
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<ItCountsDto> GetCountsAsync(ItActor actor, CancellationToken ct)
    {
        var mine = actor.EmployeeId is int me
            ? await db.ItRequests.CountAsync(r => r.EmployeeId == me && ItRequestStatus.Open.Contains(r.Status), ct) : 0;
        var approvals = 0;
        if (actor.IsAdmin)
            approvals = await db.ItRequests.CountAsync(r => r.Status == ItRequestStatus.PendingApproval && r.EmployeeId != actor.EmployeeId, ct);
        else if (actor.EmployeeId is int mgr)
        {
            var team = await org.GetSubordinateIdsAsync(mgr, ct);
            if (team.Count > 0)
                approvals = await db.ItRequests.CountAsync(r => r.Status == ItRequestStatus.PendingApproval && team.Contains(r.EmployeeId), ct);
        }
        var queue = actor.IsItStaff || actor.CanViewAll ? await db.ItRequests.CountAsync(r => r.Status == ItRequestStatus.Approved, ct) : 0;
        return new ItCountsDto(mine, approvals, queue);
    }

    // ------------------------------------------------------------------ Helpers
    private async Task<ItRequest> LoadAsync(int id, CancellationToken ct) =>
        await db.ItRequests.FirstOrDefaultAsync(r => r.RequestId == id, ct) ?? throw new NotFoundException("IT request not found.");

    private async Task<ItRequest> LoadForApprovalAsync(int id, ItActor actor, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        if (r.EmployeeId == actor.EmployeeId) throw new ForbiddenException("You cannot approve your own request.");
        if (!actor.IsAdmin && (actor.EmployeeId is not int me || !await org.IsSubordinateAsync(me, r.EmployeeId, ct)))
            throw new ForbiddenException("Only the requester's reporting manager can approve this request.");
        if (r.Status != ItRequestStatus.PendingApproval) throw new ConflictException($"This request is already {Label(r.Status)}.");
        return r;
    }

    private async Task<ItRequest> LoadForItAsync(int id, ItActor actor, CancellationToken ct)
    {
        if (!actor.IsItStaff) throw new ForbiddenException("Only the IT team can do this.");
        var r = await LoadAsync(id, ct);
        if (r.Status == ItRequestStatus.PendingApproval) throw new ConflictException("This request is still waiting for the manager's approval.");
        if (r.Status is not (ItRequestStatus.Approved or ItRequestStatus.InProgress)) throw new ConflictException($"This request is already {Label(r.Status)}.");
        return r;
    }

    private void Touch(ItRequest r, ItActor actor, string action, string? note)
    {
        var now = UtcNow;
        r.UpdatedAt = now;
        db.ItRequestEvents.Add(new ItRequestEvent { RequestId = r.RequestId, ByUserId = actor.UserId, Action = action, Note = note, At = now });
    }

    private static ItRequestEvent Event(ItActor actor, string action, string? note, DateTime at) =>
        new() { ByUserId = actor.UserId, Action = action, Note = note, At = at };

    /// <summary>Users who can work on IT requests: roles holding it.edit, plus admins.</summary>
    private IQueryable<User> ItStaffQuery() =>
        db.Users.Where(u => u.IsActive && (u.RoleId == Roles.AdminId || u.Role.Permissions.Any(p => p.Permission == Permissions.ItWork)));

    private async Task NotifyItTeamAsync(ItRequest r, string requesterName, CancellationToken ct)
    {
        var team = await db.Users.Where(u => u.IsActive && u.Employee != null && u.Role.Permissions.Any(p => p.Permission == Permissions.ItWork))
            .Select(u => new { u.Employee!.Email, u.Employee.FirstName, u.Employee.LastName }).ToListAsync(ct);
        if (team.Count == 0)   // no IT role assigned yet - admins handle IT
            team = await db.Users.Where(u => u.IsActive && u.Employee != null && u.RoleId == Roles.AdminId)
                .Select(u => new { u.Employee!.Email, u.Employee.FirstName, u.Employee.LastName }).ToListAsync(ct);
        var company = await CompanyNameAsync(ct);
        var code = ItRequest.Code(r.RequestId);
        foreach (var m in team)
            mail.Enqueue(m.Email, $"{m.FirstName} {m.LastName}", $"New IT request {code}: {r.Title}",
                Layout(company, "New approved IT request",
                    P($"Dear {m.FirstName},") +
                    P($"{requesterName} needs: {r.Title} ({TypeLabel(r.RequestType)}, {r.Priority} priority" +
                      (r.RequiredBy is DateOnly by ? $", needed by {by:dd MMM yyyy}" : "") + ").") +
                    P(r.Details),
                    "Open service desk", $"{mail.AppUrl}/it-requests/desk"), MailCategory);
    }

    private async Task MailRequesterAsync(ItRequest r, string heading, string message, string? note, CancellationToken ct)
    {
        var requester = await RequesterAsync(r.EmployeeId, ct);
        mail.Enqueue(requester.Email, requester.Name, $"{heading}: {ItRequest.Code(r.RequestId)}",
            Layout(await CompanyNameAsync(ct), heading,
                P($"Dear {requester.FirstName},") + P(message) + (note is null ? "" : P(note)),
                "View my requests", $"{mail.AppUrl}/me/it-requests"), MailCategory);
    }

    private async Task<(string Email, string FirstName, string Name)> RequesterAsync(int employeeId, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == employeeId)
            .Select(x => new { x.Email, x.FirstName, x.LastName }).FirstAsync(ct);
        return (e.Email, e.FirstName, $"{e.FirstName} {e.LastName}");
    }

    private Task<string> UserNameAsync(int userId, CancellationToken ct) =>
        db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username).FirstAsync(ct);

    private async Task<string> CompanyNameAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Label(string status) => status switch
    {
        ItRequestStatus.PendingApproval => "waiting for approval",
        ItRequestStatus.InProgress => "in progress",
        _ => status.ToLowerInvariant(),
    };

    private static string TypeLabel(string type) => type switch
    {
        ItRequestTypes.Laptop => "Laptop / desktop",
        ItRequestTypes.Hardware => "Hardware & accessories",
        ItRequestTypes.Software => "Software installation",
        ItRequestTypes.Access => "Access & accounts",
        _ => "Other",
    };

    private static IQueryable<ItRequestDto> Project(IQueryable<ItRequest> q) => q.Select(r => new ItRequestDto(
        r.RequestId, ItRequest.Code(r.RequestId),   // evaluated in memory (top-level projection)
        r.RequestType, r.Title, r.Details, r.Priority, r.RequiredBy, r.Status,
        r.EmployeeId, r.Employee.FirstName + " " + r.Employee.LastName, r.Employee.EmployeeCode, r.Employee.Department.Name,
        r.Approver != null ? r.Approver.FirstName + " " + r.Approver.LastName : null,
        r.ApprovedBy != null ? (r.ApprovedBy.Employee != null ? r.ApprovedBy.Employee.FirstName + " " + r.ApprovedBy.Employee.LastName : r.ApprovedBy.Username) : null,
        r.ApprovedAt, r.ApprovalRemarks, r.AssignedToUserId,
        r.AssignedTo != null ? (r.AssignedTo.Employee != null ? r.AssignedTo.Employee.FirstName + " " + r.AssignedTo.Employee.LastName : r.AssignedTo.Username) : null,
        r.ResolutionNotes, r.CompletedAt, r.CreatedAt, r.UpdatedAt));
}
