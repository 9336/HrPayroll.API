using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Expenses.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Expenses.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Expenses.Services;

/// <summary>Who is acting: the claimant / reporting manager (employee id) and whether they hold travel.approve.</summary>
/// <summary>CanViewAll: travel.view; CanApproveFinal: travel.approve; CanPay: travel.pay.</summary>
public record TravelActor(int UserId, int? EmployeeId, bool CanViewAll, bool CanApproveFinal, bool CanPay);

/// <summary>
/// Travel expense claims. The employee submits a trip with its expense lines (and receipts); their reporting
/// manager (or anyone above them) approves; then an admin (travel.approve) gives the final approval and marks
/// it paid. Every step is a timeline event and the people involved are emailed.
/// </summary>
public class TravelClaimService(ExpensesDbContext db, OrgService org, EmailQueue mail, TimeProvider time)
{
    public const int MaxReceiptBytes = 5 * 1024 * 1024;
    private const string MailCategory = "Travel";

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Employee
    public async Task<TravelClaimDetailDto> CreateAsync(TravelActor actor, CreateTravelClaimRequest req, CancellationToken ct)
    {
        var employeeId = actor.EmployeeId ?? throw new ForbiddenException("Your login is not linked to an employee profile.");
        if (req.TripEnd < req.TripStart) throw new AppException("Trip end date must be on or after the start date.");
        if (req.TripStart > Today) throw new AppException("You can claim expenses only for a trip that has started.");
        if (req.TripEnd.DayNumber - req.TripStart.DayNumber > 90) throw new AppException("A trip can be at most 90 days long.");
        if (req.Items.Count == 0) throw new AppException("Add at least one expense.");
        foreach (var (item, i) in req.Items.Select((x, i) => (x, i + 1)))
        {
            if (!TravelExpenseCategories.All.Contains(item.Category)) throw new AppException($"Line {i}: choose a category.");
            if (string.IsNullOrWhiteSpace(item.Description)) throw new AppException($"Line {i}: add a description.");
            if (item.ExpenseDate > Today) throw new AppException($"Line {i}: the expense date cannot be in the future.");
            // Travel to / from the place may be booked a little before or after the trip.
            if (item.ExpenseDate < req.TripStart.AddDays(-30) || item.ExpenseDate > req.TripEnd.AddDays(7))
                throw new AppException($"Line {i}: the expense date must be around the trip dates.");
        }

        var me = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == employeeId)
                     .Select(e => new { e.FirstName, e.LastName, e.IsActive, e.ManagerId }).FirstOrDefaultAsync(ct)
                 ?? throw new NotFoundException("Employee not found.");
        if (!me.IsActive) throw new AppException("Inactive employees cannot submit claims.");
        var manager = me.ManagerId is int mid
            ? await db.Employees.AsNoTracking().Where(e => e.EmployeeId == mid && e.IsActive)
                .Select(e => new { e.EmployeeId, e.FirstName, e.LastName, e.Email }).FirstOrDefaultAsync(ct)
            : null;

        var now = UtcNow;
        var claim = new TravelClaim
        {
            EmployeeId = employeeId, Purpose = req.Purpose.Trim(), FromPlace = req.FromPlace.Trim(), ToPlace = req.ToPlace.Trim(),
            TripStart = req.TripStart, TripEnd = req.TripEnd, CreatedAt = now, ManagerEmployeeId = manager?.EmployeeId,
            Status = manager is null ? TravelClaimStatus.PendingAdmin : TravelClaimStatus.PendingManager,
            Items = req.Items.Select(i => new TravelClaimItem
            {
                ExpenseDate = i.ExpenseDate, Category = i.Category, Description = i.Description.Trim(), Amount = Math.Round(i.Amount, 2),
            }).ToList(),
        };
        claim.TotalAmount = claim.Items.Sum(i => i.Amount);
        db.TravelClaims.Add(claim);
        await db.SaveChangesAsync(ct);

