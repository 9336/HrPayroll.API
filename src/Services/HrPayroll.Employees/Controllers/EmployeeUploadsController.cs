using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Employees.Entities;
using HrPayroll.Employees.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Employees.Controllers;

/// <summary>Documents employees upload themselves (mobile app / portal); HR verifies them.</summary>
[ApiController]
[Route("api/employee-uploads")]
[Authorize]
public class EmployeeUploadsController(EmployeeUploadService uploads) : ControllerBase
{
    /// <summary>The kinds of document employees can upload.</summary>
    [HttpGet("categories")]
    public ActionResult<IReadOnlyList<string>> Categories() => Ok(EmployeeUploadCategory.All);

    // ---------------------------------------------------------------- Employee
    [HttpGet("my")]
    public async Task<ActionResult<List<EmployeeUploadDto>>> Mine(CancellationToken ct) => Ok(await uploads.GetMineAsync(User.RequireEmployeeId(), ct));

    /// <summary>Multipart: file (PDF / JPEG / PNG / WebP, max 5 MB), category, title (optional).</summary>
    [HttpPost("my")]
    [RequestSizeLimit(EmployeeUploadService.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<EmployeeUploadDto>> Upload([FromForm] EmployeeUploadForm form, CancellationToken ct) =>
        Ok(await uploads.UploadAsync(User.RequireEmployeeId(), form, ct));

    [HttpDelete("my/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await uploads.DeleteMineAsync(User.RequireEmployeeId(), id, ct);
        return NoContent();
    }

    /// <summary>The file - the employee's own, or anyone's for HR (employees.view).</summary>
    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> File(int id, CancellationToken ct)
    {
        var (data, type, name, owner) = await uploads.GetFileAsync(id, ct);
        if (owner != User.GetEmployeeId() && !User.HasPermission(Permissions.EmployeesView)) return Forbid();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(data, type, name);
    }

    // ---------------------------------------------------------------- HR
    [HttpGet]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<List<EmployeeUploadDto>>> Search([FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await uploads.SearchAsync(employeeId, status, ct));

    [HttpGet("summary")]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<EmployeeUploadSummaryDto>> Summary(CancellationToken ct) => Ok(await uploads.SummaryAsync(ct));

    [HttpPost("{id:int}/verify")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<ActionResult<EmployeeUploadDto>> Verify(int id, UploadReviewRequest request, CancellationToken ct) =>
        Ok(await uploads.ReviewAsync(id, true, request.Note, User.GetUserId(), ct));

    [HttpPost("{id:int}/reject")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<ActionResult<EmployeeUploadDto>> Reject(int id, UploadReviewRequest request, CancellationToken ct) =>
        Ok(await uploads.ReviewAsync(id, false, request.Note, User.GetUserId(), ct));
}
