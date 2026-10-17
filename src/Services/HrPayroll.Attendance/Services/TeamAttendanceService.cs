using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Attendance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HrPayroll.Attendance.Services;

/// <summary>Office hours used to flag late check-ins and early check-outs ("Attendance" section).</summary>
public class AttendanceOptions
{
    public const string Section = "Attendance";

    /// <summary>Office start, office local time (HH:mm).</summary>
    public string OfficeStart { get; set; } = "09:30";
    /// <summary>Office end, office local time (HH:mm).</summary>
    public string OfficeEnd { get; set; } = "18:00";
    /// <summary>Check-ins up to this many minutes after the start are not late.</summary>
    public int GraceMinutes { get; set; } = 15;

    public TimeOnly Start => TimeOnly.TryParse(OfficeStart, out var t) ? t : new TimeOnly(9, 30);
    public TimeOnly End => TimeOnly.TryParse(OfficeEnd, out var t) ? t : new TimeOnly(18, 0);
}

/// <summary>
/// Check-in / check-out times of a team: a daily board and a period register. Reporting managers see everyone
/// under them (directly or through their team leads); HR / admin (employees.view) see everyone.
/// </summary>
public class TeamAttendanceService(AttendanceDbContext db, OrgService org, IOptions<AttendanceOptions> options, TimeProvider time)
{
    public const int MaxRegisterDays = 31;

    private AttendanceOptions Office => options.Value;
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public async Task<TeamAttendanceDayDto> GetDayAsync(CalendarViewer viewer, DateOnly? day, CancellationToken ct)
    {
        var date = day ?? Today;
        var people = await PeopleAsync(viewer, date, date, ct);
        var data = await LoadAsync(people.Select(p => p.EmployeeId).ToList(), date, date, ct);
        var holiday = data.Holidays.GetValueOrDefault(date);
        var isWeekend = IsWeekend(date);

        var rows = people.Select(p =>
        {
            var c = Cell(p.EmployeeId, date, data, isWeekend, holiday);
            var att = data.Attendance.GetValueOrDefault((p.EmployeeId, date));
            return new TeamAttendanceRowDto(p.EmployeeId, p.Name, p.EmployeeCode, p.Designation, p.Department, p.ManagerName,
                c.Status, c.CheckInAt, c.CheckOutAt, c.Hours, c.IsLate, LeftEarly(c, date), c.LeaveCode, c.LeaveStatus, c.HalfDayLeave,
                att?.CheckInPlace, att?.CheckInSource, att?.CheckOutPlace, att?.CheckOutSource, att?.CheckInBiometric ?? false, att?.CheckOutBiometric ?? false,
                att?.CheckInFaceScore, att?.CheckOutFaceScore);
        })
        // Most useful first: people in the office, then those who have not come in, then everyone else.
        .OrderBy(r => r.Status switch { "Present" => r.CheckOutAt is null ? 0 : 1, "NotCheckedIn" => 2, "Absent" => 3, "Leave" => 4, _ => 5 })
        .ThenBy(r => r.CheckInAt).ThenBy(r => r.Name)
        .ToList();

        var summary = new TeamAttendanceSummaryDto(
            Team: rows.Count,
            Present: rows.Count(r => r.Status == "Present"),
            InOffice: rows.Count(r => r.Status == "Present" && r.CheckOutAt is null),
            CheckedOut: rows.Count(r => r.Status == "Present" && r.CheckOutAt is not null),
            NotCheckedIn: rows.Count(r => r.Status == "NotCheckedIn"),
            OnLeave: rows.Count(r => r.Status == "Leave" || r.HalfDayLeave),
            Absent: rows.Count(r => r.Status == "Absent"),
            Late: rows.Count(r => r.IsLate));
        return new TeamAttendanceDayDto(date, date == Today, isWeekend, holiday, Office.Start.ToString("HH:mm"), Office.End.ToString("HH:mm"), summary, rows);
    }

    public async Task<TeamRegisterDto> GetRegisterAsync(CalendarViewer viewer, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new AppException("'To' date must be on or after 'From' date.");
        if (to.DayNumber - from.DayNumber >= MaxRegisterDays) throw new AppException($"Please choose a period of up to {MaxRegisterDays} days.");

        var people = await PeopleAsync(viewer, from, to, ct);
        var data = await LoadAsync(people.Select(p => p.EmployeeId).ToList(), from, to, ct);
        var days = new List<RegisterDayDto>();
        for (var d = from; d <= to; d = d.AddDays(1)) days.Add(new RegisterDayDto(d, IsWeekend(d), data.Holidays.GetValueOrDefault(d)));

        var rows = people.Select(p =>
        {
            var cells = days.Select(d =>
            {
                var c = Cell(p.EmployeeId, d.Date, data, d.IsWeekend, d.Holiday);
                return new RegisterCellDto(c.Status, c.CheckInAt, c.CheckOutAt, c.Hours, c.IsLate,
                    c.LeaveStatus == LeaveStatus.Approved ? c.LeaveCode : null, c.HalfDayLeave && c.LeaveStatus == LeaveStatus.Approved);
            }).ToList();
            var worked = cells.Where(c => c.Hours is not null).Select(c => c.Hours!.Value).ToList();
            return new RegisterRowDto(p.EmployeeId, p.Name, p.EmployeeCode, p.Department, cells,
                PresentDays: cells.Count(c => c.Status == "Present"),
                LeaveDays: cells.Where(c => c.LeaveCode is not null).Sum(c => c.HalfDayLeave ? 0.5m : 1m),
                AbsentDays: cells.Count(c => c.Status == "Absent"),
                LateDays: cells.Count(c => c.IsLate),
                TotalHours: worked.Sum(),
                AvgHours: worked.Count > 0 ? Math.Round(worked.Average(), 2) : 0);
        }).ToList();
        return new TeamRegisterDto(from, to, Office.Start.ToString("HH:mm"), days, rows);
    }

