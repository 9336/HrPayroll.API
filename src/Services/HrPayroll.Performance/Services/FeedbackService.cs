using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Performance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Performance.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Performance.Services;

/// <summary>Who is acting: managers have full rights over their reporting tree; <see cref="All"/> = rights over everyone.</summary>
public record FeedbackActor(int UserId, int? EmployeeId, Rights All);

/// <summary>
/// Appreciations and complaints about employees (from clients, senior management, colleagues…). Recorded by anyone
/// above the employee in the reporting line or by HR; the employee sees only what is shared with them and can reply.
/// The appraisal review shows everything dated within the review period.
/// </summary>
public class FeedbackService(PerformanceDbContext db, OrgService org, EmailQueue mail, TimeProvider time)
{
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Queries
    public async Task<List<FeedbackDto>> SearchAsync(FeedbackActor actor, FeedbackQuery q, CancellationToken ct)
    {
        var scope = await RequireScopeAsync(actor, actor.All.View, "Only managers and HR can see the feedback register.", ct);
        // Nobody manages feedback about themselves - their own is under My feedback.
        var query = db.EmployeeFeedback.AsNoTracking().Where(f => f.EmployeeId != actor.EmployeeId);
        if (scope is not null) query = query.Where(f => scope.Contains(f.EmployeeId));
        if (q.EmployeeId is int emp) query = query.Where(f => f.EmployeeId == emp);
        if (!string.IsNullOrWhiteSpace(q.Kind)) query = query.Where(f => f.Kind == q.Kind);
        if (q.Status == "Open") query = query.Where(f => f.Kind == FeedbackKind.Complaint && !f.IsResolved);
        if (q.From is DateOnly from) query = query.Where(f => f.EventDate >= from);
        if (q.To is DateOnly to) query = query.Where(f => f.EventDate <= to);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(f => f.Title.Contains(s) || f.GivenBy.Contains(s) || (f.Organisation != null && f.Organisation.Contains(s)) ||
                                     (f.Employee.FirstName + " " + f.Employee.LastName).Contains(s));
        }
        var list = await Project(query.OrderByDescending(f => f.EventDate).ThenByDescending(f => f.FeedbackId).Take(500)).ToListAsync(ct);
        var edit = await TeamAsync(actor, actor.All.Edit, ct);
        var delete = await TeamAsync(actor, actor.All.Delete, ct);
        return list.Select(f => f with { CanEdit = edit is null || edit.Contains(f.EmployeeId), CanDelete = delete is null || delete.Contains(f.EmployeeId) })
            .ToList();
    }

    /// <summary>The employee's own feedback that has been shared with them.</summary>
    public async Task<List<FeedbackDto>> GetMineAsync(FeedbackActor actor, CancellationToken ct)
    {
        var me = actor.EmployeeId ?? throw new ForbiddenException("Your login is not linked to an employee profile.");
        var list = await Project(db.EmployeeFeedback.AsNoTracking().Where(f => f.EmployeeId == me && f.SharedWithEmployee)
            .OrderByDescending(f => f.EventDate).ThenByDescending(f => f.FeedbackId)).ToListAsync(ct);
        return list.Select(f => f with { CanRespond = true }).ToList();
    }

    public async Task<FeedbackDto> GetAsync(int id, FeedbackActor actor, CancellationToken ct)
    {
        var f = await Project(db.EmployeeFeedback.AsNoTracking().Where(x => x.FeedbackId == id)).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Feedback not found.");
        if (await CanAsync(actor, f.EmployeeId, actor.All.View, ct))
            return f with { CanEdit = await CanAsync(actor, f.EmployeeId, actor.All.Edit, ct), CanDelete = await CanAsync(actor, f.EmployeeId, actor.All.Delete, ct) };
        if (f.EmployeeId == actor.EmployeeId && f.SharedWithEmployee) return f with { CanRespond = true };
        throw new ForbiddenException("You cannot see this feedback.");
    }

    /// <summary>Feedback dated within a period, e.g. an appraisal's review period (the employee only sees shared items).</summary>
    public async Task<FeedbackPeriodDto> ForPeriodAsync(int employeeId, DateOnly from, DateOnly to, bool includeUnshared, CancellationToken ct)
    {
        var items = await Project(db.EmployeeFeedback.AsNoTracking()
                .Where(f => f.EmployeeId == employeeId && f.EventDate >= from && f.EventDate <= to && (includeUnshared || f.SharedWithEmployee))
                .OrderByDescending(f => f.EventDate))
            .ToListAsync(ct);
        return new FeedbackPeriodDto(Summarise(items), items);
    }

    /// <summary>Appreciation / complaint counts per employee within a period (appraisal progress page).</summary>
    public async Task<Dictionary<int, (int Appreciations, int Complaints)>> CountsAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        (await db.EmployeeFeedback.AsNoTracking().Where(f => f.EventDate >= from && f.EventDate <= to)
            .GroupBy(f => f.EmployeeId)
            .Select(g => new { g.Key, A = g.Count(f => f.Kind == FeedbackKind.Appreciation), C = g.Count(f => f.Kind == FeedbackKind.Complaint) })
            .ToListAsync(ct))
        .ToDictionary(x => x.Key, x => (x.A, x.C));

    public async Task<FeedbackSummaryDto> SummaryAsync(FeedbackActor actor, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var scope = await RequireScopeAsync(actor, actor.All.View, "Only managers and HR can see the feedback register.", ct);
        var all = db.EmployeeFeedback.AsNoTracking().Where(f => f.EmployeeId != actor.EmployeeId);
        if (scope is not null) all = all.Where(f => scope.Contains(f.EmployeeId));
        var rows = await all.Where(f => f.EventDate >= from && f.EventDate <= to)
            .Select(f => new { f.Kind, f.Source, f.IsResolved, f.Severity }).ToListAsync(ct);
        var openAll = await all.CountAsync(f => f.Kind == FeedbackKind.Complaint && !f.IsResolved, ct);   // open ones of any date
        return new FeedbackSummaryDto(
            rows.Count(r => r.Kind == FeedbackKind.Appreciation),
            rows.Count(r => r.Kind == FeedbackKind.Appreciation && r.Source == FeedbackSource.Client),
            rows.Count(r => r.Kind == FeedbackKind.Complaint),
            openAll,
            rows.Count(r => r.Kind == FeedbackKind.Complaint && r.Severity is FeedbackSeverity.Major or FeedbackSeverity.Critical));
    }

    /// <summary>Employees the caller may record feedback for.</summary>
    public async Task<List<CalendarPersonDto>> GetPeopleAsync(FeedbackActor actor, CancellationToken ct)
    {
        var scope = await TeamAsync(actor, actor.All.Add, ct);
        var q = db.Employees.AsNoTracking().Where(e => e.IsActive && e.EmployeeId != actor.EmployeeId);
        if (scope is not null) q = q.Where(e => scope.Contains(e.EmployeeId));
        return await q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new CalendarPersonDto(e.EmployeeId, e.FirstName + " " + e.LastName, e.Designation, e.Department.Name)).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ Changes
    public async Task<FeedbackDto> CreateAsync(EmployeeFeedbackRequest req, FeedbackActor actor, CancellationToken ct)
    {
        await EnsureCanAsync(actor, req.EmployeeId, actor.All.Add, ct);
        var f = new EmployeeFeedback { EmployeeId = req.EmployeeId, RecordedByUserId = actor.UserId, CreatedAt = time.GetUtcNow().UtcDateTime };
        await ApplyAsync(f, req, ct);
        db.EmployeeFeedback.Add(f);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(f, actor, newlyShared: f.SharedWithEmployee, recorded: true, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(f.FeedbackId, actor, ct);
    }

    public async Task<FeedbackDto> UpdateAsync(int id, EmployeeFeedbackRequest req, FeedbackActor actor, CancellationToken ct)
    {
        var f = await LoadForManagerAsync(id, actor, ct);
        if (req.EmployeeId != f.EmployeeId) throw new AppException("Feedback cannot be moved to another employee - delete it and record it again.");
        var wasShared = f.SharedWithEmployee;
        await ApplyAsync(f, req, ct);
        f.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await NotifyAsync(f, actor, newlyShared: !wasShared && f.SharedWithEmployee, recorded: false, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, actor, ct);
    }

    public async Task DeleteAsync(int id, FeedbackActor actor, CancellationToken ct)
    {
        var f = await LoadForManagerAsync(id, actor, ct, delete: true);
        db.EmployeeFeedback.Remove(f);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FeedbackDto> ResolveAsync(int id, string? note, FeedbackActor actor, CancellationToken ct)
    {
        var f = await LoadForManagerAsync(id, actor, ct);
        if (f.Kind != FeedbackKind.Complaint) throw new AppException("Only complaints are resolved.");
        if (f.IsResolved) throw new ConflictException("This complaint is already resolved.");
        f.IsResolved = true;
        f.ResolutionNote = string.IsNullOrWhiteSpace(note) ? throw new AppException("Describe how the complaint was resolved.") : note.Trim();
        f.ResolvedAt = time.GetUtcNow().UtcDateTime;
        f.ResolvedByUserId = actor.UserId;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, actor, ct);
    }

    /// <summary>The employee's reply (their side of a complaint, or a thank-you).</summary>
    public async Task<FeedbackDto> RespondAsync(int id, string? text, FeedbackActor actor, CancellationToken ct)
    {
        var f = await db.EmployeeFeedback.FirstOrDefaultAsync(x => x.FeedbackId == id, ct) ?? throw new NotFoundException("Feedback not found.");
        if (f.EmployeeId != actor.EmployeeId || !f.SharedWithEmployee) throw new ForbiddenException("You can only reply to feedback shared with you.");
        if (string.IsNullOrWhiteSpace(text)) throw new AppException("Write your reply.");
        f.EmployeeResponse = text.Trim();
        f.RespondedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, actor, ct);
    }

    public async Task<FeedbackDto> SetAttachmentAsync(int id, IFormFile file, FeedbackActor actor, CancellationToken ct)
    {
        var f = await db.EmployeeFeedback.Include(x => x.Attachment).FirstOrDefaultAsync(x => x.FeedbackId == id, ct)
                ?? throw new NotFoundException("Feedback not found.");
        await EnsureCanAsync(actor, f.EmployeeId, actor.All.Edit, ct);
        var bytes = await FileSignatures.ReadAsync(file, MaxAttachmentBytes, ct);
        var type = FileSignatures.DetectImageOrPdf(bytes) ?? throw new AppException("Attach a PDF or an image (PNG, JPEG, WebP) - e.g. the client's email saved as PDF.");
        var ext = type switch { "application/pdf" => ".pdf", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        if (f.Attachment is null) f.Attachment = new FeedbackAttachment { FeedbackId = id, ContentType = type, Data = bytes };
        else { f.Attachment.ContentType = type; f.Attachment.Data = bytes; }
        f.AttachmentName = FileSignatures.SafeFileName(file.FileName, ext, "evidence");
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, actor, ct);
    }

    public async Task<(byte[] Data, string ContentType, string FileName)> GetAttachmentAsync(int id, FeedbackActor actor, CancellationToken ct)
    {
        var f = await GetAsync(id, actor, ct);   // access check (employees: shared items only)
        var a = await db.FeedbackAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.FeedbackId == id, ct)
                ?? throw new NotFoundException("No file attached.");
        return (a.Data, a.ContentType, f.AttachmentName ?? "evidence");
    }

    public async Task<FeedbackDto> RemoveAttachmentAsync(int id, FeedbackActor actor, CancellationToken ct)
    {
        var f = await LoadForManagerAsync(id, actor, ct);
        await db.FeedbackAttachments.Where(a => a.FeedbackId == id).ExecuteDeleteAsync(ct);
        f.AttachmentName = null;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Helpers
    /// <summary>Employee ids in the actor's reporting line, or null (everyone) when the role grants <paramref name="all"/>.</summary>
    private async Task<HashSet<int>?> TeamAsync(FeedbackActor actor, bool all, CancellationToken ct)
    {
        if (all) return null;
        return actor.EmployeeId is int me ? await org.GetSubordinateIdsAsync(me, ct) : [];
    }

    private async Task<HashSet<int>?> RequireScopeAsync(FeedbackActor actor, bool all, string message, CancellationToken ct)
    {
        var team = await TeamAsync(actor, all, ct);
        return team is { Count: 0 } ? throw new ForbiddenException(message) : team;
    }

    /// <summary>Whether the actor may act on feedback about this employee: their reporting line, or anyone with the role right.</summary>
    private async Task<bool> CanAsync(FeedbackActor actor, int employeeId, bool all, CancellationToken ct) =>
        employeeId != actor.EmployeeId &&
        (all || actor.EmployeeId is int me && await org.IsSubordinateAsync(me, employeeId, ct));

    private async Task EnsureCanAsync(FeedbackActor actor, int employeeId, bool all, CancellationToken ct)
    {
        if (employeeId == actor.EmployeeId) throw new ForbiddenException("You cannot record or change feedback about yourself.");
        if (!await CanAsync(actor, employeeId, all, ct))
            throw new ForbiddenException("You do not have permission to do this for feedback outside your reporting line.");
    }

    private async Task<EmployeeFeedback> LoadForManagerAsync(int id, FeedbackActor actor, CancellationToken ct, bool delete = false)
    {
        var f = await db.EmployeeFeedback.FirstOrDefaultAsync(x => x.FeedbackId == id, ct) ?? throw new NotFoundException("Feedback not found.");
        await EnsureCanAsync(actor, f.EmployeeId, delete ? actor.All.Delete : actor.All.Edit, ct);
        return f;
    }

    private async Task ApplyAsync(EmployeeFeedback f, EmployeeFeedbackRequest r, CancellationToken ct)
    {
        if (r.Kind is not (FeedbackKind.Appreciation or FeedbackKind.Complaint)) throw new AppException("Choose appreciation or complaint.");
        if (!FeedbackSource.All.Contains(r.Source)) throw new AppException("Choose where the feedback came from.");
        if (r.EventDate > Today) throw new AppException("The date cannot be in the future.");
        var joined = await db.Employees.Where(e => e.EmployeeId == r.EmployeeId).Select(e => (DateOnly?)e.DateOfJoining).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Employee not found.");
        if (r.EventDate < joined) throw new AppException("The date is before the employee joined.");
        if (r.Kind == FeedbackKind.Complaint && (r.Severity is null || !FeedbackSeverity.All.Contains(r.Severity)))
            throw new AppException("Choose how serious the complaint is.");
        if (r.Source == FeedbackSource.Client && string.IsNullOrWhiteSpace(r.Organisation))
            throw new AppException("Enter the client's company name.");

        f.Kind = r.Kind;
        f.Source = r.Source;
        f.GivenBy = r.GivenBy.Trim();
        f.Organisation = string.IsNullOrWhiteSpace(r.Organisation) ? null : r.Organisation.Trim();
        f.EventDate = r.EventDate;
        f.Title = r.Title.Trim();
        f.Details = r.Details.Trim();
        f.Severity = r.Kind == FeedbackKind.Complaint ? r.Severity : null;
        f.SharedWithEmployee = r.SharedWithEmployee;
        if (r.Kind == FeedbackKind.Appreciation) { f.IsResolved = false; f.ResolutionNote = null; f.ResolvedAt = null; f.ResolvedByUserId = null; }
    }

    private async Task NotifyAsync(EmployeeFeedback f, FeedbackActor actor, bool newlyShared, bool recorded, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == f.EmployeeId)
            .Select(x => new { x.FirstName, x.LastName, x.Email, x.ManagerId }).FirstAsync(ct);
        var company = await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";
        var from = f.Organisation is null ? f.GivenBy : $"{f.GivenBy} ({f.Organisation})";
        var appreciation = f.Kind == FeedbackKind.Appreciation;

        if (newlyShared)
            mail.Enqueue(e.Email, $"{e.FirstName} {e.LastName}",
                appreciation ? $"Appreciation for you from {from}" : $"Feedback shared with you: {f.Title}",
                Layout(company, appreciation ? "You have been appreciated!" : "Feedback for you",
                    P($"Dear {e.FirstName},") +
                    P(appreciation
                        ? $"{from} appreciated your work ({f.EventDate:dd MMM yyyy}): {f.Title}."
                        : $"A concern was raised by {from} on {f.EventDate:dd MMM yyyy}: {f.Title}. Please read it in the HR portal - you can add your side of it there.") +
                    (appreciation ? P(f.Details) : ""),
                    "My feedback", $"{mail.AppUrl}/me/feedback"), EmailCategories.Feedback);

        // The direct manager is told when someone else (HR, a senior manager) records feedback about their team member.
        if (recorded && e.ManagerId is int mid && mid != actor.EmployeeId)
        {
            var m = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == mid && x.IsActive)
                .Select(x => new { x.FirstName, x.LastName, x.Email }).FirstOrDefaultAsync(ct);
            if (m is not null)
                mail.Enqueue(m.Email, $"{m.FirstName} {m.LastName}",
                    $"{(appreciation ? "Appreciation" : "Complaint")} recorded for {e.FirstName} {e.LastName}",
                    Layout(company, appreciation ? "Appreciation for your team member" : "Complaint about your team member",
                        P($"Dear {m.FirstName},") +
                        P($"{(appreciation ? "An appreciation" : $"A {f.Severity?.ToLowerInvariant()} complaint")} from {from} was recorded for {e.FirstName} {e.LastName}: {f.Title}.") +
                        P("It will be shown in their appraisal review."),
                        "Open feedback register", $"{mail.AppUrl}/feedback"), EmailCategories.Feedback);
        }
    }

    private static FeedbackSummaryDto Summarise(IReadOnlyList<FeedbackDto> items) => new(
        items.Count(f => f.Kind == FeedbackKind.Appreciation),
        items.Count(f => f.Kind == FeedbackKind.Appreciation && f.Source == FeedbackSource.Client),
        items.Count(f => f.Kind == FeedbackKind.Complaint),
        items.Count(f => f.Kind == FeedbackKind.Complaint && !f.IsResolved),
        items.Count(f => f.Kind == FeedbackKind.Complaint && f.Severity is FeedbackSeverity.Major or FeedbackSeverity.Critical));

    private static IQueryable<FeedbackDto> Project(IQueryable<EmployeeFeedback> q) => q.Select(f => new FeedbackDto(
        f.FeedbackId, f.EmployeeId, f.Employee.FirstName + " " + f.Employee.LastName, f.Employee.EmployeeCode, f.Employee.Designation,
        f.Kind, f.Source, f.GivenBy, f.Organisation, f.EventDate, f.Title, f.Details, f.Severity, f.SharedWithEmployee,
        f.IsResolved, f.ResolutionNote, f.ResolvedAt,
        f.ResolvedBy != null ? (f.ResolvedBy.Employee != null ? f.ResolvedBy.Employee.FirstName + " " + f.ResolvedBy.Employee.LastName : f.ResolvedBy.Username) : null,
        f.EmployeeResponse, f.RespondedAt, f.AttachmentName,
        f.RecordedBy.Employee != null ? f.RecordedBy.Employee.FirstName + " " + f.RecordedBy.Employee.LastName : f.RecordedBy.Username,
        f.CreatedAt, false, false, false));
}
