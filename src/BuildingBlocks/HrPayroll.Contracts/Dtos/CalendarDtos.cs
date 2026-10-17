using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record AttendanceDto(DateOnly Date, DateTime CheckInAt, DateTime? CheckOutAt, decimal? Hours,
    string? CheckInPlace = null, string? CheckOutPlace = null, string? CheckInSource = null, string? CheckOutSource = null,
    bool CheckInBiometric = false, bool CheckOutBiometric = false);

/// <summary>
/// The signed-in employee's attendance for today and what they can do next. When <see cref="LocationRequired"/> is
/// true the employee must be inside one of <see cref="Places"/> to check in / out.
/// </summary>
public record TodayAttendanceDto(DateOnly Date, AttendanceDto? Attendance, bool CanCheckIn, bool CanCheckOut, string? Note,
    bool LocationRequired = false, IReadOnlyList<AttendancePlaceDto>? Places = null,
    /// <summary>Only the mobile app with Face ID / fingerprint on <see cref="MyDevice"/> can check in / out.</summary>
    bool BiometricRequired = false, MyDeviceDto? MyDevice = null,
    /// <summary>The company requires an office location and none is set for this employee.</summary>
    bool NoAreaAssigned = false,
    /// <summary>A selfie matching the registered face is required; <see cref="FaceStatus"/> = Pending / Approved / Rejected / null.</summary>
    bool FaceRequired = false, string? FaceStatus = null);

/// <summary>
/// One calendar day. Status: Present, Leave, Holiday, Weekend, Absent, NoRecord (before attendance tracking
/// started for the employee), Today, Upcoming, NotJoined.
/// </summary>
public record CalendarDayDto(
    DateOnly Date,
    string Status,
    bool IsWeekend,
    string? Holiday,
    DateTime? CheckInAt,
    DateTime? CheckOutAt,
    decimal? HoursAtWork,
    string? LeaveCode,
    string? LeaveName,
    string? LeaveStatus,     // Approved / Pending
    bool HalfDayLeave,
    decimal WorkHours,
    int WorkEntries);

public record CalendarSummaryDto(
    int WorkingDays,
    int Present,
    decimal LeaveDays,
    int Absent,
    int Holidays,
    decimal WorkHours,
    decimal AvgHoursAtWork);

public record CalendarMonthDto(
    int EmployeeId,
    string EmployeeName,
    string Designation,
    int Year,
    int Month,
    bool IsSelf,
    IReadOnlyList<CalendarDayDto> Days,
    CalendarSummaryDto Summary);

public record WorkLogDto(int WorkLogId, DateOnly WorkDate, string Title, string? Details, decimal Hours, bool CanEdit);

public class WorkLogRequest
{
    [Required] public DateOnly WorkDate { get; set; }
    [Required, MaxLength(150)] public string Title { get; set; } = default!;
    [MaxLength(1000)] public string? Details { get; set; }
    [Range(0.25, 24)] public decimal Hours { get; set; }
}

// ---------------------------------------------------------------- Team attendance (managers / HR)
/// <summary>
/// One person's attendance on one day. Status: Present, Leave, Holiday, Weekend, Absent, NotCheckedIn (today),
/// NoRecord (before attendance tracking started for them), Upcoming.
/// </summary>
public record TeamAttendanceRowDto(
    int EmployeeId,
    string Name,
    string EmployeeCode,
    string Designation,
    string Department,
    string? ManagerName,
    string Status,
    DateTime? CheckInAt,
    DateTime? CheckOutAt,
    decimal? Hours,
    bool IsLate,
    bool LeftEarly,
    string? LeaveCode,
    string? LeaveStatus,
    bool HalfDayLeave,
    string? CheckInPlace = null,      // attendance area they checked in from
    string? CheckInSource = null,     // Web / Mobile
    string? CheckOutPlace = null,
    string? CheckOutSource = null,
    bool CheckInBiometric = false,
    bool CheckOutBiometric = false,
    decimal? CheckInFaceScore = null,     // selfie face check (0–1)
    decimal? CheckOutFaceScore = null);

public record TeamAttendanceSummaryDto(int Team, int Present, int InOffice, int CheckedOut, int NotCheckedIn, int OnLeave, int Absent, int Late);

public record TeamAttendanceDayDto(
    DateOnly Date,
    bool IsToday,
    bool IsWeekend,
    string? Holiday,
    string OfficeStart,
    string OfficeEnd,
    TeamAttendanceSummaryDto Summary,
    IReadOnlyList<TeamAttendanceRowDto> Rows);

public record RegisterDayDto(DateOnly Date, bool IsWeekend, string? Holiday);

public record RegisterCellDto(string Status, DateTime? CheckInAt, DateTime? CheckOutAt, decimal? Hours, bool IsLate, string? LeaveCode, bool HalfDayLeave);

public record RegisterRowDto(
    int EmployeeId,
    string Name,
    string EmployeeCode,
    string Department,
    IReadOnlyList<RegisterCellDto> Cells,
    int PresentDays,
    decimal LeaveDays,
    int AbsentDays,
    int LateDays,
    decimal TotalHours,
    decimal AvgHours);

/// <summary>Attendance register: everyone in the caller's scope × each day of the period.</summary>
public record TeamRegisterDto(DateOnly From, DateOnly To, string OfficeStart, IReadOnlyList<RegisterDayDto> Days, IReadOnlyList<RegisterRowDto> Rows);

/// <summary>Someone whose calendar the caller may open (self, team, or everyone for HR / admin).</summary>
public record CalendarPersonDto(int EmployeeId, string FullName, string Designation, string Department);
