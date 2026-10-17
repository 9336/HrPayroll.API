namespace HrPayroll.Employees.Entities;

/// <summary>
/// A document the employee uploads themselves (ID proof, certificates…), e.g. from the mobile app. HR verifies or
/// rejects it. The file itself is in <see cref="EmployeeUploadFile"/> so lists stay light.
/// </summary>
public class EmployeeUpload
{
    public int UploadId { get; set; }
    public int EmployeeId { get; set; }
    public string Category { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public int SizeBytes { get; set; }
    public string Status { get; set; } = EmployeeUploadStatus.Pending;
    public DateTime UploadedAt { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public Employee Employee { get; set; } = default!;
    public User? ReviewedBy { get; set; }
    public EmployeeUploadFile? File { get; set; }
}

public class EmployeeUploadFile
{
    public int UploadId { get; set; }
    public byte[] Data { get; set; } = default!;
}

public static class EmployeeUploadStatus
{
    public const string Pending = "Pending", Verified = "Verified", Rejected = "Rejected";
}

/// <summary>What employees can upload.</summary>
public static class EmployeeUploadCategory
{
    public static readonly IReadOnlyList<string> All =
    [
        "Aadhaar card", "PAN card", "Passport", "Driving licence", "Voter ID", "Address proof", "Bank proof (cancelled cheque / passbook)",
        "Education certificate", "Experience / relieving letter", "Previous payslips", "Medical certificate", "Photo", "Other",
    ];
}
