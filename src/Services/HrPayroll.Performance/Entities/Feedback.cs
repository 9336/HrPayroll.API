namespace HrPayroll.Performance.Entities;

public static class FeedbackKind
{
    public const string Appreciation = "Appreciation";
    public const string Complaint = "Complaint";
}

public static class FeedbackSource
{
    public const string Client = "Client";
    public const string SeniorManagement = "SeniorManagement";
    public const string Manager = "Manager";
    public const string Colleague = "Colleague";
    public const string Other = "Other";
    public static readonly string[] All = [Client, SeniorManagement, Manager, Colleague, Other];
}

public static class FeedbackSeverity
{
    public const string Minor = "Minor";
    public const string Major = "Major";
    public const string Critical = "Critical";
    public static readonly string[] All = [Minor, Major, Critical];
}

/// <summary>
/// An appreciation or complaint about an employee - from a client, senior management, a colleague… Recorded by the
/// employee's managers or HR, and shown to the reviewer in the appraisal covering its date.
/// </summary>
public class EmployeeFeedback
{
    public int FeedbackId { get; set; }
    public int EmployeeId { get; set; }
    public string Kind { get; set; } = default!;
    public string Source { get; set; } = default!;
    /// <summary>Person who gave the feedback (e.g. "Ravi Menon, CTO").</summary>
    public string GivenBy { get; set; } = default!;
    /// <summary>Client / organisation, for client feedback.</summary>
    public string? Organisation { get; set; }
    public DateOnly EventDate { get; set; }
    public string Title { get; set; } = default!;
    public string Details { get; set; } = default!;
    /// <summary>Complaints only: Minor / Major / Critical.</summary>
    public string? Severity { get; set; }
    /// <summary>Visible to the employee (appreciations usually are; complaints once discussed).</summary>
    public bool SharedWithEmployee { get; set; }
    /// <summary>Complaints: open until resolved.</summary>
    public bool IsResolved { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedByUserId { get; set; }
    public string? EmployeeResponse { get; set; }
    public DateTime? RespondedAt { get; set; }
    public string? AttachmentName { get; set; }
    public int RecordedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Employee Employee { get; set; } = default!;
    public User RecordedBy { get; set; } = default!;
    public User? ResolvedBy { get; set; }
    public FeedbackAttachment? Attachment { get; set; }
}

/// <summary>Evidence file (client email, screenshot…) - loaded only on download.</summary>
public class FeedbackAttachment
{
    public int FeedbackId { get; set; }
    public string ContentType { get; set; } = default!;
    public byte[] Data { get; set; } = default!;
}