        var who = $"{me.FirstName} {me.LastName}";
        AddEvent(claim.ClaimId, actor, "Submitted",
            manager is null ? "No reporting manager - sent straight to admin for approval." : $"Sent to {manager.FirstName} {manager.LastName} for approval.", now);
        var code = TravelClaim.Code(claim.ClaimId);
        if (manager is not null)
            mail.Enqueue(manager.Email, $"{manager.FirstName} {manager.LastName}", $"Approval needed: {code} travel claim ({Money(claim.TotalAmount)})",
                Layout(await CompanyNameAsync(ct), "A travel claim needs your approval",
                    P($"Dear {manager.FirstName},") +
                    P($"{who} has submitted travel claim {code} for {Money(claim.TotalAmount)}: {claim.Purpose} " +
                      $"({claim.FromPlace} → {claim.ToPlace}, {claim.TripStart:dd MMM} - {claim.TripEnd:dd MMM yyyy}).") +
                    P("After your approval it goes to admin for final approval and payment."),
                    "Review claim", $"{mail.AppUrl}/travel/approvals"), MailCategory);
        else
            await NotifyAdminsAsync(claim, who, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(claim.ClaimId, actor, ct);
    }

    public async Task<List<TravelClaimDto>> GetMineAsync(TravelActor actor, CancellationToken ct)
    {
        var employeeId = actor.EmployeeId ?? throw new ForbiddenException("Your login is not linked to an employee profile.");
        return await Project(db.TravelClaims.AsNoTracking().Where(c => c.EmployeeId == employeeId).OrderByDescending(c => c.CreatedAt)).ToListAsync(ct);
    }

