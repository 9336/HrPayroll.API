using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Attendance.Controllers;

/// <summary>Attendance areas (geofences) employees must be inside to check in / out.</summary>
[ApiController]
[Route("api/attendance-locations")]
[HasPermission(Permissions.AttendanceLocationsView)]
public class AttendanceLocationsController(AttendanceLocationService locations) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AttendanceLocationDto>>> List(CancellationToken ct) => Ok(await locations.GetAllAsync(ct));

    [HttpPost]
    [HasPermission(Permissions.AttendanceLocationsAdd)]
    public async Task<ActionResult<AttendanceLocationDto>> Create(AttendanceLocationRequest request, CancellationToken ct) =>
        Ok(await locations.SaveAsync(null, request, User.GetUserId(), ct));

    [HttpPut("{id:int}")]
    [HasPermission(Permissions.AttendanceLocationsEdit)]
    public async Task<ActionResult<AttendanceLocationDto>> Update(int id, AttendanceLocationRequest request, CancellationToken ct) =>
        Ok(await locations.SaveAsync(id, request, User.GetUserId(), ct));

    // ---------------------------------------------------------------- Rules
    [HttpGet("settings")]
    public async Task<ActionResult<AttendanceSettingsDto>> Settings(CancellationToken ct) => Ok(await locations.GetSettingsAsync(ct));

    [HttpPut("settings")]
    [HasPermission(Permissions.AttendanceLocationsEdit)]
    public async Task<ActionResult<AttendanceSettingsDto>> SaveSettings(AttendanceSettingsRequest request, CancellationToken ct) =>
        Ok(await locations.SaveSettingsAsync(request, User.GetUserId(), ct));

    // ---------------------------------------------------------------- Registered phones
    [HttpGet("devices")]
    public async Task<ActionResult<List<AttendanceDeviceDto>>> Devices(CancellationToken ct) => Ok(await locations.GetDevicesAsync(ct));

    /// <summary>Resets an employee's phone (lost / changed phone) so they can register a new one.</summary>
    [HttpDelete("devices/{id:int}")]
    [HasPermission(Permissions.AttendanceLocationsEdit)]
    public async Task<IActionResult> ResetDevice(int id, CancellationToken ct)
    {
        await locations.RevokeDeviceAsync(id, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.AttendanceLocationsDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await locations.DeleteAsync(id, ct);
        return NoContent();
    }
}
