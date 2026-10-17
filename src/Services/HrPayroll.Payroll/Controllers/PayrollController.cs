using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Payroll.Entities;
using HrPayroll.Payroll.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Payroll.Controllers;

[ApiController]
[Route("api/salary")]
[Authorize]
public class SalaryController(SalaryService salary) : ControllerBase
{
    /// <summary>Current salary structure of the signed-in employee.</summary>
    [HttpGet("my")]
    public async Task<ActionResult<SalaryStructureDto>> Mine(CancellationToken ct) =>
        await salary.GetCurrentAsync(User.RequireEmployeeId(), ct) is { } s ? Ok(s) : NoContent();

    [HttpGet]
    [HasPermission(Permissions.SalaryView)]
    public async Task<ActionResult<List<SalaryOverviewDto>>> Overview(CancellationToken ct) =>
        Ok(await salary.GetOverviewAsync(ct));

    [HttpGet("employee/{employeeId:int}")]
    [HasPermission(Permissions.SalaryView)]
    public async Task<ActionResult<List<SalaryStructureDto>>> History(int employeeId, CancellationToken ct) =>
        Ok(await salary.GetHistoryAsync(employeeId, ct));

    /// <summary>Creates a new salary revision (increment / correction) effective from a month.</summary>
    [HttpPost]
    [HasPermission(Permissions.SalaryAdd)]
    public async Task<ActionResult<SalaryStructureDto>> Create(SalaryStructureRequest request, CancellationToken ct) =>
        Ok(await salary.CreateRevisionAsync(request, User.GetUserId(), ct));

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.SalaryDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await salary.DeleteRevisionAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/payroll")]
[HasPermission(Permissions.PayrollView)]
public class PayrollController(PayrollService payroll) : ControllerBase
{
    [HttpPost("run")]
    [HasPermission(Permissions.PayrollRun)]
    public async Task<ActionResult<PayrollRunResultDto>> Run(PayrollRunRequest request, CancellationToken ct) =>
        Ok(await payroll.RunAsync(request, User.GetUserId(), ct));

    [HttpGet("{year:int}/{month:int}")]
    public async Task<ActionResult<PayrollSummaryDto>> Summary(int year, int month, CancellationToken ct) =>
        Ok(await payroll.GetSummaryAsync(year, month, ct));

    [HttpPost("{year:int}/{month:int}/mark-paid")]
    [HasPermission(Permissions.PayrollPay)]
    public async Task<ActionResult<object>> MarkPaid(int year, int month, CancellationToken ct) =>
        Ok(new { updated = await payroll.MarkPaidAsync(year, month, ct) });
}

[ApiController]
[Route("api/payslips")]
[Authorize]
public class PayslipsController(PayrollService payroll) : ControllerBase
{
    [HttpGet("my")]
    public async Task<ActionResult<List<PayslipDto>>> Mine([FromQuery] int? year, CancellationToken ct) =>
        Ok(await payroll.GetForEmployeeAsync(User.RequireEmployeeId(), year, ct));

    [HttpGet("employee/{employeeId:int}")]
    [HasPermission(Permissions.SalaryView, Permissions.PayrollView)]
    public async Task<ActionResult<List<PayslipDto>>> ForEmployee(int employeeId, [FromQuery] int? year, CancellationToken ct) =>
        Ok(await payroll.GetForEmployeeAsync(employeeId, year, ct));

    /// <summary>Single payslip - employees can only open their own.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<PayslipDto>> Get(int id, CancellationToken ct) =>
        Ok(await payroll.GetAsync(id, User.GetEmployeeId(),
            User.HasPermission(Permissions.SalaryView) || User.HasPermission(Permissions.PayrollView), ct));
}
