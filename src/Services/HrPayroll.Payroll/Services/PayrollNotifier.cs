using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Payroll.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Payroll.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

using HrPayroll.BuildingBlocks.Email;

namespace HrPayroll.Payroll.Services;

/// <summary>
/// Emails for released documents and paid payslips. Called after the business change is saved; each email goes to
/// the outbox and is sent (or skipped, when switched off on the Email Settings page) by the dispatcher.
/// </summary>
public class Notifier(PayrollDbContext db, EmailQueue mail)
{
    private record Person(string Email, string FirstName, string Name);

    // ------------------------------------------------------------------ Documents
    public async Task DocumentReleasedAsync(DocumentDto d, CancellationToken ct)
    {
        var e = await EmployeeAsync(d.EmployeeId, ct);
        if (e is null) return;
        mail.Enqueue(e.Email, e.Name, $"Your {d.Title} is available",
            Layout(await CompanyAsync(ct), $"Your {d.Title} is available",
                P($"Dear {e.FirstName},") +
                P($"Your {d.Title} (ref. {d.ReferenceNo}) has been released. You can view and print it from My Documents in the HR portal."),
                "Open document", $"{mail.AppUrl}/me/documents/{d.DocumentId}"), EmailCategories.Documents);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Payroll
    /// <summary>"Your payslip is ready" to everyone whose salary for the month was just marked paid.</summary>
    public async Task PayslipsPaidAsync(int year, int month, CancellationToken ct)
    {
        var slips = await db.Payslips.AsNoTracking()
            .Where(p => p.PayYear == year && p.PayMonth == month && p.Status == PayslipStatus.Paid)
            .Select(p => new { p.PayslipId, p.Employee.Email, p.Employee.FirstName, p.Employee.LastName })
            .ToListAsync(ct);
        var company = await CompanyAsync(ct);
        var period = new DateTime(year, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var s in slips)
            mail.Enqueue(s.Email, $"{s.FirstName} {s.LastName}", $"Your payslip for {period} is ready",
                Layout(company, $"Payslip for {period}",
                    P($"Dear {s.FirstName},") +
                    P($"Your salary for {period} has been paid. Your payslip is available in the HR portal."),
                    "View payslip", $"{mail.AppUrl}/me/payslips/{s.PayslipId}"), EmailCategories.Payroll);
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
