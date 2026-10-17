using HrPayroll.BuildingBlocks.Email;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Leave.Services;

/// <summary>
/// Leave year (January - December) closing: carries each employee's unused leave into the new year as the leave policy
/// allows, lets the rest lapse and allocates the new year's quotas. Safe to run again - it only fills in what is
/// missing and recalculates the carry forward.
/// </summary>
public class LeaveYearService(LeaveDbContext db, EmailQueue mail, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Status
    public async Task<LeaveYearStatusDto> GetLeaveYearStatusAsync(CancellationToken ct)
    {
        var year = Today.Year;
        var active = await db.Employees.CountAsync(e => e.IsActive && e.DateOfJoining.Year <= year, ct);
        var withCurrent = await db.LeaveBalances.Where(b => b.Year == year && b.Employee.IsActive).Select(b => b.EmployeeId).Distinct().CountAsync(ct);
        var withNext = await db.LeaveBalances.Where(b => b.Year == year + 1 && b.Employee.IsActive).Select(b => b.EmployeeId).Distinct().CountAsync(ct);
        var runs = await db.LeaveYearRollovers.AsNoTracking().OrderByDescending(r => r.RunAt).Take(10)
            .Select(r => new LeaveRolloverRunDto(r.FromYear, r.ToYear, r.RunAt,
                r.RunBy.Employee != null ? r.RunBy.Employee.FirstName + " " + r.RunBy.Employee.LastName : r.RunBy.Username,
                r.Employees, r.BalancesCreated, r.BalancesUpdated, r.CarriedForwardDays, r.LapsedDays))
            .ToListAsync(ct);
        return new LeaveYearStatusDto(year, active, withCurrent, withNext, runs);
    }

    // ------------------------------------------------------------------ Leave year rollover
    public async Task<LeaveRolloverPreviewDto> PreviewLeaveRolloverAsync(int fromYear, CancellationToken ct)
    {
        var plan = await PlanAsync(fromYear, ct);
        return plan.Preview;
    }

    public async Task<LeaveRolloverPreviewDto> RunLeaveRolloverAsync(int fromYear, int userId, CancellationToken ct)
    {
        var plan = await PlanAsync(fromYear, ct);
        var toYear = fromYear + 1;
        var existing = await db.LeaveBalances.Where(b => b.Year == toYear).ToDictionaryAsync(b => (b.EmployeeId, b.LeaveTypeId), ct);
        var newlyAllocated = new HashSet<int>();
        int created = 0, updated = 0;

        foreach (var item in plan.Items)
        {
            if (existing.TryGetValue((item.EmployeeId, item.LeaveTypeId), out var balance))
            {
                if (balance.CarryForwarded != item.Row.CarryForward) { balance.CarryForwarded = item.Row.CarryForward; updated++; }
            }
            else
            {
                db.LeaveBalances.Add(new LeaveBalance
                {
                    EmployeeId = item.EmployeeId, LeaveTypeId = item.LeaveTypeId, Year = toYear,
                    Allocated = item.Row.NewAllocation, CarryForwarded = item.Row.CarryForward, Used = 0,
                });
                created++;
                newlyAllocated.Add(item.EmployeeId);
            }
        }

        db.LeaveYearRollovers.Add(new LeaveYearRollover
        {
            FromYear = fromYear, ToYear = toYear, RunAt = time.GetUtcNow().UtcDateTime, RunByUserId = userId,
            Employees = plan.Preview.Employees, BalancesCreated = created, BalancesUpdated = updated,
            CarriedForwardDays = plan.Preview.CarryForwardDays, LapsedDays = plan.Preview.LapsedDays,
        });

        // Tell people their new balances - only the first time their year is set up, not on re-runs.
        var company = await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";
        var emails = await db.Employees.Where(e => newlyAllocated.Contains(e.EmployeeId))
            .Select(e => new { e.EmployeeId, e.Email, e.FirstName, e.LastName }).ToListAsync(ct);
        foreach (var e in emails)
        {
            var lines = plan.Items.Where(i => i.EmployeeId == e.EmployeeId).Select(i => i.Row)
                .Select(r => $"<tr><td style=\"padding:4px 10px 4px 0\"><strong>{E(r.LeaveCode)}</strong></td>" +
                             $"<td style=\"padding:4px 10px\">{r.NewAllocation:0.#} new</td>" +
                             $"<td style=\"padding:4px 10px\">{r.CarryForward:0.#} carried forward</td>" +
                             $"<td style=\"padding:4px 0 4px 10px\"><strong>{r.Opening:0.#} days</strong></td></tr>");
            mail.Enqueue(e.Email, $"{e.FirstName} {e.LastName}", $"Your {toYear} leave balance",
                Layout(company, $"Your leave balance for {toYear}",
                    P($"Dear {e.FirstName},") +
                    P($"Leave for {toYear} has been set up. Unused leave from {fromYear} was carried forward as per the leave policy; the rest has lapsed.") +
                    $"<table style=\"border-collapse:collapse;margin:0 0 12px;font-size:14px\">{string.Concat(lines)}</table>",
                    "My leave balance", $"{mail.AppUrl}/me/dashboard"), EmailCategories.YearEnd);
        }

        await db.SaveChangesAsync(ct);
        return (await PlanAsync(fromYear, ct)).Preview;
    }

    private record PlanItem(int EmployeeId, int LeaveTypeId, LeaveRolloverRowDto Row);
    private record Plan(LeaveRolloverPreviewDto Preview, List<PlanItem> Items);

    private async Task<Plan> PlanAsync(int fromYear, CancellationToken ct)
    {
        var today = Today;
        var toYear = fromYear + 1;
        if (toYear > today.Year + 1) throw new AppException($"The {fromYear} leave year has not started yet.");
        if (fromYear < 2000) throw new AppException("Choose a valid year.");

        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive && e.DateOfJoining <= new DateOnly(toYear, 12, 31))
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new { e.EmployeeId, Name = e.FirstName + " " + e.LastName, e.EmployeeCode, e.Gender, e.DateOfJoining })
            .ToListAsync(ct);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.IsActive && t.IsBalanceTracked).OrderBy(t => t.LeaveTypeId).ToListAsync(ct);
        var ids = employees.Select(e => e.EmployeeId).ToList();
        var oldBalances = await db.LeaveBalances.AsNoTracking().Where(b => b.Year == fromYear && ids.Contains(b.EmployeeId))
            .ToDictionaryAsync(b => (b.EmployeeId, b.LeaveTypeId), ct);
        var newBalances = await db.LeaveBalances.AsNoTracking().Where(b => b.Year == toYear && ids.Contains(b.EmployeeId))
            .ToDictionaryAsync(b => (b.EmployeeId, b.LeaveTypeId), ct);
        // Pending requests could still be approved, so they are held back from the carry forward.
        var pending = await db.LeaveRequests.AsNoTracking()
            .Where(r => r.Status == LeaveStatus.Pending && r.FromDate.Year == fromYear && ids.Contains(r.EmployeeId))
            .GroupBy(r => new { r.EmployeeId, r.LeaveTypeId }).Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Days = g.Sum(r => r.TotalDays) })
            .ToDictionaryAsync(x => (x.EmployeeId, x.LeaveTypeId), x => x.Days, ct);

        var items = new List<PlanItem>();
        foreach (var e in employees)
            foreach (var t in types.Where(t => t.ApplicableGender is null || t.ApplicableGender == e.Gender))
            {
                oldBalances.TryGetValue((e.EmployeeId, t.LeaveTypeId), out var old);
                var closing = old is null ? 0 : Math.Max(0, old.Allocated + old.CarryForwarded - old.Used - pending.GetValueOrDefault((e.EmployeeId, t.LeaveTypeId)));
                var carry = t.AllowCarryForward ? Math.Min(closing, t.MaxCarryForward) : 0;
                var allocation = e.DateOfJoining.Year == toYear && t.IsProRated
                    ? Math.Round(t.AnnualQuota * (13 - e.DateOfJoining.Month) / 12m * 2, MidpointRounding.AwayFromZero) / 2
                    : t.AnnualQuota;
                var action = "New";
                if (newBalances.TryGetValue((e.EmployeeId, t.LeaveTypeId), out var current))
                {
                    allocation = current.Allocated;   // already set up: the allocation is kept, only the carry forward follows the old year
                    action = current.CarryForwarded == carry ? "Unchanged" : "Update";
                }
                items.Add(new PlanItem(e.EmployeeId, t.LeaveTypeId, new LeaveRolloverRowDto(e.EmployeeId, e.Name, e.EmployeeCode, t.Code,
                    closing, carry, closing - carry, allocation, allocation + carry, action)));
            }

        var warnings = new List<string>();
        if (today <= new DateOnly(fromYear, 12, 31))
            warnings.Add($"The {fromYear} leave year is still running. Leave taken or approved after today will not be in the carry forward - " +
                         $"run this again after 31 Dec {fromYear} to correct it (it is safe to run more than once).");
        var pendingCount = await db.LeaveRequests.CountAsync(r => r.Status == LeaveStatus.Pending && r.FromDate.Year == fromYear, ct);
        if (pendingCount > 0)
            warnings.Add($"{pendingCount} leave request(s) in {fromYear} are still pending. Their days are held back from the carry forward until they are approved or rejected.");
        var noOld = employees.Count(e => e.DateOfJoining.Year <= fromYear && !oldBalances.Keys.Any(k => k.EmployeeId == e.EmployeeId));
        if (noOld > 0) warnings.Add($"{noOld} employee(s) had no {fromYear} leave balance, so nothing is carried forward for them.");

        var rows = items.Select(i => i.Row).ToList();
        var preview = new LeaveRolloverPreviewDto(fromYear, toYear, warnings, rows, employees.Count,
            rows.Sum(r => r.CarryForward), rows.Sum(r => r.Lapsed), rows.Count(r => r.Action == "New"), rows.Count(r => r.Action == "Update"));
        return new Plan(preview, items);
    }
}
