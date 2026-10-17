using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

/// <summary>Current settings (the password itself is never returned) plus how mail is being delivered right now.</summary>
public record EmailSettingsDto(
    bool IsEnabled,
    string? Host,
    int Port,
    string Security,
    string? Username,
    bool HasPassword,
    string FromAddress,
    string FromName,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    /// <summary>Settings (this page), AppSettings (appsettings.json) or LogOnly (nothing configured - emails are only logged).</summary>
    string Mode,
    EmailDeliveryStatsDto Stats,
    IReadOnlyList<EmailNotificationDto> Notifications);

/// <summary>A kind of system email and whether it is sent.</summary>
public record EmailNotificationDto(string Code, string Name, string Description, bool Enabled, int SentLast30Days);

public record EmailDeliveryStatsDto(int Pending, int SentLast24h, int FailedLast7d, int LoggedLast7d, DateTime? LastSentAt, string? LastError);

public class EmailSettingsRequest
{
    public bool IsEnabled { get; set; }
    [MaxLength(200)] public string? Host { get; set; }
    [Range(1, 65535)] public int Port { get; set; } = 587;
    [Required] public string Security { get; set; } = "StartTls";
    [MaxLength(200)] public string? Username { get; set; }
    /// <summary>New password; leave empty to keep the saved one.</summary>
    [MaxLength(500)] public string? Password { get; set; }
    /// <summary>Remove the saved password (servers that need no login).</summary>
    public bool ClearPassword { get; set; }
    [Required, MaxLength(200), EmailAddress] public string FromAddress { get; set; } = default!;
    [Required, MaxLength(100)] public string FromName { get; set; } = default!;
    /// <summary>Kinds of email to stop sending (null = leave as they are).</summary>
    public List<string>? DisabledCategories { get; set; }
}

/// <summary>Sends a test email with the settings in the form (saved or not).</summary>
public class EmailTestRequest
{
    [Required] public EmailSettingsRequest Settings { get; set; } = default!;
    [Required, MaxLength(200), EmailAddress] public string To { get; set; } = default!;
}

public record EmailTestResultDto(bool Success, string Message, int ElapsedMs);
