using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Performance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Performance.Entities;
using HrPayroll.Performance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HrPayroll.Performance.Controllers;

[ApiController]
[Route("api/appraisals")]
[Authorize]
public class AppraisalsController(AppraisalService appraisals) : ControllerBase
{
    private AppraisalActor Actor => new(User.GetUserId(), User.GetEmployeeId(), User.HasPermission(Permissions.AppraisalsView), User.HasPermission(Permissions.AppraisalsEdit));

    // ---------------------------------------------------------------- Cycles (HR / admin)
    [HttpGet("cycles")]
    [HasPermission(Permissions.AppraisalsView)]
    public async Task<ActionResult<List<AppraisalCycleDto>>> Cycles(CancellationToken ct) => Ok(await appraisals.GetCyclesAsync(ct));

    [HttpGet("cycles/{id:int}")]
    [HasPermission(Permissions.AppraisalsView)]
    public async Task<ActionResult<AppraisalCycleDto>> Cycle(int id, CancellationToken ct) => Ok(await appraisals.GetCycleAsync(id, ct));

    [HttpPost("cycles")]
    [HasPermission(Permissions.AppraisalsAdd)]
    public async Task<ActionResult<AppraisalCycleDto>> CreateCycle(CycleRequest request, CancellationToken ct) =>
        Ok(await appraisals.CreateCycleAsync(request, User.GetUserId(), ct));

    [HttpPut("cycles/{id:int}")]
    [HasPermission(Permissions.AppraisalsEdit)]
    public async Task<ActionResult<AppraisalCycleDto>> UpdateCycle(int id, CycleRequest request, CancellationToken ct) =>
        Ok(await appraisals.UpdateCycleAsync(id, request, ct));

    /// <summary>Replaces the form's questions (only before publishing).</summary>
    [HttpPut("cycles/{id:int}/questions")]
    [HasPermission(Permissions.AppraisalsEdit)]
    public async Task<ActionResult<AppraisalCycleDto>> SaveQuestions(int id, List<QuestionRequest> questions, CancellationToken ct) =>
        Ok(await appraisals.SaveQuestionsAsync(id, questions, ct));

    /// <summary>Publishes the cycle and emails eligible employees (delivered on the opening day).</summary>
    [HttpPost("cycles/{id:int}/publish")]
    [HasPermission(Permissions.AppraisalsEdit)]
    public async Task<ActionResult<AppraisalCycleDto>> Publish(int id, CancellationToken ct) => Ok(await appraisals.PublishAsync(id, ct));

    [HttpDelete("cycles/{id:int}")]
    [HasPermission(Permissions.AppraisalsDelete)]
    public async Task<IActionResult> DeleteCycle(int id, CancellationToken ct)
    {
        await appraisals.DeleteCycleAsync(id, ct);
        return NoContent();
    }

    [HttpGet("cycles/{id:int}/progress")]
    [HasPermission(Permissions.AppraisalsView)]
    public async Task<ActionResult<List<ProgressRowDto>>> Progress(int id, CancellationToken ct) => Ok(await appraisals.GetProgressAsync(id, ct));

    [HttpPost("cycles/{id:int}/remind")]
    [HasPermission(Permissions.AppraisalsEdit)]
    public async Task<ActionResult<object>> Remind(int id, CancellationToken ct) => Ok(new { reminded = await appraisals.RemindAsync(id, ct) });

    [HttpPost("{id:int}/reopen")]
    [HasPermission(Permissions.AppraisalsEdit)]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct)
    {
        await appraisals.ReopenAsync(id, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- Employee
    [HttpGet("my")]
    public async Task<ActionResult<List<MyCycleDto>>> MyCycles(CancellationToken ct) =>
        Ok(await appraisals.GetMyCyclesAsync(User.RequireEmployeeId(), ct));

    [HttpGet("my/{cycleId:int}")]
    public async Task<ActionResult<AppraisalFormDto>> MyForm(int cycleId, CancellationToken ct) =>
        Ok(await appraisals.GetMyFormAsync(User.RequireEmployeeId(), cycleId, ct));

    /// <summary>Saves the self-appraisal as a draft, or submits it (Submit = true).</summary>
    [HttpPut("my/{cycleId:int}")]
    public async Task<ActionResult<AppraisalFormDto>> SaveMine(int cycleId, SelfAppraisalRequest request, CancellationToken ct) =>
        Ok(await appraisals.SaveMyAsync(User.RequireEmployeeId(), cycleId, request, ct));

    // ---------------------------------------------------------------- Review (reporting manager or HR / admin)
    [HttpGet("reviews")]
    public async Task<ActionResult<List<ReviewListItemDto>>> Reviews([FromQuery] int? cycleId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await appraisals.GetReviewListAsync(Actor, cycleId, status, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AppraisalFormDto>> ForReview(int id, CancellationToken ct) =>
        Ok(await appraisals.GetForReviewAsync(id, Actor, ct));

    /// <summary>Saves review progress, or completes the review (Complete = true) and emails the employee.</summary>
    [HttpPut("{id:int}/review")]
    public async Task<ActionResult<AppraisalFormDto>> Review(int id, ReviewRequest request, CancellationToken ct) =>
        Ok(await appraisals.SaveReviewAsync(id, Actor, request, ct));
}
