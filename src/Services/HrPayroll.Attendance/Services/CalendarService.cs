using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Attendance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Attendance.Services;

/// <summary>Who is looking at a calendar: their own employee id and whether they may see everyone's.</summary>
public record CalendarViewer(int? EmployeeId, bool CanViewAll);

/// <summary>
/// Employee calendar: attendance (check-in / check-out), approved and pending leave, holidays, and the work
/// the employee logged for each day. Employees see their own; managers their team; HR / admin everyone.
/// </summary>
public class CalendarService(AttendanceDbContext db, OrgService org, AttendanceLocationService locations, TimeProvider time)
{
    /// <summary>Work logs older than this cannot be added, changed or deleted.</summary>
    public const int WorkLogEditWindowDays = 60;

    private DateTime Now => time.GetLocalNow().DateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ------------------------------------------------------------------ Access
    public async Task EnsureCanViewAsync(CalendarViewer viewer, int employeeId, CancellationToken ct)
    {
        if (viewer.CanViewAll || viewer.EmployeeId == employeeId) return;
        if (viewer.EmployeeId is int me && await org.IsSubordinateAsync(me, employeeId, ct)) return;
        throw new ForbiddenException("You can only view your own calendar or your team's.");
    }

    public async Task<List<CalendarPersonDto>> GetPeopleAsync(CalendarViewer viewer, CancellationToken ct)
    {
        var query = db.Employees.AsNoTracking().Where(e => e.IsActive);
        if (!viewer.CanViewAll)
        {
            var ids = viewer.EmployeeId is int me ? await org.GetSubordinateIdsAsync(me, ct) : [];
            if (viewer.EmployeeId is int self) ids.Add(self);
            query = query.Where(e => ids.Contains(e.EmployeeId));
        }
        return await query.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new CalendarPersonDto(e.EmployeeId, e.FirstName + " " + e.LastName, e.Designation, e.Department.Name))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ Month view
    public async Task<CalendarMonthDto> GetMonthAsync(int employeeId, int year, int month, bool isSelf, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new AppException("Invalid month.");
        var emp = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == employeeId)
                      .Select(e => new { e.FirstName, e.LastName, e.Designation, e.DateOfJoining })
                      .FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Employee not found.");

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var today = Today;

