using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record SalaryStructureDto(
    int SalaryStructureId,
    int EmployeeId,
    DateOnly EffectiveFrom,
    decimal BasicPay,
    decimal Hra,
    decimal ConveyanceAllowance,
    decimal MedicalAllowance,
    decimal SpecialAllowance,
    decimal GrossMonthly,
    decimal PfPercent,
    decimal ProfessionalTax,
    decimal MonthlyTds,
    decimal EstimatedNetMonthly,
    decimal AnnualCtc,
    string? Remarks,
    DateTime CreatedAt,
    bool IsCurrent);

public class SalaryStructureRequest
{
    [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
    [Required] public DateOnly EffectiveFrom { get; set; }
    [Range(1, 10_000_000)] public decimal BasicPay { get; set; }
    [Range(0, 10_000_000)] public decimal Hra { get; set; }
    [Range(0, 10_000_000)] public decimal ConveyanceAllowance { get; set; }
    [Range(0, 10_000_000)] public decimal MedicalAllowance { get; set; }
    [Range(0, 10_000_000)] public decimal SpecialAllowance { get; set; }
    [Range(0, 100)] public decimal PfPercent { get; set; } = 12;
    [Range(0, 100_000)] public decimal ProfessionalTax { get; set; } = 200;
    [Range(0, 10_000_000)] public decimal MonthlyTds { get; set; }
    [MaxLength(300)] public string? Remarks { get; set; }
}

public record SalaryOverviewDto(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    string Designation,
    SalaryStructureDto? Current);

public record PayslipDto(
    int PayslipId,
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    string Designation,
    string? PanNumber,
    string? BankName,
    string? BankAccountNo,
    DateOnly DateOfJoining,
    int PayYear,
    int PayMonth,
    int DaysInMonth,
    decimal LopDays,
    decimal PaidDays,
    decimal BasicPay,
    decimal Hra,
    decimal ConveyanceAllowance,
    decimal MedicalAllowance,
    decimal SpecialAllowance,
    decimal GrossEarnings,
    decimal LopDeduction,
    decimal PfDeduction,
    decimal ProfessionalTax,
    decimal IncomeTax,
    decimal TotalDeductions,
    decimal NetPay,
    string Status,
    DateTime GeneratedOn,
    DateTime? PaidOn);

public record PayrollRunRequest(
    [Range(2000, 2100)] int Year,
    [Range(1, 12)] int Month,
    bool RegenerateUnpaid = false);

public record PayrollRunResultDto(
    int Year,
    int Month,
    int Generated,
    int Regenerated,
    int SkippedPaid,
    IReadOnlyList<string> Warnings,
    decimal TotalNetPay);

public record PayrollSummaryDto(
    int Year,
    int Month,
    int Employees,
    decimal TotalGross,
    decimal TotalDeductions,
    decimal TotalNet,
    int Paid,
    int Pending,
    IReadOnlyList<PayslipDto> Payslips);

public record AdminDashboardDto(
    int ActiveEmployees,
    int PendingLeaveRequests,
    int OnLeaveToday,
    decimal LastPayrollNet,
    string? LastPayrollPeriod,
    IReadOnlyList<LeaveRequestDto> RecentPendingLeaves,
    IReadOnlyList<DepartmentHeadcountDto> Headcount);

public record DepartmentHeadcountDto(string Department, int Count);

public record EmployeeDashboardDto(
    string DisplayName,
    IReadOnlyList<LeaveBalanceDto> Balances,
    PayslipDto? LatestPayslip,
    IReadOnlyList<LeaveRequestDto> UpcomingLeaves,
    int PendingRequests,
    IReadOnlyList<HolidayDto> UpcomingHolidays);

public record HolidayDto(DateOnly Date, string Name);
