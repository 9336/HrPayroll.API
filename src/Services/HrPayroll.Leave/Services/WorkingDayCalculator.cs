using HrPayroll.Leave.Data;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Leave.Services;

/// <summary>Counts working days (Mon-Fri, excluding company holidays) in a date range.</summary>
public class WorkingDayCalculator(LeaveDbContext db)
{
    public async Task<(decimal Days, List<string> Excluded)> CalculateAsync(
        DateOnly from, DateOnly to, bool isHalfDay, CancellationToken ct = default)
    {
        var holidays = await db.Holidays.AsNoTracking()
            .Where(h => h.HolidayDate >= from && h.HolidayDate <= to)
            .ToDictionaryAsync(h => h.HolidayDate, h => h.Name, ct);

        decimal days = 0;
        var excluded = new List<string>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                excluded.Add($"{d:dd MMM yyyy} (Weekend)");
            else if (holidays.TryGetValue(d, out var name))
                excluded.Add($"{d:dd MMM yyyy} ({name})");
            else
                days++;
        }

        if (isHalfDay && days > 0) days = 0.5m;
        return (days, excluded);
    }

    /// <summary>Working days of a leave request that fall inside [periodStart, periodEnd].</summary>
    public async Task<decimal> DaysWithinPeriodAsync(
        DateOnly from, DateOnly to, bool isHalfDay, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default)
    {
        var start = from > periodStart ? from : periodStart;
        var end = to < periodEnd ? to : periodEnd;
        if (start > end) return 0;
        var (days, _) = await CalculateAsync(start, end, isHalfDay, ct);
        return days;
    }
}
