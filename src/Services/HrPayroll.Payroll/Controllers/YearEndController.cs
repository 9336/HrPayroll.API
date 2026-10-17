using HrPayroll.Payroll.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Payroll.Controllers;

/// <summary>
/// Year-end closing, financial-year part. (The leave-year rollover is in the Leave service; the gateway composes
/// GET /api/year-end from both.)
/// </summary>
[ApiController]
[Route("api/year-end")]
[HasPermission(Permissions.YearEndView)]
public class YearEndController(FinancialYearService years) : ControllerBase
{
    [HttpGet("financial-years")]
    public async Task<ActionResult<FinancialYearsPart>> FinancialYears(CancellationToken ct) => Ok(await years.GetFinancialYearsAsync(ct));

    [HttpPost("financial-years/{fy:int}/close")]
    [HasPermission(Permissions.YearEndEdit)]
    public async Task<ActionResult<FinancialYearStatusDto>> Close(int fy, CloseFinancialYearRequest request, CancellationToken ct) =>
        Ok(await years.CloseFinancialYearAsync(fy, request.Notes, User.GetUserId(), ct));

    [HttpPost("financial-years/{fy:int}/reopen")]
    [HasPermission(Permissions.YearEndEdit)]
    public async Task<ActionResult<FinancialYearStatusDto>> Reopen(int fy, CancellationToken ct) =>
        Ok(await years.ReopenFinancialYearAsync(fy, ct));
}

/// <summary>Payroll part of the admin dashboard (composed by the gateway).</summary>
[ApiController]
[HasPermission(Permissions.DashboardView)]
public class PayrollDashboardController(PayrollService payroll) : ControllerBase
{
    [HttpGet("api/payroll/dashboard")]
    public async Task<ActionResult<PayrollDashboardPart>> Dashboard(CancellationToken ct) => Ok(await payroll.GetDashboardAsync(ct));
}
