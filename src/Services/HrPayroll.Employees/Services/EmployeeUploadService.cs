using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Employees.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Employees.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Employees.Services;

/// <summary>
/// Documents employees upload themselves (ID proofs, certificates…). Employees see and delete their own (until HR
/// has verified them); HR (employees.view) sees everyone's and HR with employees.edit verifies or rejects them.
/// </summary>
public class EmployeeUploadService(EmployeesDbContext db, TimeProvider time)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerEmployee = 50;

    public Task<List<EmployeeUploadDto>> GetMineAsync(int employeeId, CancellationToken ct) =>
        Project(db.EmployeeUploads.AsNoTracking().Where(u => u.EmployeeId == employeeId).OrderByDescending(u => u.UploadedAt), employeeId)
            .ToListAsync(ct);

    public Task<List<EmployeeUploadDto>> SearchAsync(int? employeeId, string? status, CancellationToken ct)
    {
        var q = db.EmployeeUploads.AsNoTracking();
        if (employeeId is int e) q = q.Where(u => u.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(u => u.Status == status);
        // Sorted before projecting (EF cannot order by a projected record): waiting for HR first, newest first.
        return Project(q.OrderBy(u => u.Status == EmployeeUploadStatus.Pending ? 0 : 1).ThenByDescending(u => u.UploadedAt).Take(500), null).ToListAsync(ct);
    }

    public async Task<EmployeeUploadSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var counts = await db.EmployeeUploads.GroupBy(u => u.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return new EmployeeUploadSummaryDto(counts.GetValueOrDefault(EmployeeUploadStatus.Pending), counts.GetValueOrDefault(EmployeeUploadStatus.Verified),
            counts.GetValueOrDefault(EmployeeUploadStatus.Rejected));
    }

    public async Task<EmployeeUploadDto> UploadAsync(int employeeId, EmployeeUploadForm form, CancellationToken ct)
    {
        var category = EmployeeUploadCategory.All.FirstOrDefault(c => string.Equals(c, form.Category.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new AppException("Choose what kind of document this is.");
        if (await db.EmployeeUploads.CountAsync(u => u.EmployeeId == employeeId, ct) >= MaxPerEmployee)
            throw new AppException($"You can keep up to {MaxPerEmployee} documents. Delete old ones first.");
        var bytes = await FileSignatures.ReadAsync(form.File, MaxBytes, ct);
        var type = FileSignatures.DetectImageOrPdf(bytes) ?? throw new AppException("Upload a PDF or a photo (JPEG, PNG or WebP).");
        var ext = type switch { "application/pdf" => ".pdf", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        var title = string.IsNullOrWhiteSpace(form.Title) ? category : form.Title.Trim();

        var upload = new EmployeeUpload
        {
            EmployeeId = employeeId, Category = category, Title = title, ContentType = type, SizeBytes = bytes.Length,
            FileName = FileSignatures.SafeFileName(form.File.FileName, ext, title), UploadedAt = time.GetUtcNow().UtcDateTime,
            File = new EmployeeUploadFile { Data = bytes },
        };
        db.EmployeeUploads.Add(upload);
        await db.SaveChangesAsync(ct);
        return await Project(db.EmployeeUploads.AsNoTracking().Where(u => u.UploadId == upload.UploadId), employeeId).FirstAsync(ct);
    }

    /// <summary>The employee removes their own document - not once HR has verified it.</summary>
    public async Task DeleteMineAsync(int employeeId, int id, CancellationToken ct)
    {
        var u = await db.EmployeeUploads.FirstOrDefaultAsync(x => x.UploadId == id, ct) ?? throw new NotFoundException("Document not found.");
        if (u.EmployeeId != employeeId) throw new ForbiddenException("You can only delete your own documents.");
        if (u.Status == EmployeeUploadStatus.Verified) throw new ConflictException("HR has verified this document, so it can no longer be deleted. Ask HR if it must be replaced.");
        db.EmployeeUploads.Remove(u);   // file cascades
        await db.SaveChangesAsync(ct);
    }

    public async Task<(byte[] Data, string ContentType, string FileName, int EmployeeId)> GetFileAsync(int id, CancellationToken ct)
    {
        var u = await db.EmployeeUploads.AsNoTracking().Where(x => x.UploadId == id)
                    .Select(x => new { x.EmployeeId, x.ContentType, x.FileName, Data = x.File!.Data }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Document not found.");
        return (u.Data, u.ContentType, u.FileName, u.EmployeeId);
    }

    public async Task<EmployeeUploadDto> ReviewAsync(int id, bool verified, string? note, int userId, CancellationToken ct)
    {
        var u = await db.EmployeeUploads.FirstOrDefaultAsync(x => x.UploadId == id, ct) ?? throw new NotFoundException("Document not found.");
        if (!verified && string.IsNullOrWhiteSpace(note)) throw new AppException("Tell the employee why the document is rejected (e.g. blurred, expired, wrong document).");
        u.Status = verified ? EmployeeUploadStatus.Verified : EmployeeUploadStatus.Rejected;
        u.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        u.ReviewedByUserId = userId;
        u.ReviewedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await Project(db.EmployeeUploads.AsNoTracking().Where(x => x.UploadId == id), null).FirstAsync(ct);
    }

    private static IQueryable<EmployeeUploadDto> Project(IQueryable<EmployeeUpload> q, int? owner) => q.Select(u => new EmployeeUploadDto(
        u.UploadId, u.EmployeeId, u.Employee.FirstName + " " + u.Employee.LastName, u.Employee.EmployeeCode, u.Category, u.Title, u.FileName,
        u.ContentType, u.SizeBytes, u.Status, u.UploadedAt,
        u.ReviewedBy != null ? (u.ReviewedBy.Employee != null ? u.ReviewedBy.Employee.FirstName + " " + u.ReviewedBy.Employee.LastName : u.ReviewedBy.Username) : null,
        u.ReviewedAt, u.ReviewNote,
        owner != null && u.EmployeeId == owner && u.Status != EmployeeUploadStatus.Verified));
}
