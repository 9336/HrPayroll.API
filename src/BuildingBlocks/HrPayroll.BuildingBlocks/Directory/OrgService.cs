using HrPayroll.BuildingBlocks.Data;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.BuildingBlocks.Directory;

/// <summary>Reporting hierarchy ("who is under whom") from the service's copy of the directory - used for team approvals.</summary>
public class OrgService(ServiceDbContext db)
{
    /// <summary>Everyone reporting to <paramref name="managerId"/>, directly or through other managers.</summary>
    public async Task<HashSet<int>> GetSubordinateIdsAsync(int managerId, CancellationToken ct)
    {
        var links = await db.Employees.AsNoTracking()
            .Where(e => e.ManagerId != null)
            .Select(e => new { e.EmployeeId, ManagerId = e.ManagerId!.Value })
            .ToListAsync(ct);
        var reportsOf = links.ToLookup(l => l.ManagerId, l => l.EmployeeId);

        var result = new HashSet<int>();
        var queue = new Queue<int>([managerId]);
        while (queue.TryDequeue(out var current))
            foreach (var id in reportsOf[current])
                if (id != managerId && result.Add(id)) queue.Enqueue(id);   // guards against bad (cyclic) data
        return result;
    }

    public async Task<bool> IsSubordinateAsync(int managerId, int employeeId, CancellationToken ct) =>
        (await GetSubordinateIdsAsync(managerId, ct)).Contains(employeeId);

    public Task<bool> HasReportsAsync(int employeeId, CancellationToken ct) =>
        db.Employees.AnyAsync(e => e.ManagerId == employeeId && e.IsActive, ct);
}
