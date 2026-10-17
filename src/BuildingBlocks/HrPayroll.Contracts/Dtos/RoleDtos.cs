using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

/// <summary>A row of the permission grid: a module and the rights (view / add / edit / delete / approve / pay) it offers.</summary>
public record PermissionModuleDto(string Key, string Group, string Name, IReadOnlyList<PermissionActionDto> Actions);

public record PermissionActionDto(string Code, string Action, string Description);

public record RoleDto(
    int RoleId,
    string Name,
    string? Description,
    bool IsSystem,
    bool IsLocked,                 // Admin: always has every permission
    IReadOnlyList<string> Permissions,
    int UserCount);

public class RoleUpsertRequest
{
    [Required, MaxLength(50), RegularExpression(@"^[A-Za-z0-9 &._-]+$", ErrorMessage = "Role name may contain letters, numbers, spaces and & . _ -")]
    public string Name { get; set; } = default!;
    [MaxLength(200)] public string? Description { get; set; }
    public List<string> Permissions { get; set; } = [];
}

public record UserAccessDto(
    int UserId,
    string Username,
    int? EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    string? Designation,
    int RoleId,
    string RoleName,
    bool IsActive,
    DateTime? LastLoginAt);

public record AssignRoleRequest([Range(1, int.MaxValue)] int RoleId);
