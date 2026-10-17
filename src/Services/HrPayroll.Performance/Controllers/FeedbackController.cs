using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Performance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Performance.Controllers;

/// <summary>
/// Appreciations and complaints about employees. Managers (their reporting line) and roles with feedback rights record
/// and manage them; employees see and reply to what is shared with them.
/// </summary>
[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController(FeedbackService feedback) : ControllerBase
{
    private FeedbackActor Actor => new(User.GetUserId(), User.GetEmployeeId(), Rights.Of(User, Permissions.FeedbackModule));

    [HttpGet]
    public async Task<ActionResult<List<FeedbackDto>>> Search([FromQuery] FeedbackQuery query, CancellationToken ct) =>
        Ok(await feedback.SearchAsync(Actor, query, ct));

    [HttpGet("summary")]
    public async Task<ActionResult<FeedbackSummaryDto>> Summary([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) =>
        Ok(await feedback.SummaryAsync(Actor, from, to, ct));

    [HttpGet("people")]
    public async Task<ActionResult<List<CalendarPersonDto>>> People(CancellationToken ct) => Ok(await feedback.GetPeopleAsync(Actor, ct));

    [HttpGet("my")]
    public async Task<ActionResult<List<FeedbackDto>>> Mine(CancellationToken ct) => Ok(await feedback.GetMineAsync(Actor, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<FeedbackDto>> Get(int id, CancellationToken ct) => Ok(await feedback.GetAsync(id, Actor, ct));

    [HttpPost]
    public async Task<ActionResult<FeedbackDto>> Create(EmployeeFeedbackRequest request, CancellationToken ct) =>
        Ok(await feedback.CreateAsync(request, Actor, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<FeedbackDto>> Update(int id, EmployeeFeedbackRequest request, CancellationToken ct) =>
        Ok(await feedback.UpdateAsync(id, request, Actor, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await feedback.DeleteAsync(id, Actor, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/resolve")]
    public async Task<ActionResult<FeedbackDto>> Resolve(int id, FeedbackNoteRequest request, CancellationToken ct) =>
        Ok(await feedback.ResolveAsync(id, request.Text, Actor, ct));

    /// <summary>The employee's reply.</summary>
    [HttpPost("{id:int}/respond")]
    public async Task<ActionResult<FeedbackDto>> Respond(int id, FeedbackNoteRequest request, CancellationToken ct) =>
        Ok(await feedback.RespondAsync(id, request.Text, Actor, ct));

    /// <summary>Evidence file (PDF / image, max 5 MB) - e.g. the client's email.</summary>
    [HttpPut("{id:int}/attachment")]
    [RequestSizeLimit(FeedbackService.MaxAttachmentBytes + 64 * 1024)]
    public async Task<ActionResult<FeedbackDto>> Attach(int id, IFormFile file, CancellationToken ct) =>
        Ok(await feedback.SetAttachmentAsync(id, file, Actor, ct));

    [HttpGet("{id:int}/attachment")]
    public async Task<IActionResult> Attachment(int id, CancellationToken ct)
    {
        var (data, type, name) = await feedback.GetAttachmentAsync(id, Actor, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(data, type, name);
    }

    [HttpDelete("{id:int}/attachment")]
    public async Task<ActionResult<FeedbackDto>> RemoveAttachment(int id, CancellationToken ct) =>
        Ok(await feedback.RemoveAttachmentAsync(id, Actor, ct));
}
