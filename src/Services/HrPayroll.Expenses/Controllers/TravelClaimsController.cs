using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Expenses.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Expenses.Controllers;

/// <summary>
/// Travel expense claims: the employee submits, the reporting manager approves, then admin (travel.approve)
/// gives the final approval (travel.approve) and marks the claim paid (travel.pay).
/// </summary>
[ApiController]
[Route("api/travel-claims")]
[Authorize]
public class TravelClaimsController(TravelClaimService travel) : ControllerBase
{
    private TravelActor Actor => new(User.GetUserId(), User.GetEmployeeId(),
        User.HasPermission(Permissions.TravelView), User.HasPermission(Permissions.TravelApprove), User.HasPermission(Permissions.TravelPay));

    // ---------------------------------------------------------------- Employee
    [HttpGet("my")]
    public async Task<ActionResult<List<TravelClaimDto>>> Mine(CancellationToken ct) => Ok(await travel.GetMineAsync(Actor, ct));

    [HttpPost]
    public async Task<ActionResult<TravelClaimDetailDto>> Create(CreateTravelClaimRequest request, CancellationToken ct) =>
        Ok(await travel.CreateAsync(Actor, request, ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<TravelClaimDetailDto>> Cancel(int id, CancellationToken ct) => Ok(await travel.CancelAsync(id, Actor, ct));

    /// <summary>Attach / replace an expense line's receipt (multipart "file": PDF / PNG / JPEG / WebP, max 5 MB).</summary>
    [HttpPut("items/{itemId:int}/receipt")]
    [RequestSizeLimit(TravelClaimService.MaxReceiptBytes + 64 * 1024)]
    public async Task<ActionResult<TravelClaimDetailDto>> UploadReceipt(int itemId, IFormFile file, CancellationToken ct) =>
        Ok(await travel.SetReceiptAsync(itemId, file, Actor, ct));

    [HttpGet("items/{itemId:int}/receipt")]
    public async Task<IActionResult> Receipt(int itemId, CancellationToken ct)
    {
        var (data, type, name) = await travel.GetReceiptAsync(itemId, Actor, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(data, type, name);
    }

    // ---------------------------------------------------------------- Shared
    [HttpGet("{id:int}")]
    public async Task<ActionResult<TravelClaimDetailDto>> Get(int id, CancellationToken ct) => Ok(await travel.GetDetailAsync(id, Actor, ct));

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<TravelClaimDetailDto>> Comment(int id, TravelCommentRequest request, CancellationToken ct) =>
        Ok(await travel.CommentAsync(id, Actor, request.Text, ct));

    [HttpGet("counts")]
    public async Task<ActionResult<TravelCountsDto>> Counts(CancellationToken ct) => Ok(await travel.GetCountsAsync(Actor, ct));

    // ---------------------------------------------------------------- Reporting manager (stage 1)
    [HttpGet("approvals")]
    public async Task<ActionResult<List<TravelClaimDto>>> Approvals([FromQuery] string? status, CancellationToken ct) =>
        Ok(await travel.GetManagerQueueAsync(Actor, status, ct));

    [HttpPost("{id:int}/manager-approve")]
    public async Task<ActionResult<TravelClaimDetailDto>> ManagerApprove(int id, TravelRemarksRequest request, CancellationToken ct) =>
        Ok(await travel.ManagerApproveAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/manager-reject")]
    public async Task<ActionResult<TravelClaimDetailDto>> ManagerReject(int id, TravelRemarksRequest request, CancellationToken ct) =>
        Ok(await travel.ManagerRejectAsync(id, Actor, request.Remarks, ct));

    // ---------------------------------------------------------------- Admin (stage 2)
    [HttpGet("admin")]
    [HasPermission(Permissions.TravelView)]
    public async Task<ActionResult<PagedResult<TravelClaimDto>>> AdminQueue([FromQuery] TravelQueueQuery query, CancellationToken ct) =>
        Ok(await travel.GetAdminQueueAsync(query, ct));

    [HttpGet("admin/stats")]
    [HasPermission(Permissions.TravelView)]
    public async Task<ActionResult<TravelAdminStatsDto>> AdminStats(CancellationToken ct) => Ok(await travel.GetAdminStatsAsync(ct));

    [HttpPost("{id:int}/approve")]
    [HasPermission(Permissions.TravelApprove)]
    public async Task<ActionResult<TravelClaimDetailDto>> Approve(int id, TravelRemarksRequest request, CancellationToken ct) =>
        Ok(await travel.AdminApproveAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/reject")]
    [HasPermission(Permissions.TravelApprove)]
    public async Task<ActionResult<TravelClaimDetailDto>> Reject(int id, TravelRemarksRequest request, CancellationToken ct) =>
        Ok(await travel.AdminRejectAsync(id, Actor, request.Remarks, ct));

    [HttpPost("{id:int}/paid")]
    [HasPermission(Permissions.TravelPay)]
    public async Task<ActionResult<TravelClaimDetailDto>> Paid(int id, TravelPaidRequest request, CancellationToken ct) =>
        Ok(await travel.MarkPaidAsync(id, Actor, request.Reference, ct));
}
