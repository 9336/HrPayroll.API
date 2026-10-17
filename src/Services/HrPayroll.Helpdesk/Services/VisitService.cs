using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Helpdesk.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Helpdesk.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Helpdesk.Services;

/// <summary>
/// Visitor management. The visitor desk (visitors.* rights) registers and checks visitors in / out and sees
/// every visit; employees see the visitors who came (or are coming) to meet them and can invite their own.
/// </summary>
public class VisitService(HelpdeskDbContext db, Notifier notify, TimeProvider time)
{
    private const int MaxDaysAhead = 90;

    /// <summary>Visit times are office local time.</summary>
    private DateTime Now => time.GetLocalNow().DateTime;

    // ------------------------------------------------------------------ Queries
    public async Task<PagedResult<VisitDto>> SearchAsync(VisitQuery q, int? onlyHostId, CancellationToken ct)
    {
        var query = db.Visits.AsNoTracking();
        if (onlyHostId is int me) query = query.Where(v => v.HostEmployeeId == me);
        if (q.HostEmployeeId is int host) query = query.Where(v => v.HostEmployeeId == host);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(v => v.Status == q.Status);
        if (q.From is DateOnly from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue);
            query = query.Where(v => (v.CheckInAt ?? v.ExpectedAt) >= start);
        }
        if (q.To is DateOnly to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = query.Where(v => (v.CheckInAt ?? v.ExpectedAt) < end);
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(v => v.VisitorName.Contains(s) || v.VisitorPhone.Contains(s) ||
                                     (v.VisitorCompany != null && v.VisitorCompany.Contains(s)) ||
                                     (v.Host.FirstName + " " + v.Host.LastName).Contains(s) ||
                                     (v.BadgeNumber != null && v.BadgeNumber == s));
        }

