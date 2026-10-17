namespace HrPayroll.Contracts.Dtos;

// Parts of the pages the gateway composes from several services (API composition). Each service answers for its own
// data; the gateway calls them in parallel and returns the same models the single API returned before.

/// <summary>Employees service: GET /api/employees/dashboard.</summary>
public record EmployeeDashboardPart(int ActiveEmployees, IReadOnlyList<DepartmentHeadcountDto> HeadcountByDepartment);

/// <summary>Leave service: GET /api/leaves/dashboard/admin.</summary>
public record LeaveAdminDashboardPart(int PendingLeaveRequests, int OnLeaveToday, IReadOnlyList<LeaveRequestDto> RecentPendingRequests);

/// <summary>Leave service: GET /api/leaves/dashboard/me.</summary>
public record LeaveEmployeeDashboardPart(IReadOnlyList<LeaveBalanceDto> Balances, IReadOnlyList<LeaveRequestDto> UpcomingLeaves, int PendingRequests);

/// <summary>Payroll service: GET /api/payroll/dashboard (the last processed month).</summary>
public record PayrollDashboardPart(decimal LastPayrollNet, string? LastPayrollPeriod);

/// <summary>Payroll service: GET /api/year-end/financial-years.</summary>
public record FinancialYearsPart(int CurrentFinancialYear, IReadOnlyList<FinancialYearStatusDto> FinancialYears);
