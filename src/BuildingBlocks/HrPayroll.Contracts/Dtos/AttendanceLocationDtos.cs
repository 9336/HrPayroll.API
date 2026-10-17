using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

/// <summary>An attendance area for the admin page.</summary>
public record AttendanceLocationDto(
    int LocationId,
    string Name,
    string? Address,
    decimal Latitude,
    decimal Longitude,
    int RadiusMeters,
    bool AllEmployees,
    bool IsActive,
    IReadOnlyList<CalendarPersonDto> Employees,
    int CheckInsLast30Days,
    DateTime CreatedAt);

public class AttendanceLocationRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = default!;
    [MaxLength(300)] public string? Address { get; set; }
    [Range(-90, 90)] public decimal Latitude { get; set; }
    [Range(-180, 180)] public decimal Longitude { get; set; }
    /// <summary>How far from the centre employees may be (metres).</summary>
    [Range(AttendanceRules.MinRadius, AttendanceRules.MaxRadius)] public int RadiusMeters { get; set; } = 150;
    public bool AllEmployees { get; set; } = true;
    /// <summary>Employees this area applies to when <see cref="AllEmployees"/> is false.</summary>
    public List<int> EmployeeIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

/// <summary>An area the signed-in employee may check in from (shown in the app with the distance to it).</summary>
public record AttendancePlaceDto(int LocationId, string Name, string? Address, decimal Latitude, decimal Longitude, int RadiusMeters);

/// <summary>
/// Check-in / check-out. Coordinates are required when an attendance area applies to the employee.
/// The mobile app sends <see cref="IsMock"/> = true when the position comes from a fake-GPS app.
/// </summary>
public class AttendancePunchRequest
{
    [Range(-90, 90)] public decimal? Latitude { get; set; }
    [Range(-180, 180)] public decimal? Longitude { get; set; }
    [Range(0, 100000)] public double? AccuracyMeters { get; set; }
    public bool IsMock { get; set; }
    /// <summary>Web or Mobile.</summary>
    [MaxLength(10)] public string? Source { get; set; }

    // Face ID / fingerprint (when the company requires it): the registered phone signs the one-time challenge.
    public Guid? InstallId { get; set; }
    [MaxLength(100)] public string? Challenge { get; set; }
    [MaxLength(200)] public string? Signature { get; set; }

    /// <summary>Selfie (JPEG, base64) when the company requires the face check.</summary>
    [MaxLength(6_000_000)] public string? Selfie { get; set; }
}

/// <summary>The employee's face registration (null status = not registered).</summary>
public record FaceStatusDto(string? Status, DateTime? EnrolledAt, string? Note, bool HasIdPhoto);

/// <summary>An employee's registered face, for HR.</summary>
public record EmployeeFaceDto(int EmployeeId, string Status, DateTime EnrolledAt, decimal? IdPhotoScore, string? Note, string? ReviewedBy,
    DateTime? ReviewedAt, string PhotoDataUrl);

public record FaceEnrollRequest([Required, MaxLength(6_000_000)] string Selfie);

public record FaceReviewRequest([MaxLength(300)] string? Note);

/// <summary>
/// What the phone signs for a check-in / check-out: "challenge|employeeId|action|latitude|longitude" (action = check-in /
/// check-out, coordinates with 6 decimals, invariant culture, "-" when not sent).
/// </summary>
public static class PunchPayload
{
    public static string Build(string challenge, int employeeId, string action, decimal? lat, decimal? lng) =>
        string.Join('|', challenge, employeeId, action, Coord(lat), Coord(lng));

    private static string Coord(decimal? v) => v is decimal d ? Math.Round(d, 6).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) : "-";
}

/// <summary>Company-wide attendance rules with how many people they would currently block.</summary>
public record AttendanceSettingsDto(bool RequireLocation, bool RequireBiometric, int EmployeesWithoutArea, int EmployeesWithoutPhone, DateTime? UpdatedAt,
    bool RequireFace = false, int EmployeesWithoutFace = 0);

public record AttendanceSettingsRequest(bool RequireLocation, bool RequireBiometric, bool RequireFace = false);

/// <summary>An employee's registered check-in phone (admin list).</summary>
public record AttendanceDeviceDto(int DeviceId, int EmployeeId, string EmployeeName, string EmployeeCode, string Platform, string DeviceName,
    DateTime RegisteredAt, DateTime? LastUsedAt);

/// <summary>The signed-in employee's registered phone.</summary>
public record MyDeviceDto(Guid InstallId, string Platform, string DeviceName, DateTime RegisteredAt);

public class RegisterDeviceRequest
{
    [Required] public Guid InstallId { get; set; }
    [Required, MaxLength(20)] public string Platform { get; set; } = default!;
    [Required, MaxLength(100)] public string DeviceName { get; set; } = default!;
    /// <summary>ECDSA P-256 public key, SubjectPublicKeyInfo (X.509) DER, base64.</summary>
    [Required, MaxLength(500)] public string PublicKey { get; set; } = default!;
}

public record AttendanceChallengeDto(string Challenge, DateTime ExpiresAt);

public static class AttendanceRules
{
    public const int MinRadius = 20, MaxRadius = 5000;
    /// <summary>Readings less accurate than this are refused (the phone does not really know where it is).</summary>
    public const int MaxAccuracyMeters = 150;
    /// <summary>GPS jitter allowed past the edge of the area.</summary>
    public const int EdgeToleranceMeters = 25;
}