        var pageSize = Math.Clamp(q.PageSize, 1, 100);
        var page = Math.Max(q.Page, 1);
        var total = await query.CountAsync(ct);
        var items = await Project(query
                .OrderByDescending(v => v.Status == VisitStatus.CheckedIn)         // people inside first
                .ThenByDescending(v => v.CheckInAt ?? v.ExpectedAt)
                .ThenByDescending(v => v.VisitId)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);
        return new PagedResult<VisitDto>(items, total, page, pageSize);
    }

    public async Task<VisitSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var start = Now.Date;
        var end = start.AddDays(1);
        var today = db.Visits.Where(v => (v.CheckInAt ?? v.ExpectedAt) >= start && (v.CheckInAt ?? v.ExpectedAt) < end);
        return new VisitSummaryDto(
            await today.CountAsync(v => v.Status == VisitStatus.Expected, ct),
            await db.Visits.CountAsync(v => v.Status == VisitStatus.CheckedIn, ct),
            await today.CountAsync(v => v.Status == VisitStatus.CheckedOut, ct),
            await today.CountAsync(v => v.Status != VisitStatus.Cancelled, ct));
    }

    public async Task<VisitDto> GetAsync(int id, CancellationToken ct) =>
        await Project(db.Visits.AsNoTracking().Where(v => v.VisitId == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Visit not found.");

    // ------------------------------------------------------------------ Create
    public async Task<VisitDto> InviteAsync(int hostEmployeeId, InviteVisitorRequest req, int userId, CancellationToken ct)
    {
        await EnsureActiveHostAsync(hostEmployeeId, ct);
        ValidateExpected(req.ExpectedAt);
        var visit = NewVisit(req, hostEmployeeId, userId);
        visit.ExpectedAt = req.ExpectedAt;
        return await SaveNewAsync(visit, ct);
    }

    public async Task<VisitDto> RegisterAsync(RegisterVisitRequest req, int userId, CancellationToken ct)
    {
        await EnsureActiveHostAsync(req.HostEmployeeId, ct);
        var visit = NewVisit(req, req.HostEmployeeId, userId);
        if (req.CheckInNow)
        {
            visit.Status = VisitStatus.CheckedIn;
            visit.CheckInAt = Now;
            visit.ExpectedAt = req.ExpectedAt;
            visit.IdProofType = Clean(req.IdProofType);
            visit.IdProofLast4 = Clean(req.IdProofLast4)?.ToUpperInvariant();
            visit.BadgeNumber = Clean(req.BadgeNumber);
        }
        else
        {
            ValidateExpected(req.ExpectedAt ?? throw new AppException("Please enter when the visitor is expected."));
            visit.ExpectedAt = req.ExpectedAt;
        }
        return await SaveNewAsync(visit, ct);
    }

    // ------------------------------------------------------------------ Status changes
    public async Task<VisitDto> CheckInAsync(int id, CheckInRequest req, CancellationToken ct)
    {
        var visit = await LoadAsync(id, ct);
        if (visit.Status != VisitStatus.Expected)
            throw new ConflictException($"Only expected visitors can be checked in (this visit is {Describe(visit.Status)}).");
        visit.Status = VisitStatus.CheckedIn;
        visit.CheckInAt = Now;
        visit.IdProofType = Clean(req.IdProofType) ?? visit.IdProofType;
        visit.IdProofLast4 = Clean(req.IdProofLast4)?.ToUpperInvariant() ?? visit.IdProofLast4;
        visit.BadgeNumber = Clean(req.BadgeNumber) ?? visit.BadgeNumber;
        await db.SaveChangesAsync(ct);
        await notify.VisitorArrivedAsync(id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<VisitDto> CheckOutAsync(int id, CancellationToken ct)
    {
        var visit = await LoadAsync(id, ct);
        if (visit.Status != VisitStatus.CheckedIn)
            throw new ConflictException($"Only visitors who are checked in can be checked out (this visit is {Describe(visit.Status)}).");
        var now = Now;
        visit.Status = VisitStatus.CheckedOut;
        visit.CheckOutAt = now < visit.CheckInAt ? visit.CheckInAt : now;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Cancels an expected visit - by the visitor desk, or by the host for their own visitors.</summary>
    public async Task<VisitDto> CancelAsync(int id, int? callerEmployeeId, bool isDesk, CancellationToken ct)
    {
        var visit = await LoadAsync(id, ct);
        if (!isDesk && visit.HostEmployeeId != callerEmployeeId) throw new ForbiddenException();
        if (visit.Status != VisitStatus.Expected)
            throw new ConflictException($"Only expected visits can be cancelled (this visit is {Describe(visit.Status)}).");
        visit.Status = VisitStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ Helpers
    private Visit NewVisit(VisitorDetails d, int hostId, int userId) => new()
    {
        VisitorName = d.VisitorName.Trim(),
        VisitorPhone = d.VisitorPhone.Trim(),
        VisitorEmail = Clean(d.VisitorEmail)?.ToLowerInvariant(),
        VisitorCompany = Clean(d.VisitorCompany),
        Purpose = d.Purpose.Trim(),
        Notes = Clean(d.Notes),
        HostEmployeeId = hostId,
        Status = VisitStatus.Expected,
        CreatedByUserId = userId,
    };

    private async Task<VisitDto> SaveNewAsync(Visit visit, CancellationToken ct)
    {
        db.Visits.Add(visit);
        await db.SaveChangesAsync(ct);
        if (visit.Status == VisitStatus.CheckedIn) await notify.VisitorArrivedAsync(visit.VisitId, ct);   // walk-in
        return await GetAsync(visit.VisitId, ct);
    }

    private void ValidateExpected(DateTime expectedAt)
    {
        if (expectedAt.Date < Now.Date) throw new AppException("Expected date cannot be in the past.");
        if (expectedAt > Now.AddDays(MaxDaysAhead)) throw new AppException($"Visits can be scheduled at most {MaxDaysAhead} days ahead.");
    }

    private async Task EnsureActiveHostAsync(int hostId, CancellationToken ct)
    {
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == hostId && e.IsActive, ct))
            throw new AppException("The person being visited must be an active employee.");
    }

    private async Task<Visit> LoadAsync(int id, CancellationToken ct) =>
        await db.Visits.FirstOrDefaultAsync(v => v.VisitId == id, ct) ?? throw new NotFoundException("Visit not found.");

    private static string Describe(string status) => status switch
    {
        VisitStatus.CheckedIn => "checked in",
        VisitStatus.CheckedOut => "checked out",
        _ => status.ToLowerInvariant(),
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IQueryable<VisitDto> Project(IQueryable<Visit> source) => source.Select(v => new VisitDto(
        v.VisitId, v.VisitorName, v.VisitorPhone, v.VisitorEmail, v.VisitorCompany, v.Purpose, v.HostEmployeeId,
        v.Host.FirstName + " " + v.Host.LastName, v.Host.Department.Name, v.ExpectedAt, v.CheckInAt, v.CheckOutAt, v.Status,
        v.IdProofType, v.IdProofLast4, v.BadgeNumber, v.Notes, v.CreatedBy != null ? v.CreatedBy.Username : null, v.CreatedAt));
}
