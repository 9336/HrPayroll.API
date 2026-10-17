namespace HrPayroll.Notifications.Entities;

public static class EmailStatus
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    /// <summary>SMTP is not configured - the message was written to the log instead of being sent.</summary>
    public const string Logged = "Logged";
    /// <summary>This kind of email is switched off on the Email Settings page.</summary>
    public const string Skipped = "Skipped";
}

/// <summary>Outbox entry. Emails are queued with the business change and sent by the background dispatcher.</summary>
public class EmailMessage
{
    public long EmailId { get; set; }
    public string ToAddress { get; set; } = default!;
    public string? ToName { get; set; }
    public string Subject { get; set; } = default!;
    public string HtmlBody { get; set; } = default!;
    public string Category { get; set; } = "General";
    public string Status { get; set; } = EmailStatus.Pending;
    /// <summary>Do not send before this time (office local time).</summary>
    public DateTime NotBefore { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
