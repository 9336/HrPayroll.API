using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record ExpenseCategoryDto(int Id, string Name, bool IsActive, int ExpenseCount);

public record ExpenseCategoryRequest([Required, MaxLength(100)] string Name, bool IsActive = true);

public record ExpenseDto(
    int ExpenseId,
    DateOnly ExpenseDate,
    int CategoryId,
    string Category,
    string Description,
    decimal Amount,
    string? PaidTo,
    string PaymentMode,
    string? BillNumber,
    bool HasReceipt,
    string? ReceiptFileName,
    string Status,
    string RecordedBy,
    int RecordedByUserId,
    DateTime CreatedAt,
    string? ActionedBy,
    DateTime? ActionedAt,
    string? ReviewRemarks);

public class ExpenseRequest
{
    [Required] public DateOnly ExpenseDate { get; set; }
    [Range(1, int.MaxValue)] public int CategoryId { get; set; }
    [Required, MaxLength(300)] public string Description { get; set; } = default!;
    [Range(0.01, 10_000_000)] public decimal Amount { get; set; }
    [MaxLength(150)] public string? PaidTo { get; set; }
    [Required, RegularExpression("^(Cash|UPI|Card|BankTransfer|Other)$", ErrorMessage = "Invalid payment mode.")]
    public string PaymentMode { get; set; } = "Cash";
    [MaxLength(50)] public string? BillNumber { get; set; }
}

public record ExpenseReviewRequest([MaxLength(300)] string? Remarks);

public record ExpenseQuery(
    DateOnly? From,
    DateOnly? To,
    int? CategoryId,
    string? Status,
    string? PaymentMode,
    string? Search,
    bool MineOnly = false,
    int Page = 1,
    int PageSize = 25);

/// <summary>Paged expense list plus totals of everything matching the filter (not just the page).</summary>
public record ExpenseSearchResult(
    IReadOnlyList<ExpenseDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    decimal TotalAmount,
    decimal ApprovedAmount,
    decimal PendingAmount);

public record CategoryTotalDto(string Category, decimal Amount, int Count);

public record DayTotalDto(DateOnly Date, decimal Amount, int Count);

/// <summary>Totals exclude rejected expenses.</summary>
public record ExpenseSummaryDto(
    DateOnly From,
    DateOnly To,
    decimal Today,
    decimal ThisMonth,
    decimal PeriodTotal,
    decimal PeriodApproved,
    int PendingCount,
    decimal PendingAmount,
    IReadOnlyList<CategoryTotalDto> ByCategory,
    IReadOnlyList<DayTotalDto> ByDay);
