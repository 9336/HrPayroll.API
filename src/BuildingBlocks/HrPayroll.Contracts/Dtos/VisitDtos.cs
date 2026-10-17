using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record VisitDto(
    int VisitId,
    string VisitorName,
    string VisitorPhone,
    string? VisitorEmail,
    string? VisitorCompany,
    string Purpose,
    int HostEmployeeId,
    string HostName,
    string HostDepartment,
    DateTime? ExpectedAt,
    DateTime? CheckInAt,
    DateTime? CheckOutAt,
    string Status,
    string? IdProofType,
    string? IdProofLast4,
    string? BadgeNumber,
    string? Notes,
    string? CreatedBy,
    DateTime CreatedAt);

public record VisitSummaryDto(int ExpectedToday, int InsideNow, int CheckedOutToday, int VisitorsToday);

public record VisitQuery(
    DateOnly? From,
    DateOnly? To,
    string? Status,
    int? HostEmployeeId,
    string? Search,
    int Page = 1,
    int PageSize = 20);

public abstract class VisitorDetails
{
    [Required, MaxLength(150)] public string VisitorName { get; set; } = default!;
    [Required, RegularExpression(@"^[0-9+\- ]{7,20}$", ErrorMessage = "Enter a valid phone number.")]
    public string VisitorPhone { get; set; } = default!;
    [EmailAddress, MaxLength(200)] public string? VisitorEmail { get; set; }
    [MaxLength(150)] public string? VisitorCompany { get; set; }
    [Required, MaxLength(300)] public string Purpose { get; set; } = default!;
    [MaxLength(500)] public string? Notes { get; set; }
}

/// <summary>An employee pre-registers a visitor coming to meet them.</summary>
public class InviteVisitorRequest : VisitorDetails
{
    [Required] public DateTime ExpectedAt { get; set; }
}

/// <summary>Visitor desk: register a walk-in (checked in now) or an expected visitor for any host.</summary>
public class RegisterVisitRequest : VisitorDetails
{
    [Range(1, int.MaxValue)] public int HostEmployeeId { get; set; }
    public bool CheckInNow { get; set; } = true;
    /// <summary>Required when not checking in now.</summary>
    public DateTime? ExpectedAt { get; set; }
    [MaxLength(30)] public string? IdProofType { get; set; }
    [RegularExpression("^[A-Za-z0-9]{4}$", ErrorMessage = "Enter only the last 4 characters of the ID.")]
    public string? IdProofLast4 { get; set; }
    [MaxLength(20)] public string? BadgeNumber { get; set; }
}

public class CheckInRequest
{
    [MaxLength(30)] public string? IdProofType { get; set; }
    [RegularExpression("^[A-Za-z0-9]{4}$", ErrorMessage = "Enter only the last 4 characters of the ID.")]
    public string? IdProofLast4 { get; set; }
    [MaxLength(20)] public string? BadgeNumber { get; set; }
}
