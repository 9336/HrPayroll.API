using HrPayroll.Leave.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Leave.Controllers;

/// <summary>
/// Year-end closing, leave part: the leave-year rollover. (Financial years are closed in the Payroll service; the gateway
/// composes GET /api/year-end from both.)
/// </summary>
[ApiController]
[Route("api/year-end")]
[HasPermission(Permissions.YearEndView)]
public class YearEndController(LeaveYearService yearEnd) : ControllerBase
{
    [HttpGet("leave-year")]
    public async Task<ActionResult<LeaveYearStatusDto>> LeaveYear(CancellationToken ct) => Ok(await yearEnd.GetLeaveYearStatusAsync(ct));

    /// <summary>What the rollover from <paramref name="fromYear"/> to the next year would do (nothing is saved).</summary>
    [HttpGet("leave-rollover")]
    public async Task<ActionResult<LeaveRolloverPreviewDto>> Preview([FromQuery] int fromYear, CancellationToken ct) =>
        Ok(await yearEnd.PreviewLeaveRolloverAsync(fromYear, ct));

    [HttpPost("leave-rollover")]
    [HasPermission(Permissions.YearEndEdit)]
    public async Task<ActionResult<LeaveRolloverPreviewDto>> Rollover(LeaveRolloverRequest request, CancellationToken ct) =>
        Ok(await yearEnd.RunLeaveRolloverAsync(request.FromYear, User.GetUserId(), ct));
}
