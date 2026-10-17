namespace HrPayroll.Expenses.Entities;

/// <summary>
/// Flow: PendingManager -(reporting manager)-> PendingAdmin -(admin, travel.approve)-> Approved -> Paid.
/// Either approver can reject; the employee can cancel until the manager acts. No manager = straight to admin.
/// </summary>
public static class TravelClaimStatus
{
    public const string PendingManager = "PendingManager";
    public const string PendingAdmin = "PendingAdmin";
    public const string Approved = "Approved";
    public const string Paid = "Paid";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [PendingManager, PendingAdmin, Approved, Paid, Rejected, Cancelled];
    public static readonly string[] Open = [PendingManager, PendingAdmin, Approved];
}

public static class TravelExpenseCategories
{
    public static readonly string[] All = ["Flight", "Train", "Bus", "Taxi", "Fuel", "Hotel", "Food", "Other"];
}

/// <summary>A travel expense claim for one trip, with its expense lines.</summary>
public class TravelClaim
{
    public int ClaimId { get; set; }
    public int EmployeeId { get; set; }
    public string Purpose { get; set; } = default!;
    public string FromPlace { get; set; } = default!;
    public string ToPlace { get; set; } = default!;
    public DateOnly TripStart { get; set; }
    public DateOnly TripEnd { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = TravelClaimStatus.PendingManager;
    /// <summary>Reporting manager when the claim was submitted (null = no manager, goes straight to admin).</summary>
    public int? ManagerEmployeeId { get; set; }
    public int? ManagerActionByUserId { get; set; }
    public DateTime? ManagerActionAt { get; set; }
    public string? ManagerRemarks { get; set; }
    public int? AdminActionByUserId { get; set; }
    public DateTime? AdminActionAt { get; set; }
    public string? AdminRemarks { get; set; }
    public int? PaidByUserId { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public Employee Employee { get; set; } = default!;
    public Employee? Manager { get; set; }
    public User? ManagerActionBy { get; set; }
    public User? AdminActionBy { get; set; }
    public List<TravelClaimItem> Items { get; set; } = [];

    /// <summary>Human-readable id, e.g. TRV-00042.</summary>
    public static string Code(int id) => $"TRV-{id:D5}";
}

public class TravelClaimItem
{
    public int ItemId { get; set; }
    public int ClaimId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    public decimal Amount { get; set; }
    public string? ReceiptFileName { get; set; }

    public TravelReceipt? Receipt { get; set; }
}

/// <summary>Receipt file of an expense line (bytes only loaded on download).</summary>
public class TravelReceipt
{
    public int ItemId { get; set; }
    public string ContentType { get; set; } = default!;
    public byte[] Data { get; set; } = default!;
}

/// <summary>Timeline: Submitted, ManagerApproved, ManagerRejected, Approved, Rejected, Paid, Cancelled, Comment.</summary>
public class TravelClaimEvent
{
    public long EventId { get; set; }
    public int ClaimId { get; set; }
    public int ByUserId { get; set; }
    public string Action { get; set; } = default!;
    public string? Note { get; set; }
    public DateTime At { get; set; }

    public User By { get; set; } = default!;
}
