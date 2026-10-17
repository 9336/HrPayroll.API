namespace HrPayroll.Expenses.Entities;

public static class ExpenseStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class PaymentModes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string> { "Cash", "UPI", "Card", "BankTransfer", "Other" };
}

public class ExpenseCategory
{
    public int ExpenseCategoryId { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

/// <summary>A day-to-day office expense (petty cash register entry).</summary>
public class OfficeExpense
{
    public int ExpenseId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public int ExpenseCategoryId { get; set; }
    public string Description { get; set; } = default!;
    public decimal Amount { get; set; }
    public string? PaidTo { get; set; }
    public string PaymentMode { get; set; } = "Cash";
    public string? BillNumber { get; set; }
    public byte[]? Receipt { get; set; }
    public string? ReceiptContentType { get; set; }
    public string? ReceiptFileName { get; set; }
    public string Status { get; set; } = ExpenseStatus.Pending;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? ActionedByUserId { get; set; }
    public DateTime? ActionedAt { get; set; }
    public string? ReviewRemarks { get; set; }

    public ExpenseCategory Category { get; set; } = default!;
    public User CreatedBy { get; set; } = default!;
    public User? ActionedBy { get; set; }
}
