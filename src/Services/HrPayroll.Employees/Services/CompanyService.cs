using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Employees.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Employees.Entities;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Employees.Services;

public class CompanyService(EmployeesDbContext db, TimeProvider time)
{
    public const int MaxLogoBytes = 512 * 1024;

    public async Task<CompanyDto> GetAsync(CancellationToken ct)
    {
        var c = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct)
                ?? new CompanyProfile { Name = "Your Company" };
        return ToDto(c);
    }

    public async Task<CompanyDto> UpdateAsync(UpdateCompanyRequest req, CancellationToken ct)
    {
        var c = await LoadOrCreateAsync(ct);
        c.Name = req.Name.Trim();
        c.LegalName = Clean(req.LegalName);
        c.AddressLine1 = Clean(req.AddressLine1);
        c.AddressLine2 = Clean(req.AddressLine2);
        c.City = Clean(req.City);
        c.State = Clean(req.State);
        c.PinCode = Clean(req.PinCode);
        c.Phone = Clean(req.Phone);
        c.Email = Clean(req.Email)?.ToLowerInvariant();
        c.Website = Clean(req.Website);
        c.Pan = Clean(req.Pan)?.ToUpperInvariant();
        c.Tan = Clean(req.Tan)?.ToUpperInvariant();
        c.Gstin = Clean(req.Gstin)?.ToUpperInvariant();
        c.SignatoryName = Clean(req.SignatoryName);
        c.SignatoryDesignation = Clean(req.SignatoryDesignation);
        c.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CompanyDto> SetLogoAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new AppException("The selected file is empty.");
        if (file.Length > MaxLogoBytes) throw new AppException($"Logo must be {MaxLogoBytes / 1024} KB or smaller.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        // Trust the file signature, not the client-supplied content type. SVG is rejected on purpose (can carry script).
        var contentType = FileSignatures.DetectImage(bytes)
                          ?? throw new AppException("Logo must be a PNG, JPEG or WebP image.");

        var c = await LoadOrCreateAsync(ct);
        c.Logo = bytes;
        c.LogoContentType = contentType;
        c.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CompanyDto> RemoveLogoAsync(CancellationToken ct)
    {
        var c = await LoadOrCreateAsync(ct);
        c.Logo = null;
        c.LogoContentType = null;
        c.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    // ------------------------------------------------------------------
    private async Task<CompanyProfile> LoadOrCreateAsync(CancellationToken ct)
    {
        var c = await db.CompanyProfiles.FirstOrDefaultAsync(ct);
        if (c is not null) return c;
        c = new CompanyProfile { Name = "Your Company" };
        db.CompanyProfiles.Add(c);
        return c;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static CompanyDto ToDto(CompanyProfile c) => new(
        c.Name, c.LegalName, c.AddressLine1, c.AddressLine2, c.City, c.State, c.PinCode, c.Phone, c.Email, c.Website,
        c.Pan, c.Tan, c.Gstin, c.SignatoryName, c.SignatoryDesignation,
        c.Logo is { Length: > 0 } && c.LogoContentType is not null
            ? $"data:{c.LogoContentType};base64,{Convert.ToBase64String(c.Logo)}"
            : null,
        c.UpdatedAt);
}
