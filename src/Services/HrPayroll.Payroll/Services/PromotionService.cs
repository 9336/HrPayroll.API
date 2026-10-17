using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Payroll.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Payroll.Entities;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Payroll.Services;

/// <summary>Who is promoting: HR (promotions.*) and whether they may also see / raise salaries.</summary>
/// <summary>CanSeeSalary: salary.view (current pay shown); CanGiveIncrement: salary.add.</summary>
public record PromotionActor(int UserId, bool CanSeeSalary, bool CanGiveIncrement);

/// <summary>
/// Employee promotions. A promotion changes the designation (and optionally department and reporting manager) on its
/// effective date - right away when that date has come, otherwise <see cref="PromotionScheduler"/> applies it then.
/// An increment becomes a normal, dated salary revision; a promotion letter is released to the employee and the
/// employee and managers are emailed.
/// </summary>
public class PromotionService(PayrollDbContext db, OrgService org, SalaryService salary, DocumentService documents,
    EmailQueue mail, TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public async Task<List<PromotionDto>> SearchAsync(string? status, int? employeeId, int? year, CancellationToken ct)
    {
        await ApplyDueAsync(ct);   // keep the list truthful even if the scheduler has not run yet today
        var q = db.Promotions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
        if (employeeId is int emp) q = q.Where(p => p.EmployeeId == emp);
        if (year is int y) q = q.Where(p => p.EffectiveDate.Year == y);
        return await Project(q.OrderByDescending(p => p.EffectiveDate).ThenByDescending(p => p.PromotionId).Take(500)).ToListAsync(ct);
    }

    public async Task<PromotionContextDto> GetContextAsync(int employeeId, PromotionActor actor, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == employeeId)
                    .Select(x => new
                    {
                        x.EmployeeId, Name = x.FirstName + " " + x.LastName, x.Designation, x.DepartmentId, Department = x.Department.Name,
                        x.ManagerId, Manager = x.Manager != null ? x.Manager.FirstName + " " + x.Manager.LastName : null, x.DateOfJoining,
                    }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Employee not found.");
        var last = await db.Promotions.AsNoTracking().Where(p => p.EmployeeId == employeeId && p.Status == PromotionStatus.Applied)
            .OrderByDescending(p => p.EffectiveDate).Select(p => new { p.EffectiveDate, p.NewDesignation }).FirstOrDefaultAsync(ct);
        var scheduled = await db.Promotions.AnyAsync(p => p.EmployeeId == employeeId && p.Status == PromotionStatus.Scheduled, ct);
        decimal? gross = actor.CanSeeSalary ? (await salary.GetCurrentAsync(employeeId, ct))?.GrossMonthly : null;
        return new PromotionContextDto(e.EmployeeId, e.Name, e.Designation, e.DepartmentId, e.Department, e.ManagerId, e.Manager,
            e.DateOfJoining, gross, last?.EffectiveDate, last?.NewDesignation, scheduled);
    }

    public async Task<PromotionDto> CreateAsync(CreatePromotionRequest req, PromotionActor actor, CancellationToken ct)
    {
        var e = await db.Employees.Include(x => x.Department).FirstOrDefaultAsync(x => x.EmployeeId == req.EmployeeId, ct)
                ?? throw new NotFoundException("Employee not found.");
        if (!e.IsActive) throw new AppException("Only active employees can be promoted.");
        if (req.EffectiveDate < e.DateOfJoining) throw new AppException("The effective date cannot be before the date of joining.");
        if (req.EffectiveDate > Today.AddYears(1)) throw new AppException("The effective date can be at most one year ahead.");
        if (await db.Promotions.AnyAsync(p => p.EmployeeId == e.EmployeeId && p.Status == PromotionStatus.Scheduled, ct))
            throw new ConflictException($"{e.FullName} already has a scheduled promotion. Cancel it first to schedule a different one.");
        if (await db.Promotions.AnyAsync(p => p.EmployeeId == e.EmployeeId && p.Status == PromotionStatus.Applied && p.EffectiveDate >= req.EffectiveDate, ct))
            throw new AppException("The effective date must be after the employee's last promotion.");

        var newDesignation = req.NewDesignation.Trim();
        var newDepartmentId = req.NewDepartmentId ?? e.DepartmentId;
        var newDepartment = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.DepartmentId == newDepartmentId, ct)
                            ?? throw new AppException("Choose a valid department.");
        if (req.NewManagerId is int newManager)
        {
            if (newManager == e.EmployeeId) throw new AppException("An employee cannot report to themselves.");
            if (!await db.Employees.AnyAsync(x => x.EmployeeId == newManager && x.IsActive, ct)) throw new AppException("Choose an active reporting manager.");
            if (await org.IsSubordinateAsync(e.EmployeeId, newManager, ct))
                throw new AppException("The new manager reports to this employee - that would make a loop in the hierarchy.");
        }
        var increment = req.IncrementPercent is > 0 ? Math.Round(req.IncrementPercent.Value, 2) : (decimal?)null;
        if (increment is not null && !actor.CanGiveIncrement)
            throw new ForbiddenException("You need the Salaries: Add permission to give an increment.");
        var managerChanges = req.NewManagerId is int m && m != e.ManagerId;
        if (newDesignation == e.Designation && newDepartmentId == e.DepartmentId && !managerChanges && increment is null)
            throw new AppException("Nothing changes - enter the new designation (or a department, manager or increment change).");

        var promotion = new Promotion
        {
            EmployeeId = e.EmployeeId, EffectiveDate = req.EffectiveDate,
            OldDesignation = e.Designation, NewDesignation = newDesignation,
            OldDepartmentId = e.DepartmentId, NewDepartmentId = newDepartmentId,
            OldManagerId = e.ManagerId, NewManagerId = managerChanges ? req.NewManagerId : e.ManagerId,
            IncrementPercent = increment, Remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim(),
            Status = PromotionStatus.Scheduled, CreatedByUserId = actor.UserId, CreatedAt = time.GetUtcNow().UtcDateTime,
        };

        // Increment = a dated salary revision (revisions start on the 1st, so a mid-month promotion pays from next month).
        SalaryStructureDto? revised = null;
        if (increment is decimal pct)
        {
            var salaryFrom = req.EffectiveDate.Day == 1 ? req.EffectiveDate : new DateOnly(req.EffectiveDate.Year, req.EffectiveDate.Month, 1).AddMonths(1);
            var current = await db.SalaryStructures.AsNoTracking().Where(s => s.EmployeeId == e.EmployeeId && s.EffectiveFrom <= salaryFrom)
                              .OrderByDescending(s => s.EffectiveFrom).FirstOrDefaultAsync(ct)
                          ?? throw new AppException($"{e.FullName} has no salary structure yet - set up their salary before giving an increment.");
            var factor = 1 + pct / 100m;
            revised = await salary.CreateRevisionAsync(new SalaryStructureRequest
            {
                EmployeeId = e.EmployeeId, EffectiveFrom = salaryFrom,
                BasicPay = Math.Round(current.BasicPay * factor, 2), Hra = Math.Round(current.Hra * factor, 2),
                ConveyanceAllowance = Math.Round(current.ConveyanceAllowance * factor, 2), MedicalAllowance = Math.Round(current.MedicalAllowance * factor, 2),
                SpecialAllowance = Math.Round(current.SpecialAllowance * factor, 2),
                PfPercent = current.PfPercent, ProfessionalTax = current.ProfessionalTax, MonthlyTds = current.MonthlyTds,
                Remarks = $"Promotion to {newDesignation} (+{pct:0.##}%)",
            }, actor.UserId, ct);
            promotion.OldMonthlyGross = current.Gross;
            promotion.NewMonthlyGross = revised.GrossMonthly;
            promotion.SalaryStructureId = revised.SalaryStructureId;
            promotion.SalaryEffectiveFrom = salaryFrom;
        }

        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(ct);

        if (req.IssueLetter)
        {
            var reportingTo = promotion.NewManagerId is int mgr
                ? await db.Employees.Where(x => x.EmployeeId == mgr).Select(x => x.FirstName + " " + x.LastName + ", " + x.Designation).FirstOrDefaultAsync(ct)
                : null;
            var letter = new PromotionLetterData(e.FullName, e.EmployeeCode, e.Designation, newDesignation, newDepartment.Name,
                newDepartmentId != e.DepartmentId ? e.Department.Name : null, req.EffectiveDate, reportingTo,
                increment, revised?.GrossMonthly, revised?.AnnualCtc, promotion.SalaryEffectiveFrom, promotion.Remarks);
            var doc = await documents.SaveAsync(e.EmployeeId, DocumentTypes.PromotionLetter, null, "Promotion Letter", Today, letter,
                actor.UserId, ct, emailEmployee: false);
            promotion.DocumentId = doc.DocumentId;
        }

        if (promotion.EffectiveDate <= Today) Apply(promotion);
        await NotifyAsync(promotion, e, newDepartment.Name, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(promotion.PromotionId, ct);
    }

    /// <summary>A scheduled promotion can be withdrawn; its salary revision and letter go with it.</summary>
    public async Task<PromotionDto> CancelAsync(int id, CancellationToken ct)
    {
        var p = await db.Promotions.FirstOrDefaultAsync(x => x.PromotionId == id, ct) ?? throw new NotFoundException("Promotion not found.");
        if (p.Status != PromotionStatus.Scheduled)
            throw new ConflictException(p.Status == PromotionStatus.Applied
                ? "This promotion has already taken effect. Record a new promotion (or edit the employee) to change it."
                : "This promotion is already cancelled.");
        if (p.SalaryStructureId is int sid && await db.SalaryStructures.AnyAsync(s => s.SalaryStructureId == sid, ct))
            await salary.DeleteRevisionAsync(sid, ct);   // refuses if payroll already used it
        if (p.DocumentId is int did) await db.EmployeeDocuments.Where(d => d.DocumentId == did).ExecuteDeleteAsync(ct);
        p.Status = PromotionStatus.Cancelled;
        p.SalaryStructureId = null;
        p.DocumentId = null;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Applies scheduled promotions whose effective date has come. Returns how many were applied.</summary>
    public async Task<int> ApplyDueAsync(CancellationToken ct)
    {
        var today = Today;
        var due = await db.Promotions.Where(p => p.Status == PromotionStatus.Scheduled && p.EffectiveDate <= today).ToListAsync(ct);
        if (due.Count == 0) return 0;
        foreach (var p in due) Apply(p);
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    public async Task<PromotionDto> GetAsync(int id, CancellationToken ct) =>
        await Project(db.Promotions.AsNoTracking().Where(p => p.PromotionId == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Promotion not found.");

    // ------------------------------------------------------------------ Helpers
    /// <summary>
    /// Marks the promotion applied and tells the Employees service (hr.promotions), which updates the employee record -
    /// only what the promotion changes, so an edit made to the employee meanwhile is kept.
    /// </summary>
    private void Apply(Promotion p)
    {
        db.Publish(Topics.Promotions, p.EmployeeId.ToString(), new PromotionApplied(p.PromotionId, p.EmployeeId, p.EffectiveDate,
            p.NewDesignation, p.NewDepartmentId != p.OldDepartmentId ? p.NewDepartmentId : null,
            p.NewManagerId != p.OldManagerId, p.NewManagerId));
        p.Status = PromotionStatus.Applied;
        p.AppliedAt = time.GetUtcNow().UtcDateTime;
    }

    private async Task NotifyAsync(Promotion p, Employee e, string newDepartment, CancellationToken ct)
    {
        var company = await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";
        var when = p.EffectiveDate <= Today ? $"with effect from {p.EffectiveDate:dd MMM yyyy}" : $"effective {p.EffectiveDate:dd MMM yyyy}";
        mail.Enqueue(e.Email, e.FullName, $"Congratulations on your promotion to {p.NewDesignation}",
            Layout(company, "Congratulations on your promotion!",
                P($"Dear {e.FirstName},") +
                P($"We are pleased to promote you to {p.NewDesignation}{(p.NewDepartmentId != p.OldDepartmentId ? $" in {newDepartment}" : "")}, {when}.") +
                (p.IncrementPercent is decimal pct ? P($"Your salary is revised by {pct:0.##}% from {p.SalaryEffectiveFrom:MMMM yyyy}.") : "") +
                (p.Remarks is null ? "" : P(p.Remarks)) +
                P("Thank you for your contribution - we look forward to your continued success."),
                p.DocumentId is null ? "Open HR portal" : "View promotion letter",
                p.DocumentId is int d ? $"{mail.AppUrl}/me/documents/{d}" : $"{mail.AppUrl}/me/dashboard"), EmailCategories.Promotion);

        // Reporting managers (new and, if it changes, the old one) are told too - salary details are not included.
        var managerIds = new[] { p.NewManagerId, p.OldManagerId }.OfType<int>().Distinct().ToList();
        var managers = await db.Employees.Where(x => managerIds.Contains(x.EmployeeId) && x.IsActive)
            .Select(x => new { x.EmployeeId, x.Email, x.FirstName, x.LastName }).ToListAsync(ct);
        foreach (var m in managers)
        {
            var note = m.EmployeeId == p.NewManagerId
                ? p.NewManagerId != p.OldManagerId ? $" {e.FirstName} will now report to you." : ""
                : $" {e.FirstName} will report to a new manager from that date.";
            mail.Enqueue(m.Email, $"{m.FirstName} {m.LastName}", $"{e.FullName} promoted to {p.NewDesignation}",
                Layout(company, "Team update: promotion",
                    P($"Dear {m.FirstName},") + P($"{e.FullName} ({e.EmployeeCode}) has been promoted from {p.OldDesignation} to {p.NewDesignation}, {when}.{note}"),
                    "Company hierarchy", $"{mail.AppUrl}/org-chart"), EmailCategories.Promotion);
        }
    }

    private static IQueryable<PromotionDto> Project(IQueryable<Promotion> q) => q.Select(p => new PromotionDto(
        p.PromotionId, p.EmployeeId, p.Employee.FirstName + " " + p.Employee.LastName, p.Employee.EmployeeCode, p.EffectiveDate,
        p.OldDesignation, p.NewDesignation, p.OldDepartment.Name, p.NewDepartment.Name,
        p.OldManager != null ? p.OldManager.FirstName + " " + p.OldManager.LastName : null,
        p.NewManager != null ? p.NewManager.FirstName + " " + p.NewManager.LastName : null,
        p.IncrementPercent, p.OldMonthlyGross, p.NewMonthlyGross, p.SalaryEffectiveFrom, p.Remarks, p.Status, p.DocumentId,
        p.CreatedBy.Employee != null ? p.CreatedBy.Employee.FirstName + " " + p.CreatedBy.Employee.LastName : p.CreatedBy.Username,
        p.CreatedAt, p.AppliedAt));
}

/// <summary>Applies promotions on their effective date (checks every hour).</summary>
public class PromotionScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<PromotionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var applied = await scope.ServiceProvider.GetRequiredService<PromotionService>().ApplyDueAsync(stoppingToken);
                if (applied > 0) logger.LogInformation("Applied {Count} scheduled promotion(s)", applied);
                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Applying scheduled promotions failed");
                try { await Task.Delay(TimeSpan.FromMinutes(10), time, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
