using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Leave.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Leave.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

using HrPayroll.BuildingBlocks.Email;

namespace HrPayroll.Leave.Services;

/// <summary>
/// Leave emails (applied, approved / rejected, cancelled). Called after the business change is saved; each email goes to
/// the outbox and is sent (or skipped, when switched off on the Email Settings page) by the dispatcher.
/// </summary>
public class Notifier(LeaveDbContext db, EmailQueue mail)
{
    private record Person(string Email, string FirstName, string Name);

    // ------------------------------------------------------------------ Leave
    /// <summary>To the reporting manager - or, when there is none, to the leave approvers.</summary>
    public async Task LeaveAppliedAsync(LeaveRequestDto r, CancellationToken ct)
    {
        var managerId = await db.Employees.Where(e => e.EmployeeId == r.EmployeeId).Select(e => e.ManagerId).FirstOrDefaultAsync(ct);
        var recipients = managerId is int mid ? await PeopleAsync(e => e.EmployeeId == mid && e.IsActive, ct) : [];
        var link = $"{mail.AppUrl}/me/team-leaves";
        if (recipients.Count == 0)
        {
            recipients = await ApproversAsync(Permissions.LeaveApprove, r.EmployeeId, ct);
            link = $"{mail.AppUrl}/admin/leave-approvals";
        }
        var company = await CompanyAsync(ct);
        foreach (var m in recipients)
            mail.Enqueue(m.Email, m.Name, $"Leave request: {r.EmployeeName} - {r.LeaveCode} {Dates(r)}",
                Layout(company, "A leave request needs your approval",
                    P($"Dear {m.FirstName},") +
                    P($"{r.EmployeeName} ({r.EmployeeCode}) has applied for {r.LeaveName} on {Dates(r)} - {r.TotalDays:0.#} working day(s).") +
                    P($"Reason: {r.Reason}"),
                    "Review leave", link), EmailCategories.Leave);
        await db.SaveChangesAsync(ct);
    }

    public async Task LeaveDecidedAsync(LeaveRequestDto r, int approverUserId, CancellationToken ct)
    {
        var e = await EmployeeAsync(r.EmployeeId, ct);
        if (e is null) return;
        var by = await UserNameAsync(approverUserId, ct);
        var approved = r.Status == LeaveStatus.Approved;
        mail.Enqueue(e.Email, e.Name, $"Your leave was {(approved ? "approved" : "rejected")}: {r.LeaveCode} {Dates(r)}",
            Layout(await CompanyAsync(ct), approved ? "Your leave is approved" : "Your leave request was rejected",
                P($"Dear {e.FirstName},") +
                P($"Your {r.LeaveName} for {Dates(r)} ({r.TotalDays:0.#} day(s)) was {(approved ? "approved" : "rejected")} by {by}.") +
                (r.AdminRemarks is null ? "" : P($"Remarks: {r.AdminRemarks}")),
                "My leaves", $"{mail.AppUrl}/me/leaves"), EmailCategories.Leave);
        await db.SaveChangesAsync(ct);
    }

    public async Task LeaveCancelledAsync(LeaveRequestDto r, CancellationToken ct)
    {
        var managerId = await db.Employees.Where(e => e.EmployeeId == r.EmployeeId).Select(e => e.ManagerId).FirstOrDefaultAsync(ct);
        if (managerId is not int mid) return;
        var company = await CompanyAsync(ct);
        foreach (var m in await PeopleAsync(e => e.EmployeeId == mid && e.IsActive, ct))
            mail.Enqueue(m.Email, m.Name, $"Leave cancelled: {r.EmployeeName} - {r.LeaveCode} {Dates(r)}",
                Layout(company, "Approved leave was cancelled",
                    P($"Dear {m.FirstName},") + P($"{r.EmployeeName} cancelled their approved {r.LeaveName} for {Dates(r)}. They will be working on these days."),
                    "Team leaves", $"{mail.AppUrl}/me/team-leaves"), EmailCategories.Leave);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Helpers
    private Task<List<Person>> PeopleAsync(System.Linq.Expressions.Expression<Func<Employee, bool>> where, CancellationToken ct) =>
        db.Employees.AsNoTracking().Where(where).Select(e => new Person(e.Email, e.FirstName, e.FirstName + " " + e.LastName)).ToListAsync(ct);

    private async Task<Person?> EmployeeAsync(int employeeId, CancellationToken ct) =>
        (await PeopleAsync(e => e.EmployeeId == employeeId, ct)).FirstOrDefault();

    /// <summary>Users whose role grants <paramref name="permission"/> (admins when no role has it), except the employee.</summary>
    private async Task<List<Person>> ApproversAsync(string permission, int exceptEmployeeId, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking().Where(u => u.IsActive && u.Employee != null && u.EmployeeId != exceptEmployeeId);
        var list = await q.Where(u => u.Role.Permissions.Any(p => p.Permission == permission))
            .Select(u => new Person(u.Employee!.Email, u.Employee.FirstName, u.Employee.FirstName + " " + u.Employee.LastName)).ToListAsync(ct);
        return list.Count > 0 ? list
            : await q.Where(u => u.RoleId == Roles.AdminId)
                .Select(u => new Person(u.Employee!.Email, u.Employee.FirstName, u.Employee.FirstName + " " + u.Employee.LastName)).ToListAsync(ct);
    }

    private Task<string> UserNameAsync(int userId, CancellationToken ct) =>
        db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username).FirstAsync(ct);

    private async Task<string> CompanyAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private static string Dates(LeaveRequestDto r) =>
        r.FromDate == r.ToDate ? $"{r.FromDate:dd MMM yyyy}{(r.IsHalfDay ? " (half day)" : "")}" : $"{r.FromDate:dd MMM} - {r.ToDate:dd MMM yyyy}";
}
