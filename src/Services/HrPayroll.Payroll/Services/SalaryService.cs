using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Payroll.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Payroll.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Payroll.Services;

public class SalaryService(PayrollDbContext db, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public async Task<List<SalaryOverviewDto>> GetOverviewAsync(CancellationToken ct)
    {
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.EmployeeCode)
            .Select(e => new { e.EmployeeId, e.EmployeeCode, Name = e.FirstName + " " + e.LastName, Dept = e.Department.Name, e.Designation })
            .ToListAsync(ct);

        var today = Today;
        var current = (await db.SalaryStructures.AsNoTracking()
                .Where(s => s.EffectiveFrom <= today)
                .ToListAsync(ct))
            .GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.EffectiveFrom)!);

        return employees.Select(e => new SalaryOverviewDto(e.EmployeeId, e.EmployeeCode, e.Name, e.Dept, e.Designation,
                current.TryGetValue(e.EmployeeId, out var s) ? ToDto(s, true) : null))
            .ToList();
    }

    /// <summary>All revisions for an employee, newest first. The one in force today is flagged IsCurrent.</summary>
    public async Task<List<SalaryStructureDto>> GetHistoryAsync(int employeeId, CancellationToken ct)
    {
        var list = await db.SalaryStructures.AsNoTracking()
            .Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.EffectiveFrom)
            .ToListAsync(ct);
        var currentId = list.FirstOrDefault(s => s.EffectiveFrom <= Today)?.SalaryStructureId;
        return list.Select(s => ToDto(s, s.SalaryStructureId == currentId)).ToList();
    }

    public async Task<SalaryStructureDto?> GetCurrentAsync(int employeeId, CancellationToken ct)
    {
        var today = Today;
        var s = await db.SalaryStructures.AsNoTracking()
            .Where(x => x.EmployeeId == employeeId && x.EffectiveFrom <= today)
            .OrderByDescending(x => x.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        return s is null ? null : ToDto(s, true);
    }

    public async Task<SalaryStructureDto> CreateRevisionAsync(SalaryStructureRequest req, int adminUserId, CancellationToken ct)
    {
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == req.EmployeeId, ct))
            throw new NotFoundException("Employee not found.");
        if (req.EffectiveFrom.Day != 1)
            throw new AppException("Salary revisions must be effective from the 1st day of a month.");
        if (await db.SalaryStructures.AnyAsync(s => s.EmployeeId == req.EmployeeId && s.EffectiveFrom == req.EffectiveFrom, ct))
            throw new ConflictException("A salary revision already exists for this effective date.");
        await EnsureNoPaidPayrollFromAsync(req.EmployeeId, req.EffectiveFrom, ct);
        await FinancialYears.EnsureOpenAsync(db, req.EffectiveFrom.Year, req.EffectiveFrom.Month, ct);

        var entity = new SalaryStructure
        {
            EmployeeId = req.EmployeeId,
            EffectiveFrom = req.EffectiveFrom,
            BasicPay = req.BasicPay,
            Hra = req.Hra,
            ConveyanceAllowance = req.ConveyanceAllowance,
            MedicalAllowance = req.MedicalAllowance,
            SpecialAllowance = req.SpecialAllowance,
            PfPercent = req.PfPercent,
            ProfessionalTax = req.ProfessionalTax,
            MonthlyTds = req.MonthlyTds,
            Remarks = req.Remarks?.Trim(),
            CreatedByUserId = adminUserId,
        };
        db.SalaryStructures.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity, entity.EffectiveFrom <= Today);
    }

    public async Task DeleteRevisionAsync(int salaryStructureId, CancellationToken ct)
    {
        var s = await db.SalaryStructures.FindAsync([salaryStructureId], ct)
                ?? throw new NotFoundException("Salary revision not found.");
        if (await db.Payslips.AnyAsync(p => p.SalaryStructureId == salaryStructureId, ct))
            throw new ConflictException("This revision has already been used in payroll and cannot be deleted. Create a new revision instead.");
        db.SalaryStructures.Remove(s);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNoPaidPayrollFromAsync(int employeeId, DateOnly from, CancellationToken ct)
    {
        var periodKey = from.Year * 100 + from.Month;
        if (await db.Payslips.AnyAsync(p => p.EmployeeId == employeeId && p.Status == PayslipStatus.Paid
                                            && p.PayYear * 100 + p.PayMonth >= periodKey, ct))
            throw new ConflictException("Payroll for this period has already been paid. Choose a later effective month.");
    }

    public static SalaryStructureDto ToDto(SalaryStructure s, bool isCurrent) => new(
        s.SalaryStructureId, s.EmployeeId, s.EffectiveFrom, s.BasicPay, s.Hra, s.ConveyanceAllowance,
        s.MedicalAllowance, s.SpecialAllowance, s.Gross, s.PfPercent, s.ProfessionalTax, s.MonthlyTds,
        PayrollCalculator.EstimatedNet(s), s.Gross * 12 + Math.Round(s.BasicPay * s.PfPercent / 100m, 2) * 12,
        s.Remarks, s.CreatedAt, isCurrent);
}
