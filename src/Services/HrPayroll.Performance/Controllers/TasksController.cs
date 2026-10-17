using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Performance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Performance.Controllers;

/// <summary>
/// Daily tasks. Team leads / managers (anyone with reports) create and edit tasks for
/// their team (tasks: View / Add / Edit / Delete extend that to everyone); the assignee only reports status and percentage complete.
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController(TaskService tasks) : ControllerBase
{
    private TaskActor Actor => new(User.GetUserId(), User.GetEmployeeId(), Rights.Of(User, Permissions.TasksModule));

    [HttpGet("my")]
    public async Task<ActionResult<List<WorkTaskDto>>> Mine(CancellationToken ct) =>
        Ok(await tasks.GetMyTasksAsync(User.RequireEmployeeId(), Actor, ct));

    /// <summary>Tasks of the caller's team (everyone with tasks: View).</summary>
    [HttpGet("team")]
    public async Task<ActionResult<TeamTasksDto>> Team([FromQuery] TaskQuery query, CancellationToken ct) =>
        Ok(await tasks.GetTeamTasksAsync(Actor, query, ct));

    [HttpGet("assignable")]
    public async Task<ActionResult<List<CalendarPersonDto>>> Assignable(CancellationToken ct) => Ok(await tasks.GetAssignableAsync(Actor, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkTaskDetailDto>> Get(int id, CancellationToken ct) => Ok(await tasks.GetAsync(id, Actor, ct));

    [HttpPost]
    public async Task<ActionResult<WorkTaskDto>> Create(WorkTaskRequest request, CancellationToken ct) =>
        Ok(await tasks.CreateAsync(Actor, request, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<WorkTaskDto>> Update(int id, WorkTaskRequest request, CancellationToken ct) =>
        Ok(await tasks.UpdateAsync(id, Actor, request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await tasks.DeleteAsync(id, Actor, ct);
        return NoContent();
    }

    /// <summary>Assignee reports status and percentage complete (kept as a dated update).</summary>
    [HttpPost("{id:int}/progress")]
    public async Task<ActionResult<WorkTaskDto>> Progress(int id, TaskProgressRequest request, CancellationToken ct) =>
        Ok(await tasks.ReportProgressAsync(id, Actor, request, ct));
}
