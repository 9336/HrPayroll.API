using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Helpdesk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Helpdesk.Controllers;

[ApiController]
[Route("api/visits")]
[Authorize]
public class VisitsController(VisitService visits) : ControllerBase
{
    // ---------------------------------------------------------------- Employee (host)
    /// <summary>Visitors who came, or are expected, to meet the signed-in employee.</summary>
    [HttpGet("my")]
    public async Task<ActionResult<PagedResult<VisitDto>>> Mine([FromQuery] VisitQuery query, CancellationToken ct) =>
        Ok(await visits.SearchAsync(query with { HostEmployeeId = null }, User.RequireEmployeeId(), ct));

    /// <summary>Pre-registers a visitor coming to meet the signed-in employee.</summary>
    [HttpPost("my")]
    public async Task<ActionResult<VisitDto>> Invite(InviteVisitorRequest request, CancellationToken ct) =>
        Ok(await visits.InviteAsync(User.RequireEmployeeId(), request, User.GetUserId(), ct));

    /// <summary>Host (own expected visits) or visitor desk.</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<VisitDto>> Cancel(int id, CancellationToken ct) =>
        Ok(await visits.CancelAsync(id, User.GetEmployeeId(), User.HasPermission(Permissions.VisitorsDelete), ct));

    // ---------------------------------------------------------------- Visitor desk / admin
    /// <summary>All visits (filter by date range, status, host, or search text).</summary>
    [HttpGet]
    [HasPermission(Permissions.VisitorsView)]
    public async Task<ActionResult<PagedResult<VisitDto>>> Search([FromQuery] VisitQuery query, CancellationToken ct) =>
        Ok(await visits.SearchAsync(query, null, ct));

    [HttpGet("summary")]
    [HasPermission(Permissions.VisitorsView)]
    public async Task<ActionResult<VisitSummaryDto>> Summary(CancellationToken ct) => Ok(await visits.GetSummaryAsync(ct));

    [HttpPost]
    [HasPermission(Permissions.VisitorsAdd)]
    public async Task<ActionResult<VisitDto>> Register(RegisterVisitRequest request, CancellationToken ct) =>
        Ok(await visits.RegisterAsync(request, User.GetUserId(), ct));

    [HttpPost("{id:int}/check-in")]
    [HasPermission(Permissions.VisitorsEdit)]
    public async Task<ActionResult<VisitDto>> CheckIn(int id, CheckInRequest request, CancellationToken ct) =>
        Ok(await visits.CheckInAsync(id, request, ct));

    [HttpPost("{id:int}/check-out")]
    [HasPermission(Permissions.VisitorsEdit)]
    public async Task<ActionResult<VisitDto>> CheckOut(int id, CancellationToken ct) => Ok(await visits.CheckOutAsync(id, ct));
}
