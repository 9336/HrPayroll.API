using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Employees.Entities;
using HrPayroll.Employees.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Employees.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController(EmployeeService employees) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<List<EmployeeListItemDto>>> Search(
        [FromQuery] string? search, [FromQuery] int? departmentId, [FromQuery] bool? isActive, CancellationToken ct) =>
        Ok(await employees.SearchAsync(search, departmentId, isActive, ct));

    /// <summary>Profile of the signed-in employee.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<EmployeeDto>> Me(CancellationToken ct) =>
        Ok(await employees.GetAsync(User.RequireEmployeeId(), ct));

    [HttpGet("{id:int}")]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<EmployeeDto>> Get(int id, CancellationToken ct) =>
        Ok(await employees.GetAsync(id, ct));

    [HttpPost]
    [HasPermission(Permissions.EmployeesAdd)]
    public async Task<ActionResult<EmployeeDto>> Create(CreateEmployeeRequest request, CancellationToken ct)
    {
        var created = await employees.CreateAsync(request, User.HasPermission(Permissions.RolesEdit), ct);
        return CreatedAtAction(nameof(Get), new { id = created.EmployeeId }, created);
    }

    [HttpPut("{id:int}")]
    /// <summary>Edit needs employees.edit; switching Active on / off needs employees.delete.</summary>
    [HasPermission(Permissions.EmployeesEdit, Permissions.EmployeesDelete)]
    public async Task<ActionResult<EmployeeDto>> Update(int id, EmployeeUpsertRequest request, CancellationToken ct) =>
        Ok(await employees.UpdateAsync(id, request, User.HasPermission(Permissions.EmployeesEdit), User.HasPermission(Permissions.EmployeesDelete), ct));

    // POST {id}/reset-password is served by the Identity service (it keeps the logins).

    // ---------------------------------------------------------------- ID card
    /// <summary>ID card of the signed-in employee.</summary>
    [HttpGet("me/id-card")]
    public async Task<ActionResult<IdCardDto>> MyIdCard(CancellationToken ct) =>
        Ok(await employees.GetIdCardAsync(User.RequireEmployeeId(), ct));

    [HttpGet("{id:int}/id-card")]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<IdCardDto>> IdCard(int id, CancellationToken ct) =>
        Ok(await employees.GetIdCardAsync(id, ct));

    /// <summary>Sets the ID card photo (multipart field "file": PNG / JPEG / WebP, max 1 MB).</summary>
    [HttpPut("{id:int}/photo")]
    [HasPermission(Permissions.EmployeesEdit)]
    [RequestSizeLimit(EmployeeService.MaxPhotoBytes + 64 * 1024)]
    public async Task<IActionResult> UploadPhoto(int id, IFormFile file, CancellationToken ct)
    {
        await employees.SetPhotoAsync(id, file, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/photo")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<IActionResult> RemovePhoto(int id, CancellationToken ct)
    {
        await employees.RemovePhotoAsync(id, ct);
        return NoContent();
    }

    [HttpGet("/api/departments")]
    public async Task<ActionResult<List<LookupDto>>> Departments(CancellationToken ct) =>
        Ok(await employees.GetDepartmentsAsync(ct));
}
