using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Attendance.Controllers;

/// <summary>
/// Employee calendar, attendance and work log. Everyone sees their own; reporting managers their team;
/// users with employees.view everyone (read only).
/// </summary>
[ApiController]
[Route("api/calendar")]
[Authorize]
public class CalendarController(CalendarService calendar, TeamAttendanceService team, AttendanceLocationService locations, AttendanceFaceService faces) : ControllerBase
{
    /// <summary>
    /// The selfie taken at check-in (kind=in) or check-out (kind=out) - for the employee, their managers and
    /// users who can see everyone's attendance.
    /// </summary>
    [HttpGet("selfie")]
    public async Task<IActionResult> Selfie([FromQuery] int employeeId, [FromQuery] DateOnly date, [FromQuery] string kind, CancellationToken ct)
    {
        await calendar.EnsureCanViewAsync(Viewer, employeeId, ct);
        var photo = await faces.SelfieAsync(employeeId, date, string.Equals(kind, "out", StringComparison.OrdinalIgnoreCase) ? "Out" : "In", ct);
        if (photo is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(photo, "image/jpeg");
    }

    // ---------------------------------------------------------------- Face ID / fingerprint check-in (mobile app)
    /// <summary>Links this phone's biometric-protected key to the signed-in employee (one phone per employee).</summary>
    [HttpPost("attendance-device")]
    public async Task<ActionResult<MyDeviceDto>> RegisterDevice(RegisterDeviceRequest request, CancellationToken ct) =>
        Ok(await locations.RegisterDeviceAsync(User.RequireEmployeeId(), request, ct));

    /// <summary>One-time value the phone signs after Face ID / fingerprint, valid for 2 minutes.</summary>
    [HttpPost("attendance-challenge")]
    public ActionResult<AttendanceChallengeDto> IssueChallenge() => Ok(locations.IssueChallenge(User.RequireEmployeeId()));

    private CalendarViewer Viewer => new(User.GetEmployeeId(),
        User.HasPermission(Permissions.EmployeesView));

    /// <summary>Month view (defaults to the signed-in employee).</summary>
    [HttpGet]
    public async Task<ActionResult<CalendarMonthDto>> Month([FromQuery] int year, [FromQuery] int month, [FromQuery] int? employeeId,
        CancellationToken ct)
    {
        var target = employeeId ?? User.RequireEmployeeId();
        await calendar.EnsureCanViewAsync(Viewer, target, ct);
        return Ok(await calendar.GetMonthAsync(target, year, month, target == User.GetEmployeeId(), ct));
    }

    /// <summary>People whose calendar the caller can open.</summary>
    [HttpGet("people")]
    public async Task<ActionResult<List<CalendarPersonDto>>> People(CancellationToken ct) =>
        Ok(await calendar.GetPeopleAsync(Viewer, ct));

    // ---------------------------------------------------------------- Attendance (self)
    [HttpGet("today")]
    public async Task<ActionResult<TodayAttendanceDto>> Today(CancellationToken ct) =>
        Ok(await calendar.GetTodayAsync(User.RequireEmployeeId(), ct));

    [HttpPost("check-in")]
    public async Task<ActionResult<TodayAttendanceDto>> CheckIn(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] AttendancePunchRequest? request, CancellationToken ct) =>
        Ok(await calendar.CheckInAsync(User.RequireEmployeeId(), request, ct));

    [HttpPost("check-out")]
    public async Task<ActionResult<TodayAttendanceDto>> CheckOut(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] AttendancePunchRequest? request, CancellationToken ct) =>
        Ok(await calendar.CheckOutAsync(User.RequireEmployeeId(), request, ct));

    // ---------------------------------------------------------------- Team attendance (managers: their team; HR / admin: everyone)
    /// <summary>Check-in / check-out of everyone in the caller's team on one day (default today).</summary>
    [HttpGet("team-attendance")]
    public async Task<ActionResult<TeamAttendanceDayDto>> TeamDay([FromQuery] DateOnly? date, CancellationToken ct) =>
        Ok(await team.GetDayAsync(Viewer, date, ct));

    /// <summary>Attendance register (up to 31 days): each person × each day with check-in / check-out.</summary>
    [HttpGet("team-attendance/register")]
    public async Task<ActionResult<TeamRegisterDto>> TeamRegister([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) =>
        Ok(await team.GetRegisterAsync(Viewer, from, to, ct));

    // ---------------------------------------------------------------- Work log
    [HttpGet("worklogs")]
    public async Task<ActionResult<List<WorkLogDto>>> WorkLogs([FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] int? employeeId, CancellationToken ct)
    {
        var target = employeeId ?? User.RequireEmployeeId();
        await calendar.EnsureCanViewAsync(Viewer, target, ct);
        return Ok(await calendar.GetWorkLogsAsync(target, from, to, target == User.GetEmployeeId(), ct));
    }

    [HttpPost("worklogs")]
    public async Task<ActionResult<WorkLogDto>> AddWorkLog(WorkLogRequest request, CancellationToken ct) =>
        Ok(await calendar.AddWorkLogAsync(User.RequireEmployeeId(), request, ct));

    [HttpPut("worklogs/{id:int}")]
    public async Task<ActionResult<WorkLogDto>> UpdateWorkLog(int id, WorkLogRequest request, CancellationToken ct) =>
        Ok(await calendar.UpdateWorkLogAsync(User.RequireEmployeeId(), id, request, ct));

    [HttpDelete("worklogs/{id:int}")]
    public async Task<IActionResult> DeleteWorkLog(int id, CancellationToken ct)
    {
        await calendar.DeleteWorkLogAsync(User.RequireEmployeeId(), id, ct);
        return NoContent();
    }
}
