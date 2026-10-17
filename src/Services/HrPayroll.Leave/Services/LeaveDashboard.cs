using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Leave.Services;

/// <summary>The leave parts of the admin and employee dashboards (the gateway composes the dashboards).</summary>
public class LeaveDashboard(LeaveDbContext db, LeaveService leaves, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public async Task<LeaveAdminDashboardPart> GetAdminAsync(CancellationToken ct)
    {
        var today = Today;
        var pending = await db.LeaveRequests.CountAsync(r => r.Status == LeaveStatus.Pending, ct);
        var onLeave = await db.LeaveRequests.CountAsync(r =>
            r.Status == LeaveStatus.Approved && r.FromDate <= today && r.ToDate >= today, ct);
        var recent = await leaves.Project(db.LeaveRequests.AsNoTracking()
                .Where(r => r.Status == LeaveStatus.Pending)
                .OrderBy(r => r.FromDate).Take(5))
            .ToListAsync(ct);
        return new LeaveAdminDashboardPart(pending, onLeave, recent);
    }

    public async Task<LeaveEmployeeDashboardPart> GetEmployeeAsync(int employeeId, CancellationToken ct)
    {
        var today = Today;
        var upcoming = await leaves.Project(db.LeaveRequests.AsNoTracking()
                .Where(r => r.EmployeeId == employeeId && r.ToDate >= today
                            && (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending))
                .OrderBy(r => r.FromDate).Take(5))
            .ToListAsync(ct);
        var pendingCount = await db.LeaveRequests.CountAsync(r => r.EmployeeId == employeeId && r.Status == LeaveStatus.Pending, ct);
        return new LeaveEmployeeDashboardPart(await leaves.GetBalancesAsync(employeeId, today.Year, ct), upcoming, pendingCount);
    }
}

/// <summary>A new employee (Employees service): allocate this year's (pro-rated) leave quota.</summary>
public class NewEmployeeLeaveAllocator(LeaveDbContext db, TimeProvider time, ILogger<NewEmployeeLeaveAllocator> logger)
    : IIntegrationEventHandler<EmployeeUpserted>
{
    public async Task HandleAsync(EmployeeUpserted m, EventContext context, CancellationToken ct)
    {
        if (!m.IsNew || !m.IsActive) return;
        // The copy of the employee is saved first (the procedure reads it). Both steps are safe to repeat if the message is redelivered.
        await db.SaveChangesAsync(ct);
        var year = Math.Max(DateOnly.FromDateTime(time.GetLocalNow().DateTime).Year, m.DateOfJoining.Year);
        await db.Database.ExecuteSqlRawAsync("EXEC dbo.usp_AllocateYearlyLeaves @Year, @EmployeeId",
            [new SqlParameter("@Year", year), new SqlParameter("@EmployeeId", m.EmployeeId)], ct);
        logger.LogInformation("Allocated {Year} leave for new employee {Code}", year, m.EmployeeCode);
    }
}
