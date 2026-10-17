namespace HrPayroll.Helpdesk.Entities;

public static class VisitStatus
{
    public const string Expected = "Expected";
    public const string CheckedIn = "CheckedIn";
    public const string CheckedOut = "CheckedOut";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A visitor coming to meet an employee (the host). Visit times (Expected / CheckIn / CheckOut) are
/// office local time; CreatedAt is UTC like the other audit columns.
/// </summary>
public class Visit
{
    public int VisitId { get; set; }
    public string VisitorName { get; set; } = default!;
    public string VisitorPhone { get; set; } = default!;
    public string? VisitorEmail { get; set; }
    public string? VisitorCompany { get; set; }
    public string Purpose { get; set; } = default!;
    public int HostEmployeeId { get; set; }
    public DateTime? ExpectedAt { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string Status { get; set; } = VisitStatus.Expected;
    public string? IdProofType { get; set; }
    /// <summary>Only the last 4 characters of the ID document are stored.</summary>
    public string? IdProofLast4 { get; set; }
    public string? BadgeNumber { get; set; }
    public string? Notes { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Employee Host { get; set; } = default!;
    public User? CreatedBy { get; set; }
}
