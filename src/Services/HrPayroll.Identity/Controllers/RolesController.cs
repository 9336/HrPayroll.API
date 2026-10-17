using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Identity.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize]
public class RolesController(RoleService roles) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.RolesView)]
    public async Task<ActionResult<List<RoleDto>>> List(CancellationToken ct) => Ok(await roles.GetRolesAsync(ct));

    /// <summary>All permissions that can be granted to a role.</summary>
    [HttpGet("permissions")]
    [HasPermission(Permissions.RolesView)]
    public ActionResult<IReadOnlyList<PermissionModuleDto>> PermissionCatalog() => Ok(RoleService.Catalog());

    /// <summary>Role names for the "new employee" form.</summary>
    [HttpGet("lookup")]
    [HasPermission(Permissions.RolesView, Permissions.EmployeesAdd)]
    public async Task<ActionResult<List<LookupDto>>> Lookup(CancellationToken ct) => Ok(await roles.LookupAsync(ct));

    [HttpPost]
    [HasPermission(Permissions.RolesAdd)]
    public async Task<ActionResult<RoleDto>> Create(RoleUpsertRequest request, CancellationToken ct) =>
        Ok(await roles.CreateAsync(request, ct));

    [HttpPut("{id:int}")]
    [HasPermission(Permissions.RolesEdit)]
    public async Task<ActionResult<RoleDto>> Update(int id, RoleUpsertRequest request, CancellationToken ct) =>
        Ok(await roles.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.RolesDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await roles.DeleteAsync(id, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- User access
    [HttpGet("users")]
    [HasPermission(Permissions.RolesView)]
    public async Task<ActionResult<List<UserAccessDto>>> Users(CancellationToken ct) => Ok(await roles.GetUsersAsync(ct));

    [HttpPut("users/{userId:int}")]
    [HasPermission(Permissions.RolesEdit)]
    public async Task<IActionResult> AssignRole(int userId, AssignRoleRequest request, CancellationToken ct)
    {
        await roles.AssignRoleAsync(userId, request.RoleId, ct);
        return NoContent();
    }
}
