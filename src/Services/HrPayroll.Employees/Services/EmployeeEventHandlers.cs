using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;

namespace HrPayroll.Employees.Services;

/// <summary>Reacts to the Payroll service: applies promotions to the employee record and keeps the salary copy.</summary>
public class EmployeeEventHandlers(EmployeesDbContext db, EmployeeService employees) :
    IIntegrationEventHandler<PromotionApplied>,
    IIntegrationEventHandler<SalaryStructureChanged>
{
    /// <summary>Saving the employee publishes the change, so every other service's copy follows.</summary>
    public Task HandleAsync(PromotionApplied m, EventContext context, CancellationToken ct) => employees.ApplyPromotionAsync(m, ct);

    public async Task HandleAsync(SalaryStructureChanged m, EventContext context, CancellationToken ct)
    {
        var s = await db.SalarySnapshots.FindAsync([m.SalaryStructureId], ct);
        if (m.Deleted)
        {
            if (s is not null) db.SalarySnapshots.Remove(s);
            return;
        }
        s ??= db.SalarySnapshots.Add(new SalarySnapshot { SalaryStructureId = m.SalaryStructureId }).Entity;
        s.EmployeeId = m.EmployeeId;
        s.EffectiveFrom = m.EffectiveFrom;
        s.GrossMonthly = m.GrossMonthly;
    }
}
