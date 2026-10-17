using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Employees.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Employees.Controllers;

/// <summary>Holidays and the employee part of the admin dashboard (the gateway composes the full dashboard).</summary>
[ApiController]
[Authorize]
public class DirectoryController(EmployeeService employees) : ControllerBase
{
    [HttpGet("api/holidays")]
    public async Task<ActionResult<List<HolidayDto>>> Holidays([FromQuery] int year, CancellationToken ct) =>
        Ok(await employees.GetHolidaysAsync(year, ct));

    [HttpGet("api/holidays/upcoming")]
    public async Task<ActionResult<List<HolidayDto>>> UpcomingHolidays([FromQuery] int take = 4, CancellationToken ct = default) =>
        Ok(await employees.GetUpcomingHolidaysAsync(take, ct));

    [HttpGet("api/employees/dashboard")]
    [HasPermission(Permissions.DashboardView)]
    public async Task<ActionResult<EmployeeDashboardPart>> Dashboard(CancellationToken ct) => Ok(await employees.GetDashboardAsync(ct));
}

/// <summary>For other services only (never routed by the gateway).</summary>
[ApiController]
[Route("internal/employees")]
[InternalOnly]
public class InternalEmployeesController(EmployeeService employees) : ControllerBase
{
    /// <summary>The ID-card photo (the Attendance service compares registered faces with it).</summary>
    [HttpGet("{id:int}/photo")]
    public async Task<IActionResult> Photo(int id, CancellationToken ct) =>
        await employees.GetPhotoAsync(id, ct) is { } p ? File(p.Photo, p.ContentType) : NotFound();
}
