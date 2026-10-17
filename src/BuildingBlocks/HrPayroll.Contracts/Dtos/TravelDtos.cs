using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record TravelClaimDto(
    int ClaimId,
    string Code,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string Department,
    string Purpose,
    string FromPlace,
    string ToPlace,
    DateOnly TripStart,
    DateOnly TripEnd,
    decimal TotalAmount,
    int ItemCount,
    string Status,
    string? ManagerName,
    string? ManagerActionByName,
    DateTime? ManagerActionAt,
    string? ManagerRemarks,
    string? AdminActionByName,
    DateTime? AdminActionAt,
    string? AdminRemarks,
    DateTime? PaidAt,
    string? PaymentReference,
    DateTime CreatedAt);

public record TravelClaimItemDto(int ItemId, DateOnly ExpenseDate, string Category, string Description, decimal Amount,
    bool HasReceipt, string? ReceiptFileName);

public record TravelClaimEventDto(long EventId, string Action, string? Note, string ByName, DateTime At);

/// <summary>A claim with its lines and timeline, plus what the caller may do with it.</summary>
public record TravelClaimDetailDto(
    TravelClaimDto Claim,
    List<TravelClaimItemDto> Items,
    List<TravelClaimEventDto> Events,
    bool CanCancel,
    bool CanAddReceipts,
    bool CanManagerAct,
    bool CanAdminAct,
    bool CanMarkPaid,
    bool CanComment);

public class TravelClaimItemRequest
{
    [Required] public DateOnly ExpenseDate { get; set; }
    [Required] public string Category { get; set; } = default!;
    [Required, MaxLength(300)] public string Description { get; set; } = default!;
    [Range(0.01, 1_000_000)] public decimal Amount { get; set; }
}

public class CreateTravelClaimRequest
{
    [Required, MaxLength(200)] public string Purpose { get; set; } = default!;
    [Required, MaxLength(100)] public string FromPlace { get; set; } = default!;
    [Required, MaxLength(100)] public string ToPlace { get; set; } = default!;
    [Required] public DateOnly TripStart { get; set; }
    [Required] public DateOnly TripEnd { get; set; }
    [MinLength(1), MaxLength(50)] public List<TravelClaimItemRequest> Items { get; set; } = [];
}

public record TravelRemarksRequest([MaxLength(1000)] string? Remarks);

public record TravelPaidRequest([MaxLength(100)] string? Reference);

public record TravelCommentRequest([Required, MaxLength(1000)] string Text);

public class TravelQueueQuery
{
    /// <summary>A status, "Open" (waiting for admin or payment, the default) or "All".</summary>
    public string? Status { get; set; } = "Open";
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public record TravelAdminStatsDto(int AwaitingAdmin, decimal AwaitingAdminAmount, int ToPay, decimal ToPayAmount,
    int WithManagers, decimal PaidThisMonthAmount);

/// <summary>Numbers for the sidebar badges.</summary>
public record TravelCountsDto(int MyOpen, int AwaitingMyApproval, int AwaitingAdmin);
