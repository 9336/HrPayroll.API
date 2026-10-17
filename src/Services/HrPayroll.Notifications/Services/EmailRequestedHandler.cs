using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;

namespace HrPayroll.Notifications.Services;

/// <summary>
/// An email requested by any service (hr.notifications.emails): it goes into the email log as Pending and the
/// <see cref="EmailDispatcher"/> sends it (or skips it, when that kind of email is switched off).
/// </summary>
public class EmailRequestedHandler(NotificationsDbContext db) : IIntegrationEventHandler<EmailRequested>
{
    public Task HandleAsync(EmailRequested m, EventContext context, CancellationToken ct)
    {
        db.EmailMessages.Add(new EmailMessage
        {
            ToAddress = m.To,
            ToName = m.ToName,
            Subject = m.Subject.Length > 200 ? m.Subject[..200] : m.Subject,
            HtmlBody = m.HtmlBody,
            Category = m.Category,
            Status = EmailStatus.Pending,
            NotBefore = m.NotBefore,
        });
        return Task.CompletedTask;
    }
}
