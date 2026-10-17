using System.Text.Json;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Payroll.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Payroll.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Services;

/// <summary>
/// Releases offer letters, relieving letters and Form 16 to employees. Each document stores a JSON snapshot
/// of its contents, so it reads the same later even if the employee record or salary changes.
/// </summary>
public class DocumentService(PayrollDbContext db, Notifier notify, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Queries
    /// <param name="allowedTypes">Restricts the result to these document types (null = all, e.g. the employee's own list).</param>
    public async Task<List<DocumentSummaryDto>> SearchAsync(int? employeeId, string? type, IReadOnlyCollection<string>? allowedTypes,
        CancellationToken ct)
    {
        var query = db.EmployeeDocuments.AsNoTracking();
        if (allowedTypes is not null) query = query.Where(d => allowedTypes.Contains(d.DocumentType));
        if (employeeId is int emp) query = query.Where(d => d.EmployeeId == emp);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(d => d.DocumentType == type);

        var rows = await query
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.DocumentId)
            .Select(d => new
            {
                d.DocumentId, d.EmployeeId, d.Employee.EmployeeCode, Name = d.Employee.FirstName + " " + d.Employee.LastName,
                d.DocumentType, d.Title, d.FinancialYear, d.IssueDate, d.ReleasedOn,
                ReleasedBy = d.ReleasedBy != null ? d.ReleasedBy.Username : null,
            })
            .ToListAsync(ct);

        return rows.Select(d => new DocumentSummaryDto(d.DocumentId, d.EmployeeId, d.EmployeeCode, d.Name, d.DocumentType,
                d.Title, ReferenceNo(d.DocumentType, d.IssueDate, d.DocumentId), d.FinancialYear, d.IssueDate, d.ReleasedOn,
                d.ReleasedBy))
            .ToList();
    }

    public async Task<DocumentDto> GetAsync(int id, CancellationToken ct)
    {
        var d = await db.EmployeeDocuments.AsNoTracking()
                    .Where(x => x.DocumentId == id)
                    .Select(x => new
                    {
                        x.DocumentId, x.EmployeeId, x.Employee.EmployeeCode, Name = x.Employee.FirstName + " " + x.Employee.LastName,
                        x.DocumentType, x.Title, x.FinancialYear, x.IssueDate, x.ReleasedOn,
                        ReleasedBy = x.ReleasedBy != null ? x.ReleasedBy.Username : null, x.Data,
                    })
                    .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Document not found.");

        using var json = JsonDocument.Parse(d.Data);
        return new DocumentDto(d.DocumentId, d.EmployeeId, d.EmployeeCode, d.Name, d.DocumentType, d.Title,
            ReferenceNo(d.DocumentType, d.IssueDate, d.DocumentId), d.FinancialYear, d.IssueDate, d.ReleasedOn,
            d.ReleasedBy, json.RootElement.Clone());
    }

    // ------------------------------------------------------------------ Release
    public async Task<DocumentDto> ReleaseOfferLetterAsync(OfferLetterRequest req, int userId, CancellationToken ct)
    {
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        if (req.AcceptBy is { } acceptBy && acceptBy < (req.IssueDate ?? Today))
            throw new AppException("'Accept by' date cannot be before the issue date.");

        IReadOnlyList<SalaryLineDto>? breakup = null;
        decimal ctc;
        if (req.IncludeSalaryBreakup)
        {
            var s = await CurrentStructureAsync(e.EmployeeId, ct)
                    ?? throw new AppException($"{e.FullName} has no salary structure yet. Set up their salary first, or untick 'Include salary breakup' and enter the CTC.");
            var employerPf = Math.Round(s.BasicPay * s.PfPercent / 100m, 2);
            breakup =
            [
                Line("Basic Pay", s.BasicPay),
                Line("House Rent Allowance", s.Hra),
                Line("Conveyance Allowance", s.ConveyanceAllowance),
                Line("Medical Allowance", s.MedicalAllowance),
                Line("Special Allowance", s.SpecialAllowance),
                Line("Employer PF Contribution", employerPf),
            ];
            ctc = breakup.Sum(l => l.Annual);   // same formula as SalaryService (gross x 12 + employer PF x 12)
        }
        else
        {
            ctc = req.AnnualCtc is > 0 ? req.AnnualCtc.Value : throw new AppException("Please enter the annual CTC.");
        }

        // Reporting manager: as chosen on the form, else the employee's current manager.
        string? reportingTo = null;
        if ((req.ReportingManagerId ?? e.ManagerId) is int managerId)
        {
            if (managerId == e.EmployeeId) throw new AppException("An employee cannot report to themselves.");
            var m = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == managerId)
                        .Select(x => new { Name = x.FirstName + " " + x.LastName, x.Designation }).FirstOrDefaultAsync(ct)
                    ?? throw new AppException("Selected reporting manager was not found.");
            reportingTo = $"{m.Name}, {m.Designation}";
        }

        var data = new OfferLetterData(e.FullName, e.EmployeeCode, e.Email, req.Designation.Trim(), req.Department.Trim(),
            req.DateOfJoining, ctc, breakup, req.ProbationMonths, req.NoticePeriodDays, Clean(req.WorkLocation), req.AcceptBy,
            Clean(req.AdditionalTerms), reportingTo);
        return await SaveAsync(e.EmployeeId, DocumentTypes.OfferLetter, null, "Offer Letter", req.IssueDate, data, userId, ct);
    }

    public async Task<DocumentDto> ReleaseRelievingLetterAsync(RelievingLetterRequest req, int userId, CancellationToken ct)
    {
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        if (req.LastWorkingDay < e.DateOfJoining)
            throw new AppException("Last working day cannot be before the date of joining.");
        if (req.ResignationDate is { } resigned && (resigned > req.LastWorkingDay || resigned < e.DateOfJoining))
            throw new AppException("Resignation date must be between the date of joining and the last working day.");

        var manager = e.ManagerId is int mid
            ? await db.Employees.AsNoTracking().Where(x => x.EmployeeId == mid)
                .Select(x => x.FirstName + " " + x.LastName + ", " + x.Designation).FirstOrDefaultAsync(ct)
            : null;
        var data = new RelievingLetterData(e.FullName, e.EmployeeCode, Clean(req.Designation) ?? e.Designation,
            e.Department.Name, e.DateOfJoining, req.ResignationDate, req.LastWorkingDay, Clean(req.Remarks), manager);
        return await SaveAsync(e.EmployeeId, DocumentTypes.RelievingLetter, null, "Relieving Letter", req.IssueDate, data, userId, ct);
    }

    public async Task<DocumentDto> ReleaseForm16Async(Form16Request req, int userId, CancellationToken ct)
    {
        var e = await LoadEmployeeAsync(req.EmployeeId, ct);
        var fy = req.FinancialYear;
        var fyStart = new DateOnly(fy, 4, 1);
        var fyEnd = new DateOnly(fy + 1, 3, 31);
        if (fyStart > Today) throw new AppException($"FY {FyLabel(fy)} has not started yet.");

        // Only paid payslips count: TDS is deducted when salary is paid.
        var payslips = await db.Payslips.AsNoTracking()
            .Where(p => p.EmployeeId == e.EmployeeId && p.Status == PayslipStatus.Paid &&
                        ((p.PayYear == fy && p.PayMonth >= 4) || (p.PayYear == fy + 1 && p.PayMonth <= 3)))
            .OrderBy(p => p.PayYear).ThenBy(p => p.PayMonth)
            .ToListAsync(ct);
        if (payslips.Count == 0)
            throw new AppException($"{e.FullName} has no paid payslips in FY {FyLabel(fy)}.");

        var months = payslips.Select(p => new Form16MonthDto(p.PayYear, p.PayMonth, p.GrossEarnings - p.LopDeduction,
            p.PfDeduction, p.ProfessionalTax, p.IncomeTax)).ToList();

        string[] quarterNames = ["Q1 (Apr - Jun)", "Q2 (Jul - Sep)", "Q3 (Oct - Dec)", "Q4 (Jan - Mar)"];
        var quarters = quarterNames.Select((name, q) =>
        {
            var inQuarter = months.Where(m => QuarterOf(m.Month) == q).ToList();
            return new Form16QuarterDto(name, inQuarter.Sum(m => m.GrossEarned), inQuarter.Sum(m => m.Tds));
        }).ToList();

        var gross = months.Sum(m => m.GrossEarned);
        // New tax regime (s.115BAC): only the standard deduction applies; professional tax is not deductible.
        var standardDeduction = Math.Min(gross, fy >= 2024 ? 75_000m : 50_000m);
        var last = payslips[^1];

        var data = new Form16Data(e.FullName, e.EmployeeCode, e.PanNumber, e.Designation, e.Department.Name,
            FyLabel(fy), FyLabel(fy + 1),
            PeriodFrom: e.DateOfJoining > fyStart ? e.DateOfJoining : fyStart,
            PeriodTo: new DateOnly(last.PayYear, last.PayMonth, DateTime.DaysInMonth(last.PayYear, last.PayMonth)),
            IsProvisional: fyEnd >= Today,
            TaxRegime: "New tax regime (section 115BAC)",
            months, quarters, gross, standardDeduction, gross - standardDeduction,
            months.Sum(m => m.ProfessionalTax), months.Sum(m => m.Pf), months.Sum(m => m.Tds));

        return await SaveAsync(e.EmployeeId, DocumentTypes.Form16, fy, $"Form 16 - FY {FyLabel(fy)}", req.IssueDate, data, userId, ct);
    }

    /// <summary>Withdraws a released document (it can then be released again with corrected details).</summary>
    public async Task RevokeAsync(int id, IReadOnlyCollection<string> allowedTypes, CancellationToken ct)
    {
        var type = await db.EmployeeDocuments.Where(d => d.DocumentId == id).Select(d => d.DocumentType).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Document not found.");
        if (!allowedTypes.Contains(type)) throw new ForbiddenException("You are not allowed to revoke this type of document.");
        await db.EmployeeDocuments.Where(d => d.DocumentId == id).ExecuteDeleteAsync(ct);
    }

    // ------------------------------------------------------------------ Helpers
    /// <param name="emailEmployee">Tell the employee by email (promotions send their own, richer email).</param>
    internal async Task<DocumentDto> SaveAsync(int employeeId, string type, int? financialYear, string title,
        DateOnly? issueDate, object data, int userId, CancellationToken ct, bool emailEmployee = true)
    {
        // One offer / relieving letter and one Form 16 per year; an employee can be promoted many times.
        if (type != DocumentTypes.PromotionLetter &&
            await db.EmployeeDocuments.AnyAsync(d => d.EmployeeId == employeeId && d.DocumentType == type &&
                                                     d.FinancialYear == financialYear, ct))
            throw new ConflictException($"A {title} has already been released to this employee. Revoke it first to issue a new one.");

        var doc = new EmployeeDocument
        {
            EmployeeId = employeeId,
            DocumentType = type,
            FinancialYear = financialYear,
            Title = title,
            IssueDate = issueDate ?? Today,
            Data = JsonSerializer.Serialize(data, data.GetType(), Json),
            ReleasedByUserId = userId,
        };
        db.EmployeeDocuments.Add(doc);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            throw new ConflictException($"A {title} has already been released to this employee.");
        }
        var dto = await GetAsync(doc.DocumentId, ct);
        if (emailEmployee) await notify.DocumentReleasedAsync(dto, ct);
        return dto;
    }

    private async Task<Employee> LoadEmployeeAsync(int id, CancellationToken ct) =>
        await db.Employees.AsNoTracking().Include(e => e.Department).FirstOrDefaultAsync(e => e.EmployeeId == id, ct)
        ?? throw new NotFoundException("Employee not found.");

    /// <summary>Structure in force today, or the earliest one if all start in the future (new joiner).</summary>
    private async Task<SalaryStructure?> CurrentStructureAsync(int employeeId, CancellationToken ct)
    {
        var today = Today;
        var mine = db.SalaryStructures.AsNoTracking().Where(s => s.EmployeeId == employeeId);
        return await mine.Where(s => s.EffectiveFrom <= today).OrderByDescending(s => s.EffectiveFrom).FirstOrDefaultAsync(ct)
               ?? await mine.OrderBy(s => s.EffectiveFrom).FirstOrDefaultAsync(ct);
    }

    private static SalaryLineDto Line(string name, decimal monthly) => new(name, monthly, monthly * 12);

    private static int QuarterOf(int month) => month >= 4 ? (month - 4) / 3 : 3;

    private static string FyLabel(int startYear) => $"{startYear}-{(startYear + 1) % 100:D2}";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string ReferenceNo(string type, DateOnly issueDate, int id) =>
        $"{type switch { DocumentTypes.OfferLetter => "OL", DocumentTypes.RelievingLetter => "RL", DocumentTypes.PromotionLetter => "PL", _ => "F16" }}/{issueDate.Year}/{id:D4}";
}