    // ------------------------------------------------------------------ Helpers
    private record Person(int EmployeeId, string Name, string EmployeeCode, string Designation, string Department, string? ManagerName);

    private record Loaded(
        Dictionary<(int, DateOnly), AttendanceRecord> Attendance,
        List<LeaveSpan> Leaves,
        Dictionary<DateOnly, string> Holidays,
        Dictionary<int, DateOnly> TrackingStart,
        Dictionary<int, DateOnly> Joined);

    private record LeaveSpan(int EmployeeId, DateOnly From, DateOnly To, bool IsHalfDay, string Status, string Code);

    private record DayCell(string Status, DateTime? CheckInAt, DateTime? CheckOutAt, decimal? Hours, bool IsLate,
        string? LeaveCode, string? LeaveStatus, bool HalfDayLeave);

    /// <summary>Active employees the viewer may see who had joined by the end of the period (the viewer is not included).</summary>
    private async Task<List<Person>> PeopleAsync(CalendarViewer viewer, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var q = db.Employees.AsNoTracking().Where(e => e.IsActive && e.DateOfJoining <= to);
        if (!viewer.CanViewAll)
        {
            if (viewer.EmployeeId is not int me) throw new ForbiddenException("Your login is not linked to an employee profile.");
            var team = await org.GetSubordinateIdsAsync(me, ct);
            if (team.Count == 0) throw new ForbiddenException("Nobody reports to you, so there is no team attendance to show.");
            q = q.Where(e => team.Contains(e.EmployeeId));
        }
        else if (viewer.EmployeeId is int self)
            q = q.Where(e => e.EmployeeId != self);

        return await q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new Person(e.EmployeeId, e.FirstName + " " + e.LastName, e.EmployeeCode, e.Designation, e.Department.Name,
                e.Manager != null ? e.Manager.FirstName + " " + e.Manager.LastName : null))
            .ToListAsync(ct);
    }

    private async Task<Loaded> LoadAsync(List<int> ids, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var attendance = await db.Attendance.AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.AttendanceDate >= from && a.AttendanceDate <= to)
            .ToDictionaryAsync(a => (a.EmployeeId, a.AttendanceDate), ct);
        var leaves = await db.LeaveRecords.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.FromDate <= to && r.ToDate >= from &&
                        (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending))
            .Select(r => new LeaveSpan(r.EmployeeId, r.FromDate, r.ToDate, r.IsHalfDay, r.Status, r.LeaveCode))
            .ToListAsync(ct);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.HolidayDate >= from && h.HolidayDate <= to)
            .ToDictionaryAsync(h => h.HolidayDate, h => h.Name, ct);
        // Days before someone's first ever check-in are "no record", not "absent" (attendance tracking started later).
        var trackingStart = await db.Attendance.Where(a => ids.Contains(a.EmployeeId))
            .GroupBy(a => a.EmployeeId).Select(g => new { g.Key, First = g.Min(a => a.AttendanceDate) })
            .ToDictionaryAsync(x => x.Key, x => x.First, ct);
        var joined = await db.Employees.Where(e => ids.Contains(e.EmployeeId))
            .ToDictionaryAsync(e => e.EmployeeId, e => e.DateOfJoining, ct);
        return new Loaded(attendance, leaves, holidays, trackingStart, joined);
    }

    private DayCell Cell(int employeeId, DateOnly date, Loaded data, bool isWeekend, string? holiday)
    {
        var att = data.Attendance.GetValueOrDefault((employeeId, date));
        var working = !isWeekend && holiday is null;
        var leave = working
            ? data.Leaves.Where(l => l.EmployeeId == employeeId && l.From <= date && l.To >= date)
                .OrderBy(l => l.Status == LeaveStatus.Approved ? 0 : 1).FirstOrDefault()
            : null;
        var today = Today;

        var status =
            date < data.Joined.GetValueOrDefault(employeeId) ? "NotJoined" :
            att is not null ? "Present" :
            holiday is not null ? "Holiday" :
            isWeekend ? "Weekend" :
            leave is { Status: LeaveStatus.Approved } ? "Leave" :   // full or half day, and no check-in
            date > today ? "Upcoming" :
            date == today ? "NotCheckedIn" :
            !data.TrackingStart.TryGetValue(employeeId, out var start) || date < start ? "NoRecord" :
            "Absent";

        var hours = att?.CheckOutAt is DateTime outAt ? Math.Round((decimal)(outAt - att.CheckInAt).TotalHours, 2) : (decimal?)null;
        var lateAfter = Office.Start.AddMinutes(Office.GraceMinutes);
        // A half-day leave in the morning explains a late arrival, so it is not flagged.
        var isLate = att is not null && working && leave is not { Status: LeaveStatus.Approved, IsHalfDay: true }
                     && TimeOnly.FromDateTime(att.CheckInAt) > lateAfter;
        return new DayCell(status, att?.CheckInAt, att?.CheckOutAt, hours, isLate, leave?.Code, leave?.Status, leave?.IsHalfDay ?? false);
    }

    private bool LeftEarly(DayCell c, DateOnly date) =>
        c.CheckOutAt is DateTime outAt && c.Status == "Present" && !IsWeekend(date) && !c.HalfDayLeave
        && TimeOnly.FromDateTime(outAt) < Office.End;

    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
