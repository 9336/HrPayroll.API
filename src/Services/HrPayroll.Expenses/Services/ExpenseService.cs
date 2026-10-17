using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Expenses.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Expenses.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Expenses.Services;

/// <summary>Who is acting on expenses: recorders change their own pending entries; expenses.edit / .delete anyone's.</summary>
public record ExpenseActor(int UserId, bool CanEditAll, bool CanDeleteAll);

/// <summary>
/// Daily office expenses (petty cash register). Recorders add expenses with an optional receipt;
/// approvers approve / reject them, manage categories and see reports. Only pending entries can change.
/// </summary>
public class ExpenseService(ExpensesDbContext db, TimeProvider time)
{
    public const int MaxReceiptBytes = 2 * 1024 * 1024;

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ------------------------------------------------------------------ Categories
    public Task<List<ExpenseCategoryDto>> GetCategoriesAsync(bool activeOnly, CancellationToken ct) =>
        db.ExpenseCategories.AsNoTracking()
            .Where(c => !activeOnly || c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new ExpenseCategoryDto(c.ExpenseCategoryId, c.Name, c.IsActive,
                db.OfficeExpenses.Count(e => e.ExpenseCategoryId == c.ExpenseCategoryId)))
            .ToListAsync(ct);

    public async Task<ExpenseCategoryDto> SaveCategoryAsync(int? id, ExpenseCategoryRequest req, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.ExpenseCategories.AnyAsync(c => c.Name == name && c.ExpenseCategoryId != id, ct))
            throw new ConflictException($"A category named '{name}' already exists.");

