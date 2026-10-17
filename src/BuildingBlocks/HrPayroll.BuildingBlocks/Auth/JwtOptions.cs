namespace HrPayroll.BuildingBlocks.Auth;

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = default!;
    public string Audience { get; set; } = default!;
    /// <summary>HMAC-SHA256 signing key - at least 32 characters. Keep it in user-secrets / a vault in production.</summary>
    public string SigningKey { get; set; } = default!;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
