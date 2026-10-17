using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Leave.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Leave.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Leave.Services;

public class LeaveService(LeaveDbContext db, WorkingDayCalculator calculator, OrgService org, Notifier notify, TimeProvider time)
{
    /// <summary>How far in the past an employee may apply for leave (e.g. sick leave after returning).</summary>
    private const int MaxBackdatedDays = 30;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Leave types
    public async Task<List<LeaveTypeDto>> GetLeaveTypesAsync(bool activeOnly, CancellationToken ct) =>
        await db.LeaveTypes.AsNoTracking()
            .Where(t => !activeOnly || t.IsActive)
            .OrderBy(t => t.LeaveTypeId)
            .Select(t => new LeaveTypeDto(t.LeaveTypeId, t.Code, t.Name, t.AnnualQuota, t.IsPaid, t.IsBalanceTracked,
                t.AllowCarryForward, t.MaxCarryForward, t.AllowHalfDay, t.ApplicableGender, t.Description, t.IsActive))
            .ToListAsync(ct);

    public async Task UpdateLeaveTypeAsync(int id, UpdateLeaveTypeRequest req, CancellationToken ct)
    {
        var type = await db.LeaveTypes.FindAsync([id], ct) ?? throw new NotFoundException("Leave type not found.");
        type.Name = req.Name.Trim();
        type.AnnualQuota = req.AnnualQuota;
        type.AllowCarryForward = req.AllowCarryForward;
        type.MaxCarryForward = req.AllowCarryForward ? req.MaxCarryForward : 0;
        type.AllowHalfDay = req.AllowHalfDay;
        type.Description = req.Description;
        type.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Balances
    public async Task<List<LeaveBalanceDto>> GetBalancesAsync(int employeeId, int? year, CancellationToken ct)
    {
        var y = year ?? Today.Year;

        var pending = await db.LeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.Status == LeaveStatus.Pending && r.FromDate.Year == y)
            .GroupBy(r => r.LeaveTypeId)
            .Select(g => new { g.Key, Days = g.Sum(r => r.TotalDays) })
            .ToDictionaryAsync(x => x.Key, x => x.Days, ct);

        var balances = await db.LeaveBalances.AsNoTracking()
            .Where(b => b.EmployeeId == employeeId && b.Year == y && b.LeaveType.IsActive)
            .OrderBy(b => b.LeaveTypeId)
            .Select(b => new { b, b.LeaveType.Code, b.LeaveType.Name, b.LeaveType.IsPaid })
            .ToListAsync(ct);

        return balances.Select(x => new LeaveBalanceDto(
                x.b.LeaveTypeId, x.Code, x.Name, x.IsPaid, y, x.b.Allocated, x.b.CarryForwarded, x.b.Used,
                pending.GetValueOrDefault(x.b.LeaveTypeId), x.b.Available - pending.GetValueOrDefault(x.b.LeaveTypeId)))
            .ToList();
    }

    public async Task<LeavePreviewDto> PreviewAsync(DateOnly from, DateOnly to, bool isHalfDay, CancellationToken ct)
    {
        if (to < from) throw new AppException("'To' date must be on or after 'From' date.");
        if (to.DayNumber - from.DayNumber > 366) throw new AppException("Date range is too large.");
        var (days, excluded) = await calculator.CalculateAsync(from, to, isHalfDay, ct);
        return new LeavePreviewDto(days, excluded);
    }

    // ------------------------------------------------------------------ Apply / cancel (employee)
    public async Task<LeaveRequestDto> ApplyAsync(int employeeId, ApplyLeaveRequest req, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.EmployeeId == employeeId, ct)
                       ?? throw new NotFoundException("Employee not found.");
        if (!employee.IsActive) throw new AppException("Inactive employees cannot apply for leave.");

        var type = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(t => t.LeaveTypeId == req.LeaveTypeId && t.IsActive, ct)
                   ?? throw new AppException("Selected leave type is not available.");

        if (type.ApplicableGender is not null && type.ApplicableGender != employee.Gender)
            throw new AppException($"{type.Name} is not applicable to you.");
        if (req.ToDate < req.FromDate)
            throw new AppException("'To' date must be on or after 'From' date.");
        if (req.FromDate.Year != req.ToDate.Year)
            throw new AppException("A leave request cannot span two calendar years. Please split it into two requests.");
        if (req.FromDate < Today.AddDays(-MaxBackdatedDays))
            throw new AppException($"Leave cannot be applied more than {MaxBackdatedDays} days in the past.");
        if (req.FromDate < employee.DateOfJoining)
            throw new AppException("Leave cannot start before your date of joining.");
        if (req.IsHalfDay && req.FromDate != req.ToDate)
            throw new AppException("Half-day leave must be for a single date.");
        if (req.IsHalfDay && !type.AllowHalfDay)
            throw new AppException($"{type.Name} cannot be taken as half day.");

