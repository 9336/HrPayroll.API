using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record LeaveTypeDto(
    int LeaveTypeId,
    string Code,
    string Name,
    decimal AnnualQuota,
    bool IsPaid,
    bool IsBalanceTracked,
    bool AllowCarryForward,
    decimal MaxCarryForward,
    bool AllowHalfDay,
    string? ApplicableGender,
    string? Description,
    bool IsActive);

public class UpdateLeaveTypeRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = default!;
    [Range(0, 365)] public decimal AnnualQuota { get; set; }
    public bool AllowCarryForward { get; set; }
    [Range(0, 365)] public decimal MaxCarryForward { get; set; }
    public bool AllowHalfDay { get; set; }
    [MaxLength(300)] public string? Description { get; set; }
    public bool IsActive { get; set; }
}

public record LeaveBalanceDto(
    int LeaveTypeId,
    string Code,
    string Name,
    bool IsPaid,
    int Year,
    decimal Allocated,
    decimal CarryForwarded,
    decimal Used,
    decimal Pending,
    decimal Available);

public record LeaveRequestDto(
    int LeaveRequestId,
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string LeaveCode,
    string LeaveName,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsHalfDay,
    decimal TotalDays,
    string Reason,
    string Status,
    DateTime AppliedOn,
    string? ActionedBy,
    DateTime? ActionedOn,
    string? AdminRemarks,
    bool CanCancel);

public class ApplyLeaveRequest
{
    [Range(1, int.MaxValue)] public int LeaveTypeId { get; set; }
    [Required] public DateOnly FromDate { get; set; }
    [Required] public DateOnly ToDate { get; set; }
    public bool IsHalfDay { get; set; }
    [Required, MinLength(5), MaxLength(500)] public string Reason { get; set; } = default!;
}

public record LeaveActionRequest([MaxLength(500)] string? Remarks);

/// <summary>
/// Who is approving: someone with the leave.approve permission (any request) or a reporting manager
/// (requests of their team).
/// </summary>
public record Approver(int UserId, int? EmployeeId, bool CanApproveAll);

public record LeavePreviewDto(decimal WorkingDays, IReadOnlyList<string> ExcludedDays);

public record LeaveQuery(int? Year, string? Status, int? EmployeeId, int Page = 1, int PageSize = 20);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
