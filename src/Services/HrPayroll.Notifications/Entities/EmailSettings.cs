namespace HrPayroll.Notifications.Entities;

/// <summary>How the SMTP connection is secured.</summary>
public static class SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (port 587) - Gmail, Outlook, most providers.</summary>
    public const string StartTls = "StartTls";
    /// <summary>TLS from the first byte (port 465).</summary>
    public const string SslOnConnect = "SslOnConnect";
    /// <summary>Let the client decide from the port.</summary>
    public const string Auto = "Auto";
    /// <summary>No encryption (internal relays only).</summary>
    public const string None = "None";
    public static readonly string[] All = [StartTls, SslOnConnect, Auto, None];
}

/// <summary>
/// Outgoing mail (SMTP) settings edited by an admin - a single row (<see cref="SingletonId"/>). When enabled they
/// take precedence over the "Email" section of appsettings.json. The password is stored encrypted.
/// </summary>
public class EmailSettings
{
    public const int SingletonId = 1;

    public int SettingsId { get; set; } = SingletonId;
    public bool IsEnabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string Security { get; set; } = SmtpSecurity.StartTls;
    public string? Username { get; set; }
    /// <summary>SMTP password / app password, encrypted with ASP.NET Core Data Protection.</summary>
    public string? PasswordProtected { get; set; }
    public string FromAddress { get; set; } = default!;
    public string FromName { get; set; } = default!;
    /// <summary>Comma-separated email categories that are switched off (see EmailCategories).</summary>
    public string? DisabledCategories { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
}
