using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Identity.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Identity.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Identity.Services;

/// <summary>Role-based access: roles, their permissions and which role each login has.</summary>
public class RoleService(IdentityDbContext db)
{
    public static IReadOnlyList<PermissionModuleDto> Catalog() =>
        Permissions.Modules.Select(m => new PermissionModuleDto(m.Key, m.Group, m.Name,
            m.Actions.Select(a => new PermissionActionDto(a.Code(m.Key), a.Action, a.Description)).ToList())).ToList();

    public async Task<List<RoleDto>> GetRolesAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.RoleId).ToListAsync(ct);
        var counts = await db.Users.GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return roles.Select(r => ToDto(r, counts.GetValueOrDefault(r.RoleId))).ToList();
    }

    public Task<List<LookupDto>> LookupAsync(CancellationToken ct) =>
        db.Roles.AsNoTracking().OrderBy(r => r.RoleId).Select(r => new LookupDto(r.RoleId, r.Name)).ToListAsync(ct);

    public async Task<RoleDto> CreateAsync(RoleUpsertRequest req, CancellationToken ct)
    {
        var name = req.Name.Trim();
        await EnsureUniqueNameAsync(name, null, ct);
        var role = new Role
        {
            RoleId = (await db.Roles.MaxAsync(r => (int?)r.RoleId, ct) ?? 0) + 1,   // Roles.RoleId is not an identity column
            Name = name,
            Description = Clean(req.Description),
            Permissions = ValidPermissions(req.Permissions).Select(p => new RolePermission { Permission = p }).ToList(),
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return ToDto(role, 0);
    }

    public async Task<RoleDto> UpdateAsync(int id, RoleUpsertRequest req, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.RoleId == id, ct)
                   ?? throw new NotFoundException("Role not found.");
        var name = req.Name.Trim();
        if (role.IsSystem && !string.Equals(name, role.Name, StringComparison.Ordinal))
            throw new AppException($"The built-in {role.Name} role cannot be renamed.");
        await EnsureUniqueNameAsync(name, id, ct);

        role.Name = name;
        role.Description = Clean(req.Description);
        if (role.RoleId != Roles.AdminId)   // Admin always has everything; its permission list is not stored
        {
            var wanted = ValidPermissions(req.Permissions);
            role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
            foreach (var p in wanted.Where(w => role.Permissions.All(x => x.Permission != w)))
                role.Permissions.Add(new RolePermission { RoleId = role.RoleId, Permission = p });
        }
        await db.SaveChangesAsync(ct);
        return ToDto(role, await db.Users.CountAsync(u => u.RoleId == id, ct));
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.RoleId == id, ct) ?? throw new NotFoundException("Role not found.");
        if (role.IsSystem) throw new AppException($"The built-in {role.Name} role cannot be deleted.");
        var users = await db.Users.CountAsync(u => u.RoleId == id, ct);
        if (users > 0) throw new ConflictException($"{users} user(s) still have the {role.Name} role. Move them to another role first.");
        db.Roles.Remove(role);   // permissions cascade
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ User access
    public Task<List<UserAccessDto>> GetUsersAsync(CancellationToken ct) =>
        db.Users.AsNoTracking()
            .OrderBy(u => u.Employee != null ? u.Employee.EmployeeCode : u.Username)
            .Select(u => new UserAccessDto(u.UserId, u.Username, u.EmployeeId,
                u.Employee != null ? u.Employee.EmployeeCode : null,
                u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : null,
                u.Employee != null ? u.Employee.Designation : null,
                u.RoleId, u.Role.Name, u.IsActive, u.LastLoginAt))
            .ToListAsync(ct);

    /// <summary>Changes a login's role. The new permissions apply from the user's next token refresh.</summary>
    public async Task AssignRoleAsync(int userId, int roleId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct) ?? throw new NotFoundException("User not found.");
        if (!await db.Roles.AnyAsync(r => r.RoleId == roleId, ct)) throw new AppException("Selected role does not exist.");
        if (user.RoleId == roleId) return;

        if (user.RoleId == Roles.AdminId && user.IsActive &&
            !await db.Users.AnyAsync(u => u.UserId != userId && u.RoleId == Roles.AdminId && u.IsActive, ct))
            throw new AppException("This is the last active Admin. Make someone else an Admin first.");

        user.RoleId = roleId;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    private async Task EnsureUniqueNameAsync(string name, int? excludeId, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(r => r.Name == name && r.RoleId != excludeId, ct))
            throw new ConflictException($"A role named '{name}' already exists.");
    }

    private static HashSet<string> ValidPermissions(IEnumerable<string> requested)
    {
        var set = requested.Select(p => p.Trim()).ToHashSet();
        var unknown = set.Where(p => !Permissions.Codes.Contains(p)).ToList();
        if (unknown.Count > 0) throw new AppException($"Unknown permission(s): {string.Join(", ", unknown)}");
        return Permissions.Normalize(set);   // Add / Edit / Delete need View
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static RoleDto ToDto(Role r, int userCount) => new(
        r.RoleId, r.Name, r.Description, r.IsSystem, r.RoleId == Roles.AdminId, AuthService.EffectivePermissions(r), userCount);
}
