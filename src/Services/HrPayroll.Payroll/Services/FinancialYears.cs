using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Payroll.Data;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Services;

/// <summary>Indian financial year: 1 April - 31 March, identified by its first calendar year (2025 = FY 2025-26).</summary>
public static class FinancialYears
{
    public static int StartYearOf(int year, int month) => month >= 4 ? year : year - 1;

    public static int StartYearOf(DateOnly d) => StartYearOf(d.Year, d.Month);

    public static DateOnly Start(int fy) => new(fy, 4, 1);

    public static DateOnly End(int fy) => new(fy + 1, 3, 31);

    public static string Label(int fy) => $"{fy}-{(fy + 1) % 100:D2}";

    /// <summary>Payroll and salary changes are blocked in a closed financial year.</summary>
    public static async Task EnsureOpenAsync(PayrollDbContext db, int year, int month, CancellationToken ct)
    {
        var fy = StartYearOf(year, month);
        if (await db.ClosedFinancialYears.AnyAsync(c => c.FinancialYear == fy, ct))
            throw new ConflictException($"FY {Label(fy)} is closed. Reopen it on the Year-end page to change its payroll or salaries.");
    }
}