    public async Task<TravelClaimDetailDto> CancelAsync(int id, TravelActor actor, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct);
        if (c.EmployeeId != actor.EmployeeId) throw new ForbiddenException("You can only cancel your own claims.");
        if (!CanEditReceipts(c))   // nobody has reviewed it yet
            throw new ConflictException("This claim has already been reviewed and can no longer be cancelled.");
        c.Status = TravelClaimStatus.Cancelled;
        Touch(c, actor, "Cancelled", null);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    /// <summary>Attach / replace the receipt of a line (claimant, before anyone has reviewed the claim).</summary>
    public async Task<TravelClaimDetailDto> SetReceiptAsync(int itemId, IFormFile file, TravelActor actor, CancellationToken ct)
    {
        var item = await db.TravelClaimItems.Include(i => i.Receipt).FirstOrDefaultAsync(i => i.ItemId == itemId, ct)
                   ?? throw new NotFoundException("Expense line not found.");
        var claim = await LoadAsync(item.ClaimId, ct);
        if (claim.EmployeeId != actor.EmployeeId) throw new ForbiddenException("You can only add receipts to your own claims.");
        if (!CanEditReceipts(claim)) throw new ConflictException("Receipts can only be added before the claim is reviewed.");
        var bytes = await FileSignatures.ReadAsync(file, MaxReceiptBytes, ct);
        var type = FileSignatures.DetectImageOrPdf(bytes) ?? throw new AppException("Receipt must be a PDF, PNG, JPEG or WebP file.");
        var ext = type switch { "application/pdf" => ".pdf", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        if (item.Receipt is null) item.Receipt = new TravelReceipt { ItemId = itemId, ContentType = type, Data = bytes };
        else { item.Receipt.ContentType = type; item.Receipt.Data = bytes; }
        item.ReceiptFileName = FileSignatures.SafeFileName(file.FileName, ext, "receipt");
        claim.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(claim.ClaimId, actor, ct);
    }

    public async Task<(byte[] Data, string ContentType, string FileName)> GetReceiptAsync(int itemId, TravelActor actor, CancellationToken ct)
    {
        var item = await db.TravelClaimItems.AsNoTracking().Where(i => i.ItemId == itemId)
                       .Select(i => new { i.ClaimId, i.ReceiptFileName }).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Receipt not found.");
        await GetDetailAsync(item.ClaimId, actor, ct);   // access check
        var r = await db.TravelReceipts.AsNoTracking().FirstOrDefaultAsync(x => x.ItemId == itemId, ct)
                ?? throw new NotFoundException("This expense has no receipt.");
        return (r.Data, r.ContentType, item.ReceiptFileName ?? "receipt");
    }

    // ------------------------------------------------------------------ Reporting manager (stage 1)
    public async Task<List<TravelClaimDto>> GetManagerQueueAsync(TravelActor actor, string? status, CancellationToken ct)
    {
        if (actor.EmployeeId is not int me) return [];
        var team = await org.GetSubordinateIdsAsync(me, ct);
        var q = db.TravelClaims.AsNoTracking().Where(c => team.Contains(c.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        return await Project(q.OrderByDescending(c => c.Status == TravelClaimStatus.PendingManager).ThenByDescending(c => c.CreatedAt).Take(300)).ToListAsync(ct);
    }

    public async Task<TravelClaimDetailDto> ManagerApproveAsync(int id, TravelActor actor, string? remarks, CancellationToken ct)
    {
        var c = await LoadForManagerAsync(id, actor, ct);
        c.Status = TravelClaimStatus.PendingAdmin;
        c.ManagerActionByUserId = actor.UserId;
        c.ManagerActionAt = UtcNow;
        c.ManagerRemarks = Clean(remarks);
        Touch(c, actor, "ManagerApproved", c.ManagerRemarks);
        var requester = await RequesterAsync(c.EmployeeId, ct);
        await MailRequesterAsync(c, "Your travel claim was approved by your manager",
            $"Your travel claim {TravelClaim.Code(c.ClaimId)} ({Money(c.TotalAmount)}) was approved by {await UserNameAsync(actor.UserId, ct)} " +
            "and is now with admin for final approval.", c.ManagerRemarks, ct);
        await NotifyAdminsAsync(c, requester.Name, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<TravelClaimDetailDto> ManagerRejectAsync(int id, TravelActor actor, string? remarks, CancellationToken ct)
    {
        var reason = Clean(remarks) ?? throw new AppException("Please give a reason for rejecting the claim.");
        var c = await LoadForManagerAsync(id, actor, ct);
        c.Status = TravelClaimStatus.Rejected;
        c.ManagerActionByUserId = actor.UserId;
        c.ManagerActionAt = UtcNow;
        c.ManagerRemarks = reason;
        Touch(c, actor, "ManagerRejected", reason);
        await MailRequesterAsync(c, "Your travel claim was rejected",
            $"Your travel claim {TravelClaim.Code(c.ClaimId)} ({Money(c.TotalAmount)}) was rejected by your reporting manager.", reason, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Admin (stage 2)
    public async Task<PagedResult<TravelClaimDto>> GetAdminQueueAsync(TravelQueueQuery query, CancellationToken ct)
    {
        var q = db.TravelClaims.AsNoTracking();
        if (query.Status == "Open") q = q.Where(c => c.Status == TravelClaimStatus.PendingAdmin || c.Status == TravelClaimStatus.Approved);
        else if (!string.IsNullOrWhiteSpace(query.Status) && query.Status != "All") q = q.Where(c => c.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            var digits = s.StartsWith("TRV-", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
            var id = int.TryParse(digits, out var n) ? n : (int?)null;
            q = q.Where(c => c.ClaimId == id || c.Purpose.Contains(s) || c.ToPlace.Contains(s) || c.FromPlace.Contains(s) ||
                             (c.Employee.FirstName + " " + c.Employee.LastName).Contains(s) || c.Employee.EmployeeCode.Contains(s));
        }
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(query.Page, 1);
        var total = await q.CountAsync(ct);
        var items = await Project(q
                .OrderBy(c => c.Status == TravelClaimStatus.PendingAdmin ? 0 : c.Status == TravelClaimStatus.Approved ? 1 : 2)
                .ThenBy(c => c.Status == TravelClaimStatus.PendingAdmin || c.Status == TravelClaimStatus.Approved ? c.CreatedAt : DateTime.MaxValue)
                .ThenByDescending(c => c.ClaimId)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);
        return new PagedResult<TravelClaimDto>(items, total, page, pageSize);
    }

    public async Task<TravelAdminStatsDto> GetAdminStatsAsync(CancellationToken ct)
    {
        var monthStart = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var s = await db.TravelClaims.GroupBy(_ => 1).Select(g => new
        {
            Awaiting = g.Count(c => c.Status == TravelClaimStatus.PendingAdmin),
            AwaitingAmount = g.Where(c => c.Status == TravelClaimStatus.PendingAdmin).Sum(c => (decimal?)c.TotalAmount) ?? 0,
            ToPay = g.Count(c => c.Status == TravelClaimStatus.Approved),
            ToPayAmount = g.Where(c => c.Status == TravelClaimStatus.Approved).Sum(c => (decimal?)c.TotalAmount) ?? 0,
            WithManagers = g.Count(c => c.Status == TravelClaimStatus.PendingManager),
            Paid = g.Where(c => c.Status == TravelClaimStatus.Paid && c.PaidAt >= monthStart).Sum(c => (decimal?)c.TotalAmount) ?? 0,
        }).FirstOrDefaultAsync(ct);
        return s is null ? new(0, 0, 0, 0, 0, 0) : new(s.Awaiting, s.AwaitingAmount, s.ToPay, s.ToPayAmount, s.WithManagers, s.Paid);
    }

    public async Task<TravelClaimDetailDto> AdminApproveAsync(int id, TravelActor actor, string? remarks, CancellationToken ct)
    {
        var c = await LoadForAdminAsync(id, actor, TravelClaimStatus.PendingAdmin, ct);
        c.Status = TravelClaimStatus.Approved;
        c.AdminActionByUserId = actor.UserId;
        c.AdminActionAt = UtcNow;
        c.AdminRemarks = Clean(remarks);
        Touch(c, actor, "Approved", c.AdminRemarks);
        await MailRequesterAsync(c, "Your travel claim is approved",
            $"Your travel claim {TravelClaim.Code(c.ClaimId)} for {Money(c.TotalAmount)} has been approved and will be paid.", c.AdminRemarks, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<TravelClaimDetailDto> AdminRejectAsync(int id, TravelActor actor, string? remarks, CancellationToken ct)
    {
        var reason = Clean(remarks) ?? throw new AppException("Please give a reason for rejecting the claim.");
        var c = await LoadForAdminAsync(id, actor, TravelClaimStatus.PendingAdmin, ct);
        c.Status = TravelClaimStatus.Rejected;
        c.AdminActionByUserId = actor.UserId;
        c.AdminActionAt = UtcNow;
        c.AdminRemarks = reason;
        Touch(c, actor, "Rejected", reason);
        await MailRequesterAsync(c, "Your travel claim was rejected",
            $"Your travel claim {TravelClaim.Code(c.ClaimId)} ({Money(c.TotalAmount)}) was rejected at the final approval.", reason, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<TravelClaimDetailDto> MarkPaidAsync(int id, TravelActor actor, string? reference, CancellationToken ct)
    {
        var c = await LoadForAdminAsync(id, actor, TravelClaimStatus.Approved, ct);
        c.Status = TravelClaimStatus.Paid;
        c.PaidByUserId = actor.UserId;
        c.PaidAt = UtcNow;
        c.PaymentReference = Clean(reference);
        Touch(c, actor, "Paid", c.PaymentReference is null ? null : $"Reference: {c.PaymentReference}");
        await MailRequesterAsync(c, "Your travel claim has been paid",
            $"{Money(c.TotalAmount)} for travel claim {TravelClaim.Code(c.ClaimId)} has been paid." +
            (c.PaymentReference is null ? "" : $" Payment reference: {c.PaymentReference}."), null, ct);
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    // ------------------------------------------------------------------ Shared
    public async Task<TravelClaimDetailDto> GetDetailAsync(int id, TravelActor actor, CancellationToken ct)
    {
        var dto = await Project(db.TravelClaims.AsNoTracking().Where(c => c.ClaimId == id)).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Travel claim not found.");
        var isOwner = dto.EmployeeId == actor.EmployeeId;
        var isManager = !isOwner && actor.EmployeeId is int me && await org.IsSubordinateAsync(me, dto.EmployeeId, ct);
        if (!isOwner && !isManager && !actor.CanViewAll) throw new ForbiddenException("You cannot view this claim.");

        var items = await db.TravelClaimItems.AsNoTracking().Where(i => i.ClaimId == id).OrderBy(i => i.ExpenseDate).ThenBy(i => i.ItemId)
            .Select(i => new TravelClaimItemDto(i.ItemId, i.ExpenseDate, i.Category, i.Description, i.Amount, i.Receipt != null, i.ReceiptFileName))
            .ToListAsync(ct);
        var events = await db.TravelClaimEvents.AsNoTracking().Where(e => e.ClaimId == id).OrderBy(e => e.EventId)
            .Select(e => new TravelClaimEventDto(e.EventId, e.Action, e.Note,
                e.By.Employee != null ? e.By.Employee.FirstName + " " + e.By.Employee.LastName : e.By.Username, e.At))
            .ToListAsync(ct);
        var reviewed = dto.ManagerActionAt is not null || dto.AdminActionAt is not null;
        var notReviewed = dto.Status is TravelClaimStatus.PendingManager or TravelClaimStatus.PendingAdmin && !reviewed;
        return new TravelClaimDetailDto(dto, items, events,
            CanCancel: isOwner && notReviewed,
            CanAddReceipts: isOwner && notReviewed,
            CanManagerAct: isManager && dto.Status == TravelClaimStatus.PendingManager,
            CanAdminAct: actor.CanApproveFinal && !isOwner && dto.Status == TravelClaimStatus.PendingAdmin,
            CanMarkPaid: actor.CanPay && !isOwner && dto.Status == TravelClaimStatus.Approved,
            CanComment: TravelClaimStatus.Open.Contains(dto.Status));
    }

    public async Task<TravelClaimDetailDto> CommentAsync(int id, TravelActor actor, string text, CancellationToken ct)
    {
        var detail = await GetDetailAsync(id, actor, ct);
        if (!detail.CanComment) throw new ConflictException("Comments are closed on this claim.");
        var c = await LoadAsync(id, ct);
        Touch(c, actor, "Comment", text.Trim());
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, actor, ct);
    }

    public async Task<TravelCountsDto> GetCountsAsync(TravelActor actor, CancellationToken ct)
    {
        var mine = 0;
        var managerQueue = 0;
        if (actor.EmployeeId is int me)
        {
            mine = await db.TravelClaims.CountAsync(c => c.EmployeeId == me && TravelClaimStatus.Open.Contains(c.Status), ct);
            var team = await org.GetSubordinateIdsAsync(me, ct);
            if (team.Count > 0)
                managerQueue = await db.TravelClaims.CountAsync(c => c.Status == TravelClaimStatus.PendingManager && team.Contains(c.EmployeeId), ct);
        }
        var adminQueue = actor.CanApproveFinal
            ? await db.TravelClaims.CountAsync(c => c.Status == TravelClaimStatus.PendingAdmin && c.EmployeeId != actor.EmployeeId, ct) : 0;
        return new TravelCountsDto(mine, managerQueue, adminQueue);
    }

    // ------------------------------------------------------------------ Helpers
    private static bool CanEditReceipts(TravelClaim c) =>
        c.ManagerActionAt is null && c.AdminActionAt is null &&
        c.Status is TravelClaimStatus.PendingManager or TravelClaimStatus.PendingAdmin;

    private async Task<TravelClaim> LoadAsync(int id, CancellationToken ct) =>
        await db.TravelClaims.FirstOrDefaultAsync(c => c.ClaimId == id, ct) ?? throw new NotFoundException("Travel claim not found.");

    private async Task<TravelClaim> LoadForManagerAsync(int id, TravelActor actor, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct);
        if (c.EmployeeId == actor.EmployeeId) throw new ForbiddenException("You cannot approve your own claim.");
        if (actor.EmployeeId is not int me || !await org.IsSubordinateAsync(me, c.EmployeeId, ct))
            throw new ForbiddenException("Only the employee's reporting manager can approve this claim.");
        if (c.Status != TravelClaimStatus.PendingManager)
            throw new ConflictException(c.Status == TravelClaimStatus.PendingAdmin ? "This claim is already with admin." : $"This claim is already {Label(c.Status)}.");
        return c;
    }

    private async Task<TravelClaim> LoadForAdminAsync(int id, TravelActor actor, string requiredStatus, CancellationToken ct)
    {
        if (!(requiredStatus == TravelClaimStatus.Approved ? actor.CanPay : actor.CanApproveFinal))
            throw new ForbiddenException("You do not have permission to do this.");
        var c = await LoadAsync(id, ct);
        if (c.EmployeeId == actor.EmployeeId) throw new ForbiddenException("You cannot approve or pay your own claim.");
        if (c.Status == TravelClaimStatus.PendingManager) throw new ConflictException("This claim is still waiting for the reporting manager.");
        if (c.Status != requiredStatus) throw new ConflictException($"This claim is already {Label(c.Status)}.");
        return c;
    }

    private void Touch(TravelClaim c, TravelActor actor, string action, string? note)
    {
        var now = UtcNow;
        c.UpdatedAt = now;
        AddEvent(c.ClaimId, actor, action, note, now);
    }

    private void AddEvent(int claimId, TravelActor actor, string action, string? note, DateTime at) =>
        db.TravelClaimEvents.Add(new TravelClaimEvent { ClaimId = claimId, ByUserId = actor.UserId, Action = action, Note = note, At = at });

    /// <summary>Final approvers: roles holding travel.approve; admins when no such role is assigned.</summary>
    private async Task NotifyAdminsAsync(TravelClaim c, string requesterName, CancellationToken ct)
    {
        var approvers = await db.Users.Where(u => u.IsActive && u.Employee != null && u.EmployeeId != c.EmployeeId &&
                                                  u.Role.Permissions.Any(p => p.Permission == Permissions.TravelApprove))
            .Select(u => new { u.Employee!.Email, u.Employee.FirstName, u.Employee.LastName }).ToListAsync(ct);
        if (approvers.Count == 0)
            approvers = await db.Users.Where(u => u.IsActive && u.Employee != null && u.EmployeeId != c.EmployeeId && u.RoleId == Roles.AdminId)
                .Select(u => new { u.Employee!.Email, u.Employee.FirstName, u.Employee.LastName }).ToListAsync(ct);
        var company = await CompanyNameAsync(ct);
        var code = TravelClaim.Code(c.ClaimId);
        foreach (var a in approvers)
            mail.Enqueue(a.Email, $"{a.FirstName} {a.LastName}", $"Final approval needed: {code} travel claim ({Money(c.TotalAmount)})",
                Layout(company, "A travel claim needs final approval",
                    P($"Dear {a.FirstName},") +
                    P($"{requesterName}'s travel claim {code} for {Money(c.TotalAmount)} ({c.Purpose}, {c.FromPlace} → {c.ToPlace}) " +
                      (c.ManagerActionAt is null ? "has no reporting manager and needs your approval." : "was approved by the reporting manager and needs your final approval.")),
                    "Review claims", $"{mail.AppUrl}/travel/admin"), MailCategory);
    }

    private async Task MailRequesterAsync(TravelClaim c, string heading, string message, string? note, CancellationToken ct)
    {
        var r = await RequesterAsync(c.EmployeeId, ct);
        mail.Enqueue(r.Email, r.Name, $"{heading}: {TravelClaim.Code(c.ClaimId)}",
            Layout(await CompanyNameAsync(ct), heading, P($"Dear {r.FirstName},") + P(message) + (note is null ? "" : P($"Remarks: {note}")),
                "View my claims", $"{mail.AppUrl}/me/travel"), MailCategory);
    }

    private async Task<(string Email, string FirstName, string Name)> RequesterAsync(int employeeId, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Where(x => x.EmployeeId == employeeId).Select(x => new { x.Email, x.FirstName, x.LastName }).FirstAsync(ct);
        return (e.Email, e.FirstName, $"{e.FirstName} {e.LastName}");
    }

    private Task<string> UserNameAsync(int userId, CancellationToken ct) =>
        db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username).FirstAsync(ct);

    private async Task<string> CompanyNameAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private static string Money(decimal amount) => "Rs. " + amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Label(string status) => status switch
    {
        TravelClaimStatus.PendingManager => "waiting for the manager",
        TravelClaimStatus.PendingAdmin => "waiting for admin",
        _ => status.ToLowerInvariant(),
    };

    private static IQueryable<TravelClaimDto> Project(IQueryable<TravelClaim> q) => q.Select(c => new TravelClaimDto(
        c.ClaimId, TravelClaim.Code(c.ClaimId),   // evaluated in memory (top-level projection)
        c.EmployeeId, c.Employee.FirstName + " " + c.Employee.LastName, c.Employee.EmployeeCode, c.Employee.Department.Name,
        c.Purpose, c.FromPlace, c.ToPlace, c.TripStart, c.TripEnd, c.TotalAmount, c.Items.Count, c.Status,
        c.Manager != null ? c.Manager.FirstName + " " + c.Manager.LastName : null,
        c.ManagerActionBy != null ? (c.ManagerActionBy.Employee != null ? c.ManagerActionBy.Employee.FirstName + " " + c.ManagerActionBy.Employee.LastName : c.ManagerActionBy.Username) : null,
        c.ManagerActionAt, c.ManagerRemarks,
        c.AdminActionBy != null ? (c.AdminActionBy.Employee != null ? c.AdminActionBy.Employee.FirstName + " " + c.AdminActionBy.Employee.LastName : c.AdminActionBy.Username) : null,
        c.AdminActionAt, c.AdminRemarks, c.PaidAt, c.PaymentReference, c.CreatedAt));
}
