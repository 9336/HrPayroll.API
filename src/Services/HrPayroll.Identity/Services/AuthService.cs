using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Identity.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HrPayroll.Identity.Services;

public class AuthService(
    IdentityDbContext db,
    ITokenService tokens,
    IOptions<JwtOptions> jwt,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    private static AppException Unauthorized(string message) => new(message, StatusCodes.Status401Unauthorized);

    public async Task<TokenResponse> LoginAsync(LoginRequest req, string? ip, CancellationToken ct)
    {
        var user = await UsersWithProfile().FirstOrDefaultAsync(u => u.Username == req.Username.Trim(), ct);

        // Same message for unknown user / wrong password to avoid username enumeration.
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            logger.LogWarning("Failed login for {Username} from {Ip}", req.Username, ip);
            throw Unauthorized("Invalid username or password.");
        }
        if (!user.IsActive || (user.Employee is not null && !user.Employee.IsActive))
            throw Unauthorized("Your account is disabled. Please contact HR.");

        user.LastLoginAt = time.GetUtcNow().UtcDateTime;
        return await IssueTokensAsync(user, ip, replacing: null, ct);
    }

    /// <summary>Refresh-token rotation with reuse detection (OAuth 2.0 Security BCP).</summary>
    public async Task<TokenResponse> RefreshAsync(string refreshToken, string? ip, CancellationToken ct)
    {
        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                     ?? throw Unauthorized("Invalid refresh token.");

        if (stored.RevokedAt is not null)
        {
            // A rotated token was presented again - possible theft. Revoke the whole family.
            logger.LogWarning("Refresh token reuse detected for user {UserId}", stored.UserId);
            await RevokeAllAsync(stored.UserId, ct);
            throw Unauthorized("Session expired. Please sign in again.");
        }
        if (stored.ExpiresAt <= time.GetUtcNow().UtcDateTime)
            throw Unauthorized("Session expired. Please sign in again.");

        var user = await UsersWithProfile().FirstAsync(u => u.UserId == stored.UserId, ct);
        if (!user.IsActive) throw Unauthorized("Your account is disabled.");

        return await IssueTokensAsync(user, ip, stored, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (stored is null) return;
        stored.RevokedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest req, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.UserId == userId, ct);
        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            throw new AppException("Current password is incorrect.");
        if (req.CurrentPassword == req.NewPassword)
            throw new AppException("New password must be different from the current password.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword, workFactor: 11);
        await db.SaveChangesAsync(ct);
        await RevokeAllAsync(userId, ct);   // sign out other sessions
    }

    public async Task<UserProfileDto> GetProfileAsync(int userId, CancellationToken ct) =>
        await ToProfileAsync(await UsersWithProfile().AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
                             ?? throw new NotFoundException("User not found."), ct);

    // ------------------------------------------------------------------
    private IQueryable<User> UsersWithProfile() =>
        db.Users.Include(u => u.Role).ThenInclude(r => r.Permissions)
            .Include(u => u.Employee).ThenInclude(e => e!.Department);

    /// <summary>The built-in Admin role always has every permission (it cannot be locked out).</summary>
    public static IReadOnlyList<string> EffectivePermissions(Role role) =>
        role.RoleId == Roles.AdminId
            ? Permissions.Codes.Order().ToList()
            : Permissions.Normalize(role.Permissions.Select(p => p.Permission)).Order().ToList();

    private async Task<TokenResponse> IssueTokensAsync(User user, string? ip, RefreshToken? replacing, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (access, expires) = tokens.CreateAccessToken(user, EffectivePermissions(user.Role));
        var refresh = tokens.CreateRefreshToken();
        var refreshHash = tokens.Hash(refresh);

        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.ReplacedByHash = refreshHash;
        }

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.UserId,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwt.Value.RefreshTokenDays),
            CreatedByIp = ip,
        });

        // Housekeeping: drop this user's tokens that expired more than a day ago.
        await db.RefreshTokens
            .Where(t => t.UserId == user.UserId && t.ExpiresAt < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);

        return new TokenResponse(access, "Bearer", (int)(expires - now).TotalSeconds, refresh, await ToProfileAsync(user, ct));
    }

    private async Task RevokeAllAsync(int userId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<UserProfileDto> ToProfileAsync(User u, CancellationToken ct) => new(
        u.UserId, u.Username, u.Role.Name, u.EmployeeId, u.Employee?.EmployeeCode,
        u.Employee?.FullName ?? u.Username, u.Employee?.Designation, u.Employee?.Department.Name,
        u.EmployeeId is int empId && await db.Employees.AnyAsync(e => e.ManagerId == empId && e.IsActive, ct),
        EffectivePermissions(u.Role));
}