        var (days, _) = await calculator.CalculateAsync(req.FromDate, req.ToDate, req.IsHalfDay, ct);
        if (days <= 0) throw new AppException("The selected dates contain no working days (weekends / holidays).");

        var overlapping = await db.LeaveRequests.AnyAsync(r =>
            r.EmployeeId == employeeId &&
            (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved) &&
            r.FromDate <= req.ToDate && r.ToDate >= req.FromDate, ct);
        if (overlapping) throw new ConflictException("You already have a pending or approved leave overlapping these dates.");

        if (type.IsBalanceTracked)
        {
            var balance = (await GetBalancesAsync(employeeId, req.FromDate.Year, ct))
                .FirstOrDefault(b => b.LeaveTypeId == type.LeaveTypeId)
                ?? throw new AppException($"No {type.Name} balance allocated for {req.FromDate.Year}. Please contact HR.");
            if (balance.Available < days)
                throw new AppException(
                    $"Insufficient {type.Code} balance. Available: {balance.Available:0.#} day(s) " +
                    $"(after {balance.Pending:0.#} pending), requested: {days:0.#} day(s).");
        }

        var entity = new LeaveRequest
        {
            EmployeeId = employeeId,
            LeaveTypeId = type.LeaveTypeId,
            FromDate = req.FromDate,
            ToDate = req.ToDate,
            IsHalfDay = req.IsHalfDay,
            TotalDays = days,
            Reason = req.Reason.Trim(),
            Status = LeaveStatus.Pending,
        };
        db.LeaveRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        var dto = await GetByIdAsync(entity.LeaveRequestId, ct);
        await notify.LeaveAppliedAsync(dto, ct);
        return dto;
    }

    public async Task<LeaveRequestDto> CancelAsync(int employeeId, int leaveRequestId, CancellationToken ct)
    {
        var wasApproved = false;
        await db.InTransactionAsync(async token =>
        {
            var request = await db.LeaveRequests.FirstOrDefaultAsync(r => r.LeaveRequestId == leaveRequestId, token)
                          ?? throw new NotFoundException("Leave request not found.");
            if (request.EmployeeId != employeeId) throw new ForbiddenException();
            wasApproved = request.Status == LeaveStatus.Approved;

            if (request.Status == LeaveStatus.Approved)
            {
                if (request.FromDate <= Today)
                    throw new AppException("Approved leave that has already started cannot be cancelled. Please contact HR.");
                await AdjustUsedAsync(request, -request.TotalDays, token);
            }
            else if (request.Status != LeaveStatus.Pending)
            {
                throw new AppException($"A {request.Status.ToLower()} request cannot be cancelled.");
            }

            request.Status = LeaveStatus.Cancelled;
            return await db.SaveChangesAsync(token);
        }, ct);
        var dto = await GetByIdAsync(leaveRequestId, ct);
        if (wasApproved) await notify.LeaveCancelledAsync(dto, ct);   // the manager planned around it
        return dto;
    }

    // ------------------------------------------------------------------ Approve / reject (admin or reporting manager)
    public async Task<LeaveRequestDto> ApproveAsync(Approver approver, int id, string? remarks, CancellationToken ct)
    {
        await db.InTransactionAsync(async token =>
        {
            var request = await LoadPendingForActionAsync(id, approver, token);
            var type = await db.LeaveTypes.AsNoTracking().FirstAsync(t => t.LeaveTypeId == request.LeaveTypeId, token);

            if (type.IsBalanceTracked)
            {
                var balance = await db.LeaveBalances.FirstOrDefaultAsync(b =>
                    b.EmployeeId == request.EmployeeId && b.LeaveTypeId == request.LeaveTypeId && b.Year == request.FromDate.Year, token);
                if (balance is null || balance.CurrentAvailable < request.TotalDays)
                    throw new AppException($"Employee does not have enough {type.Code} balance to approve this request.");
            }

            await AdjustUsedAsync(request, request.TotalDays, token);
            SetAction(request, LeaveStatus.Approved, approver.UserId, remarks);
            return await db.SaveChangesAsync(token);
        }, ct);
        var dto = await GetByIdAsync(id, ct);
        await notify.LeaveDecidedAsync(dto, approver.UserId, ct);
        return dto;
    }

    public async Task<LeaveRequestDto> RejectAsync(Approver approver, int id, string? remarks, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remarks)) throw new AppException("Please provide a reason for rejection.");
        var request = await LoadPendingForActionAsync(id, approver, ct);
        SetAction(request, LeaveStatus.Rejected, approver.UserId, remarks);
        await db.SaveChangesAsync(ct);
        var dto = await GetByIdAsync(id, ct);
        await notify.LeaveDecidedAsync(dto, approver.UserId, ct);
        return dto;
    }

    // ------------------------------------------------------------------ Team (reporting manager)
    /// <summary>Leave requests of everyone reporting to <paramref name="managerId"/>, directly or indirectly.</summary>
    public async Task<PagedResult<LeaveRequestDto>> SearchTeamAsync(int managerId, LeaveQuery q, CancellationToken ct)
    {
        var team = await org.GetSubordinateIdsAsync(managerId, ct);
        if (q.EmployeeId is int emp && !team.Contains(emp))
            return new PagedResult<LeaveRequestDto>([], 0, 1, q.PageSize);
        return await SearchAsync(q, ct, team);
    }

    public async Task<List<LeaveBalanceDto>> GetTeamMemberBalancesAsync(int managerId, int employeeId, int? year, CancellationToken ct)
    {
        if (!await org.IsSubordinateAsync(managerId, employeeId, ct))
            throw new ForbiddenException("This employee does not report to you.");
        return await GetBalancesAsync(employeeId, year, ct);
    }

    /// <summary>Whether the caller may see this request: a leave approver, the applicant, or someone they report to.</summary>
    public async Task<bool> CanViewAsync(LeaveRequestDto request, Approver viewer, CancellationToken ct) =>
        viewer.CanApproveAll || request.EmployeeId == viewer.EmployeeId ||
        (viewer.EmployeeId is int me && await org.IsSubordinateAsync(me, request.EmployeeId, ct));

    // ------------------------------------------------------------------ Queries
    public async Task<PagedResult<LeaveRequestDto>> SearchAsync(LeaveQuery q, CancellationToken ct,
        IReadOnlyCollection<int>? restrictToEmployees = null)
    {
        var query = db.LeaveRequests.AsNoTracking().AsQueryable();
        if (restrictToEmployees is not null) query = query.Where(r => restrictToEmployees.Contains(r.EmployeeId));
        if (q.EmployeeId is int emp) query = query.Where(r => r.EmployeeId == emp);
        if (q.Year is int y) query = query.Where(r => r.FromDate.Year == y);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(r => r.Status == q.Status);

        var pageSize = Math.Clamp(q.PageSize, 1, 100);
        var page = Math.Max(q.Page, 1);
        var total = await query.CountAsync(ct);
        var items = await Project(query
                .OrderByDescending(r => r.Status == LeaveStatus.Pending)
                .ThenByDescending(r => r.FromDate)
                .ThenByDescending(r => r.LeaveRequestId)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);

        return new PagedResult<LeaveRequestDto>(items, total, page, pageSize);
    }

    public async Task<LeaveRequestDto> GetByIdAsync(int id, CancellationToken ct) =>
        await Project(db.LeaveRequests.AsNoTracking().Where(r => r.LeaveRequestId == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Leave request not found.");

    public IQueryable<LeaveRequestDto> Project(IQueryable<LeaveRequest> source)
    {
        var today = Today;
        return source.Select(r => new LeaveRequestDto(
            r.LeaveRequestId, r.EmployeeId, r.Employee.EmployeeCode, r.Employee.FirstName + " " + r.Employee.LastName,
            r.LeaveType.Code, r.LeaveType.Name, r.FromDate, r.ToDate, r.IsHalfDay, r.TotalDays, r.Reason, r.Status,
            r.AppliedOn, r.ActionedBy != null ? r.ActionedBy.Username : null, r.ActionedOn, r.AdminRemarks,
            r.Status == LeaveStatus.Pending || (r.Status == LeaveStatus.Approved && r.FromDate > today)));
    }

    // ------------------------------------------------------------------ Helpers
    private async Task<LeaveRequest> LoadPendingForActionAsync(int id, Approver approver, CancellationToken ct)
    {
        var request = await db.LeaveRequests.FirstOrDefaultAsync(r => r.LeaveRequestId == id, ct)
                      ?? throw new NotFoundException("Leave request not found.");
        if (approver.EmployeeId == request.EmployeeId)
            throw new ForbiddenException("You cannot approve or reject your own leave request.");
        if (!approver.CanApproveAll &&
            (approver.EmployeeId is not int me || !await org.IsSubordinateAsync(me, request.EmployeeId, ct)))
            throw new ForbiddenException("You can only act on leave requests of employees who report to you.");
        if (request.Status != LeaveStatus.Pending)
            throw new ConflictException($"This request is already {request.Status.ToLower()}.");
        return request;
    }

    private void SetAction(LeaveRequest r, string status, int approverUserId, string? remarks)
    {
        r.Status = status;
        r.ActionedByUserId = approverUserId;
        r.ActionedOn = time.GetUtcNow().UtcDateTime;
        r.AdminRemarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    private async Task AdjustUsedAsync(LeaveRequest request, decimal delta, CancellationToken ct)
    {
        var balance = await db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId && b.LeaveTypeId == request.LeaveTypeId && b.Year == request.FromDate.Year, ct);
        if (balance is not null) balance.Used = Math.Max(0, balance.Used + delta);
    }
}
