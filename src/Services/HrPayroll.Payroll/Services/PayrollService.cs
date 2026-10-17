using System.Globalization;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Payroll.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Payroll.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Services;

public class PayrollService(PayrollDbContext db, WorkingDayCalculator calculator, Notifier notify, TimeProvider time)
{
    /// <summary>Generates payslips for all active employees for the given month.</summary>
    public async Task<PayrollRunResultDto> RunAsync(PayrollRunRequest req, int adminUserId, CancellationToken ct)
    {
        var periodStart = new DateOnly(req.Year, req.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (periodStart > today)
            throw new AppException("Payroll cannot be processed for a future month.");
        await FinancialYears.EnsureOpenAsync(db, req.Year, req.Month, ct);

        var employees = await db.Employees
            .Where(e => e.IsActive && e.DateOfJoining <= periodEnd)
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);
        var ids = employees.Select(e => e.EmployeeId).ToList();

        var structures = (await db.SalaryStructures
                .Where(s => ids.Contains(s.EmployeeId) && s.EffectiveFrom <= periodEnd)
                .ToListAsync(ct))
            .GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.EffectiveFrom)!);

        var existing = await db.Payslips
            .Where(p => p.PayYear == req.Year && p.PayMonth == req.Month)
            .ToDictionaryAsync(p => p.EmployeeId, ct);

        // Unpaid leave (LWP) approved in this period
        var lwp = await db.LeaveRecords.AsNoTracking()   // copy of the Leave service's requests
            .Where(r => ids.Contains(r.EmployeeId) && r.Status == LeaveStatus.Approved && !r.IsPaid
                        && r.FromDate <= periodEnd && r.ToDate >= periodStart)
            .ToListAsync(ct);

        int generated = 0, regenerated = 0, skippedPaid = 0;
        var warnings = new List<string>();

        foreach (var emp in employees)
        {
            if (!structures.TryGetValue(emp.EmployeeId, out var structure))
            {
                warnings.Add($"{emp.EmployeeCode} {emp.FullName}: no salary structure defined - skipped.");
                continue;
            }

            existing.TryGetValue(emp.EmployeeId, out var current);
            if (current?.Status == PayslipStatus.Paid) { skippedPaid++; continue; }
            if (current is not null && !req.RegenerateUnpaid)
            {
                warnings.Add($"{emp.EmployeeCode} {emp.FullName}: payslip already generated (tick 'Regenerate' to recalculate).");
                continue;
            }

            // LOP = days before joining (mid-month joiners) + approved LWP working days in the period
            decimal lopDays = emp.DateOfJoining > periodStart ? emp.DateOfJoining.Day - 1 : 0;
            foreach (var r in lwp.Where(r => r.EmployeeId == emp.EmployeeId))
                lopDays += await calculator.DaysWithinPeriodAsync(r.FromDate, r.ToDate, r.IsHalfDay, periodStart, periodEnd, ct);

            var slip = PayrollCalculator.Calculate(structure, req.Year, req.Month, lopDays);
            slip.GeneratedByUserId = adminUserId;
            slip.GeneratedOn = time.GetUtcNow().UtcDateTime;

            if (current is null)
            {
                db.Payslips.Add(slip);
                generated++;
            }
            else
            {
                slip.PayslipId = current.PayslipId;
                db.Entry(current).CurrentValues.SetValues(slip);
                regenerated++;
            }
        }

        await db.SaveChangesAsync(ct);   // single SaveChanges = one atomic transaction for the whole run

        var totalNet = await db.Payslips.Where(p => p.PayYear == req.Year && p.PayMonth == req.Month)
            .SumAsync(p => (decimal?)p.NetPay, ct) ?? 0;

        return new PayrollRunResultDto(req.Year, req.Month, generated, regenerated, skippedPaid, warnings, totalNet);
    }

    public async Task<PayrollSummaryDto> GetSummaryAsync(int year, int month, CancellationToken ct)
    {
        var slips = await Project(db.Payslips.AsNoTracking()
                .Where(p => p.PayYear == year && p.PayMonth == month)
                .OrderBy(p => p.Employee.EmployeeCode))
            .ToListAsync(ct);

        return new PayrollSummaryDto(year, month, slips.Count,
            slips.Sum(p => p.GrossEarnings), slips.Sum(p => p.TotalDeductions), slips.Sum(p => p.NetPay),
            slips.Count(p => p.Status == PayslipStatus.Paid), slips.Count(p => p.Status != PayslipStatus.Paid), slips);
    }

    public async Task<int> MarkPaidAsync(int year, int month, CancellationToken ct)
    {
        await FinancialYears.EnsureOpenAsync(db, year, month, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var count = await db.Payslips
            .Where(p => p.PayYear == year && p.PayMonth == month && p.Status == PayslipStatus.Processed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PayslipStatus.Paid).SetProperty(p => p.PaidOn, now), ct);
        if (count == 0) throw new AppException("There are no processed payslips to mark as paid for this period.");
        await notify.PayslipsPaidAsync(year, month, ct);   // "your payslip is ready"
        return count;
    }

    public Task<List<PayslipDto>> GetForEmployeeAsync(int employeeId, int? year, CancellationToken ct) =>
        Project(db.Payslips.AsNoTracking()
                .Where(p => p.EmployeeId == employeeId && (year == null || p.PayYear == year))
                .OrderByDescending(p => p.PayYear).ThenByDescending(p => p.PayMonth))
            .ToListAsync(ct);

    public async Task<PayslipDto> GetAsync(int payslipId, int? callerEmployeeId, bool canViewAll, CancellationToken ct)
    {
        var slip = await Project(db.Payslips.AsNoTracking().Where(p => p.PayslipId == payslipId)).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Payslip not found.");
        if (!canViewAll && slip.EmployeeId != callerEmployeeId) throw new ForbiddenException();
        return slip;
    }

    public async Task<PayslipDto?> GetLatestAsync(int employeeId, CancellationToken ct) =>
        (await GetForEmployeeAsync(employeeId, null, ct)).FirstOrDefault();

    /// <summary>The payroll part of the admin dashboard: net pay of the last processed month.</summary>
    public async Task<PayrollDashboardPart> GetDashboardAsync(CancellationToken ct)
    {
        var last = await db.Payslips.AsNoTracking()
            .GroupBy(p => new { p.PayYear, p.PayMonth })
            .Select(g => new { g.Key.PayYear, g.Key.PayMonth, Net = g.Sum(p => p.NetPay) })
            .OrderByDescending(x => x.PayYear).ThenByDescending(x => x.PayMonth)
            .FirstOrDefaultAsync(ct);
        return new PayrollDashboardPart(last?.Net ?? 0, last is null ? null : PeriodLabel(last.PayYear, last.PayMonth));
    }

    public static string PeriodLabel(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private static IQueryable<PayslipDto> Project(IQueryable<Payslip> source) =>
        source.Select(p => new PayslipDto(
            p.PayslipId, p.EmployeeId, p.Employee.EmployeeCode, p.Employee.FirstName + " " + p.Employee.LastName,
            p.Employee.Department.Name, p.Employee.Designation, p.Employee.PanNumber, p.Employee.BankName,
            p.Employee.BankAccountNo, p.Employee.DateOfJoining, p.PayYear, p.PayMonth,
            p.DaysInMonth, p.LopDays, p.PaidDays, p.BasicPay, p.Hra, p.ConveyanceAllowance, p.MedicalAllowance,
            p.SpecialAllowance, p.GrossEarnings, p.LopDeduction, p.PfDeduction, p.ProfessionalTax, p.IncomeTax,
            p.TotalDeductions, p.NetPay, p.Status, p.GeneratedOn, p.PaidOn));
}
