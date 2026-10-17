using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Notifications.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Notifications.Entities;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

using HrPayroll.BuildingBlocks.Email;

namespace HrPayroll.Notifications.Services;

/// <summary>SMTP connection details actually used for sending.</summary>
public record SmtpConfig(string Host, int Port, string Security, string? Username, string? Password, string FromAddress, string FromName,
    string Source, bool PasswordUnreadable = false);

/// <summary>
/// Outgoing mail settings: the admin page (stored in the database, password encrypted) wins over appsettings.json;
/// with neither, emails are only written to the log.
/// </summary>
public class EmailSettingsService(NotificationsDbContext db, IDataProtectionProvider protection, IOptions<EmailOptions> options,
    TimeProvider time, ILogger<EmailSettingsService> logger)
{
    private IDataProtector Protector => protection.CreateProtector("HrPayroll.EmailSettings.Password.v1");

    /// <summary>The SMTP settings to use now, or null when sending is not configured (log only).</summary>
    public async Task<SmtpConfig?> ResolveAsync(CancellationToken ct)
    {
        var s = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is { IsEnabled: true } && !string.IsNullOrWhiteSpace(s.Host))
        {
            var (password, unreadable) = Unprotect(s.PasswordProtected);
            return new SmtpConfig(s.Host!, s.Port, s.Security, s.Username, password, s.FromAddress, s.FromName, "Settings", unreadable);
        }
        var o = options.Value;
        return o.IsConfigured
            ? new SmtpConfig(o.Host!, o.Port, o.EnableSsl ? (o.Port == 465 ? SmtpSecurity.SslOnConnect : SmtpSecurity.StartTls) : SmtpSecurity.None,
                o.Username, o.Password, o.FromAddress, o.FromName, "AppSettings")
            : null;
    }

    public async Task<EmailSettingsDto> GetAsync(CancellationToken ct)
    {
        var s = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var o = options.Value;
        var resolved = await ResolveAsync(ct);
        var updatedBy = s?.UpdatedByUserId is int uid
            ? await db.Users.Where(u => u.UserId == uid).Select(u => u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username).FirstOrDefaultAsync(ct)
            : null;
        var disabled = ParseDisabled(s?.DisabledCategories);
        var monthAgo = time.GetLocalNow().DateTime.AddDays(-30);
        var sent = await db.EmailMessages.AsNoTracking().Where(m => m.Status == EmailStatus.Sent && m.SentAt >= monthAgo)
            .GroupBy(m => m.Category).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var notifications = EmailCategories.All
            .Select(c => new EmailNotificationDto(c.Code, c.Name, c.Description, !disabled.Contains(c.Code), sent.GetValueOrDefault(c.Code)))
            .ToList();
        return new EmailSettingsDto(
            s?.IsEnabled ?? false, s?.Host, s?.Port ?? 587, s?.Security ?? SmtpSecurity.StartTls, s?.Username, s?.PasswordProtected is not null,
            s?.FromAddress ?? o.FromAddress, s?.FromName ?? o.FromName, s?.UpdatedAt, updatedBy,
            resolved?.Source ?? "LogOnly", await StatsAsync(ct), notifications);
    }

    /// <summary>Kinds of email switched off on the settings page.</summary>
    public async Task<IReadOnlySet<string>> GetDisabledCategoriesAsync(CancellationToken ct) =>
        ParseDisabled(await db.EmailSettings.AsNoTracking().Select(s => s.DisabledCategories).FirstOrDefaultAsync(ct));

    private static HashSet<string> ParseDisabled(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<EmailSettingsDto> SaveAsync(EmailSettingsRequest req, int userId, CancellationToken ct)
    {
        Validate(req);
        var s = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (s is null) db.EmailSettings.Add(s = new EmailSettings());
        s.IsEnabled = req.IsEnabled;
        s.Host = Clean(req.Host);
        s.Port = req.Port;
        s.Security = req.Security;
        s.Username = Clean(req.Username);
        if (req.ClearPassword) s.PasswordProtected = null;
        else if (!string.IsNullOrEmpty(req.Password)) s.PasswordProtected = Protector.Protect(req.Password);
        s.FromAddress = req.FromAddress.Trim();
        s.FromName = req.FromName.Trim();
        if (req.DisabledCategories is { } off)
        {
            var unknown = off.FirstOrDefault(c => !EmailCategories.Codes.Contains(c));
            if (unknown is not null) throw new AppException($"Unknown kind of email: {unknown}.");
            s.DisabledCategories = off.Count == 0 ? null : string.Join(',', off.Distinct());
        }
        s.UpdatedAt = time.GetUtcNow().UtcDateTime;
        s.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Email settings updated by user {UserId}: enabled={Enabled}, host={Host}:{Port}", userId, s.IsEnabled, s.Host, s.Port);
        return await GetAsync(ct);
    }

    /// <summary>Sends one email right away with the settings from the form (unsaved changes included) and reports the outcome.</summary>
    public async Task<EmailTestResultDto> SendTestAsync(EmailTestRequest req, CancellationToken ct)
    {
        var f = req.Settings;
        Validate(f, requireHost: true);
        var saved = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var (savedPassword, unreadable) = Unprotect(saved?.PasswordProtected);
        var password = f.ClearPassword ? null : !string.IsNullOrEmpty(f.Password) ? f.Password : savedPassword;
        var config = new SmtpConfig(f.Host!.Trim(), f.Port, f.Security, Clean(f.Username), password, f.FromAddress.Trim(), f.FromName.Trim(), "Test",
            unreadable && string.IsNullOrEmpty(f.Password));

        var company = await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";
        var subject = $"Test email from {company} HR system";
        var html = Layout(company, "Email is working",
            P("This is a test message sent from the Email Settings page of the HR system.") +
            P($"Server: {config.Host}:{config.Port} ({SecurityLabel(config.Security)}), sent at {time.GetLocalNow():dd MMM yyyy, h:mm tt}."));

        var sw = Stopwatch.StartNew();
        var log = new EmailMessage
        {
            ToAddress = req.To.Trim(), Subject = subject, HtmlBody = html, Category = "Test", Attempts = 1,
            NotBefore = time.GetLocalNow().DateTime,
        };
        try
        {
            using var client = new SmtpClient();
            await SmtpSender.ConnectAsync(client, config, ct);
            await client.SendAsync(SmtpSender.Build(config, log.ToAddress, null, subject, html), ct);
            await client.DisconnectAsync(true, ct);
            log.Status = EmailStatus.Sent;
            log.SentAt = time.GetLocalNow().DateTime;
            db.EmailMessages.Add(log);
            await db.SaveChangesAsync(ct);
            return new EmailTestResultDto(true, $"Test email sent to {log.ToAddress}. Check the inbox (and the spam folder).", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = SmtpSender.Explain(ex, config);
            log.Status = EmailStatus.Failed;
            log.LastError = message.Length > 500 ? message[..500] : message;
            db.EmailMessages.Add(log);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(ex, "Test email to {To} via {Host}:{Port} failed", log.ToAddress, config.Host, config.Port);
            return new EmailTestResultDto(false, message, (int)sw.ElapsedMilliseconds);
        }
    }

    // ------------------------------------------------------------------ Helpers
    private async Task<EmailDeliveryStatsDto> StatsAsync(CancellationToken ct)
    {
        var now = time.GetLocalNow().DateTime;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);
        var q = db.EmailMessages.AsNoTracking();
        // Only an error newer than the last successful send is still relevant.
        var lastSentId = await q.Where(m => m.Status == EmailStatus.Sent).MaxAsync(m => (long?)m.EmailId, ct) ?? 0;
        var lastError = await q.Where(m => m.LastError != null && m.Status != EmailStatus.Skipped && m.EmailId > lastSentId && m.CreatedAt >= weekAgo.ToUniversalTime())
            .OrderByDescending(m => m.EmailId).Select(m => m.LastError).FirstOrDefaultAsync(ct);
        return new EmailDeliveryStatsDto(
            await q.CountAsync(m => m.Status == EmailStatus.Pending, ct),
            await q.CountAsync(m => m.Status == EmailStatus.Sent && m.SentAt >= dayAgo, ct),
            await q.CountAsync(m => m.Status == EmailStatus.Failed && m.NotBefore >= weekAgo, ct),
            await q.CountAsync(m => m.Status == EmailStatus.Logged && m.SentAt >= weekAgo, ct),
            await q.Where(m => m.Status == EmailStatus.Sent).MaxAsync(m => m.SentAt, ct),
            lastError);
    }

    private static void Validate(EmailSettingsRequest r, bool requireHost = false)
    {
        if (!SmtpSecurity.All.Contains(r.Security)) throw new AppException("Choose how the connection is secured.");
        if ((r.IsEnabled || requireHost) && string.IsNullOrWhiteSpace(r.Host)) throw new AppException("Enter the SMTP server (e.g. smtp.gmail.com).");
        if (r.Host is { } h && (h.Contains(' ') || h.Contains("://"))) throw new AppException("Enter only the server name, e.g. smtp.gmail.com (no http:// or spaces).");
        if (r.Security == SmtpSecurity.SslOnConnect && r.Port == 587) throw new AppException("Port 587 uses STARTTLS - choose \"STARTTLS\" or use port 465 for SSL/TLS.");
        if (r.Security == SmtpSecurity.StartTls && r.Port == 465) throw new AppException("Port 465 uses SSL/TLS - choose \"SSL/TLS\" or use port 587 for STARTTLS.");
    }

    private (string? Password, bool Unreadable) Unprotect(string? value)
    {
        if (value is null) return (null, false);
        try { return (Protector.Unprotect(value), false); }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The saved SMTP password could not be decrypted (data protection keys changed?)");
            return (null, true);
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string SecurityLabel(string s) => s switch
    {
        SmtpSecurity.StartTls => "STARTTLS", SmtpSecurity.SslOnConnect => "SSL/TLS", SmtpSecurity.None => "no encryption", _ => "auto",
    };
}

/// <summary>MailKit helpers shared by the dispatcher and the test email.</summary>
public static class SmtpSender
{
    public static async Task ConnectAsync(SmtpClient client, SmtpConfig c, CancellationToken ct)
    {
        if (c.PasswordUnreadable)
            throw new InvalidOperationException("The saved SMTP password could not be read. Please enter the password again in Email Settings and save.");
        client.Timeout = 20_000;
        var options = c.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(c.Host, c.Port, options, ct);
        if (!string.IsNullOrEmpty(c.Username)) await client.AuthenticateAsync(c.Username, c.Password ?? "", ct);
    }

    public static MimeMessage Build(SmtpConfig c, string to, string? toName, string subject, string html)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(c.FromName, c.FromAddress));
        message.To.Add(new MailboxAddress(toName ?? "", to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();
        return message;
    }

    /// <summary>A message an admin can act on, for the most common SMTP problems.</summary>
    public static string Explain(Exception ex, SmtpConfig c) => ex switch
    {
        AuthenticationException => $"Login to {c.Host} was refused. Check the username and password. " +
            (c.Host.Contains("gmail", StringComparison.OrdinalIgnoreCase)
                ? "Gmail needs an App Password (Google Account → Security → 2-Step Verification → App passwords), not your normal password."
                : c.Host.Contains("office365", StringComparison.OrdinalIgnoreCase) || c.Host.Contains("outlook", StringComparison.OrdinalIgnoreCase)
                    ? "Microsoft 365 needs SMTP AUTH enabled for the mailbox, and an app password if MFA is on."
                    : "Some providers need an app-specific password."),
        SslHandshakeException => $"Secure connection to {c.Host}:{c.Port} failed. Try port 587 with STARTTLS, or port 465 with SSL/TLS.",
        SocketException or TimeoutException or IOException when ex is not SslHandshakeException =>
            $"Could not reach {c.Host}:{c.Port}. Check the server name and port, and that this server's firewall allows outgoing connections. ({ex.Message})",
        SmtpCommandException sce when sce.ErrorCode == SmtpErrorCode.SenderNotAccepted =>
            $"The server rejected the From address {c.FromAddress}. Use the mailbox you log in with (or an alias it is allowed to send as). ({sce.Message})",
        SmtpCommandException sce when sce.ErrorCode == SmtpErrorCode.RecipientNotAccepted => $"The server rejected the recipient address. ({sce.Message})",
        SmtpCommandException sce => $"The mail server refused the message: {sce.Message} (code {(int)sce.StatusCode}).",
        ServiceNotAuthenticatedException => "The server requires a login - enter the username and password.",
        _ => ex.Message,
    };
}
