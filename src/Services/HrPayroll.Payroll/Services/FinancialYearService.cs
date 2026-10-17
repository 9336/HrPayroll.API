using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Services;

/// <summary>
/// Financial year (April - March) closing: closing a year locks its payroll and salary revisions. (The leave-year
/// rollover is in the Leave service; the gateway composes the year-end page from both.)
/// </summary>
public class FinancialYearService(PayrollDbContext db, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>The current and the two previous financial years.</summary>
    public async Task<FinancialYearsPart> GetFinancialYearsAsync(CancellationToken ct)
    {
        var currentFy = FinancialYears.StartYearOf(Today);
        var fys = new List<FinancialYearStatusDto>();
        for (var fy = currentFy; fy >= currentFy - 2; fy--) fys.Add(await FinancialYearStatusAsync(fy, ct));
        return new FinancialYearsPart(currentFy, fys);
    }
    // ------------------------------------------------------------------ Financial year
    public async Task<FinancialYearStatusDto> CloseFinancialYearAsync(int fy, string? notes, int userId, CancellationToken ct)
    {
        var status = await FinancialYearStatusAsync(fy, ct);
        if (status.State == "Closed") throw new ConflictException($"FY {status.Label} is already closed.");
        if (!status.CanClose) throw new AppException(status.Issues.FirstOrDefault() ?? $"FY {status.Label} cannot be closed yet.");
        db.ClosedFinancialYears.Add(new ClosedFinancialYear
        {
            FinancialYear = fy, ClosedAt = time.GetUtcNow().UtcDateTime, ClosedByUserId = userId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return await FinancialYearStatusAsync(fy, ct);
    }

    public async Task<FinancialYearStatusDto> ReopenFinancialYearAsync(int fy, CancellationToken ct)
    {
        var closed = await db.ClosedFinancialYears.FirstOrDefaultAsync(c => c.FinancialYear == fy, ct)
                     ?? throw new ConflictException($"FY {FinancialYears.Label(fy)} is not closed.");
        if (await db.ClosedFinancialYears.AnyAsync(c => c.FinancialYear == fy + 1, ct))
            throw new ConflictException($"Reopen FY {FinancialYears.Label(fy + 1)} first - years are reopened newest first.");
        db.ClosedFinancialYears.Remove(closed);
        await db.SaveChangesAsync(ct);
        return await FinancialYearStatusAsync(fy, ct);
    }

    private async Task<FinancialYearStatusDto> FinancialYearStatusAsync(int fy, CancellationToken ct)
    {
        var start = FinancialYears.Start(fy);
        var end = FinancialYears.End(fy);
        var inFy = db.Payslips.AsNoTracking().Where(p => (p.PayYear == fy && p.PayMonth >= 4) || (p.PayYear == fy + 1 && p.PayMonth <= 3));
        var perMonth = await inFy.GroupBy(p => new { p.PayYear, p.PayMonth })
            .Select(g => new { g.Key, Unpaid = g.Count(p => p.Status != PayslipStatus.Paid) }).ToListAsync(ct);
        var months = perMonth.Count;
        var paidMonths = perMonth.Count(m => m.Unpaid == 0);
        var unpaid = perMonth.Sum(m => m.Unpaid);
        var employeesPaid = await inFy.Where(p => p.Status == PayslipStatus.Paid).Select(p => p.EmployeeId).Distinct().CountAsync(ct);
        var form16 = await db.EmployeeDocuments.CountAsync(d => d.DocumentType == DocumentTypes.Form16 && d.FinancialYear == fy, ct);
        var closed = await db.ClosedFinancialYears.AsNoTracking().Where(c => c.FinancialYear == fy)
            .Select(c => new { c.ClosedAt, c.Notes, By = c.ClosedBy.Employee != null ? c.ClosedBy.Employee.FirstName + " " + c.ClosedBy.Employee.LastName : c.ClosedBy.Username })
            .FirstOrDefaultAsync(ct);
        var nextClosed = await db.ClosedFinancialYears.AnyAsync(c => c.FinancialYear == fy + 1, ct);

        var state = closed is not null ? "Closed" : end < Today ? "Ended" : "Running";
        var issues = new List<string>();
        if (state == "Running") issues.Add($"FY {FinancialYears.Label(fy)} runs until {end:dd MMM yyyy} - it can be closed after it ends.");
        if (unpaid > 0) issues.Add($"{unpaid} payslip(s) in this year are not marked paid yet. Pay them (or regenerate) before closing.");
        var warnings = new List<string>(issues);
        if (state != "Closed" && months < 12 && end < Today) warnings.Add($"Payroll was processed for {months} of 12 months.");
        if (state != "Closed" && employeesPaid > form16 && end < Today) warnings.Add($"Form 16 released for {form16} of {employeesPaid} paid employee(s).");

        return new FinancialYearStatusDto(fy, FinancialYears.Label(fy), start, end, state, closed?.ClosedAt, closed?.By, closed?.Notes,
            months, paidMonths, unpaid, employeesPaid, form16,
            CanClose: state == "Ended" && unpaid == 0,
            CanReopen: state == "Closed" && !nextClosed,
            warnings);
    }
}
