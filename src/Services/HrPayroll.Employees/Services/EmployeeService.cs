using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Employees.Services;

public class EmployeeService(EmployeesDbContext db, OrgService org, TimeProvider time)
{
    public async Task<List<EmployeeListItemDto>> SearchAsync(string? search, int? departmentId, bool? isActive, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var query = db.Employees.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(e => e.EmployeeCode.Contains(s) || e.FirstName.Contains(s) || e.LastName.Contains(s)
                                     || e.Email.Contains(s) || e.Designation.Contains(s));
        }
        if (departmentId is int dep) query = query.Where(e => e.DepartmentId == dep);
        if (isActive is bool active) query = query.Where(e => e.IsActive == active);

        return await query
            .OrderBy(e => e.EmployeeCode)
            .Select(e => new EmployeeListItemDto(
                e.EmployeeId, e.EmployeeCode, e.FirstName + " " + e.LastName, e.Email, e.Department.Name,
                e.Designation, e.DateOfJoining, e.IsActive, e.User != null ? e.User.Username : null,
                db.SalarySnapshots.Where(s => s.EmployeeId == e.EmployeeId && s.EffectiveFrom <= today)
                    .OrderByDescending(s => s.EffectiveFrom).Select(s => (decimal?)s.GrossMonthly).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<EmployeeDto> GetAsync(int id, CancellationToken ct)
    {
        var dto = await db.Employees.AsNoTracking()
                      .Where(e => e.EmployeeId == id)
                      .Select(e => new EmployeeDto(
                          e.EmployeeId, e.EmployeeCode, e.FirstName, e.LastName, e.Email, e.Phone, e.Gender, e.DateOfBirth,
                          e.DateOfJoining, e.DepartmentId, e.Department.Name, e.Designation, e.PanNumber, e.BankName,
                          e.BankAccountNo, e.IfscCode, e.IsActive,
                          e.User != null ? e.User.Username : null, e.User != null ? e.User.Role.Name : null,
                          e.ManagerId, e.Manager != null ? e.Manager.FirstName + " " + e.Manager.LastName : null,
                          e.BloodGroup, e.EmergencyContactName, e.EmergencyContactPhone, null))
                      .FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Employee not found.");
        return dto with { PhotoDataUrl = await PhotoDataUrlAsync(id, ct) };
    }

    // ------------------------------------------------------------------ ID card & photo
    public const int MaxPhotoBytes = 1024 * 1024;

    public async Task<IdCardDto> GetIdCardAsync(int id, CancellationToken ct)
    {
        var card = await db.Employees.AsNoTracking()
                       .Where(e => e.EmployeeId == id)
                       .Select(e => new IdCardDto(e.EmployeeId, e.EmployeeCode, e.FirstName + " " + e.LastName, e.Designation,
                           e.Department.Name, e.BloodGroup, e.Phone, e.Email, e.EmergencyContactName, e.EmergencyContactPhone,
                           e.DateOfJoining, e.IsActive, null))
                       .FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Employee not found.");
        return card with { PhotoDataUrl = await PhotoDataUrlAsync(id, ct) };
    }

    public async Task SetPhotoAsync(int employeeId, IFormFile file, CancellationToken ct)
    {
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == employeeId, ct)) throw new NotFoundException("Employee not found.");
        var bytes = await FileSignatures.ReadAsync(file, MaxPhotoBytes, ct);
        var type = FileSignatures.DetectImage(bytes) ?? throw new AppException("Photo must be a PNG, JPEG or WebP image.");

        var photo = await db.EmployeePhotos.FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        if (photo is null) db.EmployeePhotos.Add(photo = new EmployeePhoto { EmployeeId = employeeId });
        photo.Photo = bytes;
        photo.ContentType = type;
        photo.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public Task RemovePhotoAsync(int employeeId, CancellationToken ct) =>
        db.EmployeePhotos.Where(p => p.EmployeeId == employeeId).ExecuteDeleteAsync(ct);

    private async Task<string?> PhotoDataUrlAsync(int employeeId, CancellationToken ct)
    {
        var p = await db.EmployeePhotos.AsNoTracking().Where(x => x.EmployeeId == employeeId)
            .Select(x => new { x.Photo, x.ContentType }).FirstOrDefaultAsync(ct);
        return p is null ? null : FileSignatures.ToDataUrl(p.Photo, p.ContentType);
    }

    public async Task<EmployeeDto> CreateAsync(CreateEmployeeRequest req, bool canAssignRoles, CancellationToken ct)
    {
        if (req.RoleId != Roles.EmployeeId && !canAssignRoles)
            throw new ForbiddenException("Only users who can manage roles may create logins with a role other than Employee.");
        if (!await db.Roles.AnyAsync(r => r.RoleId == req.RoleId, ct))
            throw new AppException("Selected role does not exist.");
        await EnsureUniqueAsync(req.Email, null, ct);
        if (await db.Users.AnyAsync(u => u.Username == req.Username, ct))
            throw new ConflictException($"Username '{req.Username}' is already taken.");
        await EnsureDepartmentAsync(req.DepartmentId, ct);
        await EnsureManagerAsync(req.ManagerId, null, ct);

        // The password is hashed here and only the hash travels (to the Identity service, which keeps the logins).
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.InitialPassword, workFactor: 11);

        var employeeId = await db.InTransactionAsync(async token =>
        {
            var employee = new Employee { EmployeeCode = await NextEmployeeCodeAsync(token) };
            Apply(employee, req);
            db.Employees.Add(employee);
            await db.SaveChangesAsync(token);   // publishes EmployeeUpserted (IsNew): the Leave service allocates this year's leave

            db.Publish(Topics.LoginRequests, employee.EmployeeId.ToString(),
                new LoginRequested(employee.EmployeeId, req.Username.Trim(), passwordHash, req.RoleId));
            await db.SaveChangesAsync(token);
            return employee.EmployeeId;
        }, ct);

        // The login is created by the Identity service a moment later; show what was asked for meanwhile.
        var created = await GetAsync(employeeId, ct);
        return created.Username is null
            ? created with { Username = req.Username.Trim(), Role = await db.Roles.Where(r => r.RoleId == req.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct) }
            : created;
    }

