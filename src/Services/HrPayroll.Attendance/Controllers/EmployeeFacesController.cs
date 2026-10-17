using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Attendance.Controllers;

/// <summary>Faces registered for the selfie check-in: the employee registers, HR approves / rejects / resets.</summary>
[ApiController]
[Route("api/employee-faces")]
[Authorize]
public class EmployeeFacesController(AttendanceFaceService faces) : ControllerBase
{
    // ---------------------------------------------------------------- Employee (mobile app)
    [HttpGet("my")]
    public async Task<ActionResult<FaceStatusDto>> Mine(CancellationToken ct) => Ok(await faces.GetMineAsync(User.RequireEmployeeId(), ct));

    /// <summary>Registers the face from a selfie (base64 JPEG). Auto-approved when it matches the ID-card photo.</summary>
    [HttpPost("my")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<FaceStatusDto>> Enroll(FaceEnrollRequest request, CancellationToken ct) =>
        Ok(await faces.EnrollAsync(User.RequireEmployeeId(), request.Selfie, ct));

    // ---------------------------------------------------------------- HR
    [HttpGet("{employeeId:int}")]
    [HasPermission(Permissions.EmployeesView)]
    public async Task<ActionResult<EmployeeFaceDto>> Get(int employeeId, CancellationToken ct) =>
        await faces.GetAsync(employeeId, ct) is { } f ? Ok(f) : NoContent();

    [HttpPost("{employeeId:int}/approve")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<ActionResult<EmployeeFaceDto>> Approve(int employeeId, FaceReviewRequest request, CancellationToken ct) =>
        Ok(await faces.ReviewAsync(employeeId, true, request.Note, User.GetUserId(), ct));

    [HttpPost("{employeeId:int}/reject")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<ActionResult<EmployeeFaceDto>> Reject(int employeeId, FaceReviewRequest request, CancellationToken ct) =>
        Ok(await faces.ReviewAsync(employeeId, false, request.Note, User.GetUserId(), ct));

    /// <summary>Deletes the registration so the employee registers again.</summary>
    [HttpDelete("{employeeId:int}")]
    [HasPermission(Permissions.EmployeesEdit)]
    public async Task<IActionResult> Reset(int employeeId, CancellationToken ct)
    {
        await faces.ResetAsync(employeeId, ct);
        return NoContent();
    }
}
