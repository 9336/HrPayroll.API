using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Notifications.Services;

/// <summary>
/// Background sender for the email outbox (retries with back-off, 5 attempts). The SMTP settings are read for every
/// batch, so changes on the Email Settings page apply within one interval - no restart needed.
/// </summary>
public class EmailDispatcher(IServiceScopeFactory scopes, TimeProvider time, ILogger<EmailDispatcher> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    private string? lastMode;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;   // the API is shutting down - stop quietly
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email dispatch failed");
                try { await Task.Delay(Interval, time, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var now = time.GetLocalNow().DateTime;
        var batch = await db.EmailMessages
            .Where(m => m.Status == EmailStatus.Pending && m.NotBefore <= now)
            .OrderBy(m => m.NotBefore).Take(20)
            .ToListAsync(ct);
        if (batch.Count == 0) return;

        var settings = scope.ServiceProvider.GetRequiredService<EmailSettingsService>();
        // Kinds of email an admin switched off are kept in the log as "Skipped".
        var disabled = await settings.GetDisabledCategoriesAsync(ct);
        foreach (var m in batch.Where(m => disabled.Contains(m.Category)))
        {
            m.Status = EmailStatus.Skipped;
            m.LastError = "This kind of email is switched off in Email Settings.";
        }
        batch = batch.Where(m => m.Status == EmailStatus.Pending).ToList();
        if (batch.Count == 0) { await db.SaveChangesAsync(ct); return; }

        var smtp = await settings.ResolveAsync(ct);
        var mode = smtp is null ? "log only" : $"{smtp.Host}:{smtp.Port} ({smtp.Source})";
        if (mode != lastMode)
        {
            if (smtp is null) logger.LogWarning("Outgoing mail is not configured - emails are written to the log instead of being sent.");
            else logger.LogInformation("Sending email through {Mode}", mode);
            lastMode = mode;
        }

        if (smtp is null)
        {
            foreach (var m in batch)
            {
                logger.LogInformation("[email not sent - SMTP not configured] To: {To} | Subject: {Subject}", m.ToAddress, m.Subject);
                m.Attempts++;
                m.Status = EmailStatus.Logged;
                m.SentAt = time.GetLocalNow().DateTime;
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        using var client = new MailKit.Net.Smtp.SmtpClient();
        try
        {
            await SmtpSender.ConnectAsync(client, smtp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Server unreachable / login refused: every message in the batch waits for the next attempt.
            var error = SmtpSender.Explain(ex, smtp);
            logger.LogWarning("Could not connect to {Host}:{Port}: {Error}", smtp.Host, smtp.Port, error);
            foreach (var m in batch) Fail(m, error);
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var m in batch)
        {
            try
            {
                await client.SendAsync(SmtpSender.Build(smtp, m.ToAddress, m.ToName, m.Subject, m.HtmlBody), ct);
                m.Attempts++;
                m.Status = EmailStatus.Sent;
                m.SentAt = time.GetLocalNow().DateTime;
                m.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(m, SmtpSender.Explain(ex, smtp));
                logger.LogWarning("Email {Id} to {To} failed (attempt {Attempt}): {Error}", m.EmailId, m.ToAddress, m.Attempts, ex.Message);
                if (!client.IsConnected) break;   // the connection dropped - the rest go in the next batch
            }
        }
        await db.SaveChangesAsync(ct);
        if (client.IsConnected) await client.DisconnectAsync(true, ct);
    }

    private void Fail(EmailMessage m, string error)
    {
        m.Attempts++;
        m.LastError = error.Length > 500 ? error[..500] : error;
        if (m.Attempts >= MaxAttempts) m.Status = EmailStatus.Failed;
        else m.NotBefore = time.GetLocalNow().DateTime.AddMinutes(Math.Pow(2, m.Attempts));   // 2, 4, 8, 16 min
    }
}