    /// <summary>
    /// <paramref name="canEdit"/> (employees.edit) changes the details; <paramref name="canDeactivate"/> (employees.delete)
    /// switches the employee active / inactive. With only one of them, the other part must stay unchanged.
    /// The Identity service deactivates / reactivates the login when it sees the change (hr.employees).
    /// </summary>
    public async Task<EmployeeDto> UpdateAsync(int id, EmployeeUpsertRequest req, bool canEdit, bool canDeactivate, CancellationToken ct)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.EmployeeId == id, ct)
                       ?? throw new NotFoundException("Employee not found.");
        if (req.IsActive != employee.IsActive && !canDeactivate)
            throw new ForbiddenException("You do not have permission to deactivate or reactivate employees.");
        if (!canEdit)
        {
            // Deactivate / reactivate only: keep every other field as it is.
            employee.IsActive = req.IsActive;
            employee.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }
        await EnsureUniqueAsync(req.Email, id, ct);
        await EnsureDepartmentAsync(req.DepartmentId, ct);
        if (req.ManagerId != employee.ManagerId) await EnsureManagerAsync(req.ManagerId, id, ct);

        Apply(employee, req);
        employee.UpdatedAt = time.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>A promotion took effect (Payroll service): only what the promotion changes is updated.</summary>
    public async Task ApplyPromotionAsync(PromotionApplied p, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.EmployeeId == p.EmployeeId, ct);
        if (e is null) return;
        e.Designation = p.NewDesignation;
        if (p.NewDepartmentId is int dep) e.DepartmentId = dep;
        if (p.ManagerChanged) e.ManagerId = p.NewManagerId;
        e.UpdatedAt = time.GetUtcNow().UtcDateTime;
    }

    /// <summary>Company hierarchy - every active employee with their reporting manager.</summary>
    public Task<List<OrgNodeDto>> GetChartAsync(CancellationToken ct) =>
        db.Employees.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new OrgNodeDto(e.EmployeeId, e.EmployeeCode, e.FirstName + " " + e.LastName, e.Designation,
                e.Department.Name, e.Email, e.ManagerId))
            .ToListAsync(ct);

    public Task<List<HolidayDto>> GetHolidaysAsync(int year, CancellationToken ct) =>
        db.Holidays.AsNoTracking().Where(h => h.HolidayDate.Year == year).OrderBy(h => h.HolidayDate)
            .Select(h => new HolidayDto(h.HolidayDate, h.Name)).ToListAsync(ct);

    public Task<List<HolidayDto>> GetUpcomingHolidaysAsync(int take, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        return db.Holidays.AsNoTracking().Where(h => h.HolidayDate >= today).OrderBy(h => h.HolidayDate).Take(Math.Clamp(take, 1, 50))
            .Select(h => new HolidayDto(h.HolidayDate, h.Name)).ToListAsync(ct);
    }

    public async Task<EmployeeDashboardPart> GetDashboardAsync(CancellationToken ct)
    {
        var active = await db.Employees.CountAsync(e => e.IsActive, ct);
        var headcount = (await db.Employees.AsNoTracking().Where(e => e.IsActive)
                .GroupBy(e => e.Department.Name)
                .Select(g => new { Department = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct))
            .Select(x => new DepartmentHeadcountDto(x.Department, x.Count))
            .ToList();
        return new EmployeeDashboardPart(active, headcount);
    }

    public async Task<(byte[] Photo, string ContentType)?> GetPhotoAsync(int employeeId, CancellationToken ct)
    {
        var p = await db.EmployeePhotos.AsNoTracking().Where(x => x.EmployeeId == employeeId)
            .Select(x => new { x.Photo, x.ContentType }).FirstOrDefaultAsync(ct);
        return p is null ? null : (p.Photo, p.ContentType);
    }

    public Task<List<LookupDto>> GetDepartmentsAsync(CancellationToken ct) =>
        db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new LookupDto(d.DepartmentId, d.Name)).ToListAsync(ct);

    // ------------------------------------------------------------------
    private static void Apply(Employee e, EmployeeUpsertRequest r)
    {
        e.FirstName = r.FirstName.Trim();
        e.LastName = r.LastName.Trim();
        e.Email = r.Email.Trim().ToLowerInvariant();
        e.Phone = r.Phone?.Trim();
        e.Gender = r.Gender;
        e.DateOfBirth = r.DateOfBirth;
        e.DateOfJoining = r.DateOfJoining;
        e.DepartmentId = r.DepartmentId;
        e.Designation = r.Designation.Trim();
        e.ManagerId = r.ManagerId;
        e.PanNumber = r.PanNumber?.ToUpperInvariant();
        e.BankName = r.BankName?.Trim();
        e.BankAccountNo = r.BankAccountNo?.Trim();
        e.IfscCode = r.IfscCode?.ToUpperInvariant();
        e.BloodGroup = string.IsNullOrWhiteSpace(r.BloodGroup) ? null : r.BloodGroup.Trim().ToUpperInvariant();
        e.EmergencyContactName = string.IsNullOrWhiteSpace(r.EmergencyContactName) ? null : r.EmergencyContactName.Trim();
        e.EmergencyContactPhone = string.IsNullOrWhiteSpace(r.EmergencyContactPhone) ? null : r.EmergencyContactPhone.Trim();
        e.IsActive = r.IsActive;
    }

    private async Task EnsureUniqueAsync(string email, int? excludeId, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == normalized && e.EmployeeId != excludeId, ct))
            throw new ConflictException($"An employee with email '{email}' already exists.");
    }

    private async Task EnsureDepartmentAsync(int departmentId, CancellationToken ct)
    {
        if (!await db.Departments.AnyAsync(d => d.DepartmentId == departmentId && d.IsActive, ct))
            throw new AppException("Invalid department.");
    }

    /// <summary>Manager must be an active employee and must not create a reporting loop.</summary>
    private async Task EnsureManagerAsync(int? managerId, int? employeeId, CancellationToken ct)
    {
        if (managerId is not int m) return;
        if (m == employeeId) throw new AppException("An employee cannot report to themselves.");
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == m && e.IsActive, ct))
            throw new AppException("Selected manager is not an active employee.");
        if (employeeId is int id && await org.IsSubordinateAsync(id, m, ct))
            throw new AppException("The selected manager already reports to this employee - that would create a reporting loop.");
    }

    private async Task<string> NextEmployeeCodeAsync(CancellationToken ct)
    {
        var codes = await db.Employees.Where(e => e.EmployeeCode.StartsWith("EMP"))
            .Select(e => e.EmployeeCode).ToListAsync(ct);
        var max = codes.Select(c => int.TryParse(c[3..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"EMP{max + 1:D3}";
    }
}
