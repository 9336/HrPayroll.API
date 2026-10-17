using System.Net;
using HrPayroll.BuildingBlocks.Data;
using HrPayroll.Contracts.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Email;

/// <summary>SMTP settings ("Email" section; the Notifications service sends) and the web app address used in links.</summary>
public class EmailOptions
{
    public const string Section = "Email";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@example.com";
    public string FromName { get; set; } = "HR Payroll";
    /// <summary>Base URL of the web app, used for links in emails.</summary>
    public string AppUrl { get; set; } = "http://localhost:4200";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Queues an email: an <see cref="EmailRequested"/> event goes into this service's outbox with the business change
/// (same transaction) and reaches the Notifications service through Kafka, which logs and sends it.
/// </summary>
public class EmailQueue(ServiceDbContext db, IOptions<EmailOptions> options, TimeProvider time, IHostEnvironment env)
{
    public string AppUrl => options.Value.AppUrl.TrimEnd('/');

    public void Enqueue(string to, string? toName, string subject, string html, string category, DateTime? notBefore = null)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        var now = time.GetLocalNow().DateTime;
        db.Publish(Topics.Emails, to.Trim().ToLowerInvariant(), new EmailRequested(
            to.Trim(), toName, subject.Length > 200 ? subject[..200] : subject, html, category,
            notBefore is DateTime nb && nb > now ? nb : now, env.ApplicationName));
    }
}

/// <summary>Builds the HTML of system emails. All dynamic text is HTML-encoded.</summary>
public static class EmailTemplates
{
    public static string Layout(string company, string heading, string bodyHtml, string? buttonText = null, string? buttonUrl = null)
    {
        var button = buttonText is null || buttonUrl is null ? "" :
            $"""<p style="margin:24px 0"><a href="{E(buttonUrl)}" style="background:#2952cc;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:600">{E(buttonText)}</a></p>""";
        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#0f172a">
              <div style="background:#0f1b3d;color:#fff;padding:14px 20px;border-radius:8px 8px 0 0;font-weight:700">{E(company)}</div>
              <div style="border:1px solid #e3e8ef;border-top:0;padding:20px;border-radius:0 0 8px 8px">
                <h2 style="margin:0 0 12px;font-size:18px">{E(heading)}</h2>
                {bodyHtml}
                {button}
                <p style="color:#64748b;font-size:12px;margin-top:24px">This is an automated message from the HR system.</p>
              </div>
            </div>
            """;
    }

    public static string P(string text) => $"<p style=\"margin:0 0 10px;line-height:1.5\">{E(text)}</p>";

    public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
