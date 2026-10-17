using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Notifications.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Notifications.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HrPayroll.Notifications.Controllers;

/// <summary>Outbox of system emails.</summary>
[ApiController]
[Route("api/emails")]
[HasPermission(Permissions.EmailLogView)]
public class EmailsController(NotificationsDbContext db, EmailSettingsService settings) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<EmailMessageDto>>> List([FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var q = db.EmailMessages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(m => m.Status == status);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(m => m.ToAddress.Contains(search) || m.Subject.Contains(search));
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.EmailId).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new EmailMessageDto(m.EmailId, m.ToAddress, m.ToName, m.Subject, m.Category, m.Status, m.NotBefore,
                m.CreatedAt, m.SentAt, m.Attempts, m.LastError))
            .ToListAsync(ct);
        return Ok(new PagedResult<EmailMessageDto>(items, total, page, pageSize));
    }

    [HttpGet("status")]
    public async Task<ActionResult<EmailStatusDto>> Status(CancellationToken ct)
    {
        var smtp = await settings.ResolveAsync(ct);
        return Ok(new EmailStatusDto(smtp is not null, smtp?.FromAddress ?? "",
            await db.EmailMessages.CountAsync(m => m.Status == EmailStatus.Pending, ct),
            await db.EmailMessages.CountAsync(m => m.Status == EmailStatus.Failed, ct)));
    }

    /// <summary>Puts a failed (or logged) email back in the queue.</summary>
    [HttpPost("{id:long}/retry")]
    [HasPermission(Permissions.EmailLogRetry)]
    public async Task<IActionResult> Retry(long id, CancellationToken ct)
    {
        var updated = await db.EmailMessages
            .Where(m => m.EmailId == id && (m.Status == EmailStatus.Failed || m.Status == EmailStatus.Logged))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, EmailStatus.Pending).SetProperty(m => m.Attempts, 0)
                .SetProperty(m => m.NotBefore, DateTime.Now).SetProperty(m => m.LastError, (string?)null), ct);
        return updated == 0 ? Conflict(new { detail = "Only failed or logged emails can be retried." }) : NoContent();
    }
}
