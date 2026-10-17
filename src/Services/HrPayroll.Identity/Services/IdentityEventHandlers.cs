using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Identity.Services;

/// <summary>Reacts to the Employees service: creates the login of a new employee, and (de)activates logins with employees.</summary>
public class IdentityEventHandlers(IdentityDbContext db, ILogger<IdentityEventHandlers> logger) :
    IIntegrationEventHandler<LoginRequested>,
    IIntegrationEventHandler<EmployeeUpserted>
{
    public async Task HandleAsync(LoginRequested m, EventContext context, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.EmployeeId == m.EmployeeId, ct)) return;   // already created (message repeated)
        if (await db.Users.AnyAsync(u => u.Username == m.Username, ct))
        {
            logger.LogError("Login for employee {EmployeeId} not created: username '{Username}' is already taken", m.EmployeeId, m.Username);
            return;
        }
        var roleId = await db.Roles.AnyAsync(r => r.RoleId == m.RoleId, ct) ? m.RoleId : Roles.EmployeeId;
        db.Users.Add(new User { Username = m.Username, PasswordHash = m.PasswordHash, RoleId = roleId, EmployeeId = m.EmployeeId });
        logger.LogInformation("Created login '{Username}' for employee {EmployeeId}", m.Username, m.EmployeeId);
    }

    /// <summary>The login follows the employee: deactivating an employee deactivates their login (and the other way round).</summary>
    public async Task HandleAsync(EmployeeUpserted m, EventContext context, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeId == m.EmployeeId, ct);
        if (user is null || user.IsActive == m.IsActive) return;
        user.IsActive = m.IsActive;
        if (!m.IsActive)   // signed out everywhere
            await db.RefreshTokens.Where(t => t.UserId == user.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }
}
