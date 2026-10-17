using System.Security.Claims;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.BuildingBlocks.Directory;

namespace HrPayroll.BuildingBlocks.Auth;

/// <summary>Custom claims in the access token issued by the Identity service.</summary>
public static class AppClaims
{
    public const string EmployeeId = "emp_id";
    public const string FullName = "full_name";
}

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? throw new ForbiddenException("Invalid token."));

    public static int? GetEmployeeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.EmployeeId), out var id) ? id : null;

    /// <summary>Employee id of the caller, or 403 if the login is not linked to an employee record.</summary>
    public static int RequireEmployeeId(this ClaimsPrincipal user) =>
        user.GetEmployeeId() ?? throw new ForbiddenException("Your login is not linked to an employee profile.");

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(Permissions.ClaimType, permission);
}
