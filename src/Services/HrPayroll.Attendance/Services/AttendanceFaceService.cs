using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Attendance.Services;

/// <summary>The selfie that passed the face check at check-in / check-out.</summary>
public record FaceCheck(decimal Score, byte[] Jpeg);

/// <summary>
/// Selfie face check for attendance. The employee's face is saved at their first check-in (front camera in the app)
/// and approved straight away - unless HR uploaded an ID-card photo that it clearly does not match, then HR decides.
/// HR can review or reset any registration. Every later check-in / check-out selfie must match the saved face.
/// </summary>
public class AttendanceFaceService(AttendanceDbContext db, FaceService faces, IdPhotoClient idPhotos, TimeProvider time)
{
    public const int MaxSelfieBytes = 4 * 1024 * 1024;

    // ------------------------------------------------------------------ Employee
    public async Task<FaceStatusDto> GetMineAsync(int employeeId, CancellationToken ct)
    {
        var f = await db.EmployeeFaces.AsNoTracking().Where(x => x.EmployeeId == employeeId)
            .Select(x => new { x.Status, x.EnrolledAt, x.Note }).FirstOrDefaultAsync(ct);
        return new FaceStatusDto(f?.Status, f?.EnrolledAt, f?.Note, await idPhotos.ExistsAsync(employeeId, ct));
    }

    /// <summary>Registers the employee's face from a selfie (again after HR rejected or reset it).</summary>
    public async Task<FaceStatusDto> EnrollAsync(int employeeId, string? selfieBase64, CancellationToken ct)
    {
        var existing = await db.EmployeeFaces.FirstOrDefaultAsync(f => f.EmployeeId == employeeId, ct);
        if (existing is { Status: EmployeeFaceStatus.Approved })
            throw new ConflictException("Your face is already registered. Ask HR to reset it if you need to register again.");
        var scan = await faces.ScanAsync(Decode(selfieBase64), ct);

        // Compare with the ID-card photo HR uploaded, when there is one.
        decimal? idScore = null;
        var idPhoto = await idPhotos.GetAsync(employeeId, ct);   // from the Employees service
        if (idPhoto is not null)
        {
            try { idScore = Round(FaceService.Similarity(scan.Embedding, (await faces.ScanAsync(idPhoto, ct)).Embedding)); }
            catch (AppException) { /* ID photo without a usable face: HR decides */ }
        }

        var face = existing ?? db.EmployeeFaces.Add(new EmployeeFace { EmployeeId = employeeId }).Entity;
        face.Embedding = FaceService.ToBytes(scan.Embedding);
        face.Photo = scan.Jpeg;
        face.IdPhotoScore = idScore;
        face.EnrolledAt = time.GetUtcNow().UtcDateTime;
        face.ReviewedByUserId = null;
        face.ReviewedAt = null;
        (face.Status, face.Note) = idScore switch
        {
            decimal s when s >= (decimal)FaceService.MatchThreshold => (EmployeeFaceStatus.Approved, "Matched your ID-card photo."),
            decimal => (EmployeeFaceStatus.Pending, "Did not clearly match your ID-card photo - HR will check it."),
            null => (EmployeeFaceStatus.Approved, "Saved from the mobile app (no ID-card photo to compare). HR can review it."),
        };
        await db.SaveChangesAsync(ct);
        return await GetMineAsync(employeeId, ct);
    }

    /// <summary>Checks a check-in / check-out selfie against the registered face.</summary>
    public async Task<FaceCheck> VerifyAsync(int employeeId, string? selfieBase64, string action, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(selfieBase64)) throw new AppException($"Take a selfie to {action} - your company requires a face check.");
        var f = await db.EmployeeFaces.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == employeeId, ct);
        switch (f?.Status)
        {
            case null: throw new AppException("Register your face in the app first (Attendance › Register my face).");
            case EmployeeFaceStatus.Pending: throw new AppException("Your face registration is waiting for HR approval - you can mark attendance once HR approves it.");
            case EmployeeFaceStatus.Rejected: throw new AppException($"HR rejected your face registration{(f.Note is null ? "" : $" ({f.Note})")}. Register again with a clear selfie.");
        }
        var scan = await faces.ScanAsync(Decode(selfieBase64), ct);
        var score = Round(FaceService.Similarity(scan.Embedding, FaceService.FromBytes(f.Embedding)));
        if (score < (decimal)FaceService.MatchThreshold)
            throw new AppException("Your face did not match your registered photo. Look straight at the camera in good light and try again.");
        return new FaceCheck(score, scan.Jpeg);
    }

    // ------------------------------------------------------------------ HR
    public async Task<EmployeeFaceDto?> GetAsync(int employeeId, CancellationToken ct) =>
        await db.EmployeeFaces.AsNoTracking().Where(f => f.EmployeeId == employeeId)
            .Select(f => new EmployeeFaceDto(f.EmployeeId, f.Status, f.EnrolledAt, f.IdPhotoScore, f.Note,
                f.ReviewedBy != null ? (f.ReviewedBy.Employee != null ? f.ReviewedBy.Employee.FirstName + " " + f.ReviewedBy.Employee.LastName : f.ReviewedBy.Username) : null,
                f.ReviewedAt, "data:image/jpeg;base64," + Convert.ToBase64String(f.Photo)))
            .FirstOrDefaultAsync(ct);

    public async Task<EmployeeFaceDto> ReviewAsync(int employeeId, bool approve, string? note, int userId, CancellationToken ct)
    {
        var f = await db.EmployeeFaces.FirstOrDefaultAsync(x => x.EmployeeId == employeeId, ct) ?? throw new NotFoundException("No face registered.");
        if (!approve && string.IsNullOrWhiteSpace(note)) throw new AppException("Tell the employee why (e.g. not their face, too dark, sunglasses).");
        f.Status = approve ? EmployeeFaceStatus.Approved : EmployeeFaceStatus.Rejected;
        f.Note = approve ? "Approved by HR." : note!.Trim();
        f.ReviewedByUserId = userId;
        f.ReviewedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (await GetAsync(employeeId, ct))!;
    }

    /// <summary>Removes the registration so the employee can register again (new look, wrong photo…).</summary>
    public async Task ResetAsync(int employeeId, CancellationToken ct) =>
        await db.EmployeeFaces.Where(f => f.EmployeeId == employeeId).ExecuteDeleteAsync(ct);

    public async Task<byte[]?> SelfieAsync(int employeeId, DateOnly date, string kind, CancellationToken ct) =>
        await db.AttendanceSelfies.AsNoTracking()
            .Where(s => s.Kind == kind && db.Attendance.Any(a => a.AttendanceId == s.AttendanceId && a.EmployeeId == employeeId && a.AttendanceDate == date))
            .Select(s => s.Photo).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ Helpers
    private static byte[] Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) throw new AppException("Take a selfie first.");
        var comma = base64.IndexOf(',');   // tolerate "data:image/jpeg;base64,..."
        byte[] bytes;
        try { bytes = Convert.FromBase64String(comma >= 0 ? base64[(comma + 1)..] : base64); }
        catch (FormatException) { throw new AppException("The photo could not be read. Take the selfie again."); }
        if (bytes.Length > MaxSelfieBytes) throw new AppException("The photo is too big (4 MB at most).");
        if (FileSignatures.DetectImage(bytes) is null) throw new AppException("The selfie must be a JPEG, PNG or WebP photo.");
        return bytes;
    }

    private static decimal Round(float score) => Math.Round((decimal)score, 3);
}
