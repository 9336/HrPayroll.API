namespace HrPayroll.Attendance.Entities;

/// <summary>
/// The employee's registered face for selfie check-in: a face "fingerprint" (ArcFace embedding, 512 numbers) and the
/// photo it came from. Approved automatically when it matches the ID-card photo, otherwise by HR.
/// </summary>
public class EmployeeFace
{
    public int EmployeeId { get; set; }
    /// <summary>512 float32 values (little-endian).</summary>
    public byte[] Embedding { get; set; } = default!;
    public byte[] Photo { get; set; } = default!;          // JPEG, max 640 px
    public string Status { get; set; } = EmployeeFaceStatus.Pending;
    /// <summary>Similarity with the ID-card photo when it was registered (null = no ID photo).</summary>
    public decimal? IdPhotoScore { get; set; }
    public DateTime EnrolledAt { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? Note { get; set; }

    public Employee Employee { get; set; } = default!;
    public User? ReviewedBy { get; set; }
}

public static class EmployeeFaceStatus
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected";
}

/// <summary>The selfie taken at check-in or check-out (kept for HR to see).</summary>
public class AttendanceSelfie
{
    public int AttendanceId { get; set; }
    /// <summary>In or Out.</summary>
    public string Kind { get; set; } = default!;
    public byte[] Photo { get; set; } = default!;           // JPEG, max 640 px
    public decimal Score { get; set; }
    public DateTime CapturedAt { get; set; }
}
