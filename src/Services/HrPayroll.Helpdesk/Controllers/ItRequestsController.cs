using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Helpdesk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Helpdesk.Controllers;

/// <summary>
/// IT service requests: employees raise them, their reporting manager approves, the IT team (it.edit) fulfils.
/// </summary>
[ApiController]
[Route("api/it-requests")]
[Authorize]
public class ItRequestsController(ItRequestService it) : ControllerBase
{
    private ItActor Actor => new(User.GetUserId(), User.GetEmployeeId(), User.HasPermission(Permissions.ItWork), User.IsAdmin(), User.HasPermission(Permissions.ItView));

    // ---------------------------------------------------------------- Requester
    [HttpGet("my")]
    public async Task<ActionResult<List<ItRequestDto>>> Mine(CancellationToken ct) => Ok(await it.GetMineAsync(Actor, ct));

    [HttpPost]
    public async Task<ActionResult<ItRequestDetailDto>> Create(CreateItRequest request, CancellationToken ct) =>
        Ok(await it.CreateAsync(Actor, request, ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<ItRequestDetailDto>> Cancel(int id, CancellationToken ct) => Ok(await it.CancelAsync(id, Actor, ct));

    // ---------------------------------------------------------------- Shared
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ItRequestDetailDto>> Get(int id, CancellationToken ct) => Ok(await it.GetDetailAsync(id, Actor, ct));

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<ItRequestDetailDto>> Comment(int id, ItCommentRequest request, CancellationToken ct) =>
        Ok(await it.CommentAsync(id, Actor, request.Text, ct));

    /// <summary>Sidebar badge numbers.</summary>
    [HttpGet("counts")]
    public async Task<ActionResult<ItCountsDto>> Counts(CancellationToken ct) => Ok(await it.GetCountsAsync(Actor, ct));

    // ---------------------------------------------------------------- Reporting manager
    [HttpGet("approvals")]
    public async Task<ActionResult<List<ItRequestDto>>> Approvals([FromQuery] string? status, CancellationToken ct) =>
        Ok(await it.GetApprovalsAsync(Actor, status, ct));

    [HttpPost("{id:int}/approve")]
    public async Task<ActionResult<ItRequestDetailDto>> Approve(int id, ItRemarksRequest request, CancellationToken ct) =>
        Ok(await it.ApproveAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/reject")]
    public async Task<ActionResult<ItRequestDetailDto>> Reject(int id, ItRemarksRequest request, CancellationToken ct) =>
        Ok(await it.RejectAsync(id, Actor, request.Remarks, ct));

    // ---------------------------------------------------------------- IT service desk
    [HttpGet("queue")]
    [HasPermission(Permissions.ItView)]
    public async Task<ActionResult<PagedResult<ItRequestDto>>> Queue([FromQuery] ItQueueQuery query, CancellationToken ct) =>
        Ok(await it.GetQueueAsync(Actor, query, ct));

    [HttpGet("stats")]
    [HasPermission(Permissions.ItView)]
    public async Task<ActionResult<ItDeskStatsDto>> Stats(CancellationToken ct) => Ok(await it.GetStatsAsync(Actor, ct));

    [HttpGet("staff")]
    [HasPermission(Permissions.ItView)]
    public async Task<ActionResult<List<ItStaffDto>>> Staff(CancellationToken ct) => Ok(await it.GetStaffAsync(ct));

    [HttpPost("{id:int}/assign")]
    [HasPermission(Permissions.ItWork)]
    public async Task<ActionResult<ItRequestDetailDto>> Assign(int id, ItAssignRequest request, CancellationToken ct) =>
        Ok(await it.AssignAsync(id, Actor, request.UserId, ct));

    [HttpPost("{id:int}/start")]
    [HasPermission(Permissions.ItWork)]
    public async Task<ActionResult<ItRequestDetailDto>> Start(int id, ItRemarksRequest request, CancellationToken ct) =>
        Ok(await it.StartAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/complete")]
    [HasPermission(Permissions.ItWork)]
    public async Task<ActionResult<ItRequestDetailDto>> Complete(int id, ItRemarksRequest request, CancellationToken ct) =>
        Ok(await it.CompleteAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/it-reject")]
    [HasPermission(Permissions.ItWork)]
    public async Task<ActionResult<ItRequestDetailDto>> ItReject(int id, ItRemarksRequest request, CancellationToken ct) =>
        Ok(await it.ItRejectAsync(id, Actor, request.Remarks, ct));
}
