using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Helpdesk.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Helpdesk.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

using HrPayroll.BuildingBlocks.Email;

namespace HrPayroll.Helpdesk.Services;

/// <summary>
/// "Your visitor has arrived" emails. Called after the business change is saved; each email goes to
/// the outbox and is sent (or skipped, when switched off on the Email Settings page) by the dispatcher.
/// </summary>
public class Notifier(HelpdeskDbContext db, EmailQueue mail)
{
    private record Person(string Email, string FirstName, string Name);

    // ------------------------------------------------------------------ Visitors
    public async Task VisitorArrivedAsync(int visitId, CancellationToken ct)
    {
        var v = await db.Visits.AsNoTracking().Where(x => x.VisitId == visitId)
            .Select(x => new { x.VisitorName, x.VisitorCompany, x.Purpose, x.HostEmployeeId, x.BadgeNumber }).FirstOrDefaultAsync(ct);
        if (v is null) return;
        var host = await EmployeeAsync(v.HostEmployeeId, ct);
        if (host is null) return;
        var who = v.VisitorCompany is null ? v.VisitorName : $"{v.VisitorName} ({v.VisitorCompany})";
        mail.Enqueue(host.Email, host.Name, $"Your visitor has arrived: {v.VisitorName}",
            Layout(await CompanyAsync(ct), "Your visitor is at reception",
                P($"Dear {host.FirstName},") +
                P($"{who} has checked in at reception to meet you{(string.IsNullOrWhiteSpace(v.Purpose) ? "" : $" - {v.Purpose}")}." +
                  (v.BadgeNumber is null ? "" : $" Visitor badge: {v.BadgeNumber}.")),
                "My visitors", $"{mail.AppUrl}/me/visitors"), EmailCategories.Visitors);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Helpers
    private Task<List<Person>> PeopleAsync(System.Linq.Expressions.Expression<Func<Employee, bool>> where, CancellationToken ct) =>
        db.Employees.AsNoTracking().Where(where).Select(e => new Person(e.Email, e.FirstName, e.FirstName + " " + e.LastName)).ToListAsync(ct);

    private async Task<Person?> EmployeeAsync(int employeeId, CancellationToken ct) =>
        (await PeopleAsync(e => e.EmployeeId == employeeId, ct)).FirstOrDefault();

    private async Task<string> CompanyAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

}
