using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Notifications.Controllers;

/// <summary>Outgoing mail server (SMTP) settings used for every system email.</summary>
[ApiController]
[Route("api/email-settings")]
[HasPermission(Permissions.EmailSettingsView)]
public class EmailSettingsController(EmailSettingsService settings) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<EmailSettingsDto>> Get(CancellationToken ct) => Ok(await settings.GetAsync(ct));

    [HttpPut]
    [HasPermission(Permissions.EmailSettingsEdit)]
    public async Task<ActionResult<EmailSettingsDto>> Save(EmailSettingsRequest request, CancellationToken ct) =>
        Ok(await settings.SaveAsync(request, User.GetUserId(), ct));

    /// <summary>Sends a test email right away with the settings in the form (they do not have to be saved).</summary>
    [HttpPost("test")]
    [HasPermission(Permissions.EmailSettingsEdit)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("email-test")]
    public async Task<ActionResult<EmailTestResultDto>> Test(EmailTestRequest request, CancellationToken ct) =>
        Ok(await settings.SendTestAsync(request, ct));
}
