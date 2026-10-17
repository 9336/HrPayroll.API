namespace HrPayroll.Attendance.Entities;

/// <summary>One day's attendance (check-in / check-out, office local time).</summary>
/// <remarks>Table dbo.Attendance (the class name differs from the service namespace).</remarks>
public class AttendanceRecord
{
    public int AttendanceId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly AttendanceDate { get; set; }
    public DateTime CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string? Notes { get; set; }

    // Where the employee was when they checked in / out (null = not captured, e.g. no area applied to them).
    public decimal? CheckInLatitude { get; set; }
    public decimal? CheckInLongitude { get; set; }
    public int? CheckInAccuracy { get; set; }     // metres (GPS accuracy)
    public string? CheckInPlace { get; set; }     // name of the attendance area they were in
    public int? CheckInDistance { get; set; }     // metres from that area's centre
    public string? CheckInSource { get; set; }    // Web / Mobile
    public decimal? CheckOutLatitude { get; set; }
    public decimal? CheckOutLongitude { get; set; }
    public int? CheckOutAccuracy { get; set; }
    public string? CheckOutPlace { get; set; }
    public int? CheckOutDistance { get; set; }
    public string? CheckOutSource { get; set; }
    /// <summary>Confirmed with Face ID / fingerprint on the employee's registered phone.</summary>
    public bool CheckInBiometric { get; set; }
    public bool CheckOutBiometric { get; set; }
    /// <summary>Selfie face-check similarity (null = no face check).</summary>
    public decimal? CheckInFaceScore { get; set; }
    public decimal? CheckOutFaceScore { get; set; }

    public Employee Employee { get; set; } = default!;
}

/// <summary>Company-wide attendance rules (a single row, Id = 1).</summary>
public class AttendanceSettings
{
    public int Id { get; set; } = 1;
    /// <summary>Everyone must be inside one of their attendance areas; employees with no area cannot check in.</summary>
    public bool RequireLocation { get; set; }
    /// <summary>Check in / out only from the mobile app, confirmed with Face ID / fingerprint on the registered phone.</summary>
    public bool RequireBiometric { get; set; }
    /// <summary>Check in / out with a selfie that must match the employee's registered face.</summary>
    public bool RequireFace { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
}

/// <summary>
/// The phone an employee checks in with. The app creates a key pair in the phone's secure hardware that can only be
/// used after Face ID / fingerprint; the public key is kept here and every check-in is signed with the private key.
/// One active phone per employee - changing phones needs HR to reset the old one.
/// </summary>
public class AttendanceDevice
{
    public int DeviceId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>Random id the app generates when it registers (identifies the installation).</summary>
    public Guid InstallId { get; set; }
    public string Platform { get; set; } = default!;      // Android / iOS
    public string DeviceName { get; set; } = default!;    // e.g. "Samsung SM-A546E"
    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, base64).</summary>
    public string PublicKey { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int? RevokedByUserId { get; set; }

    public Employee Employee { get; set; } = default!;
}

/// <summary>
/// An area (centre + radius) employees must be inside to check in / out - an office, a branch or a client site.
/// Applies to every employee, or only to the employees assigned to it.
/// </summary>
public class AttendanceLocation
{
    public int LocationId { get; set; }
    public string Name { get; set; } = default!;
    public string? Address { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int RadiusMeters { get; set; }
    public bool AllEmployees { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<AttendanceLocationEmployee> Employees { get; set; } = [];
}

public class AttendanceLocationEmployee
{
    public int LocationId { get; set; }
    public int EmployeeId { get; set; }

    public Employee Employee { get; set; } = default!;
}

/// <summary>Work an employee logged for a day (shown on their calendar).</summary>
public class WorkLog
{
    public int WorkLogId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string Title { get; set; } = default!;
    public string? Details { get; set; }
    public decimal Hours { get; set; }
    public DateTime CreatedAt { get; set; }

    public Employee Employee { get; set; } = default!;
}
