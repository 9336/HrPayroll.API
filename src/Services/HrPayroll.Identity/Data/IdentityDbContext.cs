using HrPayroll.BuildingBlocks.Data;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Identity.Data;

/// <summary>
/// HrPayroll_Identity: logins, roles and refresh tokens (owned here) and a copy of employees / departments.
/// Every change to a login or role is published (hr.users / hr.roles) for the other services' copies.
/// </summary>
public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new(OwnsUsers: true);

    protected override DirectoryModel Directory => Layout;

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.Property(x => x.TokenHash).IsFixedLength().IsUnicode(false).HasMaxLength(64);
            e.Property(x => x.ReplacedByHash).IsFixedLength().IsUnicode(false).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.Ignore(x => x.IsActive);
        });
    }

    protected override bool Publishes(Type entityType) =>
        entityType == typeof(User) || entityType == typeof(Role) || entityType == typeof(RolePermission);

    protected override async Task<IEnumerable<OutboxEvent>> EventsAfterSaveAsync(
        IReadOnlyList<(object Entity, EntityState State)> changes, CancellationToken ct)
    {
        var events = new List<OutboxEvent>();
        foreach (var (entity, _) in changes)
            if (entity is User u) events.Add(UserEvent(u));

        // A role is published with its whole permission list, whichever part changed.
        var roleIds = changes.Select(c => c.Entity switch { Role r => r.RoleId, RolePermission p => p.RoleId, _ => 0 })
            .Where(id => id > 0).Distinct().ToList();
        if (roleIds.Count > 0)
        {
            var roles = await Roles.AsNoTracking().Include(r => r.Permissions).Where(r => roleIds.Contains(r.RoleId)).ToListAsync(ct);
            foreach (var id in roleIds)
                events.Add(roles.FirstOrDefault(r => r.RoleId == id) is { } role
                    ? RoleEvent(role)
                    : new OutboxEvent(Topics.Roles, id.ToString(), new RoleDeleted(id)));
        }
        return events;
    }

    public static OutboxEvent UserEvent(User u) =>
        new(Topics.Users, u.UserId.ToString(), new UserUpserted(u.UserId, u.Username, u.RoleId, u.EmployeeId, u.IsActive, u.LastLoginAt, u.CreatedAt));

    public static OutboxEvent RoleEvent(Role r) =>
        new(Topics.Roles, r.RoleId.ToString(), new RoleUpserted(r.RoleId, r.Name, r.Description, r.IsSystem,
            r.Permissions.Select(p => p.Permission).Order().ToList()));
}