        var category = id is int existing
            ? await db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == existing, ct)
              ?? throw new NotFoundException("Category not found.")
            : db.ExpenseCategories.Add(new ExpenseCategory()).Entity;
        category.Name = name;
        category.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return new ExpenseCategoryDto(category.ExpenseCategoryId, category.Name, category.IsActive,
            await db.OfficeExpenses.CountAsync(e => e.ExpenseCategoryId == category.ExpenseCategoryId, ct));
    }

    // ------------------------------------------------------------------ Register
    public async Task<ExpenseSearchResult> SearchAsync(ExpenseQuery q, int callerUserId, CancellationToken ct)
    {
        var query = Filter(q, callerUserId);
        var pageSize = Math.Clamp(q.PageSize, 1, 500);
        var page = Math.Max(q.Page, 1);

        var totals = await query.GroupBy(_ => 1).Select(g => new
        {
            Count = g.Count(),
            Total = g.Where(e => e.Status != ExpenseStatus.Rejected).Sum(e => (decimal?)e.Amount) ?? 0,
            Approved = g.Where(e => e.Status == ExpenseStatus.Approved).Sum(e => (decimal?)e.Amount) ?? 0,
            Pending = g.Where(e => e.Status == ExpenseStatus.Pending).Sum(e => (decimal?)e.Amount) ?? 0,
        }).FirstOrDefaultAsync(ct);

        var items = await Project(query
                .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseId)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);

        return new ExpenseSearchResult(items, totals?.Count ?? 0, page, pageSize,
            totals?.Total ?? 0, totals?.Approved ?? 0, totals?.Pending ?? 0);
    }

    public async Task<ExpenseSummaryDto> GetSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var start = from ?? monthStart;
        var end = to ?? today;
        if (end < start) throw new AppException("'To' date must be on or after 'From' date.");
        if (end.DayNumber - start.DayNumber > 366) throw new AppException("Please choose a period of at most one year.");

        var counted = db.OfficeExpenses.AsNoTracking().Where(e => e.Status != ExpenseStatus.Rejected);
        var period = counted.Where(e => e.ExpenseDate >= start && e.ExpenseDate <= end);

        // Grouped in SQL; the (small) result is sorted in memory.
        var byCategory = (await period.GroupBy(e => e.Category.Name)
                .Select(g => new { g.Key, Amount = g.Sum(e => e.Amount), Count = g.Count() })
                .ToListAsync(ct))
            .OrderByDescending(c => c.Amount)
            .Select(c => new CategoryTotalDto(c.Key, c.Amount, c.Count))
            .ToList();
        var byDay = (await period.GroupBy(e => e.ExpenseDate)
                .Select(g => new { g.Key, Amount = g.Sum(e => e.Amount), Count = g.Count() })
                .ToListAsync(ct))
            .OrderBy(d => d.Key)
            .Select(d => new DayTotalDto(d.Key, d.Amount, d.Count))
            .ToList();
        var pending = await db.OfficeExpenses.Where(e => e.Status == ExpenseStatus.Pending)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Amount = g.Sum(e => e.Amount) }).FirstOrDefaultAsync(ct);

        return new ExpenseSummaryDto(start, end,
            await counted.Where(e => e.ExpenseDate == today).SumAsync(e => (decimal?)e.Amount, ct) ?? 0,
            await counted.Where(e => e.ExpenseDate >= monthStart && e.ExpenseDate <= today).SumAsync(e => (decimal?)e.Amount, ct) ?? 0,
            byCategory.Sum(c => c.Amount),
            await period.Where(e => e.Status == ExpenseStatus.Approved).SumAsync(e => (decimal?)e.Amount, ct) ?? 0,
            pending?.Count ?? 0, pending?.Amount ?? 0, byCategory, byDay);
    }

    public async Task<ExpenseDto> GetAsync(int id, CancellationToken ct) =>
        await Project(db.OfficeExpenses.AsNoTracking().Where(e => e.ExpenseId == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Expense not found.");

    // ------------------------------------------------------------------ Create / edit / delete
    public async Task<ExpenseDto> CreateAsync(ExpenseRequest req, int userId, CancellationToken ct)
    {
        await ValidateAsync(req, ct);
        var expense = new OfficeExpense { CreatedByUserId = userId, Status = ExpenseStatus.Pending };
        Apply(expense, req);
        db.OfficeExpenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return await GetAsync(expense.ExpenseId, ct);
    }

    public async Task<ExpenseDto> UpdateAsync(int id, ExpenseRequest req, ExpenseActor actor, CancellationToken ct)
    {
        var expense = await LoadEditableAsync(id, actor, ct);
        await ValidateAsync(req, ct, expense.ExpenseCategoryId);
        Apply(expense, req);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, ExpenseActor actor, CancellationToken ct)
    {
        var expense = await LoadEditableAsync(id, actor, ct, delete: true);
        db.OfficeExpenses.Remove(expense);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Review
    public Task<ExpenseDto> ApproveAsync(int id, int approverUserId, string? remarks, CancellationToken ct) =>
        ReviewAsync(id, approverUserId, ExpenseStatus.Approved, remarks, ct);

    public Task<ExpenseDto> RejectAsync(int id, int approverUserId, string? remarks, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(remarks)
            ? throw new AppException("Please give a reason for rejecting the expense.")
            : ReviewAsync(id, approverUserId, ExpenseStatus.Rejected, remarks, ct);

    private async Task<ExpenseDto> ReviewAsync(int id, int approverUserId, string status, string? remarks, CancellationToken ct)
    {
        var expense = await db.OfficeExpenses.FirstOrDefaultAsync(e => e.ExpenseId == id, ct)
                      ?? throw new NotFoundException("Expense not found.");
        if (expense.Status != ExpenseStatus.Pending)
            throw new ConflictException($"This expense is already {expense.Status.ToLowerInvariant()}.");
        expense.Status = status;
        expense.ActionedByUserId = approverUserId;
        expense.ActionedAt = time.GetUtcNow().UtcDateTime;
        expense.ReviewRemarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ Receipts
    public async Task<ExpenseDto> SetReceiptAsync(int id, IFormFile file, ExpenseActor actor, CancellationToken ct)
    {
        var expense = await LoadEditableAsync(id, actor, ct);
        if (file.Length == 0) throw new AppException("The selected file is empty.");
        if (file.Length > MaxReceiptBytes) throw new AppException($"Receipt must be {MaxReceiptBytes / (1024 * 1024)} MB or smaller.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        expense.ReceiptContentType = FileSignatures.DetectImageOrPdf(bytes) ?? throw new AppException("Receipt must be a PDF, PNG, JPEG or WebP file.");
        expense.Receipt = bytes;
        expense.ReceiptFileName = SafeFileName(file.FileName, expense.ReceiptContentType);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ExpenseDto> RemoveReceiptAsync(int id, ExpenseActor actor, CancellationToken ct)
    {
        var expense = await LoadEditableAsync(id, actor, ct);
        expense.Receipt = null;
        expense.ReceiptContentType = null;
        expense.ReceiptFileName = null;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> GetReceiptAsync(int id, CancellationToken ct)
    {
        var r = await db.OfficeExpenses.AsNoTracking().Where(e => e.ExpenseId == id)
                    .Select(e => new { e.Receipt, e.ReceiptContentType, e.ReceiptFileName }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Expense not found.");
        if (r.Receipt is null || r.ReceiptContentType is null) throw new NotFoundException("This expense has no receipt.");
        return (r.Receipt, r.ReceiptContentType, r.ReceiptFileName ?? "receipt");
    }

    // ------------------------------------------------------------------ Helpers
    private IQueryable<OfficeExpense> Filter(ExpenseQuery q, int callerUserId)
    {
        var query = db.OfficeExpenses.AsNoTracking();
        if (q.MineOnly) query = query.Where(e => e.CreatedByUserId == callerUserId);
        if (q.From is DateOnly from) query = query.Where(e => e.ExpenseDate >= from);
        if (q.To is DateOnly to) query = query.Where(e => e.ExpenseDate <= to);
        if (q.CategoryId is int cat) query = query.Where(e => e.ExpenseCategoryId == cat);
        if (!string.IsNullOrWhiteSpace(q.Status)) query = query.Where(e => e.Status == q.Status);
        if (!string.IsNullOrWhiteSpace(q.PaymentMode)) query = query.Where(e => e.PaymentMode == q.PaymentMode);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(e => e.Description.Contains(s) || (e.PaidTo != null && e.PaidTo.Contains(s)) ||
                                     (e.BillNumber != null && e.BillNumber.Contains(s)));
        }
        return query;
    }

    private async Task ValidateAsync(ExpenseRequest req, CancellationToken ct, int? currentCategoryId = null)
    {
        if (req.ExpenseDate > Today) throw new AppException("Expense date cannot be in the future.");
        // An inactive category may stay on an existing expense, but cannot be chosen for new ones.
        if (!await db.ExpenseCategories.AnyAsync(c => c.ExpenseCategoryId == req.CategoryId &&
                                                      (c.IsActive || c.ExpenseCategoryId == currentCategoryId), ct))
            throw new AppException("Please choose an active expense category.");
    }

    private static void Apply(OfficeExpense e, ExpenseRequest r)
    {
        e.ExpenseDate = r.ExpenseDate;
        e.ExpenseCategoryId = r.CategoryId;
        e.Description = r.Description.Trim();
        e.Amount = Math.Round(r.Amount, 2);
        e.PaidTo = string.IsNullOrWhiteSpace(r.PaidTo) ? null : r.PaidTo.Trim();
        e.PaymentMode = r.PaymentMode;
        e.BillNumber = string.IsNullOrWhiteSpace(r.BillNumber) ? null : r.BillNumber.Trim();
    }

    /// <summary>Pending expenses can be changed by the person who recorded them, or with expenses.edit / expenses.delete.</summary>
    private async Task<OfficeExpense> LoadEditableAsync(int id, ExpenseActor actor, CancellationToken ct, bool delete = false)
    {
        var expense = await db.OfficeExpenses.FirstOrDefaultAsync(e => e.ExpenseId == id, ct)
                      ?? throw new NotFoundException("Expense not found.");
        if (!(delete ? actor.CanDeleteAll : actor.CanEditAll) && expense.CreatedByUserId != actor.UserId)
            throw new ForbiddenException(delete ? "You can only delete expenses you recorded." : "You can only change expenses you recorded.");
        if (expense.Status != ExpenseStatus.Pending)
            throw new ConflictException($"An {expense.Status.ToLowerInvariant()} expense cannot be changed.");
        return expense;
    }

    private static string SafeFileName(string? name, string contentType)
    {
        var baseName = Path.GetFileNameWithoutExtension(name ?? "");
        baseName = new string(baseName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
        if (baseName.Length == 0) baseName = "receipt";
        if (baseName.Length > 80) baseName = baseName[..80];
        var ext = contentType switch { "application/pdf" => ".pdf", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        return baseName + ext;
    }

    private static IQueryable<ExpenseDto> Project(IQueryable<OfficeExpense> source) => source.Select(e => new ExpenseDto(
        e.ExpenseId, e.ExpenseDate, e.ExpenseCategoryId, e.Category.Name, e.Description, e.Amount, e.PaidTo, e.PaymentMode,
        e.BillNumber, e.Receipt != null, e.ReceiptFileName, e.Status,
        e.CreatedBy.Employee != null ? e.CreatedBy.Employee.FirstName + " " + e.CreatedBy.Employee.LastName : e.CreatedBy.Username,
        e.CreatedByUserId, e.CreatedAt,
        e.ActionedBy != null ? e.ActionedBy.Username : null, e.ActionedAt, e.ReviewRemarks));
}
