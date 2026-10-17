using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record YearEndOverviewDto(
    DateOnly Today,
    LeaveYearStatusDto LeaveYear,
    int CurrentFinancialYear,
    IReadOnlyList<FinancialYearStatusDto> FinancialYears);

/// <summary>Leave balances run by calendar year (January - December).</summary>
public record LeaveYearStatusDto(
    int CurrentYear,
    int ActiveEmployees,
    int EmployeesWithCurrentYearBalances,
    int EmployeesWithNextYearBalances,
    IReadOnlyList<LeaveRolloverRunDto> Runs);

public record LeaveRolloverRunDto(int FromYear, int ToYear, DateTime RunAt, string RunBy, int Employees, int BalancesCreated,
    int BalancesUpdated, decimal CarriedForwardDays, decimal LapsedDays);

public record FinancialYearStatusDto(
    int FinancialYear,
    string Label,
    DateOnly StartDate,
    DateOnly EndDate,
    /// <summary>Running, Ended (can be closed) or Closed.</summary>
    string State,
    DateTime? ClosedAt,
    string? ClosedBy,
    string? Notes,
    int PayrollMonths,
    int PaidMonths,
    int UnpaidPayslips,
    int EmployeesPaid,
    int Form16Released,
    bool CanClose,
    bool CanReopen,
    IReadOnlyList<string> Issues);

public record LeaveRolloverRowDto(
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string LeaveCode,
    decimal Closing,
    decimal CarryForward,
    decimal Lapsed,
    decimal NewAllocation,
    decimal Opening,
    /// <summary>New (balance will be created), Update (carry forward changes), Unchanged.</summary>
    string Action);

public record LeaveRolloverPreviewDto(
    int FromYear,
    int ToYear,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LeaveRolloverRowDto> Rows,
    int Employees,
    decimal CarryForwardDays,
    decimal LapsedDays,
    int ToCreate,
    int ToUpdate);

public record LeaveRolloverRequest([Range(2000, 2100)] int FromYear);

public record CloseFinancialYearRequest([MaxLength(500)] string? Notes);
