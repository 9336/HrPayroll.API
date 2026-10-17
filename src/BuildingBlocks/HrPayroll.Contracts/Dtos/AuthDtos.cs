using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record LoginRequest(
    [Required, MaxLength(50)] string Username,
    [Required, MaxLength(100)] string Password);

public record RefreshRequest([Required] string RefreshToken);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8), MaxLength(100),
     RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\w\s]).+$",
        ErrorMessage = "Password must contain upper case, lower case, a digit and a special character.")]
    string NewPassword);

/// <summary>OAuth 2.0 style token response.</summary>
public record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    UserProfileDto User);

public record UserProfileDto(
    int UserId,
    string Username,
    string Role,
    int? EmployeeId,
    string? EmployeeCode,
    string DisplayName,
    string? Designation,
    string? Department,
    bool IsManager,   // has active employees reporting to them (can approve team leave)
    IReadOnlyList<string> Permissions);
