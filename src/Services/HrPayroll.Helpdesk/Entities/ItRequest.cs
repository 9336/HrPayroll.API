namespace HrPayroll.Helpdesk.Entities;

public static class ItRequestTypes
{
    public const string Laptop = "Laptop";        // laptop / desktop
    public const string Hardware = "Hardware";    // monitor, mouse, headset, docking station…
    public const string Software = "Software";    // installation / licence
    public const string Access = "Access";        // VPN, email group, system / folder access
    public const string Other = "Other";
    public static readonly string[] All = [Laptop, Hardware, Software, Access, Other];
}

/// <summary>
/// Flow: PendingApproval -(reporting manager)-> Approved -(IT)-> InProgress -> Completed.
/// The manager can reject; IT can reject an approved request; the requester can cancel until IT starts work.
/// </summary>
public static class ItRequestStatus
{
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [PendingApproval, Approved, InProgress, Completed, Rejected, Cancelled];
    public static readonly string[] Open = [PendingApproval, Approved, InProgress];
}

/// <summary>An IT service request (laptop, software installation, access…) raised by an employee.</summary>
public class ItRequest
{
    public int RequestId { get; set; }
    public int EmployeeId { get; set; }
    public string RequestType { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Details { get; set; } = default!;
    public string Priority { get; set; } = "Medium";
    public DateOnly? RequiredBy { get; set; }
    public string Status { get; set; } = ItRequestStatus.PendingApproval;
    /// <summary>Reporting manager when the request was raised (null = no manager, goes straight to IT).</summary>
    public int? ApproverEmployeeId { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalRemarks { get; set; }
    public int? AssignedToUserId { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Employee Employee { get; set; } = default!;
    public Employee? Approver { get; set; }
    public User? ApprovedBy { get; set; }
    public User? AssignedTo { get; set; }
    public List<ItRequestEvent> Events { get; set; } = [];

    /// <summary>Human-readable id, e.g. ITR-00042.</summary>
    public static string Code(int id) => $"ITR-{id:D5}";
}

/// <summary>Timeline entry: every status change and comment on a request.</summary>
public class ItRequestEvent
{
    public long EventId { get; set; }
    public int RequestId { get; set; }
    public int ByUserId { get; set; }
    /// <summary>Submitted, Approved, Rejected, Assigned, Started, Completed, Cancelled, Comment.</summary>
    public string Action { get; set; } = default!;
    public string? Note { get; set; }
    public DateTime At { get; set; }

    public User By { get; set; } = default!;
}
