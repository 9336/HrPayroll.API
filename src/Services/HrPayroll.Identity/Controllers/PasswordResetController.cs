using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Identity.Controllers;

/// <summary>HR sets a new password for an employee's login (the gateway routes this employees URL here).</summary>
[ApiController]
[HasPermission(Permissions.EmployeesEdit)]
public class PasswordResetController(IdentityDbContext db, TimeProvider time) : ControllerBase
{
    [HttpPost("api/employees/{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeId == id, ct)
                   ?? throw new NotFoundException("This employee does not have a login.");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, workFactor: 11);
        await db.SaveChangesAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        await db.RefreshTokens.Where(t => t.UserId == user.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return NoContent();
    }
}
