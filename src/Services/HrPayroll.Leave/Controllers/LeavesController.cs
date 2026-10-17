using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Leave.Entities;
using HrPayroll.Leave.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Leave.Controllers;

[ApiController]
[Route("api/leaves")]
[Authorize]
public class LeavesController(LeaveService leaves) : ControllerBase
{
    // ---------------------------------------------------------------- Dashboard parts (composed by the gateway)
    [HttpGet("dashboard/admin")]
    [HasPermission(Permissions.DashboardView)]
    public async Task<ActionResult<LeaveAdminDashboardPart>> AdminDashboard([FromServices] LeaveDashboard dashboard, CancellationToken ct) =>
        Ok(await dashboard.GetAdminAsync(ct));

    [HttpGet("dashboard/me")]
    public async Task<ActionResult<LeaveEmployeeDashboardPart>> MyDashboard([FromServices] LeaveDashboard dashboard, CancellationToken ct) =>
        Ok(await dashboard.GetEmployeeAsync(User.RequireEmployeeId(), ct));

    // ---------------------------------------------------------------- Leave types
    [HttpGet("types")]
    public async Task<ActionResult<List<LeaveTypeDto>>> Types([FromQuery] bool activeOnly = true, CancellationToken ct = default) =>
        Ok(await leaves.GetLeaveTypesAsync(activeOnly && !User.HasPermission(Permissions.LeavePolicyEdit), ct));

    [HttpPut("types/{id:int}")]
    [HasPermission(Permissions.LeavePolicyEdit)]
    public async Task<IActionResult> UpdateType(int id, UpdateLeaveTypeRequest request, CancellationToken ct)
    {
        await leaves.UpdateLeaveTypeAsync(id, request, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- Employee self-service
    [HttpGet("my/balances")]
    public async Task<ActionResult<List<LeaveBalanceDto>>> MyBalances([FromQuery] int? year, CancellationToken ct) =>
        Ok(await leaves.GetBalancesAsync(User.RequireEmployeeId(), year, ct));

    /// <summary>Leave history of the signed-in employee (past and upcoming).</summary>
    [HttpGet("my")]
    public async Task<ActionResult<PagedResult<LeaveRequestDto>>> MyLeaves(
        [FromQuery] int? year, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await leaves.SearchAsync(new LeaveQuery(year, status, User.RequireEmployeeId(), page, pageSize), ct));

    [HttpGet("preview")]
    public async Task<ActionResult<LeavePreviewDto>> Preview(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] bool isHalfDay = false, CancellationToken ct = default) =>
        Ok(await leaves.PreviewAsync(from, to, isHalfDay, ct));

    [HttpPost]
    public async Task<ActionResult<LeaveRequestDto>> Apply(ApplyLeaveRequest request, CancellationToken ct)
    {
        var created = await leaves.ApplyAsync(User.RequireEmployeeId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.LeaveRequestId }, created);
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<LeaveRequestDto>> Cancel(int id, CancellationToken ct) =>
        Ok(await leaves.CancelAsync(User.RequireEmployeeId(), id, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LeaveRequestDto>> Get(int id, CancellationToken ct)
    {
        var dto = await leaves.GetByIdAsync(id, ct);
        if (!User.HasPermission(Permissions.LeaveView) && !await leaves.CanViewAsync(dto, User.ToApprover(), ct)) return Forbid();
        return Ok(dto);
    }

    // ---------------------------------------------------------------- Reporting manager
    /// <summary>Leave requests of everyone reporting to the signed-in employee (directly or indirectly).</summary>
    [HttpGet("team")]
    public async Task<ActionResult<PagedResult<LeaveRequestDto>>> TeamLeaves([FromQuery] LeaveQuery query, CancellationToken ct) =>
        Ok(await leaves.SearchTeamAsync(User.RequireEmployeeId(), query, ct));

    [HttpGet("team/balances/{employeeId:int}")]
    public async Task<ActionResult<List<LeaveBalanceDto>>> TeamMemberBalances(int employeeId, [FromQuery] int? year, CancellationToken ct) =>
        Ok(await leaves.GetTeamMemberBalancesAsync(User.RequireEmployeeId(), employeeId, year, ct));

    /// <summary>Admins can act on any request; reporting managers on requests of their team.</summary>
    [HttpPost("{id:int}/approve")]
    public async Task<ActionResult<LeaveRequestDto>> Approve(int id, LeaveActionRequest request, CancellationToken ct) =>
        Ok(await leaves.ApproveAsync(User.ToApprover(), id, request.Remarks, ct));

    [HttpPost("{id:int}/reject")]
    public async Task<ActionResult<LeaveRequestDto>> Reject(int id, LeaveActionRequest request, CancellationToken ct) =>
        Ok(await leaves.RejectAsync(User.ToApprover(), id, request.Remarks, ct));

    // ---------------------------------------------------------------- Admin
    [HttpGet]
    [HasPermission(Permissions.LeaveView)]
    public async Task<ActionResult<PagedResult<LeaveRequestDto>>> Search([FromQuery] LeaveQuery query, CancellationToken ct) =>
        Ok(await leaves.SearchAsync(query, ct));

    [HttpGet("balances/{employeeId:int}")]
    [HasPermission(Permissions.LeaveView)]
    public async Task<ActionResult<List<LeaveBalanceDto>>> Balances(int employeeId, [FromQuery] int? year, CancellationToken ct) =>
        Ok(await leaves.GetBalancesAsync(employeeId, year, ct));
}