        var holidays = await db.Holidays.AsNoTracking().Where(h => h.HolidayDate >= first && h.HolidayDate <= last)
            .ToDictionaryAsync(h => h.HolidayDate, h => h.Name, ct);
        var attendance = await db.Attendance.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.AttendanceDate >= first && a.AttendanceDate <= last)
            .ToDictionaryAsync(a => a.AttendanceDate, ct);
        var leaves = await db.LeaveRecords.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.FromDate <= last && r.ToDate >= first &&
                        (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending))
            .Select(r => new { r.FromDate, r.ToDate, r.IsHalfDay, r.Status, Code = r.LeaveCode, Name = r.LeaveName })
            .ToListAsync(ct);
        var work = await db.WorkLogs.AsNoTracking()
            .Where(w => w.EmployeeId == employeeId && w.WorkDate >= first && w.WorkDate <= last)
            .GroupBy(w => w.WorkDate)
            .Select(g => new { g.Key, Hours = g.Sum(w => w.Hours), Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, ct);
        // Past days before the employee's first ever attendance are "no record", not "absent".
        var trackingStart = await db.Attendance.Where(a => a.EmployeeId == employeeId)
            .MinAsync(a => (DateOnly?)a.AttendanceDate, ct);

        var days = new List<CalendarDayDto>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var holiday = holidays.GetValueOrDefault(d);
            var att = attendance.GetValueOrDefault(d);
            var leave = leaves.Where(l => l.FromDate <= d && l.ToDate >= d)
                .OrderBy(l => l.Status == LeaveStatus.Approved ? 0 : 1).FirstOrDefault();
            var isWorkingDay = !isWeekend && holiday is null;
            if (!isWorkingDay) leave = null;   // leave spanning a weekend / holiday does not use those days
            var approvedLeave = leave?.Status == LeaveStatus.Approved;

            var status =
                d < emp.DateOfJoining ? "NotJoined" :
                att is not null ? "Present" :
                holiday is not null ? "Holiday" :
                isWeekend ? "Weekend" :
                approvedLeave ? "Leave" :
                d > today ? "Upcoming" :
                d == today ? "Today" :
                trackingStart is null || d < trackingStart ? "NoRecord" :
                "Absent";

            var w = work.GetValueOrDefault(d);
            days.Add(new CalendarDayDto(d, status, isWeekend, holiday, att?.CheckInAt, att?.CheckOutAt, Hours(att),
                leave?.Code, leave?.Name, leave?.Status, leave?.IsHalfDay ?? false, w?.Hours ?? 0, w?.Count ?? 0));
        }

        var completed = days.Where(x => x.HoursAtWork is not null).Select(x => x.HoursAtWork!.Value).ToList();
        var summary = new CalendarSummaryDto(
            WorkingDays: days.Count(x => !x.IsWeekend && x.Holiday is null),
            Present: days.Count(x => x.Status == "Present"),
            LeaveDays: days.Where(x => x.LeaveStatus == LeaveStatus.Approved).Sum(x => x.HalfDayLeave ? 0.5m : 1m),
            Absent: days.Count(x => x.Status == "Absent"),
            Holidays: days.Count(x => x.Holiday is not null && !x.IsWeekend),
            WorkHours: days.Sum(x => x.WorkHours),
            AvgHoursAtWork: completed.Count > 0 ? Math.Round(completed.Average(), 2) : 0);

        return new CalendarMonthDto(employeeId, $"{emp.FirstName} {emp.LastName}", emp.Designation, year, month, isSelf, days, summary);
    }

    // ------------------------------------------------------------------ Attendance (self)
    public async Task<TodayAttendanceDto> GetTodayAsync(int employeeId, CancellationToken ct)
    {
        var today = Today;
        var att = await db.Attendance.AsNoTracking()
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.AttendanceDate == today, ct);
        var onLeave = await FullDayLeaveTodayAsync(employeeId, ct);
        string? note = onLeave ? "You are on approved leave today." : null;
        var places = await locations.PlacesForAsync(employeeId, ct);
        var rules = await locations.GetRulesAsync(ct);
        var device = rules.RequireBiometric ? await locations.MyDeviceAsync(employeeId, ct) : null;
        var faceStatus = rules.RequireFace
            ? await db.EmployeeFaces.Where(f => f.EmployeeId == employeeId).Select(f => f.Status).FirstOrDefaultAsync(ct) : null;
        return new TodayAttendanceDto(today, att is null ? null : ToDto(att),
            CanCheckIn: att is null && !onLeave, CanCheckOut: att is { CheckOutAt: null }, note, places.Count > 0 || rules.RequireLocation, places,
            rules.RequireBiometric, device, rules.RequireLocation && places.Count == 0, rules.RequireFace, faceStatus);
    }

    public async Task<TodayAttendanceDto> CheckInAsync(int employeeId, AttendancePunchRequest? punch, CancellationToken ct)
    {
        var today = Today;
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == employeeId && e.IsActive, ct))
            throw new ForbiddenException("Inactive employees cannot mark attendance.");
        if (await db.Attendance.AnyAsync(a => a.EmployeeId == employeeId && a.AttendanceDate == today, ct))
            throw new ConflictException("You have already checked in today.");
        if (await FullDayLeaveTodayAsync(employeeId, ct))
            throw new AppException("You are on approved leave today. Cancel the leave first if you are working.");
        var p = await locations.ValidateAsync(employeeId, punch, "check in", ct);

        var attendance = new AttendanceRecord
        {
            EmployeeId = employeeId, AttendanceDate = today, CheckInAt = Now,
            CheckInLatitude = p.Latitude, CheckInLongitude = p.Longitude, CheckInAccuracy = p.Accuracy, CheckInPlace = p.Place,
            CheckInDistance = p.Distance, CheckInSource = p.Source, CheckInBiometric = p.Biometric, CheckInFaceScore = p.FaceScore,
        };
        db.Attendance.Add(attendance);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)   // double click / two tabs: unique (EmployeeId, AttendanceDate)
        {
            throw new ConflictException("You have already checked in today.");
        }
        await SaveSelfieAsync(attendance.AttendanceId, "In", p, ct);
        return await GetTodayAsync(employeeId, ct);
    }

    public async Task<TodayAttendanceDto> CheckOutAsync(int employeeId, AttendancePunchRequest? punch, CancellationToken ct)
    {
        var today = Today;
        var att = await db.Attendance.FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.AttendanceDate == today, ct)
                  ?? throw new AppException("You have not checked in today.");
        if (att.CheckOutAt is not null) throw new ConflictException("You have already checked out today.");
        var p = await locations.ValidateAsync(employeeId, punch, "check out", ct);
        var now = Now;
        att.CheckOutAt = now < att.CheckInAt ? att.CheckInAt : now;
        (att.CheckOutLatitude, att.CheckOutLongitude, att.CheckOutAccuracy, att.CheckOutPlace, att.CheckOutDistance, att.CheckOutSource, att.CheckOutBiometric) =
            (p.Latitude, p.Longitude, p.Accuracy, p.Place, p.Distance, p.Source, p.Biometric);
        att.CheckOutFaceScore = p.FaceScore;
        await db.SaveChangesAsync(ct);
        await SaveSelfieAsync(att.AttendanceId, "Out", p, ct);
        return await GetTodayAsync(employeeId, ct);
    }

    /// <summary>Keeps the check-in / check-out selfie for HR (only when the face check is on).</summary>
    private async Task SaveSelfieAsync(int attendanceId, string kind, PunchPlace p, CancellationToken ct)
    {
        if (p.Selfie is null || p.FaceScore is null) return;
        db.AttendanceSelfies.Add(new AttendanceSelfie { AttendanceId = attendanceId, Kind = kind, Photo = p.Selfie, Score = p.FaceScore.Value, CapturedAt = time.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Work log
    public async Task<List<WorkLogDto>> GetWorkLogsAsync(int employeeId, DateOnly from, DateOnly to, bool isSelf, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 62) throw new AppException("Please choose a range of up to two months.");
        var editableFrom = Today.AddDays(-WorkLogEditWindowDays);
        return await db.WorkLogs.AsNoTracking()
            .Where(w => w.EmployeeId == employeeId && w.WorkDate >= from && w.WorkDate <= to)
            .OrderBy(w => w.WorkDate).ThenBy(w => w.WorkLogId)
            .Select(w => new WorkLogDto(w.WorkLogId, w.WorkDate, w.Title, w.Details, w.Hours, isSelf && w.WorkDate >= editableFrom))
            .ToListAsync(ct);
    }

    public async Task<WorkLogDto> AddWorkLogAsync(int employeeId, WorkLogRequest req, CancellationToken ct)
    {
        await ValidateWorkLogAsync(employeeId, req, null, ct);
        var log = new WorkLog { EmployeeId = employeeId };
        Apply(log, req);
        db.WorkLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return new WorkLogDto(log.WorkLogId, log.WorkDate, log.Title, log.Details, log.Hours, true);
    }

    public async Task<WorkLogDto> UpdateWorkLogAsync(int employeeId, int id, WorkLogRequest req, CancellationToken ct)
    {
        var log = await LoadOwnLogAsync(employeeId, id, ct);
        await ValidateWorkLogAsync(employeeId, req, id, ct);
        Apply(log, req);
        await db.SaveChangesAsync(ct);
        return new WorkLogDto(log.WorkLogId, log.WorkDate, log.Title, log.Details, log.Hours, true);
    }

    public async Task DeleteWorkLogAsync(int employeeId, int id, CancellationToken ct)
    {
        var log = await LoadOwnLogAsync(employeeId, id, ct);
        db.WorkLogs.Remove(log);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Helpers
    private async Task ValidateWorkLogAsync(int employeeId, WorkLogRequest req, int? excludeId, CancellationToken ct)
    {
        if (req.WorkDate > Today) throw new AppException("You cannot log work for a future date.");
        if (req.WorkDate < Today.AddDays(-WorkLogEditWindowDays))
            throw new AppException($"Work can only be logged for the last {WorkLogEditWindowDays} days.");
        var joined = await db.Employees.Where(e => e.EmployeeId == employeeId).Select(e => e.DateOfJoining).FirstAsync(ct);
        if (req.WorkDate < joined) throw new AppException("You cannot log work before your date of joining.");
        var logged = await db.WorkLogs.Where(w => w.EmployeeId == employeeId && w.WorkDate == req.WorkDate && w.WorkLogId != excludeId)
            .SumAsync(w => (decimal?)w.Hours, ct) ?? 0;
        if (logged + req.Hours > 24)
            throw new AppException($"That would make {logged + req.Hours:0.##} hours on {req.WorkDate:dd MMM} - a day has only 24.");
    }

    private async Task<WorkLog> LoadOwnLogAsync(int employeeId, int id, CancellationToken ct)
    {
        var log = await db.WorkLogs.FirstOrDefaultAsync(w => w.WorkLogId == id, ct) ?? throw new NotFoundException("Work entry not found.");
        if (log.EmployeeId != employeeId) throw new ForbiddenException("You can only change your own work entries.");
        if (log.WorkDate < Today.AddDays(-WorkLogEditWindowDays))
            throw new AppException($"Work entries older than {WorkLogEditWindowDays} days are locked.");
        return log;
    }

    private static void Apply(WorkLog log, WorkLogRequest r)
    {
        log.WorkDate = r.WorkDate;
        log.Title = r.Title.Trim();
        log.Details = string.IsNullOrWhiteSpace(r.Details) ? null : r.Details.Trim();
        log.Hours = Math.Round(r.Hours, 2);
    }

    private Task<bool> FullDayLeaveTodayAsync(int employeeId, CancellationToken ct)
    {
        var today = Today;
        return db.LeaveRecords.AnyAsync(r => r.EmployeeId == employeeId && r.Status == LeaveStatus.Approved &&
                                              !r.IsHalfDay && r.FromDate <= today && r.ToDate >= today, ct);
    }

    private static decimal? Hours(AttendanceRecord? a) =>
        a?.CheckOutAt is DateTime outAt ? Math.Round((decimal)(outAt - a.CheckInAt).TotalHours, 2) : null;

    private static AttendanceDto ToDto(AttendanceRecord a) =>
        new(a.AttendanceDate, a.CheckInAt, a.CheckOutAt, Hours(a), a.CheckInPlace, a.CheckOutPlace, a.CheckInSource, a.CheckOutSource,
            a.CheckInBiometric, a.CheckOutBiometric);
}
