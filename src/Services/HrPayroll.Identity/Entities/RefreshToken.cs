namespace HrPayroll.Identity.Entities;

/// <summary>OAuth 2.0 style refresh token (only the SHA-256 hash is stored).</summary>
public class RefreshToken
{
    public long RefreshTokenId { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }

    public User User { get; set; } = default!;

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
