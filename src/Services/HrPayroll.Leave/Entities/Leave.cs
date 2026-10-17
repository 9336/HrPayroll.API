namespace HrPayroll.Leave.Entities;

// LeaveStatus is shared (HrPayroll.BuildingBlocks.Directory) - other services keep copies of leave requests.

public class LeaveType
{
    public int LeaveTypeId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public decimal AnnualQuota { get; set; }
    public bool IsPaid { get; set; }
    public bool IsBalanceTracked { get; set; }
    public bool AllowCarryForward { get; set; }
    public decimal MaxCarryForward { get; set; }
    public bool AllowHalfDay { get; set; }
    public bool IsProRated { get; set; }
    public string? ApplicableGender { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LeaveBalance
{
    public int LeaveBalanceId { get; set; }
    public int EmployeeId { get; set; }
    public int LeaveTypeId { get; set; }
    public int Year { get; set; }
    public decimal Allocated { get; set; }
    public decimal CarryForwarded { get; set; }
    public decimal Used { get; set; }
    public decimal Available { get; private set; }   // computed column in SQL

    public LeaveType LeaveType { get; set; } = default!;
    public Employee Employee { get; set; } = default!;

    /// <summary>Available balance computed in memory (the DB column refreshes only after save).</summary>
    public decimal CurrentAvailable => Allocated + CarryForwarded - Used;
}

public class LeaveRequest
{
    public int LeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public int LeaveTypeId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public bool IsHalfDay { get; set; }
    public decimal TotalDays { get; set; }
    public string Reason { get; set; } = default!;
    public string Status { get; set; } = LeaveStatus.Pending;
    public DateTime AppliedOn { get; set; }
    public int? ActionedByUserId { get; set; }
    public DateTime? ActionedOn { get; set; }
    public string? AdminRemarks { get; set; }

    public Employee Employee { get; set; } = default!;
    public LeaveType LeaveType { get; set; } = default!;
    public User? ActionedBy { get; set; }
}

/// <summary>One run of the leave-year rollover (old year's balances carried into the new year).</summary>
public class LeaveYearRollover
{
    public int RolloverId { get; set; }
    public int FromYear { get; set; }
    public int ToYear { get; set; }
    public DateTime RunAt { get; set; }
    public int RunByUserId { get; set; }
    public int Employees { get; set; }
    public int BalancesCreated { get; set; }
    public int BalancesUpdated { get; set; }
    public decimal CarriedForwardDays { get; set; }
    public decimal LapsedDays { get; set; }

    public User RunBy { get; set; } = default!;
}